using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MouseSwipeVisualizer.Camera;
using MouseSwipeVisualizer.Diagnostics;
using MouseSwipeVisualizer.Engine;
using MouseSwipeVisualizer.Input;
using MouseSwipeVisualizer.Settings;
using MouseSwipeVisualizer.Utilities;

namespace MouseSwipeVisualizer;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\MouseSwipeVisualizer.SingleInstance";

    private Mutex? _singleInstance;
    private RawMouseInput? _input;
    private AppController? _controller;
    private SwipeEngine? _engine;
    private CameraFrameLink? _camera;
    private bool _unhandledErrorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Logger.Initialize(AppPaths.LogFile);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (SelfTest.TryParseArguments(e.Args, out SelfTestOptions? selfTestOptions))
        {
            RunSelfTest(selfTestOptions!);
            return;
        }

        if (CameraCommands.IsCameraCommand(e.Args))
        {
            Shutdown(CameraCommands.Run(e.Args));
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Mouse Swipe Visualizer is already running (see the tray icon).",
                "Mouse Swipe Visualizer", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        Logger.Info($"Application started. Version {typeof(App).Assembly.GetName().Version}, " +
                    $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, " +
                    $"{RuntimeInformation.ProcessArchitecture}.");

        var settingsService = new SettingsService(AppPaths.SettingsFile);
        AppSettings settings = settingsService.Load();

        var buffer = new MouseDeltaBuffer();
        _input = new RawMouseInput();
        _input.DeltaReceived += buffer.Push;
        try
        {
            _input.Start();
        }
        catch (RawInputException ex)
        {
            Logger.Error("Core functionality unavailable: Raw Input could not be started.", ex);
            MessageBox.Show(
                "Mouse Swipe Visualizer could not register for Raw Input mouse data, so it cannot see mouse movement.\n\n" +
                ex.Message + "\n\nDetails are in the log:\n" + AppPaths.LogFile,
                "Mouse Swipe Visualizer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // --headless / --tray: no windows at all, only the tray icon. The engine and camera output
        // are independent of windows, so this is a fully working production mode.
        bool headless = HasArgument(e.Args, "--headless") || HasArgument(e.Args, "--tray");

        // Outputs: the virtual camera first (while a consumer streams it decides size and frame rate),
        // then the preview window. Neither depends on the other.
        var preview = new PreviewFrameStore();
        _camera = new CameraFrameLink();
        ConfigureCamera(_camera, settings);
        _engine = new SwipeEngine(buffer, settings, _camera, preview);
        var keyboard = new KeyboardState();
        _engine.Keyboard = keyboard;
        keyboard.Changed += _engine.Wake;
        _input.Keyboard = keyboard;
        _input.SetKeyboardEnabled(settings.KeyboardEnabled);
        _camera.ConsumerActiveChanged += _ => _engine.Wake();
        _engine.Start();

        _controller = new AppController(settings, settingsService, _input, buffer, _engine, preview, headless);
        var cameraDiagnostics = new CameraDiagnostics(_camera);
        _controller.ExtraDiagnostics = cameraDiagnostics.Text;
        _controller.CameraSummaryProvider = cameraDiagnostics.Summary;
        _controller.CameraActionHandler = CameraDiagnostics.RunActionAsync;
        _controller.SettingsApplied += s =>
        {
            ConfigureCamera(_camera, s);
            _input.SetKeyboardEnabled(s.KeyboardEnabled);
        };
        _controller.Start();

        if (settings.OutputMode == OutputMode.NativeVirtualCamera)
        {
            // Idempotent: (re)creates "Mouse Swipe Visualizer Camera" for this user if the media source
            // is installed. Runs in the background; Media Foundation may take a moment.
            Task.Run(CameraDiagnostics.EnsureCameraRegistered);
        }
    }

    private static void ConfigureCamera(CameraFrameLink camera, AppSettings settings)
    {
        camera.Enabled = settings.OutputMode == OutputMode.NativeVirtualCamera;
        System.Windows.Media.Color background = settings.GetCaptureBackgroundColor();
        camera.BackgroundColor = 0xFF000000u | ((uint)background.R << 16) | ((uint)background.G << 8) | background.B;
    }

    internal static bool HasArgument(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private void RunSelfTest(SelfTestOptions options)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            int failures;
            try
            {
                failures = await SelfTest.RunAsync(options);
            }
            catch (Exception ex)
            {
                Logger.Error("Self-test crashed.", ex);
                failures = 100;
            }

            Shutdown(failures == 0 ? 0 : 1);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _engine?.Dispose();
        _camera?.Dispose();
        _input?.Dispose();
        if (_singleInstance != null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        Logger.Info($"Application exiting (code {e.ApplicationExitCode}).");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled exception on the UI thread.", e.Exception);

        // Keep running: the visualizer is a passive tool and most failures are recoverable. Show the
        // message only once so a failure inside the frame loop can't flood the screen.
        e.Handled = true;
        if (!_unhandledErrorShown)
        {
            _unhandledErrorShown = true;
            MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe details were written to:\n{AppPaths.LogFile}",
                "Mouse Swipe Visualizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        Logger.Error($"Unhandled exception (terminating={e.IsTerminating}).", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
