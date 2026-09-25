namespace MouseSwipeVisualizer.Swipe;

/// <summary>Why a new stroke was started (shown in the debug statistics).</summary>
public enum StrokeBreakReason
{
    /// <summary>First movement after start-up or after everything faded.</summary>
    First,

    /// <summary>No input for longer than the swipe-break time.</summary>
    Pause,

    /// <summary>Sensor cut out mid-motion: the mouse was lifted to re-center.</summary>
    Lift,
}
