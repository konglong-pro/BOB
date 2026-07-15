using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bob.Windows.Domain;

namespace Bob.Windows.UI;

public sealed class TimelineItemViewModel : INotifyPropertyChanged
{
    private const int ThumbnailMaxDimension = 360;
    private string _status;
    private string? _detail;
    private readonly TransferKind? _kind;
    private readonly Action<string>? _openImage;
    private readonly RelayCommand _viewImageCommand;
    private string? _localPath;
    private ImageSource? _thumbnail;
    private long _thumbnailRevision;

    public TimelineItemViewModel(
        Guid textId,
        string text,
        DateTimeOffset createdAt,
        bool outgoing,
        string status,
        string peerName,
        string? detail = null,
        TransferKind? kind = null,
        string? localPath = null,
        Action<string>? openImage = null)
    {
        TextId = textId;
        Text = text;
        CreatedAt = createdAt;
        Outgoing = outgoing;
        _status = status;
        _detail = detail;
        _kind = kind;
        _openImage = openImage;
        _viewImageCommand = new RelayCommand(ViewImage, CanViewImage);
        PeerName = peerName;
        LocalPath = localPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid TextId { get; }

    public string Text { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool Outgoing { get; }

    public string PeerName { get; }

    public HorizontalAlignment Alignment =>
        Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public string DirectionLabel => Outgoing ? "To phone" : $"From {PeerName}";

    public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");

    public ICommand ViewImageCommand => _viewImageCommand;

    internal Task ThumbnailLoadTask { get; private set; } = Task.CompletedTask;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public string? Detail
    {
        get => _detail;
        set
        {
            if (_detail == value)
            {
                return;
            }

            _detail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailVisibility));
        }
    }

    public Visibility DetailVisibility =>
        string.IsNullOrWhiteSpace(Detail) ? Visibility.Collapsed : Visibility.Visible;

    public string? LocalPath
    {
        get => _localPath;
        set
        {
            if (string.Equals(_localPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _localPath = value;
            var revision = Interlocked.Increment(ref _thumbnailRevision);
            Thumbnail = null;
            ThumbnailLoadTask = _kind == TransferKind.Image
                && !string.IsNullOrWhiteSpace(value)
                ? LoadThumbnailAsync(value, revision)
                : Task.CompletedTask;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ViewImageVisibility));
            _viewImageCommand.RaiseCanExecuteChanged();
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbnailVisibility));
            OnPropertyChanged(nameof(ViewImageVisibility));
            _viewImageCommand.RaiseCanExecuteChanged();
        }
    }

    public Visibility ThumbnailVisibility =>
        Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ViewImageVisibility =>
        _kind == TransferKind.Image && Thumbnail is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    private bool CanViewImage() =>
        _kind == TransferKind.Image
        && _openImage is not null
        && Thumbnail is not null
        && !string.IsNullOrWhiteSpace(LocalPath)
        && File.Exists(LocalPath);

    private void ViewImage()
    {
        if (CanViewImage())
        {
            _openImage!(LocalPath!);
        }
    }

    private async Task LoadThumbnailAsync(string path, long revision)
    {
        var thumbnail = await Task.Run(
                () => LoadImagePreview(path, ThumbnailMaxDimension))
            .ConfigureAwait(false);

        void ApplyThumbnail()
        {
            if (revision != Volatile.Read(ref _thumbnailRevision)
                || !string.Equals(path, _localPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thumbnail = thumbnail;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyThumbnail();
        }
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            await dispatcher.InvokeAsync(ApplyThumbnail);
        }
    }

    internal static ImageSource? LoadImagePreview(string? path, int maximumDimension)
    {
        if (string.IsNullOrWhiteSpace(path)
            || maximumDimension <= 0
            || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var dimensions = ReadPixelDimensions(path);
            if (dimensions is null)
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (dimensions.Value.Width >= dimensions.Value.Height)
            {
                image.DecodePixelWidth = maximumDimension;
            }
            else
            {
                image.DecodePixelHeight = maximumDimension;
            }
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or FormatException
                or InvalidOperationException
                or COMException)
        {
            return null;
        }
    }

    private static (int Width, int Height)? ReadPixelDimensions(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var frame = decoder.Frames.FirstOrDefault();
        return frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0
            ? null
            : (frame.PixelWidth, frame.PixelHeight);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand(
        Action execute,
        Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                execute();
            }
        }

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
