// Video stream of the Mouse Swipe Visualizer virtual camera.
// Architecture follows Microsoft's Windows-Camera VirtualCamera sample (SimpleMediaStream),
// Copyright (C) Microsoft Corporation, MIT License; re-implemented with WRL, frame pacing and an
// IPC-backed frame source.
#pragma once
#include "FrameLink.h"

namespace msv
{
    class SwipeMediaStream :
        public Microsoft::WRL::RuntimeClass<
            Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
            Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>>,
        private ModuleObjectCounter
    {
    public:
        SwipeMediaStream() = default;
        ~SwipeMediaStream();

        HRESULT RuntimeClassInitialize(IMFMediaSource* parent, DWORD streamId, std::shared_ptr<FrameLink> link);

        // IMFMediaEventGenerator
        IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* propValue) override;

        // IMFMediaStream
        IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
        IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
        IFACEMETHODIMP RequestSample(IUnknown* token) override;

        // IMFMediaStream2
        IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
        IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

        // Called by the source.
        HRESULT Start(IMFMediaType* mediaType);
        HRESULT Stop(bool sendEvent);
        HRESULT Shutdown();
        HRESULT SetSampleAllocator(IMFVideoSampleAllocator* allocator);
        HRESULT GetAttributes(IMFAttributes** attributes);
        DWORD Id() const { return m_id; }

    private:
        HRESULT CheckShutdownLocked() const;
        HRESULT StartLocked(bool sendEvent, IMFMediaType* mediaType);
        HRESULT StopLocked(bool sendEvent);
        HRESULT DeliverOneLocked();
        HRESULT WriteFrame(BYTE* scanline0, LONG pitch, DWORD bufferLength, FrameResult* result);
        void EnsurePacingThread();
        void PacingLoop();
        void ArmTimer(LONGLONG qpcDelta);

        std::mutex m_lock;
        Microsoft::WRL::ComPtr<IMFMediaSource> m_parent;
        Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_eventQueue;
        Microsoft::WRL::ComPtr<IMFAttributes> m_attributes;
        Microsoft::WRL::ComPtr<IMFStreamDescriptor> m_descriptor;
        Microsoft::WRL::ComPtr<IMFVideoSampleAllocator> m_allocator;
        Microsoft::WRL::ComPtr<IMFMediaType> m_allocatorType;
        std::shared_ptr<FrameLink> m_link;
        std::deque<Microsoft::WRL::ComPtr<IUnknown>> m_requests;

        DWORD m_id = 0;
        bool m_shutdown = false;
        bool m_ownAllocator = false;
        bool m_allocatorInitialized = false;
        bool m_discontinuity = true;
        MF_STREAM_STATE m_state = MF_STREAM_STATE_STOPPED;

        uint32_t m_width = 0;
        uint32_t m_height = 0;
        uint32_t m_fpsNumerator = 30;
        uint32_t m_fpsDenominator = 1;
        GUID m_subtype = GUID_NULL;
        LONGLONG m_sampleDuration = 333333;

        HANDLE m_stopEvent = nullptr;
        HANDLE m_requestEvent = nullptr;
        HANDLE m_timer = nullptr;
        std::thread m_thread;
        LONGLONG m_qpcFrequency = 0;
        LONGLONG m_intervalQpc = 0;
        LONGLONG m_nextDeadlineQpc = 0;
        LONGLONG m_lastConvertQpc = 0;

        // Last converted output (tight pitch). Repeated frames are copied from here instead of being
        // converted again, so an unchanged scene costs a memcpy per sample, not a colour conversion.
        std::vector<uint8_t> m_cache;
        bool m_cacheValid = false;
        bool m_cacheFallback = false;
        int64_t m_cacheSequence = -1;
        uint32_t m_cacheColor = 0;
        uint32_t m_cacheWidth = 0;
        uint32_t m_cacheHeight = 0;
        GUID m_cacheSubtype = GUID_NULL;
    };

    // Media types offered by the camera (the first one is the default).
    HRESULT CreateSupportedMediaTypes(std::vector<Microsoft::WRL::ComPtr<IMFMediaType>>& types);
}
