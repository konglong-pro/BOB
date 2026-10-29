using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bob.Windows.UI;

namespace Bob.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _followLatest = true;
    private bool _imeComposing;

    internal event EventHandler? ShortcutsRequested;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.Timeline.CollectionChanged += OnTimelineChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        TextCompositionManager.AddPreviewTextInputStartHandler(Composer, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(Composer, (_, _) => _imeComposing = false);
        Composer.LostKeyboardFocus += (_, _) => _imeComposing = false;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyImage, CanCopyImage));
    }

    public void FocusComposer()
    {
        Composer.Focus();
        Composer.CaretIndex = Composer.Text.Length;
    }

    private void OnHistoryFilterChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel model && sender is RadioButton { Tag: string filter })
            model.HistoryFilter = filter;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HistoryFilter))
        {
            _followLatest = true;
            Dispatcher.BeginInvoke(ScrollToLatest, DispatcherPriority.Background);
        }
    }

    private void OnBubbleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimelineItemViewModel { IsText: false })
            ((UIElement)sender).Focus();
    }

    private void CanCopyImage(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return; // Native selection copy owns text focus.
        e.CanExecute = FocusedImage?.CopyImageCommand.CanExecute(null) == true;
        e.Handled = true;
    }

    private void OnCopyImage(object sender, ExecutedRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        FocusedImage?.CopyImageCommand.Execute(null);
        e.Handled = true;
    }

    private static TimelineItemViewModel? FocusedImage =>
        (Keyboard.FocusedElement as FrameworkElement)?.DataContext is TimelineItemViewModel { Category: "Images" } item
            ? item : null;

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _imeComposing || e.IsRepeat) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (_viewModel.SendTextCommand.CanExecute(null)) _viewModel.SendTextCommand.Execute(null);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsActive || _imeComposing) return;
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey
            : e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && e.Key != Key.ImeProcessed && Keyboard.Modifiers == ModifierKeys.None)
        {
            Hide();
            e.Handled = true;
        }
        else if (key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ShortcutsRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.L)
        {
            FocusComposer();
            e.Handled = true;
        }
        else if (key == Key.O && (Keyboard.Modifiers == ModifierKeys.Control
                 || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            var command = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? _viewModel.ChooseImageCommand : _viewModel.ChooseFileCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
                 && key is Key.R or Key.E)
        {
            var command = key == Key.R ? _viewModel.ChooseReceiveDirectoryCommand
                : _viewModel.OpenReceiveDirectoryCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Timeline.CollectionChanged -= OnTimelineChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        Loaded -= OnLoaded;
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ScrollToLatest();

    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followLatest && e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.BeginInvoke(ScrollToLatest, DispatcherPriority.Background);
    }

    private void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scroll && ReferenceEquals(scroll, FindScrollViewer(TimelineList))
            && e.ExtentHeightChange == 0)
            _followLatest = scroll.ScrollableHeight - scroll.VerticalOffset < 1;
    }

    private void OnLoadEarlier(object sender, RoutedEventArgs e)
    {
        var scroll = FindScrollViewer(TimelineList);
        var anchor = TimelineList.Items.Cast<object>().ElementAtOrDefault((int)(scroll?.VerticalOffset ?? 0));
        _followLatest = false;
        if (_viewModel.LoadEarlierCommand.CanExecute(null)) _viewModel.LoadEarlierCommand.Execute(null);
        if (anchor is not null)
            Dispatcher.BeginInvoke(() => TimelineList.ScrollIntoView(anchor), DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(parent, index));
            if (found is not null) return found;
        }
        return null;
    }

    private void ScrollToLatest()
    {
        if (TimelineList.Items.Count > 0) TimelineList.ScrollIntoView(TimelineList.Items[^1]);
    }
}
