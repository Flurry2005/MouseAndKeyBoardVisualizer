namespace MouseSwipeVisualizer.Input;

/// <summary>One key of the on-screen keyboard.</summary>
/// <param name="Label">Text or symbol drawn on the key.</param>
/// <param name="Row">Row index (0 = top).</param>
/// <param name="Width">Width in key units (1 = a letter key).</param>
/// <param name="ScanCodes">Set-1 make codes (E0-prefixed codes as 0xE0xx) that light this key up.</param>
/// <param name="IsSymbol">Label is a symbol (drawn with a symbol font, slightly larger).</param>
public sealed record KeyDefinition(string Label, int Row, double Width, int[] ScanCodes, bool IsSymbol = false);

/// <summary>
/// The left-hand gaming block shown in the overlay (Esc–5 / Tab–T / Caps–G / Shift–B / Ctrl, Alt, Space).
/// Keys are matched by scan code, i.e. by physical position, so the block is the same on any
/// keyboard language (the labels follow a US/Nordic QWERTY board). Only these keys are ever tracked.
/// </summary>
public static class KeyboardLayout
{
    public const int RowCount = 5;

    /// <summary>Gap between keys, in key units.</summary>
    public const double Gap = 0.14;

    public static IReadOnlyList<KeyDefinition> Keys { get; } = new KeyDefinition[]
    {
        new("Esc", 0, 1.25, new[] { 0x01 }),
        new("1", 0, 1, new[] { 0x02 }),
        new("2", 0, 1, new[] { 0x03 }),
        new("3", 0, 1, new[] { 0x04 }),
        new("4", 0, 1, new[] { 0x05 }),
        new("5", 0, 1, new[] { 0x06 }),

        new("⇥", 1, 1.5, new[] { 0x0F }, IsSymbol: true),
        new("Q", 1, 1, new[] { 0x10 }),
        new("W", 1, 1, new[] { 0x11 }),
        new("E", 1, 1, new[] { 0x12 }),
        new("R", 1, 1, new[] { 0x13 }),
        new("T", 1, 1, new[] { 0x14 }),

        new("🔒", 2, 1.75, new[] { 0x3A }, IsSymbol: true),
        new("A", 2, 1, new[] { 0x1E }),
        new("S", 2, 1, new[] { 0x1F }),
        new("D", 2, 1, new[] { 0x20 }),
        new("F", 2, 1, new[] { 0x21 }),
        new("G", 2, 1, new[] { 0x22 }),

        new("⇧", 3, 2.25, new[] { 0x2A, 0x36 }, IsSymbol: true),
        new("Z", 3, 1, new[] { 0x2C }),
        new("X", 3, 1, new[] { 0x2D }),
        new("C", 3, 1, new[] { 0x2E }),
        new("V", 3, 1, new[] { 0x2F }),
        new("B", 3, 1, new[] { 0x30 }),

        new("^", 4, 1.5, new[] { 0x1D, 0xE01D }, IsSymbol: true),
        new("⌥", 4, 1.25, new[] { 0x38, 0xE038 }, IsSymbol: true),
        new("", 4, 4.5, new[] { 0x39 }),
    };

    private static readonly Dictionary<int, int> ScanCodeToKey = BuildMap();

    /// <summary>Total width of the widest row in key units (including gaps).</summary>
    public static double WidthUnits { get; } = ComputeWidthUnits();

    public static double HeightUnits => RowCount + (RowCount - 1) * Gap;

    /// <summary>Index into <see cref="Keys"/> for a scan code (0xE0xx for E0-prefixed), or -1.</summary>
    public static int KeyIndexForScanCode(int scanCode) => ScanCodeToKey.TryGetValue(scanCode, out int index) ? index : -1;

    private static Dictionary<int, int> BuildMap()
    {
        var map = new Dictionary<int, int>();
        for (int i = 0; i < Keys.Count; i++)
        {
            foreach (int code in Keys[i].ScanCodes)
            {
                map[code] = i;
            }
        }

        return map;
    }

    private static double ComputeWidthUnits()
    {
        double widest = 0;
        for (int row = 0; row < RowCount; row++)
        {
            double width = 0;
            int count = 0;
            foreach (KeyDefinition key in Keys)
            {
                if (key.Row == row)
                {
                    width += key.Width;
                    count++;
                }
            }

            widest = Math.Max(widest, width + Math.Max(0, count - 1) * Gap);
        }

        return widest;
    }
}

/// <summary>
/// Pressed state of the overlay keys. Written by the Raw Input thread, read by the engine thread.
/// Physical keys that share one overlay key (left/right Shift, Ctrl, Alt) are tracked separately so
/// releasing one side cannot clear the other.
/// </summary>
public sealed class KeyboardState
{
    private readonly int[] _physicalDown = new int[0xE100];
    private readonly int[] _keyDownCount = new int[KeyboardLayout.Keys.Count];
    private long _version;

    /// <summary>Raised on the input thread after an overlay key changed (the engine wakes up to redraw).</summary>
    public event Action? Changed;

    /// <summary>Changes whenever any overlay key goes down or up.</summary>
    public long Version => Interlocked.Read(ref _version);

    public bool IsDown(int keyIndex) => Volatile.Read(ref _keyDownCount[keyIndex]) > 0;

    /// <summary>Called from the input thread for every raw keyboard event.</summary>
    public void OnKey(int scanCode, bool down)
    {
        int key = KeyboardLayout.KeyIndexForScanCode(scanCode);
        if (key < 0 || scanCode < 0 || scanCode >= _physicalDown.Length)
        {
            return; // not an overlay key: not stored at all
        }

        int wasDown = _physicalDown[scanCode];
        int nowDown = down ? 1 : 0;
        if (wasDown == nowDown)
        {
            return; // auto-repeat
        }

        _physicalDown[scanCode] = nowDown;
        Interlocked.Add(ref _keyDownCount[key], down ? 1 : -1);
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    /// <summary>Releases everything (e.g. keyboard input turned off).</summary>
    public void Reset()
    {
        Array.Clear(_physicalDown);
        for (int i = 0; i < _keyDownCount.Length; i++)
        {
            Volatile.Write(ref _keyDownCount[i], 0);
        }

        Interlocked.Increment(ref _version);
    }
}
