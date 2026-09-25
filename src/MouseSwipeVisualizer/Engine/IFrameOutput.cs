namespace MouseSwipeVisualizer.Engine;

/// <summary>
/// A consumer of rendered frames (the virtual camera link, the preview window, test sinks).
/// Called on the engine thread only.
/// </summary>
public interface IFrameOutput
{
    /// <summary>Whether frames are currently wanted. Inactive outputs cost nothing.</summary>
    bool IsActive { get; }

    /// <summary>Preferred frame size when this output drives the render size (0 = no preference).</summary>
    int RequestedWidth { get; }

    int RequestedHeight { get; }

    /// <summary>Preferred frame rate (0 = no preference).</summary>
    int RequestedFps { get; }

    /// <summary>Delivers a frame (BGRA, 0xAARRGGBB per pixel, top-down, stride = width).</summary>
    /// <param name="sequence">Increments whenever the content changed.</param>
    void Publish(ReadOnlySpan<uint> pixels, int width, int height, long sequence);

    /// <summary>Called at least every ~100 ms while running, also when no new frame was rendered.</summary>
    void Tick();
}
