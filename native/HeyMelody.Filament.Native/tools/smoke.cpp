#include "hm_filament.h"
#include <atomic>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
std::atomic<uint32_t> log_messages{0};
void HM_CALL log_message(uint32_t level, const char* message, uint32_t length, void*) {
    if (level == HM_LOG_DEBUG && message && length) log_messages.fetch_add(1);
}
std::string error(hm_renderer* renderer) {
    std::vector<char> bytes(hm_get_last_error(renderer,nullptr,0));
    hm_get_last_error(renderer,bytes.data(),static_cast<uint32_t>(bytes.size()));
    return bytes.data();
}
void check(int32_t result,hm_renderer* renderer,const char* operation) {
    if(result<0)throw std::runtime_error(std::string(operation)+": "+error(renderer));
}
void require_sha256_rejection(hm_renderer* renderer,const std::vector<uint8_t>& bytes,
        const char* expected,const char* operation) {
    const auto result=hm_load_model(renderer,bytes.data(),bytes.size(),expected);
    const auto message=error(renderer);
    if(result!=HM_NATIVE_ERROR ||
            message.find("GLB SHA256 does not match the verified input")==std::string::npos)
        throw std::runtime_error(std::string(operation)+": native SHA256 rejection was not observed");
    std::cerr<<operation<<": result="<<result<<"; error="<<message<<'\n';
}
hm_frame_info await_frame(hm_renderer* renderer,uint64_t after,std::vector<uint8_t>& pixels) {
    const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(120);
    while(std::chrono::steady_clock::now()<deadline) {
        hm_frame_info info{};info.struct_size=sizeof(info);
        const auto result=hm_try_copy_latest_frame(renderer,after,pixels.data(),pixels.size(),&info);
        check(result,renderer,"copy frame");
        if(result==1)return info;
        hm_diagnostics diagnostics{};diagnostics.struct_size=sizeof(diagnostics);
        check(hm_get_diagnostics(renderer,&diagnostics),renderer,"diagnostics");
        if(diagnostics.state==HM_FAILED)throw std::runtime_error(error(renderer));
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    throw std::runtime_error("The single requested frame did not complete");
}
}

int main(int argc,char** argv) {
    if(hm_get_abi_version()!=HM_FILAMENT_ABI_VERSION)return 1;
    hm_set_log_callback(log_message, nullptr);
    if (log_messages.load() != 1) return 1;
    hm_set_log_callback(nullptr, nullptr);
    if(argc<2) {
        hm_create_options invalid{sizeof(invalid),16,16,0};
        hm_renderer* raw = nullptr;
        if (sizeof(hm_create_options) != 16 || sizeof(hm_configuration) != 64 ||
            sizeof(hm_frame_info) != 48 || sizeof(hm_diagnostics) != 96 ||
            hm_create(&invalid,&raw) != HM_INVALID_ARGUMENT || raw) return 1;
        invalid.requested_backend = 99;
        if (hm_create(&invalid,&raw) != HM_INVALID_ARGUMENT || raw) return 1;
        std::cout<<"Filament C ABI 2 layouts and explicit backend validation verified\n";
        return 0;
    }
    try {
        std::ifstream input(argv[1],std::ios::binary);
        if(!input)throw std::runtime_error("Cannot open input GLB");
        std::vector<uint8_t> glb((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>());
        const uint32_t backend = argc > 4 && std::string(argv[4]) == "opengl" ? HM_BACKEND_OPENGL :
#if defined(__APPLE__)
            HM_BACKEND_METAL;
#else
            HM_BACKEND_VULKAN;
#endif
        hm_create_options options{sizeof(options),640,580,backend};hm_renderer* raw=nullptr;
        check(hm_create(&options,&raw),nullptr,"create");
        std::cerr<<"create completed\n";
        std::unique_ptr<hm_renderer,decltype(&hm_destroy)> renderer(raw,&hm_destroy);
        hm_configuration configuration{};configuration.struct_size=sizeof(configuration);
        configuration.width=640;configuration.height=580;configuration.msaa_samples=4;
        configuration.zoom=1;configuration.vertical_fov_degrees=35;configuration.fit_margin=1.08f;
        configuration.clear_red=0.1f;configuration.clear_green=0.12f;configuration.clear_blue=0.15f;
        configuration.exposure=1;configuration.light_intensity=100000;
        check(hm_configure(raw,&configuration),raw,"configure");
        if(argc>2 && *argv[2]) {
            std::string forged=argv[2];
            forged[0]=forged[0]=='0'?'1':'0';
            require_sha256_rejection(raw,glb,forged.c_str(),"forged expected digest");
            {
                auto tampered=glb;
                if(tampered.empty())throw std::runtime_error("Input GLB is empty");
                tampered.back()^=1;
                require_sha256_rejection(raw,tampered,argv[2],"tampered model bytes");
            }
            std::cerr<<"native SHA256 rejected forged expected and tampered model bytes\n";
        }
        check(hm_load_model(raw,glb.data(),glb.size(),argc>2?argv[2]:nullptr),raw,"load");
        std::cerr<<"load completed\n";
        check(hm_render(raw),raw,"render");
        std::vector<uint8_t> pixels(640*580*4);
        auto info=await_frame(raw,0,pixels);
        std::cerr<<"first frame completed; corner="<<unsigned(pixels[0])<<','<<unsigned(pixels[1])<<','<<unsigned(pixels[2])<<','<<unsigned(pixels[3])<<'\n';
        uint64_t opaque=0,changed=0;
        for(size_t i=0;i<pixels.size();i+=4) {
            opaque+=pixels[i+3]==255;
            changed+=pixels[i]!=pixels[0] || pixels[i+1]!=pixels[1] || pixels[i+2]!=pixels[2];
        }
        if(opaque!=640*580 || changed==0)throw std::runtime_error("Frame is empty or has nonopaque pixels");
        if(argc>3) {
            std::ofstream image(argv[3],std::ios::binary);
            image<<"P6\n640 580\n255\n";
            for(size_t i=0;i<pixels.size();i+=4)image.write(reinterpret_cast<const char*>(pixels.data()+i),3);
        }
        hm_diagnostics diagnostics{};diagnostics.struct_size=sizeof(diagnostics);
        check(hm_get_diagnostics(raw,&diagnostics),raw,"diagnostics");
        configuration.width=320;configuration.height=240;configuration.yaw_radians=0.6f;
        check(hm_configure(raw,&configuration),raw,"resize");
        check(hm_render(raw),raw,"render resized");
        pixels.resize(320*240*4);
        const auto resized=await_frame(raw,info.frame_id,pixels);
        if(resized.width!=320 || resized.height!=240 || resized.generation<=info.generation)
            throw std::runtime_error("Resize returned a stale frame");
        std::cout<<"{\"abiVersion\":2,\"backend\":"<<diagnostics.backend
            <<",\"requestedBackend\":"<<diagnostics.requested_backend
            <<",\"meshes\":"<<diagnostics.source_meshes<<",\"nodes\":"<<diagnostics.source_nodes
            <<",\"primitives\":"<<diagnostics.source_primitives<<",\"triangles\":"<<diagnostics.source_triangles
            <<",\"materials\":"<<diagnostics.source_materials<<",\"patchedMaterials\":"<<diagnostics.patched_materials
            <<",\"frameId\":"<<info.frame_id<<",\"resizedFrameId\":"<<resized.frame_id
            <<",\"opaquePixels\":"<<opaque<<",\"modelPixels\":"<<changed
            <<",\"renderMs\":"<<info.render_milliseconds<<",\"readbackMs\":"<<info.readback_milliseconds<<"}\n";
        return 0;
    }catch(const std::exception& ex) { std::cerr<<ex.what()<<'\n';return 2; }
}
