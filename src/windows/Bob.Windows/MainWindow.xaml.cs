using System.Windows;
using Bob.Windows.UI;

namespace Bob.Windows;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // TODO(P1): close-to-tray requires a real NotifyIcon lifetime and explicit Exit action.
        // Until then, normal window close exits BOB and stops Kestrel.
    }
}
