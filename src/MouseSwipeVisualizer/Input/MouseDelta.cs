namespace MouseSwipeVisualizer.Input;

/// <summary>
/// One relative mouse movement as reported by Raw Input.
/// A struct so raising the event and queueing it never allocates.
/// </summary>
/// <param name="Dx">Horizontal movement in mouse counts (positive = right).</param>
/// <param name="Dy">Vertical movement in mouse counts (positive = towards the user / down).</param>
/// <param name="Timestamp">Stopwatch ticks at which WM_INPUT was processed.</param>
public readonly record struct MouseDelta(int Dx, int Dy, long Timestamp);
