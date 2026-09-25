using System.ComponentModel;
using System.Runtime.InteropServices;
using MouseSwipeVisualizer.Interop;
using MouseSwipeVisualizer.Utilities;
using static MouseSwipeVisualizer.Interop.NativeMethods;

namespace MouseSwipeVisualizer.Input;

/// <summary>Thrown when Raw Input cannot be set up; the app cannot work without it.</summary>
public sealed class RawInputException : Exception
{
    public RawInputException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Receives relative mouse movement through the Windows Raw Input API (WM_INPUT).
/// <para>
/// Why a dedicated thread with a message-only window instead of hooking the WPF window:
/// WM_INPUT can arrive at up to 8000 Hz. On the WPF UI thread it would compete with layout and
/// rendering, and a busy UI thread would delay input. Here the thread does nothing but pump
/// WM_INPUT, timestamp the delta and hand it to subscribers.
/// </para>
/// <para>
/// Raw Input delivers the device's own counts before pointer acceleration and independent of the
/// cursor, so it keeps working when a game hides, clips or re-centres the cursor (FPS mouse-look).
/// Nothing is injected or modified - this class only observes.
/// </para>
/// </summary>
public sealed class RawMouseInput : IDisposable
{
    private const string WindowClassName = "MouseSwipeVisualizer.RawInputSink";
    private const int StartupTimeoutMs = 5000;
    private const int ShutdownTimeoutMs = 2000;
    private const int MaxLoggedCallbackErrors = 5;

    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
    private static readonly uint DeviceSize = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
    private static readonly int MouseOffset = (int)HeaderSize;
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<RAWMOUSE>(nameof(RAWMOUSE.usFlags));
    private static readonly int LastXOffset = (int)Marshal.OffsetOf<RAWMOUSE>(nameof(RAWMOUSE.lLastX));
    private static readonly int LastYOffset = (int)Marshal.OffsetOf<RAWMOUSE>(nameof(RAWMOUSE.lLastY));
    private static readonly uint MinimumMousePacketSize = HeaderSize + (uint)Marshal.SizeOf<RAWMOUSE>();

    // Keeps the delegate alive for as long as the native window class may call it.
    private readonly WndProc _wndProc;
    private readonly ManualResetEventSlim _started = new(false);

    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private IntPtr _buffer;
    private uint _bufferSize;
    private Exception? _startupError;
    private long _totalEvents;
    private int _callbackErrors;
    private bool _registered;
    private bool _disposed;

    // Absolute-mode devices (tablets, RDP, VMs) report positions; we convert them to deltas.
    private bool _hasAbsolute;
    private double _lastAbsoluteX;
    private double _lastAbsoluteY;
    private double _absoluteRemainderX;
    private double _absoluteRemainderY;

    public RawMouseInput()
    {
        _wndProc = WindowProc;
    }

    /// <summary>
    /// Raised on the Raw Input thread for every non-zero movement. Handlers must be fast and
    /// thread-safe (the app just pushes into <see cref="MouseDeltaBuffer"/>).
    /// </summary>
    public event Action<MouseDelta>? DeltaReceived;

    public bool IsRunning => _registered && _thread is { IsAlive: true };

    /// <summary>Total WM_INPUT mouse packets processed (read from any thread).</summary>
    public long TotalEvents => Interlocked.Read(ref _totalEvents);

    /// <summary>HWND of the message-only window that receives WM_INPUT (diagnostics/self-test).</summary>
    public IntPtr TargetWindow => _hwnd;

    /// <summary>Starts the input thread and blocks until registration succeeded or failed.</summary>
    /// <exception cref="RawInputException">Registration failed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread != null)
        {
            throw new InvalidOperationException("Raw input has already been started.");
        }

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RawMouseInput",
            // Slightly above normal so bursts of WM_INPUT are drained promptly while a game
            // saturates the CPU; the thread sleeps in GetMessage otherwise.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        if (!_started.Wait(StartupTimeoutMs))
        {
            throw new RawInputException("Timed out waiting for the Raw Input thread to start.");
        }

        if (_startupError != null)
        {
            throw _startupError as RawInputException
                  ?? new RawInputException("Raw Input could not be initialized.", _startupError);
        }
    }

    private void ThreadMain()
    {
        try
        {
            CreateMessageWindow();
            RegisterForMouse();
            UpdateKeyboardRegistration(_keyboardWanted);
        }
        catch (Exception ex)
        {
            _startupError = ex;
            Logger.Error("Raw Input initialization failed", ex);
            DestroyMessageWindow();
            _started.Set();
            return;
        }

        _started.Set();

        try
        {
            while (true)
            {
                int result = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break; // WM_QUIT
                }

                if (result == -1)
                {
                    Logger.Error($"GetMessage failed on the Raw Input thread (error {Marshal.GetLastPInvokeError()}).");
                    break;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnregisterMouse();
            DestroyMessageWindow();
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            Logger.Info("Raw Input thread stopped.");
        }
    }

    private void CreateMessageWindow()
    {
        _hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = WindowClassName,
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new RawInputException("RegisterClassEx failed for the Raw Input window.", new Win32Exception(error));
            }
        }

        // A message-only window (parent HWND_MESSAGE) is invisible, never enumerated and never
        // activated, but can still be a Raw Input target.
        _hwnd = CreateWindowEx(0, WindowClassName, "MouseSwipeVisualizer Raw Input", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new RawInputException("CreateWindowEx failed for the Raw Input window.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        _bufferSize = Math.Max(64u, MinimumMousePacketSize);
        _buffer = Marshal.AllocHGlobal((int)_bufferSize);
    }

    private void RegisterForMouse()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                // No RIDEV_NOLEGACY: legacy WM_MOUSEMOVE etc. must keep flowing to every other app.
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _hwnd,
            },
        };

        if (!RegisterRawInputDevices(devices, 1, DeviceSize))
        {
            var win32 = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new RawInputException($"RegisterRawInputDevices failed: {win32.Message} (error {win32.NativeErrorCode}).", win32);
        }

        _registered = true;
        Logger.Info($"Raw Input registration succeeded (mouse, RIDEV_INPUTSINK, header={HeaderSize} B, RAWMOUSE offset={MouseOffset}).");
    }

    // ---------------------------------------------------------------- keyboard (overlay keys only)

    private const uint WmAppKeyboard = 0x8001; // WM_APP + 1: wParam 1 = register, 0 = remove
    private const ushort HidUsageGenericKeyboard = 0x06;
    private const uint RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 0x01;
    private const ushort RiKeyE0 = 0x02;
    private const int KeyboardPacketSize = 16; // RAWKEYBOARD
    private volatile bool _keyboardWanted;
    private bool _keyboardRegistered;

    /// <summary>Receives overlay key presses (scan codes). Only keys of <see cref="KeyboardLayout"/> are kept.</summary>
    public KeyboardState? Keyboard { get; set; }

    public bool KeyboardRegistered => _keyboardRegistered;

    /// <summary>
    /// Turns keyboard Raw Input on/off (same RIDEV_INPUTSINK registration as the mouse, so it works
    /// while a game has focus). Off = not registered at all. Thread-safe.
    /// </summary>
    public void SetKeyboardEnabled(bool enabled)
    {
        _keyboardWanted = enabled;
        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmAppKeyboard, enabled ? 1 : IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void UpdateKeyboardRegistration(bool enable)
    {
        if (enable == _keyboardRegistered)
        {
            return;
        }

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HidUsageGenericKeyboard,
                // No RIDEV_NOLEGACY / NOHOTKEYS: other applications keep getting their keys unchanged.
                dwFlags = enable ? RIDEV_INPUTSINK : RIDEV_REMOVE,
                hwndTarget = enable ? _hwnd : IntPtr.Zero,
            },
        };

        if (RegisterRawInputDevices(devices, 1, DeviceSize))
        {
            _keyboardRegistered = enable;
            Logger.Info(enable ? "Keyboard Raw Input registered (overlay keys only)." : "Keyboard Raw Input removed.");
        }
        else
        {
            Logger.Warn($"Keyboard Raw Input {(enable ? "registration" : "removal")} failed (error {Marshal.GetLastPInvokeError()}).");
        }

        if (!_keyboardRegistered)
        {
            Keyboard?.Reset();
        }
    }

    private void ProcessKeyboard()
    {
        int offset = (int)HeaderSize;
        int makeCode = (ushort)Marshal.ReadInt16(_buffer, offset);
        ushort flags = (ushort)Marshal.ReadInt16(_buffer, offset + 2);
        int vkey = (ushort)Marshal.ReadInt16(_buffer, offset + 6);
        if (makeCode == 0 || makeCode == 0xFF)
        {
            // Injected input without a scan code: derive it from the virtual key.
            makeCode = (int)MapVirtualKey((uint)vkey, 0 /* MAPVK_VK_TO_VSC */);
        }

        if (makeCode == 0)
        {
            return;
        }

        int scanCode = (flags & RiKeyE0) != 0 ? 0xE000 | makeCode : makeCode;
        Keyboard?.OnKey(scanCode, (flags & RiKeyBreak) == 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private void UnregisterMouse()
    {
        UpdateKeyboardRegistration(false);
        if (!_registered)
        {
            return;
        }

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero, // must be NULL with RIDEV_REMOVE
            },
        };

        if (RegisterRawInputDevices(devices, 1, DeviceSize))
        {
            Logger.Info("Raw Input unregistered.");
        }
        else
        {
            Logger.Warn($"Raw Input unregistration failed (error {Marshal.GetLastPInvokeError()}).");
        }

        _registered = false;
    }

    private void DestroyMessageWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_hInstance != IntPtr.Zero)
        {
            UnregisterClass(WindowClassName, _hInstance);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                // An exception escaping a native callback would terminate the process.
                try
                {
                    ProcessRawInput(lParam);
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref _callbackErrors) <= MaxLoggedCallbackErrors)
                    {
                        Logger.Error("Error while processing WM_INPUT", ex);
                    }
                }

                // The docs require DefWindowProc for WM_INPUT so the system can release the input data.
                return DefWindowProc(hWnd, msg, wParam, lParam);

            case WmAppKeyboard:
                UpdateKeyboardRegistration(wParam != IntPtr.Zero);
                return IntPtr.Zero;

            case WM_CLOSE:
                UnregisterMouse();
                DestroyWindow(hWnd);
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, HeaderSize) != 0 || size == 0)
        {
            return;
        }

        if (size > _bufferSize)
        {
            _buffer = Marshal.ReAllocHGlobal(_buffer, (IntPtr)size);
            _bufferSize = size;
        }

        uint read = GetRawInputData(hRawInput, RID_INPUT, _buffer, ref size, HeaderSize);
        if (read == uint.MaxValue)
        {
            return;
        }

        uint type = (uint)Marshal.ReadInt32(_buffer, 0);
        if (type == RimTypeKeyboard)
        {
            if (read >= HeaderSize + KeyboardPacketSize && _keyboardRegistered)
            {
                ProcessKeyboard();
            }

            return;
        }

        if (type != RIM_TYPEMOUSE || read < MinimumMousePacketSize)
        {
            return;
        }

        Interlocked.Increment(ref _totalEvents);

        ushort flags = (ushort)Marshal.ReadInt16(_buffer, MouseOffset + FlagsOffset);
        int lastX = Marshal.ReadInt32(_buffer, MouseOffset + LastXOffset);
        int lastY = Marshal.ReadInt32(_buffer, MouseOffset + LastYOffset);

        int dx;
        int dy;
        if ((flags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            if (!ConvertAbsolute(flags, lastX, lastY, out dx, out dy))
            {
                return;
            }
        }
        else
        {
            _hasAbsolute = false;
            dx = lastX;
            dy = lastY;
        }

        // Button-only packets (clicks, wheel) carry no movement; clicks are intentionally not visualized.
        if (dx == 0 && dy == 0)
        {
            return;
        }

        DeltaReceived?.Invoke(new MouseDelta(dx, dy, MonotonicClock.Now));
    }

    private bool ConvertAbsolute(ushort flags, int x, int y, out int dx, out int dy)
    {
        // Absolute coordinates are normalized to 0..65535 across either the primary screen or the
        // whole virtual desktop; converting to pixels gives deltas comparable to relative counts.
        bool virtualDesktop = (flags & MOUSE_VIRTUAL_DESKTOP) != 0;
        int width = GetSystemMetrics(virtualDesktop ? SM_CXVIRTUALSCREEN : SM_CXSCREEN);
        int height = GetSystemMetrics(virtualDesktop ? SM_CYVIRTUALSCREEN : SM_CYSCREEN);
        double px = x / 65535.0 * width;
        double py = y / 65535.0 * height;

        dx = 0;
        dy = 0;
        if (!_hasAbsolute)
        {
            _hasAbsolute = true;
            _lastAbsoluteX = px;
            _lastAbsoluteY = py;
            _absoluteRemainderX = 0;
            _absoluteRemainderY = 0;
            return false;
        }

        double fx = px - _lastAbsoluteX + _absoluteRemainderX;
        double fy = py - _lastAbsoluteY + _absoluteRemainderY;
        _lastAbsoluteX = px;
        _lastAbsoluteY = py;
        dx = (int)Math.Round(fx);
        dy = (int)Math.Round(fy);
        _absoluteRemainderX = fx - dx;
        _absoluteRemainderY = fy - dy;
        return true;
    }

    /// <summary>
    /// Reads back the process-wide Raw Input registrations and checks that the mouse is registered
    /// with RIDEV_INPUTSINK to our window. Used by the self-test.
    /// </summary>
    public bool VerifyRegistration(out string details)
    {
        uint count = 0;
        GetRegisteredRawInputDevices(null, ref count, DeviceSize);
        if (count == 0)
        {
            details = "No Raw Input devices registered.";
            return false;
        }

        var devices = new RAWINPUTDEVICE[count];
        uint result = GetRegisteredRawInputDevices(devices, ref count, DeviceSize);
        if (result == uint.MaxValue)
        {
            details = $"GetRegisteredRawInputDevices failed (error {Marshal.GetLastPInvokeError()}).";
            return false;
        }

        foreach (RAWINPUTDEVICE d in devices)
        {
            if (d.usUsagePage == HID_USAGE_PAGE_GENERIC && d.usUsage == HID_USAGE_GENERIC_MOUSE)
            {
                bool ok = (d.dwFlags & RIDEV_INPUTSINK) != 0 && d.hwndTarget == _hwnd && _hwnd != IntPtr.Zero;
                details = $"mouse flags=0x{d.dwFlags:X}, target=0x{d.hwndTarget:X}, expected target=0x{_hwnd:X}";
                return ok;
            }
        }

        details = "Mouse usage (0x01/0x02) not found among registered devices.";
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero && _thread is { IsAlive: true })
        {
            // Unregistration and window destruction happen on the owning thread (WM_CLOSE handler).
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (!_thread.Join(ShutdownTimeoutMs))
            {
                Logger.Warn("Raw Input thread did not stop within the timeout.");
            }
        }

        _started.Dispose();
    }
}
