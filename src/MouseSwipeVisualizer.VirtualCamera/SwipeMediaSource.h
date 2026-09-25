// Media source of the Mouse Swipe Visualizer virtual camera (one synthetic video stream).
// Architecture follows Microsoft's Windows-Camera VirtualCamera sample (SimpleMediaSource),
// Copyright (C) Microsoft Corporation, MIT License.
#pragma once
#include "SwipeMediaStream.h"

namespace msv
{
    class SwipeMediaSource :
        public Microsoft::WRL::RuntimeClass<
            Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
            Microsoft::WRL::ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
            IMFGetService,
            IKsControl,
            IMFSampleAllocatorControl>,
        private ModuleObjectCounter
    {
    public:
        SwipeMediaSource() = default;
        ~SwipeMediaSource();

        HRESULT RuntimeClassInitialize(IMFAttributes* activateAttributes);

        // IMFMediaEventGenerator
        IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* propValue) override;

        // IMFMediaSource
        IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
        IFACEMETHODIMP GetCharacteristics(DWORD* characteristics) override;
        IFACEMETHODIMP Pause() override;
        IFACEMETHODIMP Shutdown() override;
        IFACEMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
        IFACEMETHODIMP Stop() override;

        // IMFMediaSourceEx
        IFACEMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
        IFACEMETHODIMP GetStreamAttributes(DWORD streamIdentifier, IMFAttributes** attributes) override;
        IFACEMETHODIMP SetD3DManager(IUnknown* manager) override;

        // IMFGetService
        IFACEMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

        // IKsControl (no custom controls; mimic a driver without handlers)
        IFACEMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
        IFACEMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;
        IFACEMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* bytesReturned) override;

        // IMFSampleAllocatorControl
        IFACEMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
        IFACEMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

    private:
        enum class State { Invalid, Stopped, Started, Shutdown };

        HRESULT CheckShutdownLocked() const;
        HRESULT CreateSourceAttributes(IMFAttributes* activateAttributes);

        std::mutex m_lock;
        State m_state = State::Invalid;
        Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_eventQueue;
        Microsoft::WRL::ComPtr<IMFPresentationDescriptor> m_presentationDescriptor;
        Microsoft::WRL::ComPtr<IMFAttributes> m_attributes;
        Microsoft::WRL::ComPtr<SwipeMediaStream> m_stream;
        std::shared_ptr<FrameLink> m_link;
    };
}
