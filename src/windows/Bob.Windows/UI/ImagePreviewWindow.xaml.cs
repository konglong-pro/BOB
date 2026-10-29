using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Input;

namespace Bob.Windows.UI;

public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(string displayName, ImageSource image, Action? copyImage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(image);

        InitializeComponent();
        PreviewName.Text = displayName;
        PreviewImage.Source = image;
        if (copyImage is not null)
        {
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (_, e) => { copyImage(); e.Handled = true; },
                (_, e) => { e.CanExecute = true; e.Handled = true; }));
            var menu = new System.Windows.Controls.ContextMenu();
            var copy = new System.Windows.Controls.MenuItem { Header = "Copy image", InputGestureText = "Ctrl+C" };
            copy.Click += (_, _) => copyImage();
            menu.Items.Add(copy);
            PreviewImage.ContextMenu = menu;
        }
        AutomationProperties.SetName(
            PreviewImage,
            $"Full image preview of {displayName}");
    }
}
