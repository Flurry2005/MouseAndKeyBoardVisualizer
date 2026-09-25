// Camera management + test consumer (called from the app process, never from the Frame Server).
// Registration logic follows VCamUtils.cpp from Microsoft's VirtualCamera sample
// (Copyright (C) Microsoft Corporation, MIT License).
#include "pch.h"
#include "CameraControl.h"
#include "PixelConvert.h"
#include "SwipeMediaSourceActivate.h"

using Microsoft::WRL::ComPtr;

namespace
{
    using PFN_MFCreateVirtualCamera = HRESULT(WINAPI*)(MFVirtualCameraType, MFVirtualCameraLifetime, MFVirtualCameraAccess,
        LPCWSTR, LPCWSTR, const GUID*, ULONG, IMFVirtualCamera**);
    using PFN_MFIsVirtualCameraTypeSupported = HRESULT(WINAPI*)(MFVirtualCameraType, BOOL*);
    using PFN_RtlGetVersion = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);

    // The API is resolved at runtime so this DLL still loads on Windows 10 (where it is unsupported).
    HMODULE SensorGroupModule()
    {
        static HMODULE module = LoadLibraryExW(L"mfsensorgroup.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        return module;
    }

    PFN_MFCreateVirtualCamera CreateVirtualCameraFunction()
    {
        HMODULE module = SensorGroupModule();
        return module ? reinterpret_cast<PFN_MFCreateVirtualCamera>(GetProcAddress(module, "MFCreateVirtualCamera")) : nullptr;
    }

    int32_t WindowsBuild()
    {
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        auto rtlGetVersion = ntdll ? reinterpret_cast<PFN_RtlGetVersion>(GetProcAddress(ntdll, "RtlGetVersion")) : nullptr;
        RTL_OSVERSIONINFOW info{ sizeof(info) };
        return (rtlGetVersion != nullptr && rtlGetVersion(&info) == 0) ? static_cast<int32_t>(info.dwBuildNumber) : 0;
    }

    // COM + Media Foundation for the calling thread; balanced by the destructor.
    class MfScope
    {
    public:
        MfScope()
        {
            const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            m_uninitializeCom = SUCCEEDED(hr); // RPC_E_CHANGED_MODE: already STA, usable as is
            m_hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
        }

        ~MfScope()
        {
            if (SUCCEEDED(m_hr))
            {
                MFShutdown();
            }

            if (m_uninitializeCom)
            {
                CoUninitialize();
            }
        }

        HRESULT Result() const { return m_hr; }

    private:
        HRESULT m_hr = E_FAIL;
        bool m_uninitializeCom = false;
    };

    HRESULT CreateVirtualCamera(int32_t lifetime, int32_t access, ComPtr<IMFVirtualCamera>& camera)
    {
        PFN_MFCreateVirtualCamera create = CreateVirtualCameraFunction();
        MSV_RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED), create == nullptr);
        MSV_RETURN_HR_IF(E_INVALIDARG, lifetime < 0 || lifetime > 1 || access < 0 || access > 1);
        // Same parameters => the same camera (the API keys the camera off them).
        return create(MFVirtualCameraType_SoftwareCameraSource,
            static_cast<MFVirtualCameraLifetime>(lifetime),
            static_cast<MFVirtualCameraAccess>(access),
            MSV_CAMERA_FRIENDLY_NAME,
            MSV_CLSID_STRING,
            nullptr, 0,
            &camera);
    }

    bool InterfaceBelongsToUs(const wchar_t* interfacePath)
    {
        HKEY key = nullptr;
        if (CM_Open_Device_Interface_KeyW(interfacePath, KEY_READ, RegDisposition_OpenExisting, &key, 0) != CR_SUCCESS)
        {
            return false;
        }

        wchar_t value[64] = {};
        DWORD size = sizeof(value) - sizeof(wchar_t);
        DWORD type = 0;
        const LSTATUS status = RegQueryValueExW(key, L"CustomCaptureSourceClsid", nullptr, &type, reinterpret_cast<BYTE*>(value), &size);
        RegCloseKey(key);
        return status == ERROR_SUCCESS && type == REG_SZ && _wcsicmp(value, MSV_CLSID_STRING) == 0;
    }

    struct Consumer
    {
        ComPtr<IMFActivate> activate;
        ComPtr<IMFMediaSource> source;
        ComPtr<IMFSourceReader> reader;
        UINT32 width = 0;
        UINT32 height = 0;
        GUID subtype = GUID_NULL;
        bool comInitialized = false;
        bool mfStarted = false;
    };
}

static HRESULT FinishConsumerOpen(std::unique_ptr<Consumer>& consumer, int32_t width, int32_t height, int32_t fps, int32_t subtype, void** handle);

extern "C"
{
    HRESULT __stdcall MsvCam_QueryApiSupport(int32_t* windowsBuild, int32_t* flags)
    {
        MSV_RETURN_HR_IF(E_POINTER, windowsBuild == nullptr || flags == nullptr);
        *windowsBuild = WindowsBuild();
        *flags = 0;
        if (*windowsBuild >= 22000)
        {
            *flags |= MSV_API_BUILD_OK;
        }

        if (CreateVirtualCameraFunction() != nullptr)
        {
            *flags |= MSV_API_CREATE_PRESENT;
        }

        HMODULE module = SensorGroupModule();
        auto isSupported = module ? reinterpret_cast<PFN_MFIsVirtualCameraTypeSupported>(GetProcAddress(module, "MFIsVirtualCameraTypeSupported")) : nullptr;
        BOOL supported = FALSE;
        if (isSupported != nullptr && SUCCEEDED(isSupported(MFVirtualCameraType_SoftwareCameraSource, &supported)) && supported)
        {
            *flags |= MSV_API_TYPE_SUPPORTED;
        }

        wchar_t path[MAX_PATH];
        if (MsvCam_GetRegisteredServerPath(path, MAX_PATH) == S_OK)
        {
            *flags |= MSV_API_SOURCE_REGISTERED;
        }

        return S_OK;
    }

    HRESULT __stdcall MsvCam_GetRegisteredServerPath(wchar_t* path, int32_t capacity)
    {
        MSV_RETURN_HR_IF(E_INVALIDARG, path == nullptr || capacity <= 0);
        path[0] = L'\0';
        DWORD bytes = static_cast<DWORD>(capacity) * sizeof(wchar_t);
        const LSTATUS status = RegGetValueW(HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Classes\\CLSID\\" MSV_CLSID_STRING L"\\InprocServer32", nullptr,
            RRF_RT_REG_SZ, nullptr, path, &bytes);
        if (status == ERROR_FILE_NOT_FOUND)
        {
            return S_FALSE;
        }

        return HRESULT_FROM_WIN32(status);
    }

    HRESULT __stdcall MsvCam_CreateCamera(int32_t lifetime, int32_t access)
    {
        MfScope scope;
        MSV_RETURN_IF_FAILED(scope.Result());
        ComPtr<IMFVirtualCamera> camera;
        MSV_RETURN_IF_FAILED(CreateVirtualCamera(lifetime, access, camera));
        // Start registers the device with the camera pipeline; with System lifetime it stays
        // registered (also across reboots) after Shutdown releases this object.
        const HRESULT hr = camera->Start(nullptr);
        camera->Shutdown();
        return hr;
    }

    HRESULT __stdcall MsvCam_RemoveCamera(int32_t lifetime, int32_t access)
    {
        MfScope scope;
        MSV_RETURN_IF_FAILED(scope.Result());
        ComPtr<IMFVirtualCamera> camera;
        MSV_RETURN_IF_FAILED(CreateVirtualCamera(lifetime, access, camera));
        const HRESULT hr = camera->Remove();
        camera->Shutdown();
        return hr;
    }

    // Uninstall safety net: removes every camera device node created from our CLSID, whatever
    // lifetime/access/user it was created with. Requires administrator rights.
    HRESULT __stdcall MsvCam_RemoveAllDevnodes(int32_t* removed)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, removed);
        *removed = 0;
        GUID category = KSCATEGORY_VIDEO_CAMERA;
        ULONG length = 0;
        CONFIGRET cr = CM_Get_Device_Interface_List_SizeW(&length, &category, nullptr, CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES);
        MSV_RETURN_HR_IF(HRESULT_FROM_WIN32(CM_MapCrToWin32Err(cr, ERROR_INVALID_DATA)), cr != CR_SUCCESS);
        std::vector<wchar_t> list(length + 1, L'\0');
        cr = CM_Get_Device_Interface_ListW(&category, nullptr, list.data(), length, CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES);
        MSV_RETURN_HR_IF(HRESULT_FROM_WIN32(CM_MapCrToWin32Err(cr, ERROR_INVALID_DATA)), cr != CR_SUCCESS);

        HRESULT result = S_OK;
        for (const wchar_t* item = list.data(); *item != L'\0'; item += wcslen(item) + 1)
        {
            if (!InterfaceBelongsToUs(item))
            {
                continue;
            }

            wchar_t instanceId[MAX_DEVICE_ID_LEN] = {};
            ULONG bytes = sizeof(instanceId);
            DEVPROPTYPE type = 0;
            cr = CM_Get_Device_Interface_PropertyW(item, &DEVPKEY_Device_InstanceId, &type, reinterpret_cast<PBYTE>(instanceId), &bytes, 0);
            if (cr != CR_SUCCESS)
            {
                continue;
            }

            DEVINST node = 0;
            cr = CM_Locate_DevNodeW(&node, instanceId, CM_LOCATE_DEVNODE_PHANTOM);
            if (cr != CR_SUCCESS)
            {
                continue;
            }

            PNP_VETO_TYPE veto = PNP_VetoTypeUnknown;
            wchar_t vetoName[MAX_PATH] = {};
            CM_Query_And_Remove_SubTreeW(node, &veto, vetoName, MAX_PATH, CM_REMOVE_NO_RESTART);
            cr = CM_Uninstall_DevNode(node, 0);
            if (cr == CR_SUCCESS)
            {
                (*removed)++;
            }
            else
            {
                result = HRESULT_FROM_WIN32(CM_MapCrToWin32Err(cr, ERROR_ACCESS_DENIED));
            }
        }

        return result;
    }

    // Lists video cameras as "friendly name \t symbolic link \t 1|0 (ours)\n" using the standard
    // Media Foundation enumeration (what applications such as Medal see).
    HRESULT __stdcall MsvCam_EnumerateCameras(wchar_t* buffer, int32_t capacity, int32_t* count)
    {
        MSV_RETURN_HR_IF(E_INVALIDARG, buffer == nullptr || capacity <= 0 || count == nullptr);
        buffer[0] = L'\0';
        *count = 0;
        MfScope scope;
        MSV_RETURN_IF_FAILED(scope.Result());

        ComPtr<IMFAttributes> attributes;
        MSV_RETURN_IF_FAILED(MFCreateAttributes(&attributes, 2));
        MSV_RETURN_IF_FAILED(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
        MSV_RETURN_IF_FAILED(attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY, KSCATEGORY_VIDEO_CAMERA));
        IMFActivate** devices = nullptr;
        UINT32 deviceCount = 0;
        MSV_RETURN_IF_FAILED(MFEnumDeviceSources(attributes.Get(), &devices, &deviceCount));

        std::wstring text;
        for (UINT32 i = 0; i < deviceCount; i++)
        {
            wchar_t* name = nullptr;
            wchar_t* link = nullptr;
            UINT32 cch = 0;
            devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &cch);
            devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, &link, &cch);
            text += name ? name : L"";
            text += L'\t';
            text += link ? link : L"";
            text += L'\t';
            text += (link && InterfaceBelongsToUs(link)) ? L"1" : L"0";
            text += L'\n';
            CoTaskMemFree(name);
            CoTaskMemFree(link);
            devices[i]->Release();
        }

        CoTaskMemFree(devices);
        *count = static_cast<int32_t>(deviceCount);
        MSV_RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), text.size() + 1 > static_cast<size_t>(capacity));
        StringCchCopyW(buffer, static_cast<size_t>(capacity), text.c_str());
        return S_OK;
    }

    HRESULT __stdcall MsvCam_ConsumerOpen(const wchar_t* namePrefix, int32_t width, int32_t height, int32_t fps, int32_t subtype, void** handle)
    {
        MSV_RETURN_HR_IF(E_INVALIDARG, namePrefix == nullptr || handle == nullptr || width <= 0 || height <= 0 || fps <= 0);
        *handle = nullptr;
        auto consumer = std::make_unique<Consumer>();
        consumer->comInitialized = SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
        HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
        consumer->mfStarted = SUCCEEDED(hr);
        auto fail = [&](HRESULT error)
        {
            MsvCam_ConsumerClose(consumer.release());
            return error;
        };

        if (FAILED(hr))
        {
            return fail(hr);
        }

        ComPtr<IMFAttributes> attributes;
        hr = MFCreateAttributes(&attributes, 2);
        if (SUCCEEDED(hr)) hr = attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
        if (SUCCEEDED(hr)) hr = attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_CATEGORY, KSCATEGORY_VIDEO_CAMERA);
        IMFActivate** devices = nullptr;
        UINT32 deviceCount = 0;
        if (SUCCEEDED(hr)) hr = MFEnumDeviceSources(attributes.Get(), &devices, &deviceCount);
        if (FAILED(hr))
        {
            return fail(hr);
        }

        const size_t prefixLength = wcslen(namePrefix);
        for (UINT32 i = 0; i < deviceCount; i++)
        {
            wchar_t* name = nullptr;
            UINT32 cch = 0;
            if (!consumer->activate && SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &cch)) &&
                _wcsnicmp(name, namePrefix, prefixLength) == 0)
            {
                consumer->activate = devices[i];
            }

            CoTaskMemFree(name);
            devices[i]->Release();
        }

        CoTaskMemFree(devices);
        if (!consumer->activate)
        {
            return fail(MF_E_NOT_FOUND);
        }

        return FinishConsumerOpen(consumer, width, height, fps, subtype, handle);
    }

    // Test entry point: activates the media source in this process (no Frame Server, no
    // registration needed). Exercises the same IMFMediaSource/IMFMediaStream code and the IPC.
    HRESULT __stdcall MsvCam_ConsumerOpenInProc(int32_t width, int32_t height, int32_t fps, int32_t subtype, void** handle)
    {
        MSV_RETURN_HR_IF(E_INVALIDARG, handle == nullptr || width <= 0 || height <= 0 || fps <= 0);
        *handle = nullptr;
        auto consumer = std::make_unique<Consumer>();
        consumer->comInitialized = SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
        HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
        consumer->mfStarted = SUCCEEDED(hr);
        if (SUCCEEDED(hr))
        {
            ComPtr<msv::SwipeMediaSourceActivate> activate;
            hr = Microsoft::WRL::MakeAndInitialize<msv::SwipeMediaSourceActivate>(&activate);
            if (SUCCEEDED(hr))
            {
                consumer->activate = activate;
                return FinishConsumerOpen(consumer, width, height, fps, subtype, handle);
            }
        }

        MsvCam_ConsumerClose(consumer.release());
        return hr;
    }

}

// Shared tail of both consumer entry points (C++ linkage).
static HRESULT FinishConsumerOpen(std::unique_ptr<Consumer>& consumer, int32_t width, int32_t height, int32_t fps, int32_t subtype, void** handle)
    {
        auto fail = [&](HRESULT error)
        {
            MsvCam_ConsumerClose(consumer.release());
            return error;
        };

        HRESULT hr = consumer->activate->ActivateObject(IID_PPV_ARGS(&consumer->source));
        ComPtr<IMFAttributes> readerAttributes;
        if (SUCCEEDED(hr)) hr = MFCreateAttributes(&readerAttributes, 1);
        if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromMediaSource(consumer->source.Get(), readerAttributes.Get(), &consumer->reader);
        if (FAILED(hr))
        {
            return fail(hr);
        }

        // Pick the native media type exactly matching the request.
        const GUID wanted = subtype == 2 ? MFVideoFormat_RGB32 : MFVideoFormat_NV12;
        ComPtr<IMFMediaType> chosen;
        for (DWORD index = 0; !chosen; index++)
        {
            ComPtr<IMFMediaType> type;
            if (FAILED(consumer->reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), index, &type)))
            {
                break;
            }

            GUID typeSubtype = GUID_NULL;
            UINT32 w = 0, h = 0, num = 0, den = 0;
            type->GetGUID(MF_MT_SUBTYPE, &typeSubtype);
            MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
            MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
            if (typeSubtype == wanted && w == static_cast<UINT32>(width) && h == static_cast<UINT32>(height) && den != 0 &&
                num / den == static_cast<UINT32>(fps))
            {
                chosen = type;
            }
        }

        if (!chosen)
        {
            return fail(MF_E_INVALIDMEDIATYPE);
        }

        hr = consumer->reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
        if (SUCCEEDED(hr)) hr = consumer->reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE);
        if (SUCCEEDED(hr)) hr = consumer->reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, chosen.Get());
        if (FAILED(hr))
        {
            return fail(hr);
        }

        consumer->width = static_cast<UINT32>(width);
        consumer->height = static_cast<UINT32>(height);
        consumer->subtype = wanted;
        *handle = consumer.release();
        return S_OK;
    }

extern "C"
{
    HRESULT __stdcall MsvCam_ConsumerRead(void* handle, uint32_t* bgra, int32_t capacityPixels, MsvFrameInfo* info)
    {
        MSV_RETURN_HR_IF(E_INVALIDARG, handle == nullptr || bgra == nullptr || info == nullptr);
        auto* consumer = static_cast<Consumer*>(handle);
        MSV_RETURN_HR_IF(E_UNEXPECTED, !consumer->reader);
        const size_t pixelCount = static_cast<size_t>(consumer->width) * consumer->height;
        MSV_RETURN_HR_IF(MF_E_BUFFERTOOSMALL, capacityPixels < 0 || pixelCount > static_cast<size_t>(capacityPixels));

        ComPtr<IMFSample> sample;
        LONGLONG timestamp = 0;
        for (int attempt = 0; attempt < 100 && !sample; attempt++)
        {
            DWORD streamIndex = 0, flags = 0;
            MSV_RETURN_IF_FAILED(consumer->reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0,
                &streamIndex, &flags, &timestamp, &sample));
            MSV_RETURN_HR_IF(E_FAIL, (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM)) != 0);
        }

        MSV_RETURN_HR_IF(MF_E_END_OF_STREAM, !sample);
        LARGE_INTEGER arrival;
        QueryPerformanceCounter(&arrival);

        LONGLONG duration = 0;
        sample->GetSampleDuration(&duration);
        UINT32 discontinuity = 0;
        sample->GetUINT32(MFSampleExtension_Discontinuity, &discontinuity);

        ComPtr<IMFMediaBuffer> buffer;
        MSV_RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));
        const bool nv12 = consumer->subtype == MFVideoFormat_NV12;
        ComPtr<IMF2DBuffer> buffer2D;
        if (SUCCEEDED(buffer.As(&buffer2D)))
        {
            BYTE* scanline0 = nullptr;
            LONG pitch = 0;
            MSV_RETURN_IF_FAILED(buffer2D->Lock2D(&scanline0, &pitch));
            if (nv12)
            {
                msv::ConvertNv12ToBgra(scanline0, pitch, consumer->width, consumer->height, bgra);
            }
            else
            {
                msv::CopyRgb32ToBgra(scanline0, pitch, consumer->width, consumer->height, bgra);
            }

            buffer2D->Unlock2D();
        }
        else
        {
            BYTE* data = nullptr;
            DWORD current = 0;
            MSV_RETURN_IF_FAILED(buffer->Lock(&data, nullptr, &current));
            const DWORD needed = nv12 ? consumer->width * consumer->height * 3 / 2 : consumer->width * consumer->height * 4;
            HRESULT hr = S_OK;
            if (current < needed)
            {
                hr = MF_E_BUFFERTOOSMALL;
            }
            else if (nv12)
            {
                msv::ConvertNv12ToBgra(data, static_cast<LONG>(consumer->width), consumer->width, consumer->height, bgra);
            }
            else
            {
                msv::CopyRgb32ToBgra(data, static_cast<LONG>(consumer->width * 4), consumer->width, consumer->height, bgra);
            }

            buffer->Unlock();
            MSV_RETURN_IF_FAILED(hr);
        }

        info->width = static_cast<int32_t>(consumer->width);
        info->height = static_cast<int32_t>(consumer->height);
        info->subtype = nv12 ? 1 : 2;
        info->flags = discontinuity ? 1 : 0;
        info->timestamp100ns = timestamp;
        info->duration100ns = duration;
        info->arrivalQpc = arrival.QuadPart;
        return S_OK;
    }

    void __stdcall MsvCam_ConsumerClose(void* handle)
    {
        if (handle == nullptr)
        {
            return;
        }

        std::unique_ptr<Consumer> consumer(static_cast<Consumer*>(handle));
        consumer->reader.Reset(); // shuts the source down (default source reader behaviour)
        if (consumer->source)
        {
            consumer->source->Shutdown();
            consumer->source.Reset();
        }

        if (consumer->activate)
        {
            consumer->activate->ShutdownObject();
            consumer->activate.Reset();
        }

        if (consumer->mfStarted)
        {
            MFShutdown();
        }

        if (consumer->comInitialized)
        {
            CoUninitialize();
        }
    }
}
