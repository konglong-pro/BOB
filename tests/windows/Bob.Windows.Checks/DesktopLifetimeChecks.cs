using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using Bob.Windows;
using Bob.Windows.Persistence;
using Bob.Windows.Platform;
using Bob.Windows.Security;
using Bob.Windows.Storage;
using Bob.Windows.Transport;
using Bob.Windows.UI;

internal static class DesktopLifetimeChecks
{
    private const int ConflictProbeId = 0x4B01;
    private const int ReleaseProbeId = 0x4B02;
    private const uint HotkeyModifiers = 1 | 2 | 4; // Native MOD_ALT | MOD_CONTROL | MOD_SHIFT.
    private const ModifierKeys ShortcutModifiers = ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift;

    public static void CheckInIsolatedProcess()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Check executable is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--desktop-lifetime-check");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start desktop checks.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(40_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Isolated desktop check exceeded 40 seconds.");
        }
        var diagnostic = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
        Assert(process.ExitCode == 0, "Isolated desktop check failed: " + diagnostic);
    }

    public static void Check()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunNativeCheck(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(30)), "native desktop lifecycle check exceeded 30 seconds");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RunNativeCheck()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bob-desktop-lifetime-checks"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Application? application = null;
        MainWindow? window = null;
        DesktopLifetime? desktop = null;
        var conflictProbeRegistered = false;
        var releaseProbeRegistered = false;
        var closed = false;
        try
        {
            Assert(Application.Current is null, "native desktop checks require a fresh WPF Application");
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = LoadApplicationResources();

            var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "identity"));
            var text = new TextHistoryStore(paths);
            var transfers = new TransferHistoryStore(paths);
            var settings = new ReceiveDirectorySettings(paths, Path.Combine(root, "receive"));
            var sessions = new SessionCoordinator(text);
            using var identity = CreateIdentity();
            var model = new MainWindowViewModel(sessions,
                new TransferCoordinator(settings, transfers, sessions), settings, text, identity);
            window = new MainWindow(model)
            {
                Opacity = 0,
                ShowInTaskbar = false,
                ShowActivated = false,
                // Activation under test must not steal the user's keyboard focus.
                IsEnabled = false
            };
            application.MainWindow = window;
            window.Closed += (_, _) => closed = true;
            window.Show();
            var handle = new WindowInteropHelper(window).Handle;
            Assert(handle != IntPtr.Zero && IsWindow(handle), "the hidden test window did not create a native handle");
            EnableWindow(handle, false);

            var eventName = @"Local\BOB.DesktopChecks.Activate." + Guid.NewGuid().ToString("N");
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            using var secondaryActivation = EventWaitHandle.OpenExisting(eventName);
            desktop = new DesktopLifetime(window, model, paths, activation);

            Key? selectedKey = null;
            for (var key = Key.F24; key >= Key.F1; key--)
            {
                desktop.TryChangeHotkey(key, ShortcutModifiers);
                if (desktop.HotkeyLabel == new DesktopLifetime.Shortcut(key, ShortcutModifiers).Label
                    && ReadPrivate<bool>(desktop, "_registered"))
                {
                    selectedKey = key;
                    break;
                }
            }
            Assert(selectedKey is not null,
                $"no temporary function-key shortcut could register (Win32 {ReadPrivate<int>(desktop, "_registrationError")})");
            var settingsPath = Path.Combine(paths.RootDirectory, "desktop-settings-v1.json");
            var savedShortcut = File.ReadAllText(settingsPath);
            var selectedLabel = desktop.HotkeyLabel;

            // A separate registration owns the conflicting combination. Never press or inject a key.
            Key? conflictingKey = null;
            for (var key = Key.F24; key >= Key.F1; key--)
            {
                if (key == selectedKey) continue;
                if (RegisterHotKey(IntPtr.Zero, ConflictProbeId, HotkeyModifiers,
                    (uint)KeyInterop.VirtualKeyFromKey(key)))
                {
                    conflictingKey = key;
                    conflictProbeRegistered = true;
                    break;
                }
            }
            Assert(conflictingKey is not null, "no temporary conflicting shortcut could be reserved");
            desktop.TryChangeHotkey(conflictingKey!.Value, ShortcutModifiers);
            Assert(desktop.HotkeyLabel == selectedLabel && File.ReadAllText(settingsPath) == savedShortcut,
                "a conflicting shortcut replaced the active or persisted shortcut");
            Assert(ReadPrivate<int>(desktop, "_registrationError") == 1409,
                "a real shortcut conflict was not classified as already registered");

            window.Close();
            Assert(!window.IsVisible && !closed && IsWindow(handle),
                "closing the main window destroyed it instead of hiding to the tray");
            secondaryActivation.Set();
            PumpUntil(() => window.IsVisible, "named activation did not restore the hidden window");
            Assert(!closed && IsWindow(handle), "activation replaced or destroyed the original window");

            window.WindowState = WindowState.Minimized;
            PumpUntil(() => window.WindowState == WindowState.Minimized, "the main window did not minimize");
            Assert(window.IsVisible && !closed && IsWindow(handle),
                "minimizing hid or destroyed the taskbar window");
            secondaryActivation.Set();
            PumpUntil(() => window.WindowState == WindowState.Normal && window.IsVisible,
                "activation did not restore the previous non-minimized window state");

            // Dispatch WM_HOTKEY to our own invisible window to exercise the actual HWND hook.
            window.Close();
            var registeredId = ReadPrivate<int>(desktop, "_hotkeyId");
            SendMessage(handle, 0x0312, new IntPtr(registeredId), IntPtr.Zero);
            Assert(window.IsVisible && !closed, "the native hotkey hook did not restore the hidden window");

            releaseProbeRegistered = RegisterHotKey(IntPtr.Zero, ReleaseProbeId, HotkeyModifiers,
                (uint)KeyInterop.VirtualKeyFromKey(selectedKey!.Value));
            Assert(!releaseProbeRegistered && Marshal.GetLastWin32Error() == 1409,
                "the selected global shortcut was not actually reserved by DesktopLifetime");
            desktop.Dispose();
            desktop = null;
            releaseProbeRegistered = RegisterHotKey(IntPtr.Zero, ReleaseProbeId, HotkeyModifiers,
                (uint)KeyInterop.VirtualKeyFromKey(selectedKey.Value));
            Assert(releaseProbeRegistered, "disposing DesktopLifetime did not release its global shortcut");
            window.Close();
            Assert(closed && !IsWindow(handle), "the window could not close after desktop lifetime disposal");
        }
        finally
        {
            if (releaseProbeRegistered) UnregisterHotKey(IntPtr.Zero, ReleaseProbeId);
            if (conflictProbeRegistered) UnregisterHotKey(IntPtr.Zero, ConflictProbeId);
            desktop?.Dispose();
            if (window is not null && !closed) window.Close();
            if (application is not null)
            {
                application.Shutdown();
            }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
            if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to delete a directory outside the test root.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static T ReadPrivate<T>(DesktopLifetime desktop, string name) =>
        (T)(typeof(DesktopLifetime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(desktop) ?? throw new InvalidOperationException($"desktop field {name} was null"));

    private static void PumpUntil(Func<bool> predicate, string failure)
    {
        if (predicate()) return;
        var frame = new DispatcherFrame();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        timer.Tick += (_, _) =>
        {
            if (predicate() || stopwatch.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert(predicate(), failure);
    }

    private static ServerIdentity CreateIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BOB native desktop checks", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        return new ServerIdentity(Guid.NewGuid(), request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1)));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src/windows/Bob.Windows/App.xaml")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Could not locate the application resource source.");
        var document = XDocument.Load(Path.Combine(directory.FullName, "src/windows/Bob.Windows/App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resources.Elements().Select(element => new XElement(element)));
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
