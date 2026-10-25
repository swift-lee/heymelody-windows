#import <Foundation/Foundation.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudio/CoreAudio.h>
#include <atomic>
#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace {
AudioObjectPropertyAddress property(AudioObjectPropertySelector selector,
                                    AudioObjectPropertyScope scope = kAudioObjectPropertyScopeGlobal) {
    return {selector, scope, kAudioObjectPropertyElementMain};
}
template<class T> bool read(AudioObjectID object, AudioObjectPropertySelector selector, T& value) {
    auto address = property(selector); UInt32 size = sizeof(T);
    return AudioObjectGetPropertyData(object, &address, 0, nullptr, &size, &value) == noErr;
}
CFStringRef copyString(AudioObjectID object, AudioObjectPropertySelector selector) {
    CFStringRef value = nullptr;
    return read(object, selector, value) ? value : nullptr;
}
bool bluetoothOutput(AudioDeviceID device) {
    UInt32 transport = 0, alive = 0;
    if (!read(device, kAudioDevicePropertyTransportType, transport)
        || (transport != kAudioDeviceTransportTypeBluetooth && transport != kAudioDeviceTransportTypeBluetoothLE)
        || !read(device, kAudioDevicePropertyDeviceIsAlive, alive) || !alive) return false;
    auto address = property(kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeOutput);
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(device, &address, 0, nullptr, &size) != noErr || size < sizeof(AudioBufferList)) return false;
    std::vector<uint8_t> bytes(size);
    auto buffers = reinterpret_cast<AudioBufferList*>(bytes.data());
    if (AudioObjectGetPropertyData(device, &address, 0, nullptr, &size, buffers) != noErr) return false;
    UInt32 channels = 0;
    for (UInt32 i = 0; i < buffers->mNumberBuffers; ++i) channels += buffers->mBuffers[i].mNumberChannels;
    return channels >= 2; // The bundled audition is stereo; never switch a headset to its microphone profile.
}
AudioDeviceID resolve(CFStringRef uid) {
    auto address = property(kAudioHardwarePropertyDevices); UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, nullptr, &size) != noErr)
        return kAudioObjectUnknown;
    std::vector<AudioDeviceID> devices(size / sizeof(AudioDeviceID));
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, nullptr, &size, devices.data()) != noErr)
        return kAudioObjectUnknown;
    AudioDeviceID match = kAudioObjectUnknown;
    for (auto candidate : devices) {
        CFStringRef candidateUid = copyString(candidate, kAudioDevicePropertyDeviceUID);
        const bool same = candidateUid && CFEqual(uid, candidateUid);
        if (candidateUid) CFRelease(candidateUid);
        if (same) {
            if (match != kAudioObjectUnknown) return kAudioObjectUnknown;
            match = candidate;
        }
    }
    return match;
}
struct Stream {
    AudioQueueRef queue = nullptr;
    AudioDeviceID device = kAudioObjectUnknown;
    CFStringRef uid = nullptr;
    std::vector<int16_t> samples;
    size_t cursor = 0;
    std::atomic<bool> failed{false}, playing{false};
    std::atomic<int64_t> frames{0};
    std::mutex fill;
    dispatch_queue_t events = nullptr;
    AudioObjectPropertyListenerBlock listener = nullptr;
    bool listening = false, listeningSystem = false;
    void refill(AudioQueueBufferRef buffer) {
        std::lock_guard<std::mutex> lock(fill);
        const size_t count = buffer->mAudioDataBytesCapacity / sizeof(int16_t);
        auto output = static_cast<int16_t*>(buffer->mAudioData);
        for (size_t i = 0; i < count; ++i) { output[i] = samples[cursor]; cursor = (cursor + 1) % samples.size(); }
        buffer->mAudioDataByteSize = static_cast<UInt32>(count * sizeof(int16_t));
        if (AudioQueueEnqueueBuffer(queue, buffer, 0, nullptr) != noErr) failed = true;
    }
    ~Stream() {
        playing = false;
        if (listening) {
            auto address = property(kAudioDevicePropertyDeviceIsAlive);
            AudioObjectRemovePropertyListenerBlock(device, &address, events, listener);
        }
        if (listeningSystem) {
            auto address = property(kAudioHardwarePropertyDevices);
            AudioObjectRemovePropertyListenerBlock(kAudioObjectSystemObject, &address, events, listener);
        }
        if (events) dispatch_sync(events, ^{});
        if (queue) { AudioQueueStop(queue, true); AudioQueueDispose(queue, true); }
        if (uid) CFRelease(uid);
    }
};
void callback(void* context, AudioQueueRef, AudioQueueBufferRef buffer) {
    auto stream = static_cast<Stream*>(context);
    if (stream->failed) return;
    stream->frames += buffer->mAudioDataByteSize / 4;
    stream->refill(buffer);
}
}

extern "C" __attribute__((visibility("default"))) char* hm_audio_list() {
    @autoreleasepool {
        auto address = property(kAudioHardwarePropertyDevices); UInt32 size = 0;
        if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, nullptr, &size) != noErr) return nullptr;
        std::vector<AudioDeviceID> devices(size / sizeof(AudioDeviceID));
        if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, nullptr, &size, devices.data()) != noErr) return nullptr;
        NSMutableArray* result = [NSMutableArray array];
        for (auto device : devices) {
            if (!bluetoothOutput(device)) continue;
            CFStringRef uid = copyString(device, kAudioDevicePropertyDeviceUID);
            CFStringRef name = copyString(device, kAudioObjectPropertyName);
            if (uid && name) [result addObject:@{@"uid": (__bridge NSString*)uid, @"name": (__bridge NSString*)name}];
            if (uid) CFRelease(uid); if (name) CFRelease(name);
        }
        NSData* data = [NSJSONSerialization dataWithJSONObject:result options:0 error:nil];
        return data ? strndup(static_cast<const char*>(data.bytes), data.length) : nullptr;
    }
}
extern "C" __attribute__((visibility("default"))) void hm_audio_free_string(char* text) { free(text); }
extern "C" __attribute__((visibility("default"))) void* hm_audio_create(const char* uidText, const int16_t* samples,
                                                                         int sampleCount, uint32_t sampleRate) {
    @autoreleasepool {
        if (!uidText || !samples || sampleCount <= 0 || sampleCount % 2 || sampleRate < 8000 || sampleRate > 192000) return nullptr;
        CFStringRef uid = CFStringCreateWithCString(nullptr, uidText, kCFStringEncodingUTF8);
        if (!uid) return nullptr;
        auto stream = std::make_unique<Stream>();
        stream->uid = static_cast<CFStringRef>(CFRetain(uid));
        stream->device = resolve(uid);
        if (!bluetoothOutput(stream->device)) { CFRelease(uid); return nullptr; }
        stream->samples.assign(samples, samples + sampleCount);
        AudioStreamBasicDescription format{};
        format.mSampleRate = sampleRate; format.mFormatID = kAudioFormatLinearPCM;
        format.mFormatFlags = kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked;
        format.mBytesPerPacket = 4; format.mFramesPerPacket = 1; format.mBytesPerFrame = 4;
        format.mChannelsPerFrame = 2; format.mBitsPerChannel = 16;
        if (AudioQueueNewOutput(&format, callback, stream.get(), nullptr, nullptr, 0, &stream->queue) != noErr) {
            CFRelease(uid); return nullptr;
        }
        if (AudioQueueSetProperty(stream->queue, kAudioQueueProperty_CurrentDevice, &uid, sizeof(uid)) != noErr) {
            CFRelease(uid); return nullptr;
        }
        CFStringRef actual = nullptr; UInt32 size = sizeof(actual);
        const bool matches = AudioQueueGetProperty(stream->queue, kAudioQueueProperty_CurrentDevice, &actual, &size) == noErr
            && actual && CFEqual(uid, actual);
        if (actual) CFRelease(actual); CFRelease(uid);
        if (!matches) return nullptr;
        stream->events = dispatch_queue_create("HeyMelody.audio.device", DISPATCH_QUEUE_SERIAL);
        Stream* current = stream.get();
        stream->listener = ^(UInt32, const AudioObjectPropertyAddress*) {
            UInt32 alive = 0;
            if (!read(current->device, kAudioDevicePropertyDeviceIsAlive, alive) || !alive
                || resolve(current->uid) != current->device || !bluetoothOutput(current->device)) {
                current->failed = true; current->playing = false; AudioQueuePause(current->queue);
            }
        };
        auto alive = property(kAudioDevicePropertyDeviceIsAlive);
        if (AudioObjectAddPropertyListenerBlock(stream->device, &alive, stream->events, stream->listener) != noErr) return nullptr;
        stream->listening = true;
        auto devices = property(kAudioHardwarePropertyDevices);
        if (AudioObjectAddPropertyListenerBlock(kAudioObjectSystemObject, &devices, stream->events, stream->listener) != noErr) return nullptr;
        stream->listeningSystem = true;
        for (int i = 0; i < 3; ++i) {
            AudioQueueBufferRef buffer = nullptr;
            if (AudioQueueAllocateBuffer(stream->queue, 4096, &buffer) != noErr) return nullptr;
            stream->refill(buffer);
            if (stream->failed) return nullptr;
        }
        return stream.release();
    }
}
extern "C" __attribute__((visibility("default"))) int hm_audio_play(void* handle) {
    auto stream = static_cast<Stream*>(handle);
    if (!stream || stream->failed || !bluetoothOutput(stream->device)) return -1;
    stream->playing = true;
    const auto result = AudioQueueStart(stream->queue, nullptr);
    if (result != noErr) { stream->playing = false; stream->failed = true; }
    return static_cast<int>(result);
}
extern "C" __attribute__((visibility("default"))) int hm_audio_pause(void* handle) {
    auto stream = static_cast<Stream*>(handle); if (!stream) return -1;
    stream->playing = false; return static_cast<int>(AudioQueuePause(stream->queue));
}
extern "C" __attribute__((visibility("default"))) int64_t hm_audio_frame(void* handle) {
    auto stream = static_cast<Stream*>(handle); return stream ? stream->frames.load() : 0;
}
extern "C" __attribute__((visibility("default"))) int hm_audio_failed(void* handle) {
    auto stream = static_cast<Stream*>(handle); return !stream || stream->failed ? 1 : 0;
}
extern "C" __attribute__((visibility("default"))) void hm_audio_destroy(void* handle) { delete static_cast<Stream*>(handle); }
