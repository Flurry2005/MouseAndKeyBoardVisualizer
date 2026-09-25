// DLL entry points: COM class object, unload check and self-registration (regsvr32 compatible).
#include "pch.h"
#include "SwipeMediaSourceActivate.h"

std::atomic<long> g_objectCount{ 0 };
std::atomic<long> g_lockCount{ 0 };
HMODULE g_module = nullptr;

void MsvTrace(const wchar_t* format, ...)
{
    wchar_t buffer[512];
    va_list args;
    va_start(args, format);
    StringCchVPrintfW(buffer, ARRAYSIZE(buffer), format, args);
    va_end(args);
    StringCchCatW(buffer, ARRAYSIZE(buffer), L"\n");
    OutputDebugStringW(buffer);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;
    if (clsid != CLSID_MouseSwipeVisualizerCameraSource)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto factory = Microsoft::WRL::Make<msv::SwipeMediaSourceFactory>();
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }

    return factory.CopyTo(riid, object);
}

STDAPI DllCanUnloadNow()
{
    return (g_objectCount.load() == 0 && g_lockCount.load() == 0) ? S_OK : S_FALSE;
}

namespace
{
    constexpr wchar_t kClsidKey[] = L"SOFTWARE\\Classes\\CLSID\\" MSV_CLSID_STRING;

    HRESULT SetStringValue(HKEY key, const wchar_t* name, const wchar_t* value)
    {
        const DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
        return HRESULT_FROM_WIN32(RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), bytes));
    }
}

// Registers the media source in HKLM (required: it is loaded by the Frame Server services, which do
// not see per-user HKCU registrations). Needs administrator rights.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    if (length == 0 || length >= ARRAYSIZE(path))
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    HKEY clsidKey = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &clsidKey, nullptr);
    if (status != ERROR_SUCCESS)
    {
        return HRESULT_FROM_WIN32(status);
    }

    HRESULT hr = SetStringValue(clsidKey, nullptr, MSV_SOURCE_DESCRIPTION);
    HKEY serverKey = nullptr;
    if (SUCCEEDED(hr))
    {
        status = RegCreateKeyExW(clsidKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &serverKey, nullptr);
        hr = HRESULT_FROM_WIN32(status);
    }

    if (SUCCEEDED(hr)) hr = SetStringValue(serverKey, nullptr, path);
    if (SUCCEEDED(hr)) hr = SetStringValue(serverKey, L"ThreadingModel", L"Both");
    if (serverKey) RegCloseKey(serverKey);
    RegCloseKey(clsidKey);
    return hr;
}

STDAPI DllUnregisterServer()
{
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    return (status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(status);
}
