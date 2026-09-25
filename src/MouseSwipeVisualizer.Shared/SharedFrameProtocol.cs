// Mirror of SharedFrameProtocol.h (offsets and constants must match byte for byte).
namespace MouseSwipeVisualizer.Camera;

/// <summary>Constants and field offsets of the camera frame transport (see SharedFrameProtocol.h).</summary>
public static class SharedFrameProtocol
{
    public const string SharedMemoryName = @"Global\MouseSwipeVisualizerCamera.Frames.v1";

    /// <summary>Used when the media source runs in a normal user process (self-test); see the C header.</summary>
    public const string SharedMemoryNameLocal = @"Local\MouseSwipeVisualizerCamera.Frames.v1";

    public const uint Magic = 0x4356534D; // 'MSVC'
    public const uint Version = 1;

    public const int HeaderSize = 4096;
    public const int SlotCount = 3;
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;
    public const long SlotCapacity = 8L * 1024 * 1024;
    public const long MappingSize = HeaderSize + SlotCount * SlotCapacity;

    public const uint PixelFormatBgra32 = 1;
    public const uint SubtypeNv12 = 1;
    public const uint SubtypeRgb32 = 2;

    public const int ProducerTimeoutMs = 1000;

    public const int SlotTableOffset = 256;
    public const int SlotPitch = 64;

    /// <summary>Header field offsets.</summary>
    public static class Header
    {
        public const int Magic = 0;
        public const int Version = 4;
        public const int HeaderSize = 8;
        public const int SlotCount = 12;
        public const int SlotCapacity = 16;
        public const int MappingSize = 24;

        public const int ConsumerActive = 32;
        public const int RequestedWidth = 36;
        public const int RequestedHeight = 40;
        public const int RequestedFpsNumerator = 44;
        public const int RequestedFpsDenominator = 48;
        public const int RequestedSubtype = 52;
        public const int ConsumerHeartbeat = 56;
        public const int FramesDelivered = 64;
        public const int FramesRepeated = 72;
        public const int FramesFallback = 80;
        public const int LastStartFileTimeUtc = 88;
        public const int StreamStarts = 96;
        public const int SourceProcessId = 104;
        public const int LastErrorHr = 108;
        public const int FramesDroppedByProducer = 112;
        public const int ConvertMicrosAverage = 120;
        public const int DeliverMicrosAverage = 124;

        public const int ProducerHeartbeat = 128;
        public const int ProducerProcessId = 136;
        public const int BackgroundColor = 140;
        public const int FramesProduced = 144;
        public const int LatestSlot = 152;
        public const int LatestSequence = 160;
    }

    /// <summary>Offsets inside one slot descriptor.</summary>
    public static class Slot
    {
        public const int SeqLock = 0;
        public const int FrameSequence = 8;
        public const int Width = 16;
        public const int Height = 20;
        public const int Stride = 24;
        public const int PixelFormat = 28;
        public const int ProducerQpc = 32;
        public const int DataOffset = 40;
        public const int DataSize = 48;
    }

    public static int SlotDescriptorOffset(int slot) => SlotTableOffset + slot * SlotPitch;

    public static long SlotDataOffset(int slot) => HeaderSize + slot * SlotCapacity;
}
