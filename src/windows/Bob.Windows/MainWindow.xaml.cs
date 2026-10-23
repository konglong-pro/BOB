using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Bob.Windows.UI;

namespace Bob.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Func<IDataObject?> _readClipboard;

    public MainWindow(MainWindowViewModel viewModel)
        : this(viewModel, Clipboard.GetDataObject)
    {
    }

    internal MainWindow(MainWindowViewModel viewModel, Func<IDataObject?> readClipboard)
    {
        ArgumentNullException.ThrowIfNull(readClipboard);
        InitializeComponent();
        _viewModel = viewModel;
        _readClipboard = readClipboard;
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.Timeline.CollectionChanged += OnTimelineChanged;
        InputBindings.Add(new KeyBinding(ApplicationCommands.Paste, Key.V, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Paste, Key.Insert, ModifierKeys.Shift));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPasteExecuted, OnCanPaste));
        AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(OnPreviewPasteExecuted));
        AddHandler(CommandManager.PreviewCanExecuteEvent, new CanExecuteRoutedEventHandler(OnCanPaste));

        // TODO(P1): close-to-tray requires a real NotifyIcon lifetime and explicit Exit action.
        // Until then, normal window close exits BOB and stops Kestrel.
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Timeline.CollectionChanged -= OnTimelineChanged;
        Loaded -= OnLoaded;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ScrollToLatest();

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DropOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            e.Handled = true;
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Visible;
            DropHint.Text = _viewModel.CanSendContent
                ? "Release to send files"
                : "Connect your phone before sending";
            if (_viewModel.CanSendContent)
            {
                e.Effects = e.AllowedEffects & DragDropEffects.Copy;
            }
            else
            {
                _viewModel.ShowContentInputError("Connect a phone before sending images or files.");
            }
        }
        catch (Exception exception) when (IsDataAccessError(exception))
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                if (_viewModel.CanSendContent)
                {
                    e.Effects = e.AllowedEffects & DragDropEffects.Copy;
                }
                await _viewModel.SendDroppedFilesAsync(paths);
            }
        }
        catch (Exception exception) when (IsDataAccessError(exception))
        {
            _viewModel.ShowContentInputError("BOB could not read the dropped files. Try again.");
        }
    }

    private bool HasClipboardImage()
    {
        try
        {
            var data = _readClipboard();
            return data is not null && ClipboardImageReader.CanRead(data);
        }
        catch (Exception exception) when (IsDataAccessError(exception))
        {
            return false;
        }
    }

    private void OnCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && HasClipboardImage())
        {
            e.CanExecute = true;
            e.Handled = true;
        }
    }

    private async void OnPreviewPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && HasClipboardImage())
        {
            e.Handled = true;
            await PasteImageAsync();
        }
    }

    private async void OnPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        await PasteImageAsync();
    }

    private async void OnPasteImageClick(object sender, RoutedEventArgs e) => await PasteImageAsync();

    private async Task PasteImageAsync()
    {
        if (!_viewModel.CanSendContent)
        {
            _viewModel.ShowContentInputError("Connect a phone before sending images or files.");
            return;
        }

        try
        {
            var data = _readClipboard();
            var content = data is null ? null : ClipboardImageReader.Read(data);
            if (content is null)
            {
                _viewModel.ShowContentInputError("Copy an image or take a screenshot, then paste it here.");
                return;
            }

            if (content.Files.Count > 0)
            {
                await _viewModel.SendDroppedFilesAsync(content.Files);
            }
            else if (content.Bitmap is not null)
            {
                await _viewModel.SendClipboardImageAsync(content.Bitmap);
            }
        }
        catch (Exception exception) when (IsDataAccessError(exception))
        {
            _viewModel.ShowContentInputError("BOB could not access the clipboard image. Try again.");
        }
    }

    private static bool IsDataAccessError(Exception exception) =>
        exception is COMException or InvalidOperationException or ArgumentException
            or NotSupportedException or IOException or System.Security.SecurityException;

    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.BeginInvoke(ScrollToLatest, DispatcherPriority.Background);
        }
    }

    private void ScrollToLatest()
    {
        if (_viewModel.Timeline.Count > 0)
        {
            TimelineList.ScrollIntoView(_viewModel.Timeline[^1]);
        }
    }
}
