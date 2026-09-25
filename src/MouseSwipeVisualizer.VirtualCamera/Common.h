// Common helpers for the virtual camera media source.
#pragma once

// Stable identity of the media source (generated once for this project; never regenerate).
// {0728D89A-2065-4F45-85F7-B128DA227817}
DEFINE_GUID(CLSID_MouseSwipeVisualizerCameraSource,
    0x0728d89a, 0x2065, 0x4f45, 0x85, 0xf7, 0xb1, 0x28, 0xda, 0x22, 0x78, 0x17);

#define MSV_CLSID_STRING         L"{0728D89A-2065-4F45-85F7-B128DA227817}"
#define MSV_CAMERA_FRIENDLY_NAME L"Mouse Swipe Visualizer Camera"
#define MSV_SOURCE_DESCRIPTION   L"Mouse Swipe Visualizer virtual camera media source"

// Tracks live COM objects for DllCanUnloadNow.
extern std::atomic<long> g_objectCount;
extern std::atomic<long> g_lockCount;
extern HMODULE g_module;

struct ModuleObjectCounter
{
    ModuleObjectCounter() noexcept { ++g_objectCount; }
    ~ModuleObjectCounter() { --g_objectCount; }
    ModuleObjectCounter(const ModuleObjectCounter&) = delete;
    ModuleObjectCounter& operator=(const ModuleObjectCounter&) = delete;
};

// Debug output (visible with DebugView / an attached debugger). The source runs inside the
// FrameServer service, so there is no console; errors are also published in the shared header.
void MsvTrace(const wchar_t* format, ...);

#define MSV_RETURN_IF_FAILED(expr)                                                      \
    do                                                                                  \
    {                                                                                   \
        const HRESULT _hrMsv = (expr);                                                  \
        if (FAILED(_hrMsv))                                                             \
        {                                                                               \
            MsvTrace(L"[MSVCam] %S(%d): 0x%08X", __FUNCTION__, __LINE__, _hrMsv);       \
            return _hrMsv;                                                              \
        }                                                                               \
    } while (0)

#define MSV_RETURN_HR_IF(hr, condition)                                                 \
    do                                                                                  \
    {                                                                                   \
        if (condition)                                                                  \
        {                                                                               \
            return (hr);                                                                \
        }                                                                               \
    } while (0)

#define MSV_RETURN_HR_IF_NULL(hr, ptr) MSV_RETURN_HR_IF(hr, (ptr) == nullptr)
