using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Bob.Windows.UI;

public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(string displayName, ImageSource image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(image);

        InitializeComponent();
        PreviewName.Text = displayName;
        PreviewImage.Source = image;
        AutomationProperties.SetName(
            PreviewImage,
            $"Full image preview of {displayName}");
    }
}
