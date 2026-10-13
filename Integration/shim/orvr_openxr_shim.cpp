// orvr_openxr_shim.cpp — minimal real OpenXR client for the ORVR C# host.
//
// Build requirements (see CMakeLists.txt):
//   - OpenXR SDK 1.0+ (headers + openxr_loader.lib), Windows x64, MSVC 2022+.
//   - Links d3d11.lib only; the ID3D11Device comes from the engine (MonoGame).
//
// Design notes:
//   - One global registry maps opaque uint64 handles to contexts; the render
//     thread is the only caller, guarded by a mutex anyway.
//   - Swapchains are created lazily on first submit using the runtime's
//     recommended size (or the eye-target size, whichever is smaller) and a
//     runtime-supported color format. Eye textures arrive as DXGI shared
//     handles (the engine creates its targets with shared:true) and are
//     GPU-copied (CopySubresourceRegion) into the acquired swapchain images:
//     no CPU readback anywhere.
//   - Controllers use the standard action system with the simple-controller
//     profile (universal fallback) plus the Oculus Touch profile; runtimes
//     ignore unsupported profiles. Grip/trigger/thumbstick/buttons map to
//     the OrvrBothHands struct the C# side consumes.

#define XR_USE_PLATFORM_WIN32 1
#define XR_USE_GRAPHICS_API_D3D11 1
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <d3d11.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "orvr_openxr_shim.h"

using Microsoft::WRL::ComPtr;

namespace {

std::mutex g_mutex;

struct SwapchainSet {
    XrSwapchain handle = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageD3D11KHR> images;
    int32_t width = 0;
    int32_t height = 0;
};

struct SessionCtx {
    XrInstance instance = XR_NULL_HANDLE;
    XrSystemId systemId = XR_NULL_SYSTEM_ID;
    XrSession session = XR_NULL_HANDLE;
    XrSpace headSpace = XR_NULL_HANDLE;   // LOCAL (possibly recentered)
    XrSpace handSpace[2] = { XR_NULL_HANDLE, XR_NULL_HANDLE }; // action spaces
    XrActionSet actionSet = XR_NULL_HANDLE;
    XrAction actAimPose[2] = { XR_NULL_HANDLE, XR_NULL_HANDLE };
    XrAction actTrigger = XR_NULL_HANDLE;
    XrAction actSqueeze = XR_NULL_HANDLE;
    XrAction actStick = XR_NULL_HANDLE;
    XrAction actButtonA = XR_NULL_HANDLE;
    XrAction actButtonB = XR_NULL_HANDLE;
    XrAction actHaptic[2] = { XR_NULL_HANDLE, XR_NULL_HANDLE };
    XrPath handPath[2] = { XR_NULL_PATH, XR_NULL_PATH };
    ComPtr<ID3D11Device> device;
    int64_t frameDisplayTime = 0;
    bool frameBegun = false;
    bool sessionRunning = false;
    bool focused = false;
    bool lossPending = false;
    XrPosef eyePose[2] = {};
    XrFovf eyeFov[2] = {};
    bool haveViews = false;
    XrPosef handPose[2] = {};   // last located aim poses (head-space relative)
    bool handPoseValid[2] = {};
    SwapchainSet swap[2];
    int64_t recommendedW = 0, recommendedH = 0;
    XrSessionState state = XR_SESSION_STATE_UNKNOWN;
};

std::map<uint64_t, SessionCtx*> g_sessions; // keyed by XrSession as integer
std::map<uint64_t, SessionCtx*> g_instances; // keyed by XrInstance as integer
std::map<uint64_t, SessionCtx*> g_spaces; // keyed by XrSpace as integer (head + hands)

SessionCtx* FindSession(uint64_t session) {
    auto it = g_sessions.find(session);
    return it == g_sessions.end() ? nullptr : it->second;
}

const char* RuntimeNameOf(XrInstance instance, std::string& out) {
    XrInstanceProperties props{ XR_TYPE_INSTANCE_PROPERTIES };
    if (xrGetInstanceProperties(instance, &props) == XR_SUCCESS) {
        out = props.runtimeName;
        out += " ";
        out += std::to_string(XR_VERSION_MAJOR(props.runtimeVersion));
        out += ".";
        out += std::to_string(XR_VERSION_MINOR(props.runtimeVersion));
        return out.c_str();
    }
    out = "unknown";
    return out.c_str();
}

XrBool32 SupportsExtension(const char* name) {
    uint32_t count = 0;
    if (xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr) != XR_SUCCESS)
        return XR_FALSE;
    std::vector<XrExtensionProperties> exts(count, { XR_TYPE_EXTENSION_PROPERTIES });
    if (xrEnumerateInstanceExtensionProperties(nullptr, count, &count, exts.data()) != XR_SUCCESS)
        return XR_FALSE;
    for (auto& e : exts)
        if (strcmp(e.extensionName, name) == 0)
            return XR_TRUE;
    return XR_FALSE;
}

} // namespace

extern "C" {

ORVR_API int32_t orvr_xr_init_d3d11(const char* appName, void* d3d11Device,
    uint64_t* instance, uint64_t* systemId, char** runtimeNameUtf8,
    int32_t* recommendedWidth, int32_t* recommendedHeight) {
    if (!appName || !d3d11Device || !instance || !systemId)
        return XR_ERROR_VALIDATION_FAILURE;
    if (!SupportsExtension(XR_KHR_D3D11_ENABLE_EXTENSION_NAME))
        return XR_ERROR_EXTENSION_NOT_PRESENT;

    const char* extensions[] = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME };
    XrInstanceCreateInfo ici{ XR_TYPE_INSTANCE_CREATE_INFO };
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = extensions;
    strncpy(ici.applicationInfo.applicationName, appName, XR_MAX_APPLICATION_NAME_SIZE - 1);
    ici.applicationInfo.applicationVersion = 1;
    strncpy(ici.applicationInfo.engineName, "OpenRails-ORVR", XR_MAX_ENGINE_NAME_SIZE - 1);
    ici.applicationInfo.engineVersion = 1;
    ici.applicationInfo.apiVersion = XR_CURRENT_API_VERSION;

    XrInstance inst = XR_NULL_HANDLE;
    XrResult res = xrCreateInstance(&ici, &inst);
    if (res != XR_SUCCESS)
        return (int32_t)res;

    XrSystemGetInfo sgi{ XR_TYPE_SYSTEM_GET_INFO };
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId sys = XR_NULL_SYSTEM_ID;
    res = xrGetSystem(inst, &sgi, &sys);
    if (res != XR_SUCCESS) {
        xrDestroyInstance(inst);
        return (int32_t)res;
    }

    // Recommended eye size for the stereo view configuration.
    uint32_t viewCount = 0;
    int64_t recW = 0, recH = 0;
    if (xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
            0, &viewCount, nullptr) == XR_SUCCESS && viewCount >= 2) {
        std::vector<XrViewConfigurationView> views(viewCount, { XR_TYPE_VIEW_CONFIGURATION_VIEW });
        if (xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,
                viewCount, &viewCount, views.data()) == XR_SUCCESS) {
            recW = views[0].recommendedImageRectWidth;
            recH = views[0].recommendedImageRectHeight;
        }
    }

    std::string name;
    RuntimeNameOf(inst, name);
    char* copy = (char*)malloc(name.size() + 1);
    if (copy)
        memcpy(copy, name.c_str(), name.size() + 1);

    *instance = (uint64_t)inst;
    *systemId = (uint64_t)sys;
    if (runtimeNameUtf8)
        *runtimeNameUtf8 = copy;
    else
        free(copy);
    if (recommendedWidth)
        *recommendedWidth = (int32_t)recW;
    if (recommendedHeight)
        *recommendedHeight = (int32_t)recH;
    return (int32_t)XR_SUCCESS;
}

ORVR_API int32_t orvr_xr_create_session_d3d11(uint64_t instance, uint64_t systemId,
    void* d3d11Device, uint64_t* session) {
    if (!instance || !systemId || !d3d11Device || !session)
        return XR_ERROR_VALIDATION_FAILURE;
    XrInstance inst = (XrInstance)instance;

    // Verify the engine device meets runtime requirements (informational).
    PFN_xrGetD3D11GraphicsRequirementsKHR pfnReq = nullptr;
    if (xrGetInstanceProcAddr(inst, "xrGetD3D11GraphicsRequirementsKHR",
            (PFN_xrVoidFunction*)&pfnReq) == XR_SUCCESS && pfnReq) {
        XrGraphicsRequirementsD3D11KHR req{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11 };
        if (pfnReq(inst, (XrSystemId)systemId, &req) == XR_SUCCESS) {
            ID3D11Device* dev = (ID3D11Device*)d3d11Device;
            LUID devLuid = dev->GetAdapterLuid();
            if (memcmp(&devLuid, &req.adapterLuid, sizeof(LUID)) != 0) {
                // Different GPU than the runtime prefers. Proceed anyway: the
                // runtime copies through the shared handle on our device.
                OutputDebugStringA("orvr: engine D3D11 adapter differs from XR runtime adapter; continuing.\n");
            }
        }
    }

    XrGraphicsBindingD3D11KHR binding{ XR_TYPE_GRAPHICS_BINDING_D3D11 };
    binding.device = (ID3D11Device*)d3d11Device;

    XrSessionCreateInfo sci{ XR_TYPE_SESSION_CREATE_INFO };
    sci.next = &binding;
    sci.systemId = (XrSystemId)systemId;

    XrSession sess = XR_NULL_HANDLE;
    XrResult res = xrCreateSession(inst, &sci, &sess);
    if (res != XR_SUCCESS)
        return (int32_t)res;

    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = new SessionCtx();
    ctx->instance = inst;
    ctx->systemId = (XrSystemId)systemId;
    ctx->session = sess;
    ctx->device = (ID3D11Device*)d3d11Device;
    g_sessions[(uint64_t)sess] = ctx;
    g_instances[(uint64_t)inst] = ctx;
    *session = (uint64_t)sess;
    return (int32_t)XR_SUCCESS;
}

static XrResult CreateActions(SessionCtx* ctx) {
    XrResult res;
    res = xrStringToPath(ctx->instance, "/user/hand/left", &ctx->handPath[0]);
    if (res != XR_SUCCESS)
        return res;
    res = xrStringToPath(ctx->instance, "/user/hand/right", &ctx->handPath[1]);
    if (res != XR_SUCCESS)
        return res;

    XrActionSetCreateInfo asci{ XR_TYPE_ACTION_SET_CREATE_INFO };
    strcpy(asci.actionSetName, "orvr_hands");
    strcpy(asci.localizedActionSetName, "ORVR Hands");
    res = xrCreateActionSet(ctx->instance, &asci, &ctx->actionSet);
    if (res != XR_SUCCESS)
        return res;

    auto mkPose = [&](const char* name, const char* loc, XrAction* out) {
        XrActionCreateInfo aci{ XR_TYPE_ACTION_CREATE_INFO };
        aci.actionType = XR_ACTION_TYPE_POSE_INPUT;
        strcpy(aci.actionName, name);
        strcpy(aci.localizedActionName, loc);
        aci.countSubactionPaths = 2;
        aci.subactionPaths = ctx->handPath;
        return xrCreateAction(ctx->actionSet, &aci, out);
    };
    auto mkBool = [&](const char* name, const char* loc, XrAction* out) {
        XrActionCreateInfo aci{ XR_TYPE_ACTION_CREATE_INFO };
        aci.actionType = XR_ACTION_TYPE_BOOLEAN_INPUT;
        strcpy(aci.actionName, name);
        strcpy(aci.localizedActionName, loc);
        aci.countSubactionPaths = 2;
        aci.subactionPaths = ctx->handPath;
        return xrCreateAction(ctx->actionSet, &aci, out);
    };
    auto mkFloat = [&](const char* name, const char* loc, XrAction* out) {
        XrActionCreateInfo aci{ XR_TYPE_ACTION_CREATE_INFO };
        aci.actionType = XR_ACTION_TYPE_FLOAT_INPUT;
        strcpy(aci.actionName, name);
        strcpy(aci.localizedActionName, loc);
        aci.countSubactionPaths = 2;
        aci.subactionPaths = ctx->handPath;
        return xrCreateAction(ctx->actionSet, &aci, out);
    };
    auto mkVec2 = [&](const char* name, const char* loc, XrAction* out) {
        XrActionCreateInfo aci{ XR_TYPE_ACTION_CREATE_INFO };
        aci.actionType = XR_ACTION_TYPE_VECTOR2F_INPUT;
        strcpy(aci.actionName, name);
        strcpy(aci.localizedActionName, loc);
        aci.countSubactionPaths = 2;
        aci.subactionPaths = ctx->handPath;
        return xrCreateAction(ctx->actionSet, &aci, out);
    };
    auto mkHaptic = [&](const char* name, const char* loc, XrAction* out) {
        XrActionCreateInfo aci{ XR_TYPE_ACTION_CREATE_INFO };
        aci.actionType = XR_ACTION_TYPE_VIBRATION_OUTPUT;
        strcpy(aci.actionName, name);
        strcpy(aci.localizedActionName, loc);
        aci.countSubactionPaths = 2;
        aci.subactionPaths = ctx->handPath;
        return xrCreateAction(ctx->actionSet, &aci, out);
    };

    if ((res = mkPose("aim", "Aim Pose", &ctx->actAimPose[0])) != XR_SUCCESS)
        return res;
    ctx->actAimPose[1] = ctx->actAimPose[0]; // one action, two subaction paths
    if ((res = mkFloat("trigger", "Trigger", &ctx->actTrigger)) != XR_SUCCESS)
        return res;
    if ((res = mkBool("squeeze", "Squeeze", &ctx->actSqueeze)) != XR_SUCCESS)
        return res;
    if ((res = mkVec2("stick", "Thumbstick", &ctx->actStick)) != XR_SUCCESS)
        return res;
    if ((res = mkBool("button_a", "Button A/X", &ctx->actButtonA)) != XR_SUCCESS)
        return res;
    if ((res = mkBool("button_b", "Button B/Y", &ctx->actButtonB)) != XR_SUCCESS)
        return res;
    if ((res = mkHaptic("haptic", "Haptics", &ctx->actHaptic[0])) != XR_SUCCESS)
        return res;
    ctx->actHaptic[1] = ctx->actHaptic[0];

    // Bindings: simple_controller first (supported by every runtime incl.
// SteamVR), then Oculus Touch for analog trigger + thumbstick fidelity.
    struct Binding {
        XrAction action;
        const char* left;
        const char* right;
    };
    Binding bindings[] = {
        { ctx->actAimPose[0], "/user/hand/left/input/aim/pose", "/user/hand/right/input/aim/pose" },
        { ctx->actTrigger, "/user/hand/left/input/select/value", "/user/hand/right/input/select/value" },
        { ctx->actSqueeze, "/user/hand/left/input/squeeze/value", "/user/hand/right/input/squeeze/value" },
        { ctx->actButtonA, "/user/hand/left/input/select/click", "/user/hand/right/input/select/click" },
        { ctx->actButtonB, "/user/hand/left/input/menu/click", "/user/hand/right/input/menu/click" },
        { ctx->actHaptic[0], "/user/hand/left/output/haptic", "/user/hand/right/output/haptic" },
    };
    const char* profiles[] = {
        "/interaction_profiles/khr/simple_controller",
        "/interaction_profiles/oculus/touch_controller",
    };
    for (auto profile : profiles) {
        XrPath profilePath = XR_NULL_PATH;
        if (xrStringToPath(ctx->instance, profile, &profilePath) != XR_SUCCESS)
            continue;
        std::vector<XrActionSuggestedBinding> suggested;
        for (auto& b : bindings) {
            // Thumbstick path differs per profile; add it per profile below.
            if (b.action == ctx->actStick)
                continue;
            for (int hand = 0; hand < 2; hand++) {
                XrPath p = XR_NULL_PATH;
                if (xrStringToPath(ctx->instance, hand == 0 ? b.left : b.right, &p) != XR_SUCCESS)
                    continue;
                suggested.push_back({ b.action, p });
            }
        }
        // Thumbstick: simple profile has no stick; touch does.
        const char* stickL = profile == std::string("/interaction_profiles/oculus/touch_controller")
            ? "/user/hand/left/input/thumbstick/value"
            : nullptr;
        const char* stickR = stickL ? "/user/hand/right/input/thumbstick/value" : nullptr;
        if (stickL) {
            XrPath pl = XR_NULL_PATH, pr = XR_NULL_PATH;
            if (xrStringToPath(ctx->instance, stickL, &pl) == XR_SUCCESS &&
                xrStringToPath(ctx->instance, stickR, &pr) == XR_SUCCESS) {
                suggested.push_back({ ctx->actStick, pl });
                suggested.push_back({ ctx->actStick, pr });
            }
        }
        XrInteractionProfileSuggestedBinding sug{ XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING };
        sug.interactionProfile = profilePath;
        sug.suggestedBindings = suggested.data();
        sug.countSuggestedBindings = (uint32_t)suggested.size();
        xrSuggestInteractionProfileBindings(ctx->instance, &sug); // best effort per profile
    }

    XrSessionActionSetsAttachInfo attach{ XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO };
    attach.countActionSets = 1;
    attach.actionSets = &ctx->actionSet;
    return xrAttachSessionActionSets(ctx->session, &attach);
}

ORVR_API int32_t orvr_xr_create_spaces(uint64_t session,
    uint64_t* headSpace, uint64_t* leftSpace, uint64_t* rightSpace) {
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx)
        return XR_ERROR_HANDLE_INVALID;

    XrReferenceSpaceCreateInfo rsci{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
    rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rsci.poseInReferenceSpace = { { 0, 0, 0, 1 }, { 0, 0, 0 } };
    XrResult res = xrCreateReferenceSpace(ctx->session, &rsci, &ctx->headSpace);
    if (res != XR_SUCCESS)
        return (int32_t)res;

    res = CreateActions(ctx);
    if (res != XR_SUCCESS)
        return (int32_t)res;

    for (int hand = 0; hand < 2; hand++) {
        XrActionSpaceCreateInfo asci{ XR_TYPE_ACTION_SPACE_CREATE_INFO };
        asci.action = ctx->actAimPose[0];
        asci.subactionPath = ctx->handPath[hand];
        asci.poseInActionSpace = { { 0, 0, 0, 1 }, { 0, 0, 0 } };
        res = xrCreateActionSpace(ctx->session, &asci, &ctx->handSpace[hand]);
        if (res != XR_SUCCESS)
            return (int32_t)res;
    }

    if (headSpace)
        *headSpace = (uint64_t)ctx->headSpace;
    if (leftSpace)
        *leftSpace = (uint64_t)ctx->handSpace[0];
    if (rightSpace)
        *rightSpace = (uint64_t)ctx->handSpace[1];
    g_spaces[(uint64_t)ctx->headSpace] = ctx;
    g_spaces[(uint64_t)ctx->handSpace[0]] = ctx;
    g_spaces[(uint64_t)ctx->handSpace[1]] = ctx;
    return (int32_t)XR_SUCCESS;
}

ORVR_API int32_t orvr_xr_begin(uint64_t session) {
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx)
        return XR_ERROR_HANDLE_INVALID;
    XrSessionBeginInfo bi{ XR_TYPE_SESSION_BEGIN_INFO };
    bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    XrResult res = xrBeginSession(ctx->session, &bi);
    if (res == XR_SUCCESS)
        ctx->sessionRunning = true;
    return (int32_t)res;
}

ORVR_API int32_t orvr_xr_poll(uint64_t session, int32_t* state, int32_t* focused, int32_t* lossPending) {
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx)
        return XR_ERROR_HANDLE_INVALID;
    XrEventDataBuffer ev{ XR_TYPE_EVENT_DATA_BUFFER };
    XrResult res = XR_SUCCESS;
    while ((res = xrPollEvent(ctx->instance, &ev)) == XR_SUCCESS) {
        if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto* s = (XrEventDataSessionStateChanged*)&ev;
            if (s->session == ctx->session) {
                ctx->state = s->state;
                ctx->focused = (s->state == XR_SESSION_STATE_FOCUSED);
                if (s->state == XR_SESSION_STATE_EXITING || s->state == XR_SESSION_STATE_LOSS_PENDING)
                    ctx->lossPending = true;
            }
        }
        ev.type = XR_TYPE_EVENT_DATA_BUFFER;
    }
    if (state)
        *state = (int32_t)ctx->state;
    if (focused)
        *focused = ctx->focused ? 1 : 0;
    if (lossPending)
        *lossPending = ctx->lossPending ? 1 : 0;
    return (int32_t)XR_SUCCESS;
}

ORVR_API int32_t orvr_xr_wait_locate(uint64_t session, uint64_t headSpace,
    uint64_t leftSpace, uint64_t rightSpace, OrvrLocatedPoses* poses) {
    if (!poses)
        return XR_ERROR_VALIDATION_FAILURE;
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx || !ctx->sessionRunning)
        return XR_ERROR_SESSION_NOT_RUNNING;

    XrFrameWaitInfo fwi{ XR_TYPE_FRAME_WAIT_INFO };
    XrFrameState fs{ XR_TYPE_FRAME_STATE };
    XrResult res = xrWaitFrame(ctx->session, &fwi, &fs);
    if (res != XR_SUCCESS)
        return (int32_t)res;
    ctx->frameDisplayTime = fs.predictedDisplayTime;

    XrFrameBeginInfo fbi{ XR_TYPE_FRAME_BEGIN_INFO };
    res = xrBeginFrame(ctx->session, &fbi);
    if (res != XR_SUCCESS)
        return (int32_t)res;
    ctx->frameBegun = true;

    // Eye views at predicted time (also refreshes the FOV cache).
    XrViewLocateInfo vli{ XR_TYPE_VIEW_LOCATE_INFO };
    vli.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    vli.displayTime = fs.predictedDisplayTime;
    vli.space = ctx->headSpace;
    XrViewState vs{ XR_TYPE_VIEW_STATE };
    uint32_t count = 0;
    XrView xrViews[2] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    res = xrLocateViews(ctx->session, &vli, &vs, 2, &count, xrViews);
    if (res == XR_SUCCESS && count == 2) {
        ctx->haveViews = true;
        for (int i = 0; i < 2; i++) {
            ctx->eyePose[i] = xrViews[i].pose;
            ctx->eyeFov[i] = xrViews[i].fov;
        }
        // Head = midpoint of eyes, orientation of left eye.
        poses->predictedDisplayTime = fs.predictedDisplayTime;
        poses->headX = (xrViews[0].pose.position.x + xrViews[1].pose.position.x) * 0.5f;
        poses->headY = (xrViews[0].pose.position.y + xrViews[1].pose.position.y) * 0.5f;
        poses->headZ = (xrViews[0].pose.position.z + xrViews[1].pose.position.z) * 0.5f;
        poses->headQX = xrViews[0].pose.orientation.x;
        poses->headQY = xrViews[0].pose.orientation.y;
        poses->headQZ = xrViews[0].pose.orientation.z;
        poses->headQW = xrViews[0].pose.orientation.w;
        poses->headActive = (vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) ? 1 : 0;
    } else {
        ctx->haveViews = false;
        memset(poses, 0, sizeof(*poses));
        poses->predictedDisplayTime = fs.predictedDisplayTime;
    }

    // Hands from action spaces.
    XrSpace spaces[2] = { ctx->handSpace[0], ctx->handSpace[1] };
    float* px[2] = { &poses->leftX, &poses->rightX };
    float* qx[2] = { &poses->leftQX, &poses->rightQX };
    int32_t* act[2] = { &poses->leftActive, &poses->rightActive };
    for (int hand = 0; hand < 2; hand++) {
        XrSpaceLocation loc{ XR_TYPE_SPACE_LOCATION };
        res = xrLocateSpace(spaces[hand], ctx->headSpace, fs.predictedDisplayTime, &loc);
        bool ok = (res == XR_SUCCESS) &&
            (loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) &&
            (loc.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT);
        px[hand][0] = loc.pose.position.x;
        px[hand][1] = loc.pose.position.y;
        px[hand][2] = loc.pose.position.z;
        qx[hand][0] = loc.pose.orientation.x;
        qx[hand][1] = loc.pose.orientation.y;
        qx[hand][2] = loc.pose.orientation.z;
        qx[hand][3] = loc.pose.orientation.w;
        *act[hand] = ok ? 1 : 0;
        // Cache for orvr_xr_actions so aim pose + buttons pair by hand index.
        // NOTE: hand spaces are located relative to the head space, so these
        // positions are head-relative by design (the C# pointer is angular).
        ctx->handPose[hand] = loc.pose;
        ctx->handPoseValid[hand] = ok;
    }
    (void)headSpace;
    (void)leftSpace;
    (void)rightSpace;
    return (int32_t)XR_SUCCESS;
}

static void FillHand(SessionCtx* ctx, int hand, OrvrHandActionState* out) {
    memset(out, 0, sizeof(*out));
    XrActiveActionSet active{ ctx->actionSet, XR_NULL_PATH };
    XrActionsSyncInfo sync{ XR_TYPE_ACTIONS_SYNC_INFO };
    sync.countActiveActionSets = 1;
    sync.activeActionSets = &active;
    if (xrSyncActions(ctx->session, &sync) != XR_SUCCESS)
        return;
    out->active = 1;
    // Aim pose cached by wait_locate (paired by hand index).
    if (ctx->handPoseValid[hand]) {
        out->aimX = ctx->handPose[hand].position.x;
        out->aimY = ctx->handPose[hand].position.y;
        out->aimZ = ctx->handPose[hand].position.z;
        out->aimQX = ctx->handPose[hand].orientation.x;
        out->aimQY = ctx->handPose[hand].orientation.y;
        out->aimQZ = ctx->handPose[hand].orientation.z;
        out->aimQW = ctx->handPose[hand].orientation.w;
    }
    XrActionStateGetInfo gi{ XR_TYPE_ACTION_STATE_GET_INFO };
    gi.subactionPath = ctx->handPath[hand];

    gi.action = ctx->actTrigger;
    XrActionStateFloat tf{ XR_TYPE_ACTION_STATE_FLOAT };
    if (xrGetActionStateFloat(ctx->session, &gi, &tf) == XR_SUCCESS && tf.isActive) {
        out->trigger = tf.currentState;
        out->triggerPressed = (tf.changedSinceLastSync && tf.currentState > 0.5f) ? 1 : 0;
        out->triggerReleased = (tf.changedSinceLastSync && tf.currentState <= 0.5f) ? 1 : 0;
    }
    gi.action = ctx->actSqueeze;
    XrActionStateBoolean sb{ XR_TYPE_ACTION_STATE_BOOLEAN };
    if (xrGetActionStateBoolean(ctx->session, &gi, &sb) == XR_SUCCESS && sb.isActive)
        out->squeezePressed = sb.currentState ? 1 : 0;
    gi.action = ctx->actStick;
    XrActionStateVector2f st{ XR_TYPE_ACTION_STATE_VECTOR2F };
    if (xrGetActionStateVector2f(ctx->session, &gi, &st) == XR_SUCCESS && st.isActive) {
        out->stickX = st.currentState.x;
        out->stickY = st.currentState.y;
    }
    int32_t bits = 0;
    gi.action = ctx->actButtonA;
    if (xrGetActionStateBoolean(ctx->session, &gi, &sb) == XR_SUCCESS && sb.isActive && sb.currentState)
        bits |= 1;
    gi.action = ctx->actButtonB;
    if (xrGetActionStateBoolean(ctx->session, &gi, &sb) == XR_SUCCESS && sb.isActive && sb.currentState)
        bits |= 2;
    out->buttons = bits;
    // Aim pose is located in wait_locate; the C# side pairs them by hand index.
}

ORVR_API int32_t orvr_xr_actions(uint64_t session, OrvrBothHands* both) {
    if (!both)
        return XR_ERROR_VALIDATION_FAILURE;
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx || !ctx->sessionRunning || ctx->actionSet == XR_NULL_HANDLE)
        return XR_ERROR_SESSION_NOT_RUNNING;
    FillHand(ctx, 0, &both->left);
    FillHand(ctx, 1, &both->right);
    return (int32_t)XR_SUCCESS;
}

ORVR_API void orvr_xr_recenter(uint64_t session) {
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx || ctx->headSpace == XR_NULL_HANDLE)
        return;
    // Recenter by rebuilding LOCAL space with an origin offset equal to the
    // inverse of the current head pose (position + yaw).
    XrSpaceLocation loc{ XR_TYPE_SPACE_LOCATION };
    // Locate head space origin in itself is identity; instead use last views.
    if (!ctx->haveViews)
        return;
    XrPosef head;
    head.position.x = (ctx->eyePose[0].position.x + ctx->eyePose[1].position.x) * 0.5f;
    head.position.y = 0; // keep floor height; recenter yaw + planar position only
    head.position.z = (ctx->eyePose[0].position.z + ctx->eyePose[1].position.z) * 0.5f;
    // Yaw-only inverse: extract yaw from left-eye orientation.
    float qx = ctx->eyePose[0].orientation.x, qy = ctx->eyePose[0].orientation.y;
    float qz = ctx->eyePose[0].orientation.z, qw = ctx->eyePose[0].orientation.w;
    float yaw = atan2f(2.0f * (qw * qy + qx * qz), 1.0f - 2.0f * (qy * qy + qx * qx));
    float hy = -yaw * 0.5f;
    XrQuaternionf inv = { 0, sinf(hy), 0, cosf(hy) };
    XrReferenceSpaceCreateInfo rsci{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
    rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    rsci.poseInReferenceSpace.orientation = inv;
    // Rotate the translational offset by the inverse yaw.
    float c = cosf(-yaw), s = sinf(-yaw);
    rsci.poseInReferenceSpace.position.x = -(c * head.position.x - s * head.position.z);
    rsci.poseInReferenceSpace.position.y = 0;
    rsci.poseInReferenceSpace.position.z = -(s * head.position.x + c * head.position.z);
    XrSpace ns = XR_NULL_HANDLE;
    if (xrCreateReferenceSpace(ctx->session, &rsci, &ns) == XR_SUCCESS) {
        g_spaces.erase((uint64_t)ctx->headSpace);
        xrDestroySpace(ctx->headSpace);
        ctx->headSpace = ns;
        g_spaces[(uint64_t)ns] = ctx;
    }
    (void)loc;
}

static int32_t EnsureSwapchain(SessionCtx* ctx, int eye, int32_t width, int32_t height) {
    SwapchainSet& sw = ctx->swap[eye];
    if (sw.handle != XR_NULL_HANDLE && sw.width == width && sw.height == height)
        return (int32_t)XR_SUCCESS;
    if (sw.handle != XR_NULL_HANDLE) {
        xrDestroySwapchain(sw.handle);
        sw.handle = XR_NULL_HANDLE;
        sw.images.clear();
    }
    // Pick a supported color format.
    uint32_t fmtCount = 0;
    if (xrEnumerateSwapchainFormats(ctx->session, 0, &fmtCount, nullptr) != XR_SUCCESS || fmtCount == 0)
        return XR_ERROR_RUNTIME_FAILURE;
    std::vector<int64_t> fmts(fmtCount);
    if (xrEnumerateSwapchainFormats(ctx->session, fmtCount, &fmtCount, fmts.data()) != XR_SUCCESS)
        return XR_ERROR_RUNTIME_FAILURE;
    const int64_t candidates[] = { DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
        DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM };
    int64_t chosen = 0;
    for (auto c : candidates) {
        if (std::find(fmts.begin(), fmts.end(), c) != fmts.end()) {
            chosen = c;
            break;
        }
    }
    if (!chosen)
        chosen = fmts[0];

    XrSwapchainCreateInfo sci{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
    sci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    sci.format = chosen;
    sci.sampleCount = 1;
    sci.width = (uint32_t)width;
    sci.height = (uint32_t)height;
    sci.faceCount = 1;
    sci.arraySize = 1;
    sci.mipCount = 1;
    XrResult res = xrCreateSwapchain(ctx->session, &sci, &sw.handle);
    if (res != XR_SUCCESS)
        return (int32_t)res;
    uint32_t imgCount = 0;
    if (xrEnumerateSwapchainImages(sw.handle, 0, &imgCount, nullptr) != XR_SUCCESS || imgCount == 0)
        return XR_ERROR_RUNTIME_FAILURE;
    sw.images.resize(imgCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
    if (xrEnumerateSwapchainImages(sw.handle, imgCount, &imgCount,
            (XrSwapchainImageBaseHeader*)sw.images.data()) != XR_SUCCESS)
        return XR_ERROR_RUNTIME_FAILURE;
    sw.width = width;
    sw.height = height;
    return (int32_t)XR_SUCCESS;
}

ORVR_API int32_t orvr_xr_submit_shared(uint64_t session, void* sharedLeft,
    void* sharedRight, int32_t width, int32_t height) {
    if (!sharedLeft || !sharedRight || width <= 0 || height <= 0)
        return XR_ERROR_VALIDATION_FAILURE;
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx || !ctx->sessionRunning || !ctx->frameBegun)
        return XR_ERROR_SESSION_NOT_RUNNING;

    // Clamp to the runtime recommendation (perf guard for high render scales).
    uint32_t vc = 0;
    int64_t maxW = width, maxH = height;
    if (xrEnumerateViewConfigurationViews(ctx->instance, ctx->systemId,
            XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &vc, nullptr) == XR_SUCCESS && vc >= 2) {
        std::vector<XrViewConfigurationView> views(vc, { XR_TYPE_VIEW_CONFIGURATION_VIEW });
        if (xrEnumerateViewConfigurationViews(ctx->instance, ctx->systemId,
                XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, vc, &vc, views.data()) == XR_SUCCESS) {
            ctx->recommendedW = views[0].recommendedImageRectWidth;
            ctx->recommendedH = views[0].recommendedImageRectHeight;
        }
    }
    int32_t w = (int32_t)std::min<int64_t>(width, ctx->recommendedW > 0 ? ctx->recommendedW : width);
    int32_t h = (int32_t)std::min<int64_t>(height, ctx->recommendedH > 0 ? ctx->recommendedH : height);

    void* shared[2] = { sharedLeft, sharedRight };
    XrSwapchainSubImage sub[2];
    for (int eye = 0; eye < 2; eye++) {
        int32_t res = EnsureSwapchain(ctx, eye, w, h);
        if (res != XR_SUCCESS)
            return res;
        SwapchainSet& sw = ctx->swap[eye];
        XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
        uint32_t index = 0;
        res = (int32_t)xrAcquireSwapchainImage(sw.handle, &ai, &index);
        if (res != XR_SUCCESS)
            return res;
        XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
        wi.timeout = 1000000000; // 1 s
        res = (int32_t)xrWaitSwapchainImage(sw.handle, &wi);
        if (res != XR_SUCCESS) {
            xrReleaseSwapchainImage(sw.handle, nullptr);
            return res;
        }
        // GPU copy: shared engine texture -> swapchain image.
        ComPtr<ID3D11Texture2D> src;
        ComPtr<ID3D11DeviceContext> imm;
        ctx->device->GetImmediateContext(&imm);
        HRESULT hr = ctx->device->OpenSharedResource((HANDLE)shared[eye],
            __uuidof(ID3D11Texture2D), (void**)src.GetAddressOf());
        if (SUCCEEDED(hr) && src && imm) {
            D3D11_TEXTURE2D_DESC sd{}, dd{};
            src->GetDesc(&sd);
            sw.images[index].texture->GetDesc(&dd);
            D3D11_BOX box{ 0, 0, 0,
                std::min(sd.Width, dd.Width), std::min(sd.Height, dd.Height), 1 };
            imm->CopySubresourceRegion(sw.images[index].texture, 0, 0, 0, 0, src.Get(), 0, &box);
        }
        xrReleaseSwapchainImage(sw.handle, nullptr);
        sub[eye].swapchain = sw.handle;
        sub[eye].imageRect.offset.x = 0;
        sub[eye].imageRect.offset.y = 0;
        sub[eye].imageRect.extent.width = w;
        sub[eye].imageRect.extent.height = h;
        sub[eye].imageArrayIndex = 0;
    }

    XrCompositionLayerProjectionView projViews[2] = {
        { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW },
        { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW },
    };
    for (int eye = 0; eye < 2; eye++) {
        projViews[eye].pose = ctx->haveViews ? ctx->eyePose[eye] : XrPosef{ { 0, 0, 0, 1 }, { 0, 0, 0 } };
        projViews[eye].fov = ctx->haveViews ? ctx->eyeFov[eye]
            : XrFovf{ 0.8f, 0.8f, 0.7f, 0.7f };
        projViews[eye].subImage = sub[eye];
    }
    XrCompositionLayerProjection layer{ XR_TYPE_COMPOSITION_LAYER_PROJECTION };
    layer.space = ctx->headSpace;
    layer.viewCount = 2;
    layer.views = projViews;
    const XrCompositionLayerBaseHeader* layers[] = { (const XrCompositionLayerBaseHeader*)&layer };

    XrFrameEndInfo fei{ XR_TYPE_FRAME_END_INFO };
    fei.displayTime = ctx->frameDisplayTime;
    fei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    fei.layerCount = 1;
    fei.layers = layers;
    XrResult res = xrEndFrame(ctx->session, &fei);
    ctx->frameBegun = false;
    return (int32_t)res;
}

ORVR_API int32_t orvr_xr_get_fov(uint64_t session, int32_t eye,
    float* fovLeft, float* fovRight, float* fovUp, float* fovDown) {
    if (eye < 0 || eye > 1 || !fovLeft || !fovRight || !fovUp || !fovDown)
        return XR_ERROR_VALIDATION_FAILURE;
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx || !ctx->haveViews)
        return XR_ERROR_RUNTIME_FAILURE;
    *fovLeft = -ctx->eyeFov[eye].angleLeft;
    *fovRight = ctx->eyeFov[eye].angleRight;
    *fovUp = ctx->eyeFov[eye].angleUp;
    *fovDown = -ctx->eyeFov[eye].angleDown;
    return (int32_t)XR_SUCCESS;
}

ORVR_API void orvr_xr_haptic(uint64_t session, int32_t hand, float amplitude, int32_t durationMs) {
    if (hand < 0 || hand > 1)
        return;
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx || !ctx->sessionRunning || ctx->actHaptic[0] == XR_NULL_HANDLE)
        return;
    XrHapticVibration vib{ XR_TYPE_HAPTIC_VIBRATION };
    vib.amplitude = std::min(1.0f, std::max(0.0f, amplitude));
    vib.duration = (XrDuration)std::max<int32_t>(1, durationMs) * 1000000;
    vib.frequency = XR_FREQUENCY_UNSPECIFIED;
    XrHapticActionInfo hai{ XR_TYPE_HAPTIC_ACTION_INFO };
    hai.action = ctx->actHaptic[0];
    hai.subactionPath = ctx->handPath[hand];
    xrApplyHapticFeedback(ctx->session, &hai, (XrHapticBaseHeader*)&vib);
}

ORVR_API int32_t orvr_xr_end(uint64_t session) {
    std::lock_guard<std::mutex> lock(g_mutex);
    SessionCtx* ctx = FindSession(session);
    if (!ctx)
        return XR_ERROR_HANDLE_INVALID;
    XrResult res = xrEndSession(ctx->session);
    ctx->sessionRunning = false;
    return (int32_t)res;
}

ORVR_API void orvr_xr_destroy(uint64_t handle) {
    std::lock_guard<std::mutex> lock(g_mutex);
    // Full session teardown (C# calls this first, then spaces, then instance;
    // later calls become safe no-ops via the erased map entries).
    auto it = g_sessions.find(handle);
    if (it != g_sessions.end()) {
        SessionCtx* ctx = it->second;
        g_sessions.erase(it);
        g_instances.erase((uint64_t)ctx->instance);
        g_spaces.erase((uint64_t)ctx->headSpace);
        g_spaces.erase((uint64_t)ctx->handSpace[0]);
        g_spaces.erase((uint64_t)ctx->handSpace[1]);
        // Tear down in reverse order; ignore errors during shutdown.
        for (int i = 0; i < 2; i++) {
            if (ctx->swap[i].handle != XR_NULL_HANDLE)
                xrDestroySwapchain(ctx->swap[i].handle);
            if (ctx->handSpace[i] != XR_NULL_HANDLE)
                xrDestroySpace(ctx->handSpace[i]);
        }
        if (ctx->headSpace != XR_NULL_HANDLE)
            xrDestroySpace(ctx->headSpace);
        if (ctx->actionSet != XR_NULL_HANDLE)
            xrDestroyActionSet(ctx->actionSet);
        if (ctx->session != XR_NULL_HANDLE)
            xrDestroySession(ctx->session);
        XrInstance inst = ctx->instance;
        delete ctx;
        if (inst != XR_NULL_HANDLE)
            xrDestroyInstance(inst);
        return;
    }
    // Single space destroyed before the session (not the C# order, but safe).
    auto sp = g_spaces.find(handle);
    if (sp != g_spaces.end()) {
        SessionCtx* ctx = sp->second;
        g_spaces.erase(sp);
        XrSpace s = (XrSpace)handle;
        if (s == ctx->headSpace)
            ctx->headSpace = XR_NULL_HANDLE;
        for (int i = 0; i < 2; i++)
            if (s == ctx->handSpace[i])
                ctx->handSpace[i] = XR_NULL_HANDLE;
        xrDestroySpace(s);
        return;
    }
    // Instance destroyed on its own: only valid with no live session.
    auto ins = g_instances.find(handle);
    if (ins != g_instances.end()) {
        XrInstance inst = (XrInstance)handle;
        g_instances.erase(ins);
        xrDestroyInstance(inst);
        return;
    }
    // Unknown/stale handle: ignore (destroy is idempotent-safe).
}

ORVR_API void orvr_xr_free_string(char* s) {
    free(s);
}

} // extern "C"
