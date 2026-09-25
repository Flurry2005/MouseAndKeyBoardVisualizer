// C API exported for MouseSwipeVisualizer.exe (P/Invoke): camera management, diagnostics and a
// Media Foundation test consumer. None of this runs inside the Frame Server.
#pragma once

#pragma pack(push, 8)
struct MsvFrameInfo
{
    int32_t width;
    int32_t height;
    int32_t subtype;         // 1 = NV12, 2 = RGB32
    int32_t flags;           // bit 0: discontinuity
    int64_t timestamp100ns;  // IMFSourceReader::ReadSample timestamp
    int64_t duration100ns;
    int64_t arrivalQpc;      // QueryPerformanceCounter when ReadSample returned
};
#pragma pack(pop)

// Capability flags returned by MsvCam_QueryApiSupport.
#define MSV_API_BUILD_OK          0x1   // Windows build >= 22000
#define MSV_API_CREATE_PRESENT    0x2   // mfsensorgroup!MFCreateVirtualCamera exists
#define MSV_API_TYPE_SUPPORTED    0x4   // MFIsVirtualCameraTypeSupported(SoftwareCameraSource) == TRUE
#define MSV_API_SOURCE_REGISTERED 0x8   // our CLSID has an InprocServer32 in HKLM

extern "C"
{
    HRESULT __stdcall MsvCam_QueryApiSupport(int32_t* windowsBuild, int32_t* flags);
    HRESULT __stdcall MsvCam_GetRegisteredServerPath(wchar_t* path, int32_t capacity);
    HRESULT __stdcall MsvCam_CreateCamera(int32_t lifetime, int32_t access);
    HRESULT __stdcall MsvCam_RemoveCamera(int32_t lifetime, int32_t access);
    HRESULT __stdcall MsvCam_RemoveAllDevnodes(int32_t* removed);
    HRESULT __stdcall MsvCam_EnumerateCameras(wchar_t* buffer, int32_t capacity, int32_t* count);
    HRESULT __stdcall MsvCam_ConsumerOpen(const wchar_t* namePrefix, int32_t width, int32_t height, int32_t fps, int32_t subtype, void** handle);
    HRESULT __stdcall MsvCam_ConsumerOpenInProc(int32_t width, int32_t height, int32_t fps, int32_t subtype, void** handle);
    HRESULT __stdcall MsvCam_ConsumerRead(void* handle, uint32_t* bgra, int32_t capacityPixels, MsvFrameInfo* info);
    void __stdcall MsvCam_ConsumerClose(void* handle);
}
