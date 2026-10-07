using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bob.Windows.Domain;
using System.Diagnostics;

namespace Bob.Windows.UI;

public sealed class TimelineItemViewModel : INotifyPropertyChanged
{
    private const int ThumbnailMaxDimension = 360;
    private string _status;
    private string? _detail;
    private readonly TransferKind? _kind;
    private readonly Action<string>? _openImage;
    private readonly Action<string>? _copyText;
    private readonly Action<string>? _copyImage;
    private readonly RelayCommand _viewImageCommand;
    private readonly RelayCommand _copyTextCommand;
    private readonly RelayCommand _copyImageCommand;
    private readonly RelayCommand _expandTextCommand;
    private readonly string _textPreview;
    private bool _fullTextShown;
    private string? _localPath;
    private ImageSource? _thumbnail;
    private long _thumbnailRevision;
    private readonly Action<string>? _openFile;
    private readonly AsyncRelayCommand _cancelCommand;
    private readonly AsyncRelayCommand _retryCommand;
    private readonly RelayCommand _openFileCommand;
    private TransferState? _transferState;
    private bool _retryable;
    private bool _cancelPending;
    private long _bytesTransferred;
    private long? _totalBytes;
    private long _lastSampleTime;
    private long _lastSampleBytes;
    private double _bytesPerSecond;

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
        Func<Guid, Task>? cancel = null,
        Func<Guid, Task>? retry = null,
        Action<string>? openFile = null,
        DateTimeOffset? recordedAt = null)
    {
        TextId = textId;
        Text = text;
        _textPreview = CreateTextPreview(text);
        CreatedAt = createdAt;
        RecordedAt = recordedAt ?? DateTimeOffset.UtcNow;
        Outgoing = outgoing;
        _status = status;
        _detail = detail;
        _kind = kind;
        _openImage = openImage;
        _copyText = copyText;
        _copyImage = copyImage;
        _openFile = openFile;
        _cancelCommand = new AsyncRelayCommand(
            () => cancel?.Invoke(TextId) ?? Task.CompletedTask,
            () => cancel is not null && CanCancel);
        _retryCommand = new AsyncRelayCommand(
            () => retry?.Invoke(TextId) ?? Task.CompletedTask,
            () => retry is not null && CanRetry);
        _openFileCommand = new RelayCommand(OpenFile, CanOpenFile);
        _viewImageCommand = new RelayCommand(ViewImage, CanViewImage);
        _copyTextCommand = new RelayCommand(CopyText, CanCopyText);
        _expandTextCommand = new RelayCommand(ToggleFullText, () => CanExpandText);
        _copyImageCommand = new RelayCommand(CopyImage, CanCopyImage);
        PeerName = peerName;
        LocalPath = localPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid TextId { get; }

    public string Text { get; }

    public string DisplayText => IsText && !_fullTextShown ? _textPreview : Text;

    public string TextExpansionLabel => _fullTextShown ? "Collapse text" : "Read full text";

    public Visibility TextExpansionVisibility => CanExpandText ? Visibility.Visible : Visibility.Collapsed;

    public ICommand ExpandTextCommand => _expandTextCommand;

    private bool CanExpandText => IsText && !ReferenceEquals(_textPreview, Text);

    private void ToggleFullText()
    {
        _fullTextShown = !_fullTextShown;
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TextExpansionLabel));
    }

    private static string CreateTextPreview(string text)
    {
        var end = Math.Min(text.Length, 1_024);
        var lineStart = 0;
        for (var line = 0; line < 8; line++)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0 || newline >= end)
            {
                break;
            }
            lineStart = newline + 1;
            if (line == 7)
            {
                end = lineStart;
            }
        }
        if (end == text.Length)
        {
            return text;
        }
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }
        return text[..end] + "…";
    }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset RecordedAt { get; }

    public bool Outgoing { get; }

    public string PeerName { get; }

    public HorizontalAlignment Alignment =>
        Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public string DirectionLabel => Outgoing ? "THIS COMPUTER" : $"PHONE · {PeerName}";

    public string TimeLabel => CreatedAt.ToLocalTime().ToString("MMM d · HH:mm", CultureInfo.InvariantCulture);

    public ICommand CancelCommand => _cancelCommand;
    public ICommand RetryCommand => _retryCommand;
    public ICommand OpenFileCommand => _openFileCommand;
    private bool CanCancel => !_cancelPending && _transferState is TransferState.Queued
        or TransferState.Offered or TransferState.Accepted
        or TransferState.Transferring or TransferState.Verifying;
    private bool CanRetry => Outgoing && !_cancelPending && _retryable
        && _transferState is TransferState.Failed or TransferState.Canceled;
    public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => CanRetry ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OpenFileVisibility => CanOpenFile() ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => _transferState is TransferState.Accepted
        or TransferState.Transferring or TransferState.Verifying
        ? Visibility.Visible : Visibility.Collapsed;
    public bool ProgressIndeterminate => _totalBytes is null || _transferState == TransferState.Verifying;
    public double ProgressValue => _totalBytes is > 0
        ? Math.Clamp(100d * _bytesTransferred / _totalBytes.Value, 0d, 100d)
        : _transferState == TransferState.Transferring ? 0d : 100d;
    public string RateLabel
    {
        get
        {
            if (_transferState != TransferState.Transferring || _bytesPerSecond <= 0)
            {
                return string.Empty;
            }

            var rate = $"{FormatBytes((long)_bytesPerSecond)}/s";
            if (_totalBytes is long total && total > _bytesTransferred)
            {
                var seconds = (total - _bytesTransferred) / _bytesPerSecond;
                rate += seconds >= 3600
                    ? $" · {seconds / 3600:0.#} h left"
                    : seconds >= 60
                        ? $" · {seconds / 60:0} min left"
                        : $" · {Math.Max(1, seconds):0} sec left";
            }

            return rate;
        }
    }

    public Visibility RateVisibility => string.IsNullOrEmpty(RateLabel) ? Visibility.Collapsed : Visibility.Visible;

    public void ApplyTransferProgress(TransferSnapshot snapshot)
    {
        var now = Stopwatch.GetTimestamp();
        if (snapshot.State == TransferState.Transferring && _transferState == TransferState.Transferring
            && _lastSampleTime != 0
            && snapshot.BytesTransferred > _lastSampleBytes)
        {
            var seconds = Stopwatch.GetElapsedTime(_lastSampleTime, now).TotalSeconds;
            if (seconds > 0.1)
            {
                var sample = (snapshot.BytesTransferred - _lastSampleBytes) / seconds;
                _bytesPerSecond = _bytesPerSecond == 0 ? sample : 0.7 * _bytesPerSecond + 0.3 * sample;
                _lastSampleTime = now;
                _lastSampleBytes = snapshot.BytesTransferred;
            }
        }
        else if (_lastSampleTime == 0 || _transferState != TransferState.Transferring)
        {
            _lastSampleTime = now;
            _lastSampleBytes = snapshot.BytesTransferred;
        }

        _transferState = snapshot.State;
        _cancelPending = snapshot.CancelPending;
        _retryable = snapshot.Retryable || snapshot.State == TransferState.Canceled && !_cancelPending;
        _bytesTransferred = snapshot.BytesTransferred;
        _totalBytes = snapshot.Size;
        foreach (var property in new[] { nameof(CancelVisibility), nameof(RetryVisibility),
            nameof(OpenFileVisibility), nameof(ProgressVisibility), nameof(ProgressIndeterminate),
            nameof(ProgressValue), nameof(RateLabel), nameof(RateVisibility), nameof(Status) })
        {
            OnPropertyChanged(property);
        }
        _cancelCommand.RaiseCanExecuteChanged();
        _retryCommand.RaiseCanExecuteChanged();
        _openFileCommand.RaiseCanExecuteChanged();
    }

    public ICommand ViewImageCommand => _viewImageCommand;

    public ICommand CopyTextCommand => _copyTextCommand;

    public ICommand CopyImageCommand => _copyImageCommand;

    public bool IsText => _kind is null;

    internal Task ThumbnailLoadTask { get; private set; } = Task.CompletedTask;

    public string Status
    {
        get => _cancelPending ? "Canceling..." : _status;
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
            OnPropertyChanged(nameof(CopyImageVisibility));
            _viewImageCommand.RaiseCanExecuteChanged();
            _copyImageCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(OpenFileVisibility));
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

    private bool CanOpenFile() => _kind == TransferKind.File
        && _transferState == TransferState.Completed && _openFile is not null
        && !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);

    private void OpenFile()
    {
        if (CanOpenFile())
        {
            _openFile!(LocalPath!);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
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
