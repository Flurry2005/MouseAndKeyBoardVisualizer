// Portions of the stream state handling follow Microsoft's Windows-Camera VirtualCamera sample
// (SimpleMediaStream.cpp), Copyright (C) Microsoft Corporation, MIT License.
#include "pch.h"
#include "SwipeMediaStream.h"
#include "PixelConvert.h"

using Microsoft::WRL::ComPtr;

namespace msv
{
    namespace
    {
        constexpr size_t kMaxPendingRequests = 64;
        constexpr DWORD kAllocatorSampleCount = 10;
        constexpr DWORD kWatchdogMs = 100;

        struct FormatSpec
        {
            uint32_t width;
            uint32_t height;
            uint32_t fps;
        };

        // Order matters: the first NV12 type is the default. 1280x720@30 is the most widely
        // accepted webcam mode; 800x800 and 60 fps are offered for consumers that can use them.
        constexpr FormatSpec kFormats[] = {
            { 1280, 720, 30 },
            { 1280, 720, 60 },
            { 800, 800, 60 },
            { 800, 800, 30 },
            { 640, 480, 30 },
        };

        HRESULT CreateVideoType(const GUID& subtype, uint32_t width, uint32_t height, uint32_t fps, IMFMediaType** result)
        {
            ComPtr<IMFMediaType> type;
            MSV_RETURN_IF_FAILED(MFCreateMediaType(&type));
            const bool nv12 = subtype == MFVideoFormat_NV12;
            const UINT32 stride = nv12 ? width : width * 4;
            const UINT32 sampleSize = nv12 ? width * height * 3 / 2 : width * height * 4;
            MSV_RETURN_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            MSV_RETURN_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, subtype));
            MSV_RETURN_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            MSV_RETURN_IF_FAILED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
            MSV_RETURN_IF_FAILED(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
            MSV_RETURN_IF_FAILED(type->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize));
            MSV_RETURN_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, stride));
            MSV_RETURN_IF_FAILED(type->SetUINT32(MF_MT_AVG_BITRATE, sampleSize * 8 * fps));
            MSV_RETURN_IF_FAILED(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height));
            MSV_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, fps, 1));
            MSV_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            *result = type.Detach();
            return S_OK;
        }

        LONGLONG QpcNow()
        {
            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            return now.QuadPart;
        }
    }

    HRESULT CreateSupportedMediaTypes(std::vector<ComPtr<IMFMediaType>>& types)
    {
        types.clear();
        for (const GUID* subtype : { &MFVideoFormat_NV12, &MFVideoFormat_RGB32 })
        {
            for (const FormatSpec& format : kFormats)
            {
                ComPtr<IMFMediaType> type;
                MSV_RETURN_IF_FAILED(CreateVideoType(*subtype, format.width, format.height, format.fps, &type));
                types.push_back(type);
            }
        }

        return S_OK;
    }

    SwipeMediaStream::~SwipeMediaStream()
    {
        Shutdown();
        if (m_stopEvent) CloseHandle(m_stopEvent);
        if (m_requestEvent) CloseHandle(m_requestEvent);
        if (m_timer) CloseHandle(m_timer);
    }

    HRESULT SwipeMediaStream::RuntimeClassInitialize(IMFMediaSource* parent, DWORD streamId, std::shared_ptr<FrameLink> link)
    {
        MSV_RETURN_HR_IF_NULL(E_INVALIDARG, parent);
        m_parent = parent;
        m_id = streamId;
        m_link = std::move(link);

        LARGE_INTEGER frequency;
        QueryPerformanceFrequency(&frequency);
        m_qpcFrequency = frequency.QuadPart;

        m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        m_requestEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        m_timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (m_timer == nullptr)
        {
            m_timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
        }

        MSV_RETURN_HR_IF(E_OUTOFMEMORY, m_stopEvent == nullptr || m_requestEvent == nullptr || m_timer == nullptr);

        std::vector<ComPtr<IMFMediaType>> types;
        MSV_RETURN_IF_FAILED(CreateSupportedMediaTypes(types));
        std::vector<IMFMediaType*> raw;
        for (auto& type : types)
        {
            raw.push_back(type.Get());
        }

        MSV_RETURN_IF_FAILED(MFCreateEventQueue(&m_eventQueue));
        MSV_RETURN_IF_FAILED(MFCreateStreamDescriptor(m_id, static_cast<DWORD>(raw.size()), raw.data(), &m_descriptor));

        ComPtr<IMFMediaTypeHandler> handler;
        MSV_RETURN_IF_FAILED(m_descriptor->GetMediaTypeHandler(&handler));
        MSV_RETURN_IF_FAILED(handler->SetCurrentMediaType(raw[0]));

        // Required attributes for a Frame Server camera stream (same as the Microsoft sample).
        for (IMFAttributes* store : { static_cast<IMFAttributes*>(m_descriptor.Get()), static_cast<IMFAttributes*>(nullptr) })
        {
            if (store == nullptr)
            {
                MSV_RETURN_IF_FAILED(MFCreateAttributes(&m_attributes, 4));
                store = m_attributes.Get();
            }

            MSV_RETURN_IF_FAILED(store->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
            MSV_RETURN_IF_FAILED(store->SetUINT32(MF_DEVICESTREAM_STREAM_ID, m_id));
            MSV_RETURN_IF_FAILED(store->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
            MSV_RETURN_IF_FAILED(store->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
        }

        return S_OK;
    }

    HRESULT SwipeMediaStream::CheckShutdownLocked() const
    {
        if (m_shutdown)
        {
            return MF_E_SHUTDOWN;
        }

        return m_eventQueue ? S_OK : E_UNEXPECTED;
    }

    // ---------------------------------------------------------------- IMFMediaEventGenerator

    IFACEMETHODIMP SwipeMediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_eventQueue->BeginGetEvent(callback, state);
    }

    IFACEMETHODIMP SwipeMediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_eventQueue->EndGetEvent(result, event);
    }

    IFACEMETHODIMP SwipeMediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        // GetEvent may block indefinitely, so it must not hold the lock.
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard lock(m_lock);
            MSV_RETURN_IF_FAILED(CheckShutdownLocked());
            queue = m_eventQueue;
        }

        return queue->GetEvent(flags, event);
    }

    IFACEMETHODIMP SwipeMediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* propValue)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_eventQueue->QueueEventParamVar(type, extendedType, status, propValue);
    }

    // ---------------------------------------------------------------- IMFMediaStream

    IFACEMETHODIMP SwipeMediaStream::GetMediaSource(IMFMediaSource** source)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, source);
        *source = nullptr;
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_parent.CopyTo(source);
    }

    IFACEMETHODIMP SwipeMediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, descriptor);
        *descriptor = nullptr;
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_descriptor.CopyTo(descriptor);
    }

    IFACEMETHODIMP SwipeMediaStream::RequestSample(IUnknown* token)
    {
        {
            std::lock_guard lock(m_lock);
            MSV_RETURN_IF_FAILED(CheckShutdownLocked());
            if (m_state != MF_STREAM_STATE_RUNNING)
            {
                return MF_E_INVALIDREQUEST;
            }

            if (m_requests.size() >= kMaxPendingRequests)
            {
                return MF_E_NOTACCEPTING;
            }

            m_requests.emplace_back(token);
        }

        // Delivery happens on the pacing thread at the negotiated frame rate, not synchronously:
        // otherwise the frame rate would be whatever rate the consumer happens to ask at.
        SetEvent(m_requestEvent);
        return S_OK;
    }

    // ---------------------------------------------------------------- IMFMediaStream2

    IFACEMETHODIMP SwipeMediaStream::SetStreamState(MF_STREAM_STATE state)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        if (m_state == state)
        {
            return S_OK;
        }

        switch (state)
        {
        case MF_STREAM_STATE_PAUSED:
            // Paused: keep configuration, reject sample requests (see sample README, 07/04/2022).
            MSV_RETURN_HR_IF(MF_E_INVALID_STATE_TRANSITION, m_state != MF_STREAM_STATE_RUNNING);
            m_state = MF_STREAM_STATE_PAUSED;
            m_requests.clear();
            return S_OK;
        case MF_STREAM_STATE_RUNNING:
            // Running via SetStreamState does not send MEStreamStarted.
            return StartLocked(false, nullptr);
        case MF_STREAM_STATE_STOPPED:
            return StopLocked(false);
        default:
            return MF_E_INVALID_STATE_TRANSITION;
        }
    }

    IFACEMETHODIMP SwipeMediaStream::GetStreamState(MF_STREAM_STATE* state)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, state);
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        *state = m_state;
        return S_OK;
    }

    // ---------------------------------------------------------------- control from the source

    HRESULT SwipeMediaStream::Start(IMFMediaType* mediaType)
    {
        MSV_RETURN_HR_IF_NULL(E_INVALIDARG, mediaType);
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return StartLocked(true, mediaType);
    }

    HRESULT SwipeMediaStream::Stop(bool sendEvent)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return StopLocked(sendEvent);
    }

    HRESULT SwipeMediaStream::StartLocked(bool sendEvent, IMFMediaType* mediaType)
    {
        if (mediaType == nullptr)
        {
            mediaType = m_allocatorType.Get();
        }

        if (mediaType == nullptr)
        {
            ComPtr<IMFMediaTypeHandler> handler;
            ComPtr<IMFMediaType> current;
            MSV_RETURN_IF_FAILED(m_descriptor->GetMediaTypeHandler(&handler));
            MSV_RETURN_IF_FAILED(handler->GetCurrentMediaType(&current));
            m_allocatorType.Reset();
            return StartLocked(sendEvent, current.Get());
        }

        UINT32 width = 0, height = 0, fpsNumerator = 0, fpsDenominator = 0;
        GUID subtype = GUID_NULL;
        MSV_RETURN_IF_FAILED(mediaType->GetGUID(MF_MT_SUBTYPE, &subtype));
        MSV_RETURN_IF_FAILED(MFGetAttributeSize(mediaType, MF_MT_FRAME_SIZE, &width, &height));
        MSV_RETURN_IF_FAILED(MFGetAttributeRatio(mediaType, MF_MT_FRAME_RATE, &fpsNumerator, &fpsDenominator));
        MSV_RETURN_HR_IF(MF_E_INVALIDMEDIATYPE, subtype != MFVideoFormat_NV12 && subtype != MFVideoFormat_RGB32);
        MSV_RETURN_HR_IF(MF_E_INVALIDMEDIATYPE, width == 0 || height == 0 || width > MSV_MAX_WIDTH || height > MSV_MAX_HEIGHT ||
            (width & 1) != 0 || (height & 1) != 0 || fpsNumerator == 0 || fpsDenominator == 0);

        if (!m_allocator)
        {
            // The Frame Server normally provides an allocator (IMFSampleAllocatorControl); fall back to
            // our own so the source also works when activated directly.
            MSV_RETURN_IF_FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&m_allocator)));
            m_ownAllocator = true;
            m_allocatorInitialized = false;
        }

        BOOL sameType = FALSE;
        if (m_allocatorType)
        {
            (void)m_allocatorType->Compare(mediaType, MF_ATTRIBUTES_MATCH_ALL_ITEMS, &sameType);
        }

        if (!m_allocatorInitialized || !sameType)
        {
            if (m_allocatorInitialized)
            {
                m_allocator->UninitializeSampleAllocator();
            }

            MSV_RETURN_IF_FAILED(m_allocator->InitializeSampleAllocator(kAllocatorSampleCount, mediaType));
            m_allocatorInitialized = true;
            m_allocatorType = mediaType;
        }

        m_width = width;
        m_height = height;
        m_fpsNumerator = fpsNumerator;
        m_fpsDenominator = fpsDenominator;
        m_subtype = subtype;
        m_sampleDuration = static_cast<LONGLONG>(10'000'000ull * fpsDenominator / fpsNumerator);
        m_intervalQpc = static_cast<LONGLONG>(static_cast<unsigned long long>(m_qpcFrequency) * fpsDenominator / fpsNumerator);
        m_nextDeadlineQpc = 0;
        m_discontinuity = true;

        if (m_link)
        {
            if (FAILED(m_link->Open()))
            {
                MsvTrace(L"[MSVCam] shared memory unavailable; streaming fallback frames");
            }

            m_link->OnStreamStarted(width, height, fpsNumerator, fpsDenominator,
                subtype == MFVideoFormat_NV12 ? MSV_SUBTYPE_NV12 : MSV_SUBTYPE_RGB32);
        }

        EnsurePacingThread();
        if (sendEvent)
        {
            MSV_RETURN_IF_FAILED(m_eventQueue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
        }

        m_state = MF_STREAM_STATE_RUNNING;
        MsvTrace(L"[MSVCam] stream started %ux%u @ %u/%u %s", width, height, fpsNumerator, fpsDenominator,
            subtype == MFVideoFormat_NV12 ? L"NV12" : L"RGB32");
        return S_OK;
    }

    HRESULT SwipeMediaStream::StopLocked(bool sendEvent)
    {
        m_state = MF_STREAM_STATE_STOPPED;
        m_requests.clear(); // pending requests are discarded on stop (Media Foundation contract)
        if (m_link)
        {
            m_link->OnStreamStopped();
        }

        if (sendEvent)
        {
            MSV_RETURN_IF_FAILED(m_eventQueue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
        }

        return S_OK;
    }

    HRESULT SwipeMediaStream::Shutdown()
    {
        {
            std::lock_guard lock(m_lock);
            if (m_shutdown)
            {
                return S_OK;
            }

            m_shutdown = true;
            m_state = MF_STREAM_STATE_STOPPED;
            m_requests.clear();
            if (m_link)
            {
                m_link->OnStreamStopped();
            }

            if (m_eventQueue)
            {
                m_eventQueue->Shutdown();
            }
        }

        // Join outside the lock: the pacing thread takes it.
        if (m_stopEvent)
        {
            SetEvent(m_stopEvent);
        }

        if (m_thread.joinable())
        {
            m_thread.join();
        }

        std::lock_guard lock(m_lock);
        if (m_ownAllocator && m_allocatorInitialized && m_allocator)
        {
            m_allocator->UninitializeSampleAllocator();
        }

        m_allocator.Reset();
        m_allocatorType.Reset();
        m_allocatorInitialized = false;
        m_eventQueue.Reset();
        m_parent.Reset();
        m_link.reset();
        return S_OK;
    }

    HRESULT SwipeMediaStream::SetSampleAllocator(IMFVideoSampleAllocator* allocator)
    {
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        MSV_RETURN_HR_IF(MF_E_INVALIDREQUEST, m_state == MF_STREAM_STATE_RUNNING);
        if (m_ownAllocator && m_allocatorInitialized && m_allocator)
        {
            m_allocator->UninitializeSampleAllocator();
        }

        m_allocator = allocator;
        m_ownAllocator = false;
        m_allocatorInitialized = false;
        m_allocatorType.Reset();
        return S_OK;
    }

    HRESULT SwipeMediaStream::GetAttributes(IMFAttributes** attributes)
    {
        MSV_RETURN_HR_IF_NULL(E_POINTER, attributes);
        std::lock_guard lock(m_lock);
        MSV_RETURN_IF_FAILED(CheckShutdownLocked());
        return m_attributes.CopyTo(attributes);
    }

    // ---------------------------------------------------------------- frame pacing + delivery

    void SwipeMediaStream::EnsurePacingThread()
    {
        if (!m_thread.joinable())
        {
            m_thread = std::thread([this] { PacingLoop(); });
        }
    }

    void SwipeMediaStream::ArmTimer(LONGLONG qpcDelta)
    {
        LONGLONG due = -static_cast<LONGLONG>(static_cast<double>(qpcDelta) * 10'000'000.0 / static_cast<double>(m_qpcFrequency));
        if (due > -1)
        {
            due = -1;
        }

        LARGE_INTEGER dueTime;
        dueTime.QuadPart = due;
        SetWaitableTimer(m_timer, &dueTime, 0, nullptr, nullptr, FALSE);
    }

    void SwipeMediaStream::PacingLoop()
    {
        // High priority is justified: the thread sleeps almost all the time and only does a
        // few milliseconds of work per frame, but lateness is visible as judder.
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        HANDLE handles[3] = { m_stopEvent, m_requestEvent, m_timer };
        for (;;)
        {
            const DWORD wait = WaitForMultipleObjects(3, handles, FALSE, kWatchdogMs);
            if (wait == WAIT_OBJECT_0)
            {
                return;
            }

            std::lock_guard lock(m_lock);
            if (m_shutdown)
            {
                return;
            }

            if (m_state != MF_STREAM_STATE_RUNNING || m_requests.empty())
            {
                continue;
            }

            const LONGLONG now = QpcNow();
            if (m_nextDeadlineQpc != 0 && now < m_nextDeadlineQpc)
            {
                ArmTimer(m_nextDeadlineQpc - now);
                continue;
            }

            const HRESULT hr = DeliverOneLocked();
            if (FAILED(hr) && hr != MF_E_SAMPLEALLOCATOR_EMPTY)
            {
                MsvTrace(L"[MSVCam] sample delivery failed: 0x%08X", hr);
                if (m_link)
                {
                    m_link->SetLastError(hr);
                }

                if (m_eventQueue)
                {
                    m_eventQueue->QueueEventParamVar(MEError, GUID_NULL, hr, nullptr);
                }

                m_requests.clear();
            }

            // Absolute schedule; if we fell behind (e.g. the consumer paused requesting), restart it.
            if (m_nextDeadlineQpc == 0 || now - m_nextDeadlineQpc > m_intervalQpc)
            {
                m_nextDeadlineQpc = now + m_intervalQpc;
            }
            else
            {
                m_nextDeadlineQpc += m_intervalQpc;
            }

            if (!m_requests.empty())
            {
                ArmTimer(m_nextDeadlineQpc - QpcNow());
            }
        }
    }

    HRESULT SwipeMediaStream::WriteFrame(BYTE* scanline0, LONG pitch, DWORD bufferLength, FrameResult* result)
    {
        const bool nv12 = m_subtype == MFVideoFormat_NV12;
        const uint64_t absPitch = static_cast<uint64_t>(pitch < 0 ? -static_cast<int64_t>(pitch) : pitch);
        const uint64_t minPitch = nv12 ? m_width : static_cast<uint64_t>(m_width) * 4;
        const uint64_t rows = nv12 ? static_cast<uint64_t>(m_height) * 3 / 2 : m_height;
        // Never write past the locked buffer, whatever the allocator hands us.
        MSV_RETURN_HR_IF(MF_E_BUFFERTOOSMALL, absPitch < minPitch || (bufferLength != 0 && absPitch * rows > bufferLength));
        MSV_RETURN_HR_IF(E_UNEXPECTED, nv12 && pitch < 0);

        const uint32_t* pixels = nullptr;
        uint32_t fallbackColor = 0xFF00FF00u;
        *result = m_link ? m_link->GetFrame(m_width, m_height, &pixels, &fallbackColor) : FrameResult::Fallback;
        if (pixels == nullptr)
        {
            *result = FrameResult::Fallback;
        }

        const bool fallback = *result == FrameResult::Fallback;
        const int64_t sequence = (!fallback && m_link) ? m_link->LastSequence() : -1;
        const size_t tightPitch = static_cast<size_t>(minPitch);
        const bool cacheHit = m_cacheValid && m_cacheWidth == m_width && m_cacheHeight == m_height && m_cacheSubtype == m_subtype &&
            (fallback ? (m_cacheFallback && m_cacheColor == fallbackColor) : (!m_cacheFallback && m_cacheSequence == sequence));

        m_lastConvertQpc = 0;
        if (!cacheHit)
        {
            const LONGLONG convertStart = QpcNow();
            m_cache.resize(tightPitch * static_cast<size_t>(rows));
            const LONG cachePitch = static_cast<LONG>(tightPitch);
            if (fallback)
            {
                if (nv12) FillNv12(fallbackColor, m_width, m_height, m_cache.data(), cachePitch);
                else FillRgb32(fallbackColor, m_width, m_height, m_cache.data(), cachePitch);
            }
            else
            {
                if (nv12) ConvertBgraToNv12(pixels, m_width, m_height, m_cache.data(), cachePitch);
                else CopyBgraToRgb32(pixels, m_width, m_height, m_cache.data(), cachePitch);
            }

            m_cacheValid = true;
            m_cacheFallback = fallback;
            m_cacheColor = fallbackColor;
            m_cacheSequence = sequence;
            m_cacheWidth = m_width;
            m_cacheHeight = m_height;
            m_cacheSubtype = m_subtype;
            m_lastConvertQpc = QpcNow() - convertStart;
        }

        // Row copy honours the destination pitch (padding, or negative for bottom-up RGB32). For NV12
        // the UV plane follows the Y plane at scanline0 + pitch * height, i.e. row "height" onwards.
        for (uint64_t row = 0; row < rows; row++)
        {
            memcpy(scanline0 + static_cast<ptrdiff_t>(pitch) * static_cast<ptrdiff_t>(row), m_cache.data() + tightPitch * row, tightPitch);
        }

        return S_OK;
    }
    HRESULT SwipeMediaStream::DeliverOneLocked()
    {
        const LONGLONG deliverStart = QpcNow();
        ComPtr<IMFSample> sample;
        MSV_RETURN_IF_FAILED(m_allocator->AllocateSample(&sample));
        ComPtr<IMFMediaBuffer> buffer;
        MSV_RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));

        FrameResult result = FrameResult::Fallback;
        ComPtr<IMF2DBuffer2> buffer2D2;
        ComPtr<IMF2DBuffer> buffer2D;
        if (SUCCEEDED(buffer.As(&buffer2D2)))
        {
            BYTE* scanline0 = nullptr;
            BYTE* start = nullptr;
            LONG pitch = 0;
            DWORD length = 0;
            MSV_RETURN_IF_FAILED(buffer2D2->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline0, &pitch, &start, &length));
            const HRESULT hr = WriteFrame(scanline0, pitch, length, &result);
            buffer2D2->Unlock2D();
            MSV_RETURN_IF_FAILED(hr);
        }
        else if (SUCCEEDED(buffer.As(&buffer2D)))
        {
            BYTE* scanline0 = nullptr;
            LONG pitch = 0;
            MSV_RETURN_IF_FAILED(buffer2D->Lock2D(&scanline0, &pitch));
            DWORD contiguous = 0;
            buffer2D->GetContiguousLength(&contiguous);
            const HRESULT hr = WriteFrame(scanline0, pitch, contiguous, &result);
            buffer2D->Unlock2D();
            MSV_RETURN_IF_FAILED(hr);
        }
        else
        {
            BYTE* data = nullptr;
            DWORD maxLength = 0;
            MSV_RETURN_IF_FAILED(buffer->Lock(&data, &maxLength, nullptr));
            const bool nv12 = m_subtype == MFVideoFormat_NV12;
            const LONG pitch = static_cast<LONG>(nv12 ? m_width : m_width * 4);
            const HRESULT hr = WriteFrame(data, pitch, maxLength, &result);
            buffer->Unlock();
            MSV_RETURN_IF_FAILED(hr);
            MSV_RETURN_IF_FAILED(buffer->SetCurrentLength(nv12 ? m_width * m_height * 3 / 2 : m_width * m_height * 4));
        }

        MSV_RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
        MSV_RETURN_IF_FAILED(sample->SetSampleDuration(m_sampleDuration));
        if (m_discontinuity)
        {
            MSV_RETURN_IF_FAILED(sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE));
            m_discontinuity = false;
        }

        ComPtr<IUnknown> token = m_requests.front();
        m_requests.pop_front();
        if (token)
        {
            MSV_RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token.Get()));
        }

        MSV_RETURN_IF_FAILED(m_eventQueue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get()));
        if (m_link)
        {
            const auto micros = [this](LONGLONG qpc) { return static_cast<uint32_t>(qpc * 1'000'000 / m_qpcFrequency); };
            m_link->RecordDelivered(result, micros(m_lastConvertQpc), micros(QpcNow() - deliverStart));
        }

        return S_OK;
    }
}
