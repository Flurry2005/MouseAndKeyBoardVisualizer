namespace MouseSwipeVisualizer.Swipe;

/// <summary>
/// One accumulated trail sample in stroke-local "swipe space".
/// Units: 1.0 = the radius of the visualizer area (half its shorter side) at view scale 1.
/// Every stroke starts at (0, 0), which is displayed at the centre of the area.
/// </summary>
/// <param name="X">Accumulated horizontal position (positive = right).</param>
/// <param name="Y">Accumulated vertical position (positive = down, like screen space).</param>
/// <param name="Timestamp">Stopwatch ticks of the newest raw delta folded into this point.</param>
/// <param name="StrokeId">Stroke the point belongs to (diagnostics).</param>
public readonly record struct SwipePoint(double X, double Y, long Timestamp, int StrokeId);
