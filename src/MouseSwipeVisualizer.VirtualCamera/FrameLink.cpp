#include "pch.h"
#include "FrameLink.h"

namespace
{
    // SYSTEM, LOCAL SERVICE and Administrators: full access. Interactive users (the app): read/write.
    // Low integrity label with no-write-up so any integrity level of the interactive user can write.
    const wchar_t* const kMappingSddl = L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;IU)S:(ML;;NW;;;LW)";

    inline int64_t AtomicRead64(volatile int64_t* value)
    {
        // Full-barrier read: pairs with the producer's release writes of the seqlock and indices.
        return InterlockedCompareExchange64(value, 0, 0);
    }
}

namespace msv
{
    FrameLink::~FrameLink()
    {
        Close();
    }

    HRESULT FrameLink::Open()
    {
        if (m_header != nullptr)
        {
            return S_OK;
        }

        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(kMappingSddl, SDDL_REVISION_1, &descriptor, nullptr))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor, FALSE };
        const uint64_t size = MSV_MAPPING_SIZE;
        DWORD createError = ERROR_SUCCESS;

        // Global first (the Frame Server services may create it: SeCreateGlobalPrivilege), then the
        // session namespace (media source activated inside a normal user process, e.g. the self-test).
        // If the object already exists but CreateFileMapping may not open it (it asks for full access,
        // our DACL grants users only read/write), open it with read/write instead.
        for (const wchar_t* name : { MSV_SHARED_MEMORY_NAME, MSV_SHARED_MEMORY_NAME_LOCAL })
        {
            m_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE,
                static_cast<DWORD>(size >> 32), static_cast<DWORD>(size & 0xFFFFFFFF), name);
            createError = GetLastError();
            if (m_mapping == nullptr)
            {
                m_mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
                if (m_mapping != nullptr)
                {
                    createError = ERROR_ALREADY_EXISTS;
                }
            }

            if (m_mapping != nullptr)
            {
                break;
            }
        }

        LocalFree(descriptor);
        if (m_mapping == nullptr)
        {
            MsvTrace(L"[MSVCam] shared memory unavailable: %u", createError);
            return HRESULT_FROM_WIN32(createError);
        }
        m_view = static_cast<uint8_t*>(MapViewOfFile(m_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, static_cast<SIZE_T>(size)));
        if (m_view == nullptr)
        {
            const HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
            CloseHandle(m_mapping);
            m_mapping = nullptr;
            return hr;
        }

        m_header = reinterpret_cast<MSV_HEADER*>(m_view);

        // A mapping that already exists (the app keeps it open) keeps its producer fields. The
        // layout fields are (re)written unconditionally: they are constants of this protocol version.
        const bool existed = createError == ERROR_ALREADY_EXISTS;
        if (!existed || m_header->magic != MSV_PROTOCOL_MAGIC || m_header->version != MSV_PROTOCOL_VERSION)
        {
            memset(m_view, 0, MSV_HEADER_SIZE);
            m_header->latestSlot = -1;
        }

        m_header->headerSize = MSV_HEADER_SIZE;
        m_header->slotCount = MSV_SLOT_COUNT;
        m_header->slotCapacity = MSV_SLOT_CAPACITY;
        m_header->mappingSize = MSV_MAPPING_SIZE;
        m_header->version = MSV_PROTOCOL_VERSION;
        MemoryBarrier();
        m_header->magic = MSV_PROTOCOL_MAGIC;
        m_header->sourceProcessId = static_cast<int32_t>(GetCurrentProcessId());
        return S_OK;
    }

    void FrameLink::Close()
    {
        if (m_header != nullptr)
        {
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_header->consumerActive), 0);
        }

        if (m_view != nullptr)
        {
            UnmapViewOfFile(m_view);
            m_view = nullptr;
        }

        if (m_mapping != nullptr)
        {
            CloseHandle(m_mapping);
            m_mapping = nullptr;
        }

        m_header = nullptr;
    }

    void FrameLink::OnStreamStarted(uint32_t width, uint32_t height, uint32_t fpsNumerator, uint32_t fpsDenominator, uint32_t subtype)
    {
        if (m_header == nullptr)
        {
            return;
        }

        m_header->requestedWidth = width;
        m_header->requestedHeight = height;
        m_header->requestedFpsNumerator = fpsNumerator;
        m_header->requestedFpsDenominator = fpsDenominator;
        m_header->requestedSubtype = subtype;
        FILETIME now{};
        GetSystemTimeAsFileTime(&now);
        m_header->lastStartFileTimeUtc = (static_cast<int64_t>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
        m_header->streamStarts++;
        m_header->sourceProcessId = static_cast<int32_t>(GetCurrentProcessId());
        MemoryBarrier();
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_header->consumerActive), 1);
        InterlockedIncrement64(&m_header->consumerHeartbeat);
        m_lastSequence = -1;
    }

    void FrameLink::OnStreamStopped()
    {
        if (m_header != nullptr)
        {
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_header->consumerActive), 0);
        }
    }

    const MSV_SLOT* FrameLink::SlotAt(uint32_t index) const
    {
        return reinterpret_cast<const MSV_SLOT*>(m_view + MSV_SLOT_TABLE_OFFSET + static_cast<size_t>(index) * MSV_SLOT_PITCH);
    }

    bool FrameLink::ProducerAlive()
    {
        // The app increments the heartbeat at least every ~100 ms. Its value is meaningless across
        // processes; only whether it changed recently (by this process's clock) matters.
        const int64_t heartbeat = AtomicRead64(&m_header->producerHeartbeat);
        const ULONGLONG now = GetTickCount64();
        if (heartbeat != m_lastProducerHeartbeat)
        {
            m_lastProducerHeartbeat = heartbeat;
            m_lastHeartbeatChangeTick = now;
        }

        return heartbeat != 0 && m_lastHeartbeatChangeTick != 0 && now - m_lastHeartbeatChangeTick <= MSV_PRODUCER_TIMEOUT_MS;
    }
    bool FrameLink::TryCopySlot(uint32_t slotIndex, uint32_t width, uint32_t height, int64_t* frameSequence)
    {
        MSV_SLOT* slot = const_cast<MSV_SLOT*>(SlotAt(slotIndex));
        const int64_t before = AtomicRead64(&slot->seqLock);
        if ((before & 1) != 0)
        {
            return false; // being written
        }

        // Copy the descriptor, then validate everything against values computed here. The producer is
        // not trusted: any mismatch rejects the frame, so no read can leave the mapping.
        const uint32_t w = slot->width;
        const uint32_t h = slot->height;
        const uint32_t stride = slot->stride;
        const uint32_t format = slot->pixelFormat;
        const uint64_t offset = slot->dataOffset;
        const uint64_t size = slot->dataSize;
        const int64_t sequence = slot->frameSequence;
        const uint64_t expectedOffset = MSV_HEADER_SIZE + static_cast<uint64_t>(slotIndex) * MSV_SLOT_CAPACITY;
        if (w != width || h != height || w == 0 || h == 0 || w > MSV_MAX_WIDTH || h > MSV_MAX_HEIGHT ||
            stride != w * 4u || format != MSV_PIXEL_FORMAT_BGRA32 ||
            offset != expectedOffset || size != static_cast<uint64_t>(stride) * h || size > MSV_SLOT_CAPACITY)
        {
            return false;
        }

        const size_t pixelCount = static_cast<size_t>(w) * h;
        if (m_frame.size() < pixelCount)
        {
            m_frame.resize(pixelCount);
        }

        memcpy(m_frame.data(), m_view + offset, static_cast<size_t>(size));
        MemoryBarrier();
        const int64_t after = AtomicRead64(&slot->seqLock);
        if (after != before)
        {
            return false; // overwritten while copying: discard (would be torn)
        }

        m_frameWidth = w;
        m_frameHeight = h;
        *frameSequence = sequence;
        return true;
    }

    FrameResult FrameLink::GetFrame(uint32_t width, uint32_t height, const uint32_t** pixels, uint32_t* fallbackColor)
    {
        *pixels = nullptr;
        *fallbackColor = 0xFF00FF00u; // chroma green until the app says otherwise
        if (m_header == nullptr)
        {
            return FrameResult::Fallback;
        }

        const uint32_t background = m_header->backgroundColor;
        if (background != 0)
        {
            *fallbackColor = background | 0xFF000000u;
        }

        if (!ProducerAlive())
        {
            return FrameResult::Fallback;
        }

        const bool haveMatchingCopy = m_lastSequence >= 0 && m_frameWidth == width && m_frameHeight == height;
        const int64_t latestSequence = AtomicRead64(&m_header->latestSequence);
        if (haveMatchingCopy && latestSequence == m_lastSequence)
        {
            *pixels = m_frame.data();
            return FrameResult::Repeated;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            const LONG slot = InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(&m_header->latestSlot), 0, 0);
            if (slot < 0 || static_cast<uint32_t>(slot) >= MSV_SLOT_COUNT)
            {
                break;
            }

            int64_t sequence = 0;
            if (TryCopySlot(static_cast<uint32_t>(slot), width, height, &sequence))
            {
                if (m_lastSequence >= 0 && sequence > m_lastSequence + 1)
                {
                    m_header->framesDroppedByProducer += sequence - m_lastSequence - 1;
                }

                const bool repeated = sequence == m_lastSequence;
                m_lastSequence = sequence;
                *pixels = m_frame.data();
                return repeated ? FrameResult::Repeated : FrameResult::Fresh;
            }
        }

        if (haveMatchingCopy)
        {
            *pixels = m_frame.data();
            return FrameResult::Repeated;
        }

        return FrameResult::Fallback;
    }

    void FrameLink::RecordDelivered(FrameResult result, uint32_t convertMicros, uint32_t deliverMicros)
    {
        if (m_header == nullptr)
        {
            return;
        }

        // Exponential moving averages (1/16) for the diagnostics view.
        m_header->convertMicrosAverage = m_header->convertMicrosAverage == 0 ? convertMicros : (m_header->convertMicrosAverage * 15 + convertMicros) / 16;
        m_header->deliverMicrosAverage = m_header->deliverMicrosAverage == 0 ? deliverMicros : (m_header->deliverMicrosAverage * 15 + deliverMicros) / 16;

        m_header->framesDelivered++;
        if (result == FrameResult::Repeated)
        {
            m_header->framesRepeated++;
        }
        else if (result == FrameResult::Fallback)
        {
            m_header->framesFallback++;
        }

        InterlockedIncrement64(&m_header->consumerHeartbeat);
    }

    void FrameLink::SetLastError(HRESULT hr)
    {
        if (m_header != nullptr)
        {
            m_header->lastErrorHr = hr;
        }
    }
}
