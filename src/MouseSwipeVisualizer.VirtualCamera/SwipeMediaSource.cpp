// Portions follow Microsoft's Windows-Camera VirtualCamera sample (SimpleMediaSource.cpp),
// Copyright (C) Microsoft Corporation, MIT License.
#include "pch.h"
#include "SwipeMediaSource.h"

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::MakeAndInitialize;

namespace msv
{
    namespace
    {
        constexpr DWORD kStreamId = 0;
    }

    SwipeMediaSource::~SwipeMediaSource()
    {
        Shutdown();
    }

    HRESULT SwipeMediaSource::RuntimeClassInitialize(IMFAttributes* activateAttributes)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_HR_IF(MF_E_ALREADY_INITIALIZED, m_state != State::Invalid);

        MSV_RETURN_IF_FAILED(CreateSourceAttributes(activateAttributes));
        MSV_RETURN_IF_FAILED(MFCreateEventQueue(&m_eventQueue));

        m_link = std::make_shared<FrameLink>();
        MSV_RETURN_IF_FAILED(MakeAndInitialize<SwipeMediaStream>(&m_stream, this, kStreamId, m_link));

        ComPtr<IMFStreamDescriptor> streamDescriptor;
        MSV_RETURN_IF_FAILED(m_stream->GetStreamDescriptor(&streamDescriptor));
        IMFStreamDescriptor* descriptors[] = { streamDescriptor.Get() };
        MSV_RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, descriptors, &m_presentationDescriptor));

        m_state = State::Stopped;
        return S_OK;
    }

    HRESULT SwipeMediaSource::CreateSourceAttributes(IMFAttributes* activateAttributes)
    {
        MSV_RETURN_IF_FAILED(MFCreateAttributes(&m_attributes, 4));
        if (activateAttributes != nullptr)
        {
            MSV_RETURN_IF_FAILED(activateAttributes->CopyAllItems(m_attributes.Get()));
        }

        // Sensor profiles: the Legacy profile is mandatory for Frame Server cameras. Unlike the
        // sample (which limits Legacy to <= 30 fps) it exposes every media type, so apps that are not
        // profile-aware still see the 60 fps modes.
        ComPtr<IMFSensorProfileCollection> collection;
        ComPtr<IMFSensorProfile> profile;
        HRESULT hr = MFCreateSensorProfileCollection(&collection);
        if (SUCCEEDED(hr)) hr = MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &profile);
        if (SUCCEEDED(hr)) hr = profile->AddProfileFilter(kStreamId, L"((RES==;FRT==;SUT==))");
        if (SUCCEEDED(hr)) hr = collection->AddProfile(profile.Get());
        if (SUCCEEDED(hr)) hr = MFCreateSensorProfile(KSCAMERAPROFILE_HighFrameRate, 0, nullptr, &profile);
        if (SUCCEEDED(hr)) hr = profile->AddProfileFilter(kStreamId, L"((RES==;FRT>=60,1;SUT==))");
        if (SUCCEEDED(hr)) hr = collection->AddProfile(profile.Get());
        if (SUCCEEDED(hr)) hr = m_attributes->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, collection.Get());
        if (FAILED(hr))
        {
            MsvTrace(L"[MSVCam] sensor profiles not set (0x%08X); continuing without", hr);
        }

        return S_OK;
    }

    HRESULT SwipeMediaSource::CheckShutdownLocked() const
    {
        if (m_state == State::Shutdown)
        {
            return MF_E_SHUTDOWN;
        }

        return (m_eventQueue && m_stream) ? S_OK : E_UNEXPECTED;
    }

    // ---------------------------------------------------------------- IMFMediaEventGenerator

    IFACEMETHODIMP SwipeMediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_eventQueue->BeginGetEvent(callback, state);
    }

    IFACEMETHODIMP SwipeMediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_eventQueue->EndGetEvent(result, event);
    }

    IFACEMETHODIMP SwipeMediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard lock(m_lock);
            MSV_RETURN_IF_FAILED(CheckShutdownLocked());
            queue = m_eventQueue;
        }

        return queue->GetEvent(flags, event);
    }

    IFACEMETHODIMP SwipeMediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* propValue)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_eventQueue->QueueEventParamVar(type, extendedType, status, propValue);
    }

    // ---------------------------------------------------------------- IMFMediaSource

    IFACEMETHODIMP SwipeMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, descriptor);
        *descriptor = nullptr;
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_presentationDescriptor->Clone(descriptor);
    }

    IFACEMETHODIMP SwipeMediaSource::GetCharacteristics(DWORD* characteristics)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, characteristics);
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        *characteristics = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }

    IFACEMETHODIMP SwipeMediaSource::Pause()
    {
        return MF_E_INVALID_STATE_TRANSITION; // live source: pause is not supported
    }

    IFACEMETHODIMP SwipeMediaSource::Shutdown()
    {
        ComPtr<SwipeMediaStream> stream;
        std::shared_ptr<FrameLink> link;
        {
            std::lock_guard lock(m_lock);
            if (m_state == State::Shutdown)
            {
                return S_OK;
            }

            m_state = State::Shutdown;
            if (m_eventQueue)
            {
                m_eventQueue->Shutdown();
                m_eventQueue.Reset();
            }

            m_presentationDescriptor.Reset();
            m_attributes.Reset();
            stream = std::move(m_stream);
            link = std::move(m_link);
        }

        // Outside the lock: the stream joins its pacing thread.
        if (stream)
        {
            stream->Shutdown();
        }

        if (link)
        {
            link->Close();
        }

        return S_OK;
    }

    IFACEMETHODIMP SwipeMediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        MSV_RETURN_HR_IF(E_INVALIDARG, descriptor == nullptr || startPosition == nullptr);
        MSV_RETURN_HR_IF(MF_E_UNSUPPORTED_TIME_FORMAT, timeFormat != nullptr && *timeFormat != GUID_NULL);
        MSV_RETURN_HR_IF(MF_E_INVALID_STATE_TRANSITION, m_state == State::Invalid);

        DWORD count = 0;
        MSV_RETURN_IF_FAILED(descriptor->GetStreamDescriptorCount(&count));
        MSV_RETURN_HR_IF(E_INVALIDARG, count != 1);

        BOOL selected = FALSE;
        ComPtr<IMFStreamDescriptor> streamDescriptor;
        MSV_RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(0, &selected, &streamDescriptor));
        DWORD streamId = 0;
        MSV_RETURN_IF_FAILED(streamDescriptor->GetStreamIdentifier(&streamId));
        MSV_RETURN_HR_IF(E_INVALIDARG, streamId != kStreamId);

        BOOL wasSelected = FALSE;
        ComPtr<IMFStreamDescriptor> ours;
        MSV_RETURN_IF_FAILED(m_presentationDescriptor->GetStreamDescriptorByIndex(0, &wasSelected, &ours));

        PROPVARIANT startTime;
        MSV_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &startTime));

        if (selected)
        {
            MSV_RETURN_IF_FAILED(m_presentationDescriptor->SelectStream(0));
            ComPtr<IMFMediaTypeHandler> handler;
            ComPtr<IMFMediaType> mediaType;
            MSV_RETURN_IF_FAILED(streamDescriptor->GetMediaTypeHandler(&handler));
            MSV_RETURN_IF_FAILED(handler->GetCurrentMediaType(&mediaType));

            ComPtr<IUnknown> streamUnknown;
            MSV_RETURN_IF_FAILED(m_stream.As(&streamUnknown));
            MSV_RETURN_IF_FAILED(m_eventQueue->QueueEventParamUnk(wasSelected ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, streamUnknown.Get()));
            MSV_RETURN_IF_FAILED(m_stream->Start(mediaType.Get())); // sends MEStreamStarted
        }
        else if (wasSelected)
        {
            MSV_RETURN_IF_FAILED(m_presentationDescriptor->DeselectStream(0));
            MSV_RETURN_IF_FAILED(m_stream->Stop(false));
        }

        MSV_RETURN_IF_FAILED(m_eventQueue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &startTime));
        m_state = State::Started;
        return S_OK;
    }

    IFACEMETHODIMP SwipeMediaSource::Stop()
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        MSV_RETURN_HR_IF(MF_E_INVALID_STATE_TRANSITION, m_state != State::Started);
        m_state = State::Stopped;

        PROPVARIANT stopTime;
        MSV_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &stopTime));
        MSV_RETURN_IF_FAILED(m_stream->Stop(true));
        MSV_RETURN_IF_FAILED(m_presentationDescriptor->DeselectStream(0));
        MSV_RETURN_IF_FAILED(m_eventQueue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &stopTime));
        return S_OK;
    }

    // ---------------------------------------------------------------- IMFMediaSourceEx

    IFACEMETHODIMP SwipeMediaSource::GetSourceAttributes(IMFAttributes** attributes)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, attributes);
        *attributes = nullptr;
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        // Must return a reference, not a copy: upstream components store data in it.
        return m_attributes.CopyTo(attributes);
    }

    IFACEMETHODIMP SwipeMediaSource::GetStreamAttributes(DWORD streamIdentifier, IMFAttributes** attributes)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, attributes);
        *attributes = nullptr;
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        MSV_RETURN_HR_IF(MF_E_NOT_FOUND, streamIdentifier != kStreamId);
        return m_stream->GetAttributes(attributes);
    }

    IFACEMETHODIMP SwipeMediaSource::SetD3DManager(IUnknown*)
    {
        return E_NOTIMPL; // CPU-rendered frames; the return propValue is ignored by the framework
    }

    // ---------------------------------------------------------------- IMFGetService

    IFACEMETHODIMP SwipeMediaSource::GetService(REFGUID, REFIID, LPVOID* object)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, object);
        *object = nullptr;
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return MF_E_UNSUPPORTED_SERVICE;
    }

    // ---------------------------------------------------------------- IKsControl

    IFACEMETHODIMP SwipeMediaSource::KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID, ULONG, ULONG* bytesReturned)
    {
        MSV_RETURN_HR_IF(E_INVALIDARG, property == nullptr || propertyLength < sizeof(KSPROPERTY));
        if (bytesReturned != nullptr)
        {
            *bytesReturned = 0;
        }

        // ERROR_SET_NOT_FOUND is what the AVStream framework returns for unhandled property sets.
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    IFACEMETHODIMP SwipeMediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned != nullptr)
        {
            *bytesReturned = 0;
        }

        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    IFACEMETHODIMP SwipeMediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* bytesReturned)
    {
        if (bytesReturned != nullptr)
        {
            *bytesReturned = 0;
        }

        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    // ---------------------------------------------------------------- IMFSampleAllocatorControl

    IFACEMETHODIMP SwipeMediaSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, allocator);
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        MSV_RETURN_HR_IF(MF_E_NOT_FOUND, outputStreamId != kStreamId);
        ComPtr<IMFVideoSampleAllocator> videoAllocator;
        MSV_RETURN_IF_FAILED(allocator->QueryInterface(IID_PPV_ARGS(&videoAllocator)));
        return m_stream->SetSampleAllocator(videoAllocator.Get());
    }

    IFACEMETHODIMP SwipeMediaSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
    {
        MSV_RETURN_HR_IF(E_POINTER, inputStreamId == nullptr || usage == nullptr);
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        MSV_RETURN_HR_IF(MF_E_NOT_FOUND, outputStreamId != kStreamId);
        *inputStreamId = outputStreamId;
        *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
        return S_OK;
    }
}
