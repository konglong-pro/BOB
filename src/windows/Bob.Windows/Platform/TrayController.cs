using System.ComponentModel;
using System.Windows;
using Bob.Windows.UI;
using Forms = System.Windows.Forms;

namespace Bob.Windows.Platform;

public sealed class TrayController : IDisposable
{
    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly System.Drawing.Icon _icon;
    private readonly Action _exit;
    private bool _exiting;
    private bool _disposed;

    public TrayController(Window window, MainWindowViewModel viewModel, Action exit)
    {
        _window = window;
        _viewModel = viewModel;
        _exit = exit;
        using var resource = Application.GetResourceStream(
            new Uri($"pack://application:,,,/{typeof(TrayController).Assembly.GetName().Name};component/Assets/BOB.ico"))!.Stream;
        _icon = new System.Drawing.Icon(resource);
        _status = new Forms.ToolStripMenuItem(viewModel.ConnectionText) { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open BOB", null, (_, _) => ShowWindow());
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit BOB", null, (_, _) => Exit());
        _tray = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "BOB · " + viewModel.ConnectionText,
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += OnDoubleClick;
        window.Closing += OnClosing;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            _window.Hide();
        }
    }

    private void OnDoubleClick(object? sender, EventArgs e) => ShowWindow();

    private void ShowWindow() => _window.Dispatcher.Invoke(() =>
    {
        if (_disposed)
        {
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    });

    private void Exit() => _window.Dispatcher.Invoke(() =>
    {
        _exiting = true;
        _exit();
    });

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ConnectionText) && !_disposed)
        {
            _status.Text = _viewModel.ConnectionText;
            var text = "BOB · " + _viewModel.ConnectionText;
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _exiting = true;
        _window.Closing -= OnClosing;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _tray.DoubleClick -= OnDoubleClick;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _icon.Dispose();
    }
}
