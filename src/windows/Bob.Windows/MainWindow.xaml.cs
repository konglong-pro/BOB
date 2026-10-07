using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bob.Windows.UI;

namespace Bob.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private ScrollViewer? _timelineScroll;
    private bool _followLatest = true;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.Timeline.CollectionChanged += OnTimelineChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Timeline.CollectionChanged -= OnTimelineChanged;
        Loaded -= OnLoaded;
        if (_timelineScroll is not null)
        {
            _timelineScroll.ScrollChanged -= OnTimelineScrollChanged;
        }
        base.OnClosed(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timelineScroll = FindVisualChild<ScrollViewer>(TimelineList);
        if (_timelineScroll is not null)
        {
            _timelineScroll.ScrollChanged += OnTimelineScrollChanged;
        }
        ScrollToLatest();
    }

    private void OnTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            if (_followLatest || e.NewItems?.OfType<TimelineItemViewModel>().Any(item => item.Outgoing) == true)
            {
                Dispatcher.BeginInvoke(ScrollToLatest, DispatcherPriority.Background);
            }
            else
            {
                LatestButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void ScrollToLatest()
    {
        _followLatest = true;
        LatestButton.Visibility = Visibility.Collapsed;
        if (_viewModel.Timeline.Count > 0)
        {
            TimelineList.ScrollIntoView(_viewModel.Timeline[^1]);
        }
    }

    private void OnLatestClick(object sender, RoutedEventArgs e) => ScrollToLatest();

    private void OnTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && _timelineScroll is not null)
        {
            _followLatest = _timelineScroll.VerticalOffset >= _timelineScroll.ScrollableHeight - 48;
            if (_followLatest)
            {
                LatestButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await _viewModel.QueueFilesAsync(paths);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
            {
                return result;
            }
            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }
}
