#ifndef HEYMELODY_FILAMENT_NATIVE_H
#define HEYMELODY_FILAMENT_NATIVE_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(HM_FILAMENT_BUILD)
#    define HM_API __declspec(dllexport)
#  else
#    define HM_API __declspec(dllimport)
#  endif
#  define HM_CALL __cdecl
#else
#  define HM_API __attribute__((visibility("default")))
#  define HM_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define HM_FILAMENT_ABI_VERSION 2u
typedef struct hm_renderer hm_renderer;

/* Zero is success, negative values are errors. Frame-copy additionally returns 1. */
enum hm_result {
    HM_OK = 0, HM_INVALID_ARGUMENT = -1, HM_NATIVE_ERROR = -2,
    HM_BUFFER_TOO_SMALL = -3, HM_ABI_MISMATCH = -4, HM_MODEL_REJECTED = -5
};
enum hm_state { HM_EMPTY = 0, HM_LOADING = 1, HM_READY = 2, HM_FAILED = 3, HM_STOPPING = 4 };
enum hm_backend { HM_BACKEND_OPENGL = 1, HM_BACKEND_METAL = 2, HM_BACKEND_VULKAN = 3 };
enum hm_log_level { HM_LOG_DEBUG = 0, HM_LOG_INFORMATION = 1, HM_LOG_WARNING = 2, HM_LOG_ERROR = 3, HM_LOG_CRITICAL = 4 };
/* UTF8 bytes are valid only during this synchronous callback, which can run on any native thread.
 * The consumer must copy them before returning, must not throw, and must remain alive for the DLL lifetime. */
typedef void (HM_CALL *hm_log_callback)(uint32_t level, const char* message, uint32_t length, void* user);

typedef struct hm_create_options {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t requested_backend; /* Explicit hm_backend; zero and unavailable backends are rejected. */
} hm_create_options;

/* Dimensions are physical pixels. Angles are radians; zoom=1 fits the complete asset. */
typedef struct hm_configuration {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t msaa_samples; /* 1 or 4 */
    float yaw_radians;
    float pitch_radians;
    float zoom;
    float vertical_fov_degrees;
    float clear_red; /* display-sRGB background components in [0,1], composited after tone mapping */
    float clear_green;
    float clear_blue;
    float exposure; /* multiplicative exposure, 1 is the neutral default */
    float light_intensity;
    float fit_margin;
    float reserved[2];
} hm_configuration;

typedef struct hm_frame_info {
    uint32_t struct_size;
    uint32_t width;
    uint32_t height;
    uint32_t stride_bytes;
    uint64_t frame_id;
    uint64_t generation;
    double render_milliseconds;
    double readback_milliseconds;
} hm_frame_info;

typedef struct hm_diagnostics {
    uint32_t struct_size;
    uint32_t state;
    uint32_t backend;
    uint32_t msaa_samples;
    uint32_t width;
    uint32_t height;
    uint32_t source_meshes;
    uint32_t source_nodes;
    uint32_t source_primitives;
    uint32_t source_materials;
    uint32_t patched_materials;
    uint32_t requested_backend;
    uint64_t source_triangles;
    uint64_t frame_id;
    uint64_t generation;
    float bounds_min[3];
    float bounds_max[3];
} hm_diagnostics;

HM_API uint32_t HM_CALL hm_get_abi_version(void);
/* Additive ABI 2 export. Null restores Filament's default stream consumers. No renderer configuration changes. */
HM_API void HM_CALL hm_set_log_callback(hm_log_callback callback, void* user);
HM_API int32_t HM_CALL hm_create(const hm_create_options* options, hm_renderer** renderer);
/* Copies the GLB before returning. A nonempty expected_sha256 must match its real SHA256. */
HM_API int32_t HM_CALL hm_load_model(hm_renderer* renderer, const void* glb,
        uint64_t length, const char* expected_sha256);
/* Optional source KTX1 lighting; null/zero clears the environment. Copies before returning. */
HM_API int32_t HM_CALL hm_set_environment(hm_renderer* renderer, const void* ibl,
        uint64_t ibl_length, const void* skybox, uint64_t skybox_length);
HM_API int32_t HM_CALL hm_configure(hm_renderer* renderer, const hm_configuration* configuration);
/* Coalesced asynchronous render request; no frame callback crosses the ABI. */
HM_API int32_t HM_CALL hm_render(hm_renderer* renderer);
/* 1=copied, 0=no newer completed frame. Output is top-left RGBA8, opaque, sRGB. */
HM_API int32_t HM_CALL hm_try_copy_latest_frame(hm_renderer* renderer, uint64_t after_frame_id,
        void* destination, uint64_t capacity, hm_frame_info* frame);
HM_API int32_t HM_CALL hm_get_diagnostics(hm_renderer* renderer, hm_diagnostics* diagnostics);
/* UTF8, NUL-terminated when capacity>0. Returns required bytes including NUL. Null renderer gets the calling thread's create error. */
HM_API uint32_t HM_CALL hm_get_last_error(hm_renderer* renderer, char* destination, uint32_t capacity);
HM_API void HM_CALL hm_destroy(hm_renderer* renderer);

#ifdef __cplusplus
}
#endif
#endif
