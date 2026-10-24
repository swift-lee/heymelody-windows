#include "hm_filament.h"
#include <cstdint>
#include <iostream>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace {
std::mutex log_mutex;
void HM_CALL log_native(uint32_t level, const char* message, uint32_t length, void*) {
    std::lock_guard lock(log_mutex);
    auto& stream = level >= HM_LOG_WARNING ? std::cerr : std::cout;
    stream << "Filament[" << level << "] ";
    stream.write(message, length);
    if (!length || message[length - 1] != '\n') stream << '\n';
    stream.flush();
}
}

int main(int argc, char** argv) {
    if (argc != 3 || std::string(argv[1]) != "--backend") {
        std::cerr << "usage: hm_filament_probe --backend vulkan|opengl|metal\n";
        return 2;
    }
    const std::string name = argv[2];
    const uint32_t requested = name == "vulkan" ? HM_BACKEND_VULKAN :
        name == "opengl" ? HM_BACKEND_OPENGL : name == "metal" ? HM_BACKEND_METAL : 0;
    if (!requested) { std::cerr << "Invalid backend\n"; return 2; }
    if (hm_get_abi_version() != HM_FILAMENT_ABI_VERSION) {
        std::cerr << "Filament native ABI mismatch\n";
        return 3;
    }
    hm_set_log_callback(log_native, nullptr);
    hm_create_options options{sizeof(options), 16, 16, requested};
    hm_renderer* raw = nullptr;
    const int32_t result = hm_create(&options, &raw);
    std::unique_ptr<hm_renderer, decltype(&hm_destroy)> renderer(raw, &hm_destroy);
    if (result != HM_OK || !raw) {
        const uint32_t required = hm_get_last_error(nullptr, nullptr, 0);
        std::vector<char> message(required ? required : 1);
        hm_get_last_error(nullptr, message.data(), static_cast<uint32_t>(message.size()));
        std::cerr << "Filament " << name << " initialization failed: " << message.data() << '\n';
        return 4;
    }
    hm_diagnostics diagnostics{};
    diagnostics.struct_size = sizeof(diagnostics);
    if (hm_get_diagnostics(raw, &diagnostics) != HM_OK || diagnostics.backend != requested ||
        diagnostics.requested_backend != requested) {
        std::cerr << "Requested and actual backend differ\n";
        return 5;
    }
    renderer.reset();
    std::cout << "Filament ABI 2 backend " << name << " created and destroyed\n";
    return 0;
}
