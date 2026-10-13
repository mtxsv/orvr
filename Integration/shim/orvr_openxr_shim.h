// orvr_openxr_shim.h — C ABI consumed by Orts.VR (VROpenXRHost) via P/Invoke.
//
// Handle convention: every orvr_* function takes/returns OpenXR handles as
// uint64_t (XrInstance/XrSession/XrSpace are 64-bit). The shim owns all
// OpenXR objects; C# only stores opaque integers.
//
// Return convention: 0 == XR_SUCCESS, anything else is the raw XrResult.
// All functions are thread-safe at the granularity of one session (the
// engine calls from the render thread only). No function throws across the ABI.

#pragma once

#include <stdint.h>

#ifdef _WIN32
#define ORVR_API __declspec(dllexport)
#else
#define ORVR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Mirrors VROpenXRHost.NativeMethods.LocatedPoses (sequential layout).
typedef struct OrvrLocatedPoses {
    int64_t predictedDisplayTime;
    float headX, headY, headZ;
    float headQX, headQY, headQZ, headQW;
    int32_t headActive;
    float leftX, leftY, leftZ;
    float leftQX, leftQY, leftQZ, leftQW;
    int32_t leftActive;
    float rightX, rightY, rightZ;
    float rightQX, rightQY, rightQZ, rightQW;
    int32_t rightActive;
} OrvrLocatedPoses;

// Mirrors VROpenXRHost.NativeMethods.HandActionState.
typedef struct OrvrHandActionState {
    int32_t active;
    float aimX, aimY, aimZ;
    float aimQX, aimQY, aimQZ, aimQW;
    float trigger;
    int32_t triggerPressed;
    int32_t triggerReleased;
    int32_t squeezePressed;
    float stickX, stickY;
    int32_t buttons;
} OrvrHandActionState;

typedef struct OrvrBothHands {
    OrvrHandActionState left;
    OrvrHandActionState right;
} OrvrBothHands;

ORVR_API int32_t orvr_xr_init_d3d11(const char* appName, void* d3d11Device,
    uint64_t* instance, uint64_t* systemId, char** runtimeNameUtf8,
    int32_t* recommendedWidth, int32_t* recommendedHeight);
ORVR_API int32_t orvr_xr_create_session_d3d11(uint64_t instance, uint64_t systemId,
    void* d3d11Device, uint64_t* session);
ORVR_API int32_t orvr_xr_create_spaces(uint64_t session,
    uint64_t* headSpace, uint64_t* leftSpace, uint64_t* rightSpace);
ORVR_API int32_t orvr_xr_begin(uint64_t session);
ORVR_API int32_t orvr_xr_poll(uint64_t session, int32_t* state, int32_t* focused, int32_t* lossPending);
ORVR_API int32_t orvr_xr_wait_locate(uint64_t session, uint64_t headSpace,
    uint64_t leftSpace, uint64_t rightSpace, OrvrLocatedPoses* poses);
ORVR_API int32_t orvr_xr_actions(uint64_t session, OrvrBothHands* both);
ORVR_API void orvr_xr_recenter(uint64_t session);
ORVR_API int32_t orvr_xr_submit_shared(uint64_t session, void* sharedLeft,
    void* sharedRight, int32_t width, int32_t height);
ORVR_API int32_t orvr_xr_get_fov(uint64_t session, int32_t eye,
    float* fovLeft, float* fovRight, float* fovUp, float* fovDown);
ORVR_API void orvr_xr_haptic(uint64_t session, int32_t hand, float amplitude, int32_t durationMs);
ORVR_API int32_t orvr_xr_end(uint64_t session);
ORVR_API void orvr_xr_destroy(uint64_t handle);
ORVR_API void orvr_xr_free_string(char* s);

#ifdef __cplusplus
}
#endif
