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
    private static readonly SemaphoreSlim ThumbnailDecoders = new(2);
    private string _status;
    private string? _detail;
    private readonly TransferKind? _kind;
    private readonly Action<string>? _openImage;
    private readonly Action<string>? _copyText;
    private readonly Action<string>? _copyImage;
    private readonly Action<string>? _openFile;
    private readonly RelayCommand _viewImageCommand;
    private readonly RelayCommand _copyTextCommand;
    private readonly RelayCommand _copyImageCommand;
    private readonly RelayCommand _openFileCommand;
    private string? _localPath;
    private ImageSource? _thumbnail;
    private long _thumbnailRevision;
    private bool _isExpanded;
    private bool _thumbnailEnabled;

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
        Action<string>? openImage = null,
        Action<string>? copyText = null,
        Action<string>? copyImage = null,
        DateTimeOffset? recordedAt = null,
        bool loadThumbnail = true,
        Action<string>? openFile = null)
    {
        TextId = textId;
        Text = text;
        CreatedAt = createdAt;
        RecordedAt = recordedAt ?? createdAt;
        Outgoing = outgoing;
        _status = status;
        _detail = detail;
        _kind = kind;
        _openImage = openImage;
        _copyText = copyText;
        _copyImage = copyImage;
        _openFile = openFile;
        _viewImageCommand = new RelayCommand(ViewImage, CanViewImage);
        _copyTextCommand = new RelayCommand(CopyText, CanCopyText);
        _copyImageCommand = new RelayCommand(CopyImage, CanCopyImage);
        _openFileCommand = new RelayCommand(() => _openFile!(LocalPath!),
            () => _kind == TransferKind.File && _openFile is not null && File.Exists(LocalPath));
        _thumbnailEnabled = loadThumbnail;
        PeerName = peerName;
        LocalPath = localPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid TextId { get; }

    public string Text { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset RecordedAt { get; }

    public string Category => _kind switch
    {
        TransferKind.Image => "Images",
        TransferKind.File => "Files",
        _ => "Text"
    };

    public Visibility TextVisibility => IsText ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AttachmentVisibility => IsText ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FileVisibility => _kind == TransferKind.File ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImageNameVisibility => _kind == TransferKind.Image ? Visibility.Visible : Visibility.Collapsed;

    public bool HasLongText => IsText && Text.Length > 1200;

    public Visibility ExpandVisibility => HasLongText ? Visibility.Visible : Visibility.Collapsed;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayedText));
        }
    }

    public string DisplayedText
    {
        get
        {
            if (!HasLongText || IsExpanded) return Text;
            var end = char.IsHighSurrogate(Text[1199]) ? 1199 : 1200;
            return Text[..end] + "…";
        }
    }

    public Brush BubbleBrush => (Brush)Application.Current.FindResource(
        Outgoing ? "ButtonBrush" : "SurfaceBrush");

    public bool Outgoing { get; }

    public string PeerName { get; }

    public HorizontalAlignment Alignment =>
        Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public string DirectionLabel => Outgoing ? "To phone" : $"From {PeerName}";

    public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");

    public ICommand ViewImageCommand => _viewImageCommand;

    public ICommand CopyTextCommand => _copyTextCommand;

    public ICommand CopyImageCommand => _copyImageCommand;

    public ICommand OpenFileCommand => _openFileCommand;

    public bool IsText => _kind is null;

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
            ThumbnailLoadTask = _thumbnailEnabled && _kind == TransferKind.Image
                && !string.IsNullOrWhiteSpace(value)
                ? LoadThumbnailAsync(value, revision)
                : Task.CompletedTask;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ViewImageVisibility));
            OnPropertyChanged(nameof(CopyImageVisibility));
            _viewImageCommand.RaiseCanExecuteChanged();
            _copyImageCommand.RaiseCanExecuteChanged();
            _openFileCommand.RaiseCanExecuteChanged();
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
            OnPropertyChanged(nameof(CopyImageVisibility));
            _viewImageCommand.RaiseCanExecuteChanged();
            _copyImageCommand.RaiseCanExecuteChanged();
        }
    }

    public Visibility ThumbnailVisibility =>
        Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ViewImageVisibility =>
        _kind == TransferKind.Image && Thumbnail is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CopyTextVisibility =>
        CanCopyText() ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CopyImageVisibility =>
        CanCopyImage() ? Visibility.Visible : Visibility.Collapsed;

    private bool CanCopyText() =>
        _kind is null
        && _copyText is not null
        && !string.IsNullOrEmpty(Text);

    private void CopyText()
    {
        if (CanCopyText())
        {
            _copyText!(Text);
        }
    }

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

    private bool CanCopyImage() =>
        _kind == TransferKind.Image
        && _copyImage is not null
        && Thumbnail is not null
        && !string.IsNullOrWhiteSpace(LocalPath)
        && File.Exists(LocalPath);

    private void CopyImage()
    {
        if (CanCopyImage())
        {
            _copyImage!(LocalPath!);
        }
    }

    internal void SetThumbnailEnabled(bool enabled)
    {
        if (_thumbnailEnabled == enabled) return;
        _thumbnailEnabled = enabled;
        var revision = Interlocked.Increment(ref _thumbnailRevision);
        Thumbnail = null;
        ThumbnailLoadTask = enabled && _kind == TransferKind.Image && !string.IsNullOrWhiteSpace(_localPath)
            ? LoadThumbnailAsync(_localPath, revision) : Task.CompletedTask;
    }

    private async Task LoadThumbnailAsync(string path, long revision)
    {
        await ThumbnailDecoders.WaitAsync().ConfigureAwait(false);
        ImageSource? thumbnail;
        try
        {
            if (revision != Volatile.Read(ref _thumbnailRevision)) return;
            thumbnail = await Task.Run(() => LoadImagePreview(path, ThumbnailMaxDimension)).ConfigureAwait(false);
        }
        finally { ThumbnailDecoders.Release(); }

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

    internal static BitmapSource? LoadClipboardImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
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
