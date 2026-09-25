#include "pch.h"
#include "SwipeMediaSourceActivate.h"

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::MakeAndInitialize;

namespace msv
{
    HRESULT SwipeMediaSourceActivate::RuntimeClassInitialize()
    {
        return MFCreateAttributes(&m_attributes, 4);
    }

    IFACEMETHODIMP SwipeMediaSourceActivate::ActivateObject(REFIID riid, void** object)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, object);
        *object = nullptr;
        std::lock_guard lock(m_lock);
        ComPtr<SwipeMediaSource> source;
        MSV_RETURN_IF_FAILED(MakeAndInitialize<SwipeMediaSource>(&source, m_attributes.Get()));
        MSV_RETURN_IF_FAILED(source.CopyTo(riid, object));
        m_source = source;
        MsvTrace(L"[MSVCam] media source activated (pid %u)", GetCurrentProcessId());
        return S_OK;
    }

    IFACEMETHODIMP SwipeMediaSourceActivate::ShutdownObject()
    {
        std::lock_guard lock(m_lock);
        if (m_source)
        {
            m_source->Shutdown();
        }

        return S_OK;
    }

    IFACEMETHODIMP SwipeMediaSourceActivate::DetachObject()
    {
        std::lock_guard lock(m_lock);
        m_source.Reset();
        return S_OK;
    }

    IFACEMETHODIMP SwipeMediaSourceFactory::CreateInstance(IUnknown* outer, REFIID riid, void** object)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, object);
        *object = nullptr;
        MSV_RETURN_HR_IF(CLASS_E_NOAGGREGATION, outer != nullptr);
        ComPtr<SwipeMediaSourceActivate> activate;
        MSV_RETURN_IF_FAILED(MakeAndInitialize<SwipeMediaSourceActivate>(&activate));
        return activate.CopyTo(riid, object);
    }

    IFACEMETHODIMP SwipeMediaSourceFactory::LockServer(BOOL lock)
    {
        if (lock)
        {
            ++g_lockCount;
        }
        else
        {
            --g_lockCount;
        }

        return S_OK;
    }
}
