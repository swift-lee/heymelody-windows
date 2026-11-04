#import <Foundation/Foundation.h>
#import <AppKit/AppKit.h>
#import <IOBluetooth/IOBluetooth.h>
#import <CoreBluetooth/CoreBluetooth.h>
#include <stdint.h>

// All adapter and channel state belongs to the main CFRunLoop. Delegate data is copied
// before returning, and completion work is queued so inline IOBluetooth callbacks cannot
// reenter an open/write operation whose state has not yet been committed.
typedef void (*HMCallback)(void *, int, int64_t, int64_t, int, const void *, size_t);
@class HMChannel;
@interface HMBluetooth : NSObject<CBCentralManagerDelegate>
@property(nonatomic) HMCallback callback;
@property(nonatomic) void *context;
@property(nonatomic) BOOL active;
@property(nonatomic) BOOL scanWanted;
@property(nonatomic) BOOL suspended;
@property(nonatomic,strong) CBCentralManager *central;
@property(nonatomic,strong) CBCentralManager *scanCentral;
@property(nonatomic) int64_t scanGeneration;
@property(nonatomic) int64_t scanRequestGeneration;
@property(nonatomic) int64_t scanStartedAt;
@property(nonatomic) BOOL scanEverStarted;
@property(nonatomic,strong) NSMutableDictionary<NSNumber *,HMChannel *> *channels;
@property(nonatomic,strong) NSMutableArray<IOBluetoothUserNotification *> *disconnects;
@property(nonatomic,strong) IOBluetoothUserNotification *connections;
@property(nonatomic,strong) id sleepObserver;
@property(nonatomic,strong) id wakeObserver;
- (void)emit:(int)kind operation:(int64_t)operation channel:(int64_t)channel status:(int)status data:(NSData *)data;
- (int)accessError;
- (void)statusChanged;
- (void)updateScan;
- (void)retireScan;
- (void)observeDevices;
- (void)shutdown;
@end
@interface HMChannel : NSObject<IOBluetoothDeviceAsyncCallbacks,IOBluetoothRFCOMMChannelDelegate>
@property(nonatomic,weak) HMBluetooth *owner;
@property(nonatomic) int64_t identifier;
@property(nonatomic,strong) IOBluetoothDevice *device;
@property(nonatomic,strong) NSArray<IOBluetoothSDPUUID *> *uuids;
@property(nonatomic,strong) HMChannel *queryLifetime;
@property(nonatomic,strong) IOBluetoothRFCOMMChannel *channel;
@property(nonatomic) BOOL closed;
@property(nonatomic) BOOL opened;
@property(nonatomic) BOOL openStarted;
@property(nonatomic) BOOL sdpPending;
@property(nonatomic) BOOL openPending;
@property(nonatomic) BOOL fromCache;
@property(nonatomic) BluetoothRFCOMMChannelID selectedChannel;
@property(nonatomic) NSTimeInterval openRequestedAt;
@property(nonatomic,strong) dispatch_source_t sdpTimer;
@property(nonatomic,strong) dispatch_source_t rfcommTimer;
@property(nonatomic,strong) NSMutableArray<NSData *> *earlyData;
@property(nonatomic) NSUInteger earlyBytes;
@property(nonatomic,strong) NSData *writeData;
@property(nonatomic) NSUInteger writeOffset;
@property(nonatomic) NSUInteger writeChunk;
@property(nonatomic) int64_t writeOperation;
- (void)beginOpen;
- (void)beginSdpFallback;
- (void)openChannel:(BluetoothRFCOMMChannelID)channelID;
- (void)armSdpTimer;
- (void)armRfcommTimer;
- (void)cancelSdpTimer;
- (void)cancelRfcommTimer;
- (void)detachChannel;
- (void)emitDiagnostic:(NSString *)stage status:(int)status endpoints:(NSUInteger)endpoints
    channel:(BluetoothRFCOMMChannelID)channel source:(NSString *)source;
- (void)failOpen:(int)status;
- (void)close:(int)status;
- (void)writeNext;
@end
static HMBluetooth *gAdapter;
static void OnMain(dispatch_block_t action) { dispatch_async(dispatch_get_main_queue(), ^{ @autoreleasepool { action(); } }); }
static NSData *JSON(id value) { return [NSJSONSerialization dataWithJSONObject:value options:0 error:nil]; }
static uint64_t DeviceAddress(IOBluetoothDevice *device) {
    NSString *hex = [[device.addressString stringByReplacingOccurrencesOfString:@"-" withString:@""] stringByReplacingOccurrencesOfString:@":" withString:@""];
    unsigned long long value=0;
    if (hex.length != 12 || ![[NSScanner scannerWithString:hex] scanHexLongLong:&value]) return 0;
    return value & 0xFFFFFFFFFFFFULL;
}
static NSString *AddressString(uint64_t address) {
    NSString *hex=[NSString stringWithFormat:@"%012llX",(unsigned long long)address];
    NSMutableArray *parts=[NSMutableArray array];
    for (NSUInteger i=0;i<12;i+=2) [parts addObject:[hex substringWithRange:NSMakeRange(i,2)]];
    return [parts componentsJoinedByString:@"-"];
}
static BluetoothRFCOMMChannelID HMChannelID(IOBluetoothSDPServiceRecord *service) {
    BluetoothRFCOMMChannelID channel=0;
    if (!service || [service getRFCOMMChannelID:&channel] != kIOReturnSuccess) return 0;
    return channel >= 1 && channel <= 30 ? channel : 0;
}
// Reads the system's cached SDP records. The UUID-filtered performSDPQuery variant is
// a no-op on macOS 12+, so this must not depend on it having populated -services.
static NSArray<NSDictionary<NSString *, NSNumber *> *> *HMResolveEndpoints(IOBluetoothDevice *device,
    NSArray<IOBluetoothSDPUUID *> *uuids) {
    NSMutableDictionary<NSString *, NSNumber *> *endpoints=[NSMutableDictionary dictionary];
    NSArray<IOBluetoothSDPServiceRecord *> *services=device.services;
    for (IOBluetoothSDPUUID *uuid in uuids) {
        BOOL found=NO;
        for (IOBluetoothSDPServiceRecord *service in services) {
            if (![service matchesUUIDArray:@[uuid]]) continue;
            BluetoothRFCOMMChannelID channel=HMChannelID(service);
            if (!channel) continue;
            endpoints[[NSString stringWithFormat:@"%@:%u",uuid,channel]]=@(channel);
            found=YES;
        }
        if (found) continue;
        // getServiceRecordForUUID: is synchronous and may expose the system cache even
        // when -services has not been populated for this process.
        IOBluetoothSDPServiceRecord *service=[device getServiceRecordForUUID:uuid];
        BluetoothRFCOMMChannelID channel=HMChannelID(service);
        if (channel) endpoints[[NSString stringWithFormat:@"%@:%u",uuid,channel]]=@(channel);
    }
    NSMutableArray<NSDictionary<NSString *, NSNumber *> *> *result=[NSMutableArray arrayWithCapacity:endpoints.count];
    [endpoints enumerateKeysAndObjectsUsingBlock:^(NSString *key, NSNumber *channel, BOOL *stop) {
        (void)stop;
        NSMutableDictionary *endpoint=[NSMutableDictionary dictionary];
        endpoint[@"key"]=key; endpoint[@"channel"]=channel;
        [result addObject:endpoint];
    }];
    return result;
}
static int HMOpenStatus(IOReturn status) {
    return status == kIOReturnExclusiveAccess || status == kIOReturnBusy ? -11 : (int)status;
}
@implementation HMBluetooth
- (instancetype)init {
    if ((self=[super init])) { _active=YES; _channels=[NSMutableDictionary dictionary]; _disconnects=[NSMutableArray array]; }
    return self;
}
- (void)emit:(int)kind operation:(int64_t)operation channel:(int64_t)channel status:(int)status data:(NSData *)data {
    if (self.active && self.callback) self.callback(self.context,kind,operation,channel,status,data.bytes,data.length);
}
- (int)accessError {
    switch ([CBManager authorization]) {
        case CBManagerAuthorizationDenied:return -4;
        case CBManagerAuthorizationRestricted:return -5;
        case CBManagerAuthorizationNotDetermined:return -6;
        case CBManagerAuthorizationAllowedAlways:break;
    }
    if (self.central.state == CBManagerStateUnsupported) return -7;
    if (self.central.state == CBManagerStatePoweredOff) return -8;
    return 0;
}
- (void)statusChanged {
    int access=0;
    switch ([CBManager authorization]) {
        case CBManagerAuthorizationNotDetermined:access=1;break;
        case CBManagerAuthorizationAllowedAlways:access=2;break;
        case CBManagerAuthorizationDenied:access=3;break;
        case CBManagerAuthorizationRestricted:access=4;break;
    }
    if (self.central.state == CBManagerStateUnsupported) access=5;
    id radio=(self.central.state == CBManagerStatePoweredOn) ? @YES :
        ((self.central.state == CBManagerStatePoweredOff || self.central.state == CBManagerStateUnsupported) ? @NO : [NSNull null]);
    [self emit:0 operation:0 channel:0 status:0 data:JSON(@{@"access":@(access),@"radioAvailable":radio})];
    [self updateScan];
}
- (void)centralManagerDidUpdateState:(CBCentralManager *)central {
    if (!self.active) return;
    if (central != self.central) {
        if (central == self.scanCentral) [self updateScan];
        return;
    }
    [self statusChanged];
    [self emit:6 operation:0 channel:0 status:0 data:nil];
}
- (void)updateScan {
    BOOL wanted=self.active && self.scanWanted && !self.suspended && self.central.state == CBManagerStatePoweredOn && [self accessError] == 0;
    if (!wanted) { [self retireScan]; return; }
    if (self.scanCentral && self.scanEverStarted && self.scanCentral.state != CBManagerStatePoweredOn) [self retireScan];
    if (!self.scanCentral) {
        self.scanGeneration++; self.scanEverStarted=NO;
        self.scanStartedAt=(int64_t)([NSDate date].timeIntervalSince1970*1000);
        self.scanCentral=[[CBCentralManager alloc] initWithDelegate:self queue:dispatch_get_main_queue() options:@{CBCentralManagerOptionShowPowerAlertKey:@NO}];
        [self emit:10 operation:0 channel:0 status:1 data:JSON(@{@"generation":@(self.scanGeneration),
            @"requestGeneration":@(self.scanRequestGeneration),@"startedAt":@(self.scanStartedAt)})];
    }
    if (self.scanCentral.state == CBManagerStatePoweredOn && !self.scanCentral.isScanning) {
        self.scanEverStarted=YES;
        [self.scanCentral scanForPeripheralsWithServices:nil options:@{CBCentralManagerScanOptionAllowDuplicatesKey:@YES}];
    }
}
- (void)retireScan {
    CBCentralManager *retired=self.scanCentral;
    if (!retired) return;
    // Identity is invalidated before detaching: cached callbacks from the old
    // manager can never be attributed to a later scan, even after sleep/wake.
    self.scanCentral=nil; self.scanEverStarted=NO;
    retired.delegate=nil; [retired stopScan];
    [self emit:10 operation:0 channel:0 status:0 data:JSON(@{@"generation":@(self.scanGeneration),
        @"requestGeneration":@(self.scanRequestGeneration),@"startedAt":@(self.scanStartedAt)})];
}
- (void)centralManager:(CBCentralManager *)central didDiscoverPeripheral:(CBPeripheral *)peripheral advertisementData:(NSDictionary<NSString *,id> *)advertisementData RSSI:(NSNumber *)RSSI {
    if (!self.active || central != self.scanCentral) return;
    NSData *manufacturer=advertisementData[CBAdvertisementDataManufacturerDataKey];
    if (!self.scanWanted || self.suspended || manufacturer.length < 2 || manufacturer.length > 4098 || RSSI.intValue == 127) return;
    const uint8_t *bytes=(const uint8_t *)manufacturer.bytes;
    uint16_t company=bytes[0] | ((uint16_t)bytes[1]<<8);
    if (company != 0x079A && company != 0x072F) return;
    NSData *payload=[manufacturer subdataWithRange:NSMakeRange(2,manufacturer.length-2)];
    // CoreBluetooth exposes peripheral UUIDs, not Classic MAC addresses. Only the
    // protocol parser's independently embedded address can associate this observation.
    [self emit:7 operation:0 channel:0 status:0 data:JSON(@{@"companyId":@(company),@"data":[payload base64EncodedStringWithOptions:0],
        @"timestamp":@((int64_t)([NSDate date].timeIntervalSince1970*1000)),@"rssi":RSSI,
        @"generation":@(self.scanGeneration),@"requestGeneration":@(self.scanRequestGeneration)})];
}
- (void)connected:(IOBluetoothUserNotification *)notification device:(IOBluetoothDevice *)device {
    uint64_t address=DeviceAddress(device);
    OnMain(^{
        if (!self.active) return;
        [self emit:9 operation:0 channel:(int64_t)address status:1 data:nil];
        [self observeDevices]; [self emit:6 operation:0 channel:0 status:0 data:nil];
    });
}
- (void)disconnected:(IOBluetoothUserNotification *)notification device:(IOBluetoothDevice *)device {
    uint64_t address=DeviceAddress(device);
    OnMain(^{
        if (!self.active) return;
        [self emit:9 operation:0 channel:(int64_t)address status:0 data:nil];
        [self emit:6 operation:0 channel:0 status:0 data:nil];
    });
}
- (void)observeDevices {
    for (IOBluetoothUserNotification *notification in self.disconnects) [notification unregister];
    [self.disconnects removeAllObjects];
    if ([self accessError] != 0) return;
    for (IOBluetoothDevice *device in [IOBluetoothDevice pairedDevices]) {
        if (device.isConnected) {
            IOBluetoothUserNotification *notification=[device registerForDisconnectNotification:self selector:@selector(disconnected:device:)];
            if (notification) [self.disconnects addObject:notification];
        }
    }
}
- (void)shutdown {
    self.active=NO;
    [self retireScan];
    [self.central stopScan]; self.central.delegate=nil; self.central=nil;
    for (HMChannel *channel in self.channels.allValues) [channel close:-9];
    [self.channels removeAllObjects];
    [self.connections unregister]; self.connections=nil;
    for (IOBluetoothUserNotification *notification in self.disconnects) [notification unregister];
    [self.disconnects removeAllObjects];
    NSNotificationCenter *center=NSWorkspace.sharedWorkspace.notificationCenter;
    if (self.sleepObserver) [center removeObserver:self.sleepObserver];
    if (self.wakeObserver) [center removeObserver:self.wakeObserver];
    self.sleepObserver=nil; self.wakeObserver=nil; self.callback=nullptr; self.context=nullptr;
}
@end
@implementation HMChannel
- (void)remoteNameRequestComplete:(IOBluetoothDevice *)device status:(IOReturn)status { }
- (void)connectionComplete:(IOBluetoothDevice *)device status:(IOReturn)status { }
- (NSString *)sourceName { return self.fromCache ? @"cache" : @"sdp"; }
- (void)emitDiagnostic:(NSString *)stage status:(int)status endpoints:(NSUInteger)endpoints
    channel:(BluetoothRFCOMMChannelID)channel source:(NSString *)source {
    NSMutableDictionary *data=[NSMutableDictionary dictionary];
    data[@"stage"]=stage;
    data[@"endpoints"]=@(endpoints);
    if (channel) data[@"channel"]=@(channel);
    if (status) data[@"status"]=@(status);
    if (source) data[@"source"]=source;
    data[@"elapsedMs"]=@(self.openRequestedAt > 0
        ? (int64_t)(([NSDate date].timeIntervalSince1970-self.openRequestedAt)*1000) : 0);
    [self.owner emit:11 operation:self.identifier channel:self.identifier status:status data:JSON(data)];
}
- (void)cancelSdpTimer {
    dispatch_source_t timer=self.sdpTimer;
    if (!timer) return;
    self.sdpTimer=nil; dispatch_source_cancel(timer);
}
- (void)cancelRfcommTimer {
    dispatch_source_t timer=self.rfcommTimer;
    if (!timer) return;
    self.rfcommTimer=nil; dispatch_source_cancel(timer);
}
- (void)detachChannel {
    IOBluetoothRFCOMMChannel *channel=self.channel;
    self.channel=nil;
    if (!channel) return;
    [channel setDelegate:nil]; [channel closeChannel];
}
- (void)armSdpTimer {
    [self cancelSdpTimer];
    dispatch_source_t timer=dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER,0,0,dispatch_get_main_queue());
    dispatch_source_set_timer(timer,dispatch_time(DISPATCH_TIME_NOW,(int64_t)(5*NSEC_PER_SEC)),DISPATCH_TIME_FOREVER,0);
    __weak HMChannel *weakSelf=self;
    dispatch_source_set_event_handler(timer,^{
        HMChannel *live=weakSelf;
        if (!live || live.closed || !live.sdpPending) return;
        live.sdpPending=NO;
        [live cancelSdpTimer];
        // Keep queryLifetime: IOBluetooth may still deliver sdpQueryComplete, and the
        // delegate target must outlive that callback even after this operation failed.
        [live emitDiagnostic:@"failed" status:-9 endpoints:0 channel:0 source:@"query"];
        [live failOpen:-9];
    });
    self.sdpTimer=timer;
    dispatch_resume(timer);
}
- (void)armRfcommTimer {
    [self cancelRfcommTimer];
    dispatch_source_t timer=dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER,0,0,dispatch_get_main_queue());
    dispatch_source_set_timer(timer,dispatch_time(DISPATCH_TIME_NOW,(int64_t)(5*NSEC_PER_SEC)),DISPATCH_TIME_FOREVER,0);
    __weak HMChannel *weakSelf=self;
    dispatch_source_set_event_handler(timer,^{
        HMChannel *live=weakSelf;
        if (!live || live.closed || !live.openPending || live.opened) return;
        live.openPending=NO;
        [live cancelRfcommTimer];
        [live detachChannel];
        [live emitDiagnostic:@"failed" status:-10 endpoints:1 channel:live.selectedChannel source:live.sourceName];
        [live failOpen:-10];
    });
    self.rfcommTimer=timer;
    dispatch_resume(timer);
}
- (void)beginOpen {
    if (self.closed || self.openStarted) return;
    self.openStarted=YES;
    self.openRequestedAt=[NSDate date].timeIntervalSince1970;
    NSArray<NSDictionary<NSString *, NSNumber *> *> *endpoints=HMResolveEndpoints(self.device,self.uuids);
    [self emitDiagnostic:@"cached" status:0 endpoints:endpoints.count channel:0 source:@"cache"];
    if (endpoints.count == 1) {
        self.fromCache=YES;
        self.selectedChannel=(BluetoothRFCOMMChannelID)[endpoints[0][@"channel"] unsignedShortValue];
        [self openChannel:self.selectedChannel];
        return;
    }
    // Match the Windows path: a missing or non-unique cached result is refreshed
    // over the air before uniqueness is enforced.
    [self beginSdpFallback];
}
- (void)beginSdpFallback {
    if (self.closed || self.sdpPending || self.opened) return;
    self.fromCache=NO;
    self.sdpPending=YES;
    self.queryLifetime=self;
    [self armSdpTimer];
    [self emitDiagnostic:@"query" status:0 endpoints:0 channel:0 source:@"sdp"];
    IOReturn status=[self.device performSDPQuery:self];
    if (status != kIOReturnSuccess) {
        [self cancelSdpTimer];
        self.sdpPending=NO;
        // No callback is delivered after an immediate failure, so the explicit hold
        // can be released here.
        self.queryLifetime=nil;
        [self emitDiagnostic:@"failed" status:(int)status endpoints:0 channel:0 source:@"sdp"];
        [self failOpen:(int)status];
    }
}
- (void)openChannel:(BluetoothRFCOMMChannelID)channelID {
    if (self.closed || self.opened) return;
    self.selectedChannel=channelID;
    self.openPending=YES;
    [self armRfcommTimer];
    IOBluetoothRFCOMMChannel *opened=nil;
    IOReturn result=[self.device openRFCOMMChannelAsync:&opened withChannelID:channelID delegate:self];
    if (opened) {
        if (result != kIOReturnSuccess || self.closed) {
            [opened setDelegate:nil]; [opened closeChannel];
        } else self.channel=opened;
        // Balance the documented retained out value on every path, including
        // a defensive non-null out value accompanying an immediate failure.
        CFRelease((__bridge CFTypeRef)opened);
    }
    if (result != kIOReturnSuccess) {
        [self cancelRfcommTimer]; self.openPending=NO;
        int code=HMOpenStatus(result);
        [self emitDiagnostic:@"failed" status:code endpoints:1 channel:channelID source:self.sourceName];
        [self failOpen:code];
        return;
    }
    if (!self.closed && !opened) {
        [self cancelRfcommTimer]; self.openPending=NO;
        [self emitDiagnostic:@"failed" status:-1 endpoints:1 channel:channelID source:self.sourceName];
        [self failOpen:-1];
    }
}
- (void)failOpen:(int)status {
    if (self.closed) return;
    [self close:status];
}
- (void)close:(int)status {
    if (self.closed) return;
    self.closed=YES;
    [self cancelSdpTimer]; [self cancelRfcommTimer];
    self.sdpPending=NO; self.openPending=NO;
    [self detachChannel];
    if (!self.opened) [self.owner emit:2 operation:self.identifier channel:self.identifier status:status ?: -1 data:nil];
    if (self.writeOperation) [self.owner emit:5 operation:self.writeOperation channel:self.identifier status:status ?: -1 data:nil];
    self.writeOperation=0; self.writeData=nil; self.earlyData=nil; self.earlyBytes=0;
    [self.owner emit:4 operation:0 channel:self.identifier status:status data:nil];
    [self.owner.channels removeObjectForKey:@(self.identifier)];
}
- (void)sdpQueryComplete:(IOBluetoothDevice *)device status:(IOReturn)status {
    OnMain(^{
        // IOBluetooth does not document ownership of its asynchronous SDP target.
        // The queued block now owns us; release the explicit query hold even if canceled.
        [self cancelSdpTimer];
        BOOL pending=self.sdpPending;
        self.sdpPending=NO;
        self.queryLifetime=nil;
        if (self.closed || !pending) return;
        if (status != kIOReturnSuccess) {
            [self emitDiagnostic:@"failed" status:(int)status endpoints:0 channel:0 source:@"sdp"];
            [self failOpen:(int)status];
            return;
        }
        NSArray<NSDictionary<NSString *, NSNumber *> *> *endpoints=HMResolveEndpoints(self.device,self.uuids);
        [self emitDiagnostic:@"resolved" status:0 endpoints:endpoints.count channel:0 source:@"sdp"];
        if (endpoints.count != 1) {
            int code=endpoints.count == 0 ? -2 : -3;
            [self emitDiagnostic:@"failed" status:code endpoints:endpoints.count channel:0 source:@"sdp"];
            [self failOpen:code];
            return;
        }
        self.fromCache=NO;
        self.selectedChannel=(BluetoothRFCOMMChannelID)[endpoints[0][@"channel"] unsignedShortValue];
        [self openChannel:self.selectedChannel];
    });
}
- (void)rfcommChannelOpenComplete:(IOBluetoothRFCOMMChannel *)channel status:(IOReturn)status {
    OnMain(^{
        [self cancelRfcommTimer];
        self.openPending=NO;
        if (self.closed) { [channel setDelegate:nil]; [channel closeChannel]; return; }
        if (status != kIOReturnSuccess) {
            if (channel && channel != self.channel) { [channel setDelegate:nil]; [channel closeChannel]; }
            [self detachChannel];
            int code=HMOpenStatus(status);
            [self emitDiagnostic:@"failed" status:code endpoints:1 channel:self.selectedChannel source:self.sourceName];
            [self failOpen:code];
            return;
        }
        self.channel=channel; self.opened=YES;
        [self emitDiagnostic:@"opened" status:0 endpoints:1 channel:self.selectedChannel source:self.sourceName];
        [self.owner emit:2 operation:self.identifier channel:self.identifier status:0 data:nil];
        for (NSData *data in self.earlyData) [self.owner emit:3 operation:0 channel:self.identifier status:0 data:data];
        self.earlyData=nil; self.earlyBytes=0;
    });
}
- (void)rfcommChannelData:(IOBluetoothRFCOMMChannel *)channel data:(void *)dataPointer length:(size_t)dataLength {
    if (dataLength == 0) return;
    if (dataLength > 1024*1024) { OnMain(^{ [self close:-1]; }); return; }
    NSData *copy=[NSData dataWithBytes:dataPointer length:dataLength];
    OnMain(^{
        if (self.closed) return;
        if (self.opened) [self.owner emit:3 operation:0 channel:self.identifier status:0 data:copy];
        else {
            self.earlyBytes+=copy.length;
            if (self.earlyBytes > 1024*1024) { [self close:-1]; return; }
            if (!self.earlyData) self.earlyData=[NSMutableArray array];
            [self.earlyData addObject:copy];
        }
    });
}
- (void)rfcommChannelClosed:(IOBluetoothRFCOMMChannel *)channel { OnMain(^{ [self close:0]; }); }
- (void)writeNext {
    if (self.closed || !self.writeOperation) return;
    if (self.writeOffset == self.writeData.length) {
        int64_t operation=self.writeOperation;
        self.writeOperation=0; self.writeData=nil;
        [self.owner emit:5 operation:operation channel:self.identifier status:0 data:nil]; return;
    }
    NSUInteger mtu=[self.channel getMTU];
    if (mtu == 0) { [self close:-1]; return; }
    self.writeChunk=MIN(MIN(mtu,(NSUInteger)UINT16_MAX),self.writeData.length-self.writeOffset);
    // Keep the exact buffer and channel alive until the native invocation unwinds,
    // even if a future OS implementation pumps its run loop during the call.
    __attribute__((objc_precise_lifetime)) NSData *submitted=self.writeData;
    __attribute__((objc_precise_lifetime)) IOBluetoothRFCOMMChannel *sending=self.channel;
    IOReturn status=[sending writeAsync:(uint8_t *)submitted.bytes+self.writeOffset length:(UInt16)self.writeChunk refcon:(void *)(uintptr_t)self.writeOperation];
    if (status != kIOReturnSuccess) [self close:(int)status];
}
- (void)rfcommChannelWriteComplete:(IOBluetoothRFCOMMChannel *)channel refcon:(void *)refcon status:(IOReturn)status {
    int64_t operation=(int64_t)(uintptr_t)refcon;
    OnMain(^{
        if (self.closed || self.writeOperation != operation) return;
        if (status != kIOReturnSuccess) { [self close:(int)status]; return; }
        self.writeOffset+=self.writeChunk;
        OnMain(^{ if (!self.closed && self.writeOperation == operation) [self writeNext]; });
    });
}
@end

extern "C" {
int hm_bt_bridge_version() { return 2; }
void hm_bt_start(HMCallback callback, void *context) {
    OnMain(^{
        if (gAdapter && gAdapter.context != context) [gAdapter shutdown];
        if (gAdapter && gAdapter.context == context && gAdapter.active) return;
        HMBluetooth *adapter=[HMBluetooth new]; gAdapter=adapter;
        adapter.callback=callback; adapter.context=context;
        adapter.central=[[CBCentralManager alloc] initWithDelegate:adapter queue:dispatch_get_main_queue() options:@{CBCentralManagerOptionShowPowerAlertKey:@NO}];
        adapter.connections=[IOBluetoothDevice registerForConnectNotifications:adapter selector:@selector(connected:device:)];
        NSNotificationCenter *center=NSWorkspace.sharedWorkspace.notificationCenter;
        __weak HMBluetooth *weak=adapter;
        adapter.sleepObserver=[center addObserverForName:NSWorkspaceWillSleepNotification object:nil queue:NSOperationQueue.mainQueue usingBlock:^(NSNotification *note) {
            HMBluetooth *live=weak; if (!live.active) return;
            live.suspended=YES; [live updateScan]; [live emit:8 operation:0 channel:0 status:1 data:nil];
        }];
        adapter.wakeObserver=[center addObserverForName:NSWorkspaceDidWakeNotification object:nil queue:NSOperationQueue.mainQueue usingBlock:^(NSNotification *note) {
            HMBluetooth *live=weak; if (!live.active) return;
            live.suspended=NO; [live emit:8 operation:0 channel:0 status:0 data:nil]; [live statusChanged];
            [live observeDevices]; [live emit:6 operation:0 channel:0 status:0 data:nil];
        }];
        [adapter statusChanged]; [adapter observeDevices];
    });
}
void hm_bt_shutdown(void *context) {
    dispatch_block_t action=^{ if (gAdapter.context == context) { [gAdapter shutdown]; gAdapter=nil; } };
    if (NSThread.isMainThread) action(); else dispatch_sync(dispatch_get_main_queue(),action);
}
void hm_bt_enumerate(int64_t operation) {
    OnMain(^{
        HMBluetooth *adapter=gAdapter; int error=[adapter accessError];
        if (error) { [adapter emit:1 operation:operation channel:0 status:error data:nil]; return; }
        NSMutableArray *result=[NSMutableArray array];
        for (IOBluetoothDevice *device in [IOBluetoothDevice pairedDevices]) {
            if (result.count >= 4096) { [adapter emit:1 operation:operation channel:0 status:-1 data:nil]; return; }
            uint64_t address=DeviceAddress(device); if (!address || !device.isPaired) continue;
            [result addObject:@{@"address":@(address),@"name":device.name ?: @"蓝牙设备",@"connected":@(device.isConnected),
                @"remembered":@YES,@"authenticated":@YES,@"classOfDevice":@(device.classOfDevice)}];
        }
        [adapter observeDevices]; [adapter emit:1 operation:operation channel:0 status:0 data:JSON(result)];
    });
}
void hm_bt_open(int64_t operation, uint64_t address, const char *serviceIds) {
    NSString *input=serviceIds ? [NSString stringWithUTF8String:serviceIds] : @"[]";
    OnMain(^{
        HMBluetooth *adapter=gAdapter; int error=[adapter accessError];
        if (error) { [adapter emit:2 operation:operation channel:operation status:error data:nil]; return; }
        NSArray *strings=[NSJSONSerialization JSONObjectWithData:[input dataUsingEncoding:NSUTF8StringEncoding] options:0 error:nil];
        if (![strings isKindOfClass:NSArray.class] || strings.count == 0 || strings.count > 64) { [adapter emit:2 operation:operation channel:operation status:-1 data:nil]; return; }
        NSMutableArray *uuids=[NSMutableArray array];
        for (NSString *value in strings) {
            if (![value isKindOfClass:NSString.class]) continue;
            NSUUID *uuid=[[NSUUID alloc] initWithUUIDString:value]; if (!uuid) continue;
            uuid_t bytes; [uuid getUUIDBytes:bytes];
            IOBluetoothSDPUUID *bluetooth=[IOBluetoothSDPUUID uuidWithBytes:bytes length:16]; if (bluetooth) [uuids addObject:bluetooth];
        }
        IOBluetoothDevice *device=[IOBluetoothDevice deviceWithAddressString:AddressString(address)];
        if (!device.isPaired || !device.isConnected || uuids.count != strings.count) { [adapter emit:2 operation:operation channel:operation status:-1 data:nil]; return; }
        HMChannel *channel=[HMChannel new]; channel.owner=adapter; channel.identifier=operation; channel.device=device; channel.uuids=uuids;
        adapter.channels[@(operation)]=channel;
        // beginOpen resolves the system's cached SDP records first and only performs
        // an unfiltered query when the cache is missing or non-unique.
        [channel beginOpen];
    });
}
void hm_bt_write(int64_t channel, int64_t operation, const void *data, size_t length) {
    NSData *copy=length <= 1024*1024 ? [NSData dataWithBytes:data length:length] : nil;
    OnMain(^{
        HMChannel *live=gAdapter.channels[@(channel)];
        if (!live || live.closed || !live.opened || live.writeOperation || !copy) { [gAdapter emit:5 operation:operation channel:channel status:-1 data:nil]; return; }
        live.writeOperation=operation; live.writeData=copy; live.writeOffset=0; [live writeNext];
    });
}
void hm_bt_close(int64_t channel) { OnMain(^{ [gAdapter.channels[@(channel)] close:0]; }); }
void hm_bt_cancel(int64_t operation) {
    OnMain(^{
        HMChannel *opening=gAdapter.channels[@(operation)];
        if (opening && !opening.opened) { [opening close:-9]; return; }
        for (HMChannel *channel in gAdapter.channels.allValues) if (channel.writeOperation == operation) { [channel close:-9]; return; }
    });
}
void hm_bt_scan(int wanted, int64_t requestGeneration) {
    OnMain(^{
        if (requestGeneration < gAdapter.scanRequestGeneration) return;
        if (gAdapter.scanRequestGeneration != requestGeneration) [gAdapter retireScan];
        gAdapter.scanRequestGeneration=requestGeneration;
        gAdapter.scanWanted=wanted != 0; [gAdapter updateScan];
    });
}
void hm_bt_run_loop() { @autoreleasepool {
    CFRunLoopSourceContext context={0};
    CFRunLoopSourceRef keepAlive=CFRunLoopSourceCreate(kCFAllocatorDefault,0,&context);
    CFRunLoopAddSource(CFRunLoopGetMain(),keepAlive,kCFRunLoopDefaultMode);
    CFRunLoopRun();
    CFRunLoopRemoveSource(CFRunLoopGetMain(),keepAlive,kCFRunLoopDefaultMode); CFRelease(keepAlive);
} }
void hm_bt_stop_loop() { OnMain(^{ CFRunLoopStop(CFRunLoopGetMain()); }); }
}
