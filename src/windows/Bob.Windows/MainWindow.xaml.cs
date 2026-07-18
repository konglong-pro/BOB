using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Bob.Windows.UI;

namespace Bob.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.Timeline.CollectionChanged += OnTimelineChanged;

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
