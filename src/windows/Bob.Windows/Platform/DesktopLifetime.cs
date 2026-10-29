using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Bob.Windows.UI;
using Forms = System.Windows.Forms;

namespace Bob.Windows.Platform;

internal sealed class DesktopLifetime : IDisposable
{
    private const int HotkeyMessage = 0x0312;
    private const uint NoRepeat = 0x4000;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _model;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly HwndSource _source;
    private readonly string _settingsPath;
    private readonly RegisteredWaitHandle _activationWait;
    private Shortcut _shortcut;
    private int _hotkeyId = 0xB0B;
    private bool _registered;
    private bool _exiting;
    private WindowState _restoreState = WindowState.Normal;
    private int _registrationError;
    private ShortcutSettingsWindow? _shortcutsWindow;

    internal DesktopLifetime(MainWindow window, MainWindowViewModel model, AppPaths paths, EventWaitHandle activateSignal)
    {
        _window = window;
        _model = model;
        _settingsPath = Path.Combine(paths.RootDirectory, "desktop-settings-v1.json");
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("The BOB window is not ready.");
        _source.AddHook(OnWindowMessage);
        _shortcut = LoadShortcut();
        _registered = Register(_hotkeyId, _shortcut);
        HotkeyStatus = _registered ? "Focus the shortcut field and press a new combination to change it."
            : UnavailableShortcutMessage;

        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/BOB;component/Assets/BOB.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        _tray = new Forms.NotifyIcon
        {
            Icon = (System.Drawing.Icon)icon.Clone(),
            Text = "BOB",
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open BOB", null, (_, _) => ShowWindow());
        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _tray.ContextMenuStrip.Items.Add(_statusItem);
        _tray.ContextMenuStrip.Items.Add("Keyboard shortcuts...", null, (_, _) => ShowShortcuts());
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Exit BOB", null, (_, _) => Exit());
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.Visible = true;
        _model.PropertyChanged += OnModelChanged;
        UpdateStatus();
        _window.Closing += OnClosing;
        _window.StateChanged += OnStateChanged;
        _window.ShortcutsRequested += OnShortcutsRequested;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(activateSignal,
            (_, _) => _window.Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public string HotkeyLabel => _shortcut.Label;
    public string HotkeyStatus { get; private set; }

    public void TryChangeHotkey(Key key, ModifierKeys modifiers)
    {
        var shortcut = new Shortcut(key, modifiers);
        if (!shortcut.IsValid)
        {
            HotkeyStatus = "Use Ctrl or Alt with a letter, number, or function key.";
            return;
        }
        if (_registered && shortcut == _shortcut)
        {
            HotkeyStatus = "This shortcut is already active.";
            return;
        }
        var nextId = _hotkeyId == 0xB0B ? 0xB0C : 0xB0B;
        if (!Register(nextId, shortcut))
        {
            HotkeyStatus = UnavailableShortcutMessage + " Your previous shortcut has been kept.";
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, shortcut);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _settingsPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UnregisterHotKey(_source.Handle, nextId);
            HotkeyStatus = "BOB could not save that shortcut. Your previous shortcut has been kept.";
            return;
        }
        if (_registered) UnregisterHotKey(_source.Handle, _hotkeyId);
        _hotkeyId = nextId;
        _shortcut = shortcut;
        _registered = true;
        HotkeyStatus = "Shortcut saved. It works while BOB is running, including from the tray.";
    }

    private Shortcut LoadShortcut()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var saved = JsonSerializer.Deserialize<Shortcut>(File.ReadAllText(_settingsPath));
                if (saved?.IsValid == true) return saved;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return new Shortcut(Key.B, ModifierKeys.Control | ModifierKeys.Alt);
    }

    private string UnavailableShortcutMessage => _registrationError == 1409
        ? "This shortcut is already in use by another app. Choose a different combination; the tray can still open BOB."
        : "Windows could not enable this shortcut. Choose a different combination; the tray can still open BOB.";

    private bool Register(int id, Shortcut shortcut)
    {
        var registered = RegisterHotKey(_source.Handle, id, (uint)shortcut.Modifiers | NoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key));
        _registrationError = registered ? 0 : Marshal.GetLastWin32Error();
        return registered;
    }

    private void ShowShortcuts()
    {
        if (_shortcutsWindow is { IsVisible: true })
        {
            _shortcutsWindow.Activate();
            return;
        }
        _shortcutsWindow = new ShortcutSettingsWindow(this) { Owner = _window };
        _shortcutsWindow.Show();
    }

    private void OnShortcutsRequested(object? sender, EventArgs e) => ShowShortcuts();

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == HotkeyMessage && wParam.ToInt32() == _hotkeyId)
        {
            if (_window.IsVisible && (_window.IsActive || _window.OwnedWindows.Cast<Window>().Any(w => w.IsActive)))
                _window.Hide();
            else ShowWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _restoreState;
        _window.Activate();
        SetForegroundWindow(_source.Handle);
        var preview = _window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
        if (preview is not null) preview.Activate();
        else _window.FocusComposer();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        _window.Hide();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState != WindowState.Minimized) _restoreState = _window.WindowState;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ConnectionText)) UpdateStatus();
    }

    private void UpdateStatus()
    {
        _statusItem.Text = _model.ConnectionText;
        var text = "BOB · " + _model.ConnectionText;
        _tray.Text = text[..Math.Min(text.Length, 63)];
    }

    internal void AllowExit() => _exiting = true;

    private void Exit()
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _activationWait.Unregister(null);
        _window.Closing -= OnClosing;
        _window.StateChanged -= OnStateChanged;
        _window.ShortcutsRequested -= OnShortcutsRequested;
        _model.PropertyChanged -= OnModelChanged;
        if (_registered) UnregisterHotKey(_source.Handle, _hotkeyId);
        _source.RemoveHook(OnWindowMessage);
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
    }

    internal sealed record Shortcut(Key Key, ModifierKeys Modifiers)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsValid => (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
            && (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0
            && (Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.F1 and <= Key.F24);

        [System.Text.Json.Serialization.JsonIgnore]
        public string Label => string.Join("+", new[]
        {
            Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl" : null,
            Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
            Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift" : null,
            Modifiers.HasFlag(ModifierKeys.Windows) ? "Win" : null,
            Key is >= Key.D0 and <= Key.D9 ? ((int)Key - (int)Key.D0).ToString() : Key.ToString()
        }.Where(value => value is not null));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
