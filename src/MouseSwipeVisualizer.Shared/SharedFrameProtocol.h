// SharedFrameProtocol.h - frame transport between MouseSwipeVisualizer.exe (producer) and the
// virtual camera media source (consumer, loaded by the Windows Camera Frame Server service).
//
// Must stay byte-identical with SharedFrameProtocol.cs. Bump MSV_PROTOCOL_VERSION on any change.
//
// Transport: one named file mapping in the Global namespace. The media source runs in session 0
// under LOCAL SERVICE / LOCAL SYSTEM, so a Local\ object of the user's session would be invisible to
// it. Normal user processes may not *create* Global\ objects (SeCreateGlobalPrivilege), therefore the
// media source creates the mapping (with a DACL granting interactive users read/write) when a
// consumer starts streaming, and the app opens it. The mapping appearing is also the app's
// "a camera consumer is active" signal.
//
// Tearing protection: 3 frame slots, each guarded by a sequence lock (seqlock). The producer writes
// only to a slot that is not the published one: seq odd -> write -> seq even -> publish slot index.
// The consumer copies a slot and accepts the copy only if the seq was even and unchanged before and
// after the copy. No mutex; a reader can never observe half a frame.
//
// Everything read from the mapping is untrusted by both sides and validated before use.
#pragma once
#include <stdint.h>
#include <stddef.h>

#define MSV_SHARED_MEMORY_NAME L"Global\\MouseSwipeVisualizerCamera.Frames.v1"
// Fallback when the creator lacks SeCreateGlobalPrivilege (media source activated inside a normal
// user process, e.g. the self-test). The Frame Server services always use the Global name.
#define MSV_SHARED_MEMORY_NAME_LOCAL L"Local\\MouseSwipeVisualizerCamera.Frames.v1"

#define MSV_PROTOCOL_MAGIC   0x4356534Du   // 'MSVC'
#define MSV_PROTOCOL_VERSION 1u

#define MSV_HEADER_SIZE      4096u
#define MSV_SLOT_COUNT       3u
#define MSV_MAX_WIDTH        1920u
#define MSV_MAX_HEIGHT       1080u
#define MSV_SLOT_CAPACITY    (8u * 1024u * 1024u)   // >= 1920*1080*4
#define MSV_MAPPING_SIZE     (MSV_HEADER_SIZE + MSV_SLOT_COUNT * MSV_SLOT_CAPACITY)

#define MSV_PIXEL_FORMAT_BGRA32 1u

#define MSV_SUBTYPE_NV12  1u
#define MSV_SUBTYPE_RGB32 2u

// Producer heartbeat older than this (as seen by the consumer's clock) => app not running.
#define MSV_PRODUCER_TIMEOUT_MS 1000u

#pragma pack(push, 8)

typedef struct MSV_SLOT
{
    volatile int64_t seqLock;       // even = stable, odd = being written
    int64_t  frameSequence;         // producer frame counter
    uint32_t width;
    uint32_t height;
    uint32_t stride;                // bytes per row
    uint32_t pixelFormat;           // MSV_PIXEL_FORMAT_BGRA32
    int64_t  producerQpc;           // QueryPerformanceCounter at render time (diagnostics)
    uint64_t dataOffset;            // from the start of the mapping
    uint64_t dataSize;
} MSV_SLOT;                         // 56 bytes, slots start at offset 256 with a 64-byte pitch

typedef struct MSV_HEADER
{
    // ---- layout (written by the media source when it creates the mapping)
    uint32_t magic;                 // 0
    uint32_t version;               // 4
    uint32_t headerSize;            // 8
    uint32_t slotCount;             // 12
    uint64_t slotCapacity;          // 16
    uint64_t mappingSize;           // 24

    // ---- consumer side (media source)
    volatile int32_t consumerActive;          // 32  1 while a stream is started
    uint32_t requestedWidth;                  // 36
    uint32_t requestedHeight;                 // 40
    uint32_t requestedFpsNumerator;           // 44
    uint32_t requestedFpsDenominator;         // 48
    uint32_t requestedSubtype;                // 52  MSV_SUBTYPE_*
    volatile int64_t consumerHeartbeat;       // 56  +1 per delivered sample
    int64_t  framesDelivered;                 // 64
    int64_t  framesRepeated;                  // 72  delivered with the same content as before
    int64_t  framesFallback;                  // 80  delivered while the app was not producing
    int64_t  lastStartFileTimeUtc;            // 88
    int64_t  streamStarts;                    // 96
    int32_t  sourceProcessId;                 // 104
    int32_t  lastErrorHr;                     // 108
    int64_t  framesDroppedByProducer;         // 112 producer frames never delivered (skipped)
    uint32_t convertMicrosAverage;            // 120 BGRA->NV12/RGB32 conversion time, moving average (us)
    uint32_t deliverMicrosAverage;            // 124 whole sample (allocate, lock, fill, queue), moving average (us)

    // ---- producer side (app)
    volatile int64_t producerHeartbeat;       // 128 +1 per engine tick (~>= 10 Hz)
    int32_t  producerProcessId;               // 136
    uint32_t backgroundColor;                 // 140 0xAARRGGBB, used for fallback frames
    int64_t  framesProduced;                  // 144
    volatile int32_t latestSlot;              // 152 -1 = none
    uint32_t producerReserved;                // 156
    volatile int64_t latestSequence;          // 160
} MSV_HEADER;

#pragma pack(pop)

#define MSV_SLOT_TABLE_OFFSET 256u
#define MSV_SLOT_PITCH        64u

#ifdef __cplusplus
static_assert(sizeof(MSV_SLOT) == 56, "MSV_SLOT layout");
static_assert(offsetof(MSV_HEADER, consumerActive) == 32, "layout");
static_assert(offsetof(MSV_HEADER, consumerHeartbeat) == 56, "layout");
static_assert(offsetof(MSV_HEADER, framesDroppedByProducer) == 112, "layout");
static_assert(offsetof(MSV_HEADER, producerHeartbeat) == 128, "layout");
static_assert(offsetof(MSV_HEADER, latestSlot) == 152, "layout");
static_assert(offsetof(MSV_HEADER, latestSequence) == 160, "layout");
static_assert(sizeof(MSV_HEADER) <= MSV_SLOT_TABLE_OFFSET, "header fits before the slot table");
static_assert(MSV_SLOT_CAPACITY >= MSV_MAX_WIDTH * MSV_MAX_HEIGHT * 4, "slot holds the largest frame");
#endif
