// Camera side of the shared-memory frame transport (see SharedFrameProtocol.h).
#pragma once
#include "..\MouseSwipeVisualizer.Shared\SharedFrameProtocol.h"

namespace msv
{
    enum class FrameResult
    {
        Fresh,      // a new frame from the app
        Repeated,   // the app is alive but has not produced a newer frame
        Fallback,   // the app is not running / no valid frame: solid background
    };

    class FrameLink
    {
    public:
        FrameLink() = default;
        ~FrameLink();
        FrameLink(const FrameLink&) = delete;
        FrameLink& operator=(const FrameLink&) = delete;

        // Creates or opens the Global mapping. Failure is not fatal: the camera then streams fallback frames.
        HRESULT Open();
        void Close();
        bool IsOpen() const { return m_header != nullptr; }

        void OnStreamStarted(uint32_t width, uint32_t height, uint32_t fpsNumerator, uint32_t fpsDenominator, uint32_t subtype);
        void OnStreamStopped();

        // Returns the frame to deliver. For Fresh/Repeated *pixels points to width*height BGRA pixels
        // owned by this object (valid until the next call); for Fallback use *fallbackColor.
        FrameResult GetFrame(uint32_t width, uint32_t height, const uint32_t** pixels, uint32_t* fallbackColor);

        void RecordDelivered(FrameResult result, uint32_t convertMicros, uint32_t deliverMicros);
        void SetLastError(HRESULT hr);

        // Sequence of the frame last returned as Fresh/Repeated (keys the stream's conversion cache).
        int64_t LastSequence() const { return m_lastSequence; }

    private:
        const MSV_SLOT* SlotAt(uint32_t index) const;
        bool TryCopySlot(uint32_t slotIndex, uint32_t width, uint32_t height, int64_t* frameSequence);
        bool ProducerAlive();

        HANDLE m_mapping = nullptr;
        uint8_t* m_view = nullptr;
        MSV_HEADER* m_header = nullptr;

        std::vector<uint32_t> m_frame;      // private, consistent copy of the last good frame
        uint32_t m_frameWidth = 0;
        uint32_t m_frameHeight = 0;
        int64_t m_lastSequence = -1;
        int64_t m_lastProducerHeartbeat = 0;
        ULONGLONG m_lastHeartbeatChangeTick = 0;
    };
}
