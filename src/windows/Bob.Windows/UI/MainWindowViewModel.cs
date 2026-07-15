using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using Bob.Windows.Domain;
using Bob.Windows.Security;
using Bob.Windows.Storage;
using Bob.Windows.Transport;
using Microsoft.Win32;

namespace Bob.Windows.UI;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly Brush ConnectedBrush = Freeze("#276B45");
    private static readonly Brush WaitingBrush = Freeze("#7A4D00");
    private readonly SessionCoordinator _sessions;
    private readonly TransferCoordinator _transfers;
    private readonly ReceiveDirectorySettings _receiveDirectorySettings;
    private readonly AsyncRelayCommand _sendTextCommand;
    private readonly AsyncRelayCommand _chooseImageCommand;
    private readonly AsyncRelayCommand _chooseFileCommand;
    private readonly AsyncRelayCommand _chooseReceiveDirectoryCommand;
    private readonly AsyncRelayCommand _openReceiveDirectoryCommand;
    private readonly Dictionary<Guid, TimelineItemViewModel> _outgoing = new();
    private readonly Dictionary<Guid, TimelineItemViewModel> _transferItems = new();
    private readonly Dictionary<Guid, long> _transferRevisions = new();
    private string _draftText = string.Empty;
    private string _connectionText = "Waiting for phone";
    private string _connectionDetail = $"BOB server is ready on port {BobProtocol.DefaultPort}.";
    private Brush _connectionBrush = WaitingBrush;
    private string _composerHint = "Connect a phone to send text";
    private string _settingsHint = "New received files are saved in this folder.";
    private long _connectionRevision = -1;

    public MainWindowViewModel(
        SessionCoordinator sessions,
        TransferCoordinator transfers,
        ReceiveDirectorySettings receiveDirectorySettings,
        ServerIdentity identity)
    {
        _sessions = sessions;
        _transfers = transfers;
        _receiveDirectorySettings = receiveDirectorySettings;
        ServerDetails = $"HTTPS :{BobProtocol.DefaultPort} · {identity.CertificatePin[..10]}…";
        _sendTextCommand = new AsyncRelayCommand(SendTextAsync, CanSendText);
        _chooseImageCommand = new AsyncRelayCommand(ChooseImagesAsync, CanChooseFiles);
        _chooseFileCommand = new AsyncRelayCommand(ChooseFilesAsync, CanChooseFiles);
        _chooseReceiveDirectoryCommand = new AsyncRelayCommand(
            ChooseReceiveDirectoryAsync,
            () => true);
        _openReceiveDirectoryCommand = new AsyncRelayCommand(
            OpenReceiveDirectoryAsync,
            () => true);

        sessions.ConnectionChanged += OnConnectionChanged;
        sessions.TextReceived += OnTextReceived;
        sessions.TextAcknowledged += OnTextAcknowledged;
        transfers.TransferChanged += OnTransferChanged;
        ApplyConnectionSnapshot(sessions.Snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = new();

    public string ServerDetails { get; }

    public ICommand SendTextCommand => _sendTextCommand;

    public ICommand ChooseImageCommand => _chooseImageCommand;

    public ICommand ChooseFileCommand => _chooseFileCommand;

    public ICommand ChooseReceiveDirectoryCommand => _chooseReceiveDirectoryCommand;

    public ICommand OpenReceiveDirectoryCommand => _openReceiveDirectoryCommand;

    public string ReceiveDirectory => _receiveDirectorySettings.ReceiveDirectory;

    public string DraftText
    {
        get => _draftText;
        set
        {
            if (_draftText == value)
            {
                return;
            }

            _draftText = value;
            OnPropertyChanged();
            _sendTextCommand.RaiseCanExecuteChanged();

            if (Encoding.UTF8.GetByteCount(value) > BobProtocol.MaxTextBytes)
            {
                ComposerHint = "Text exceeds the 256 KiB limit";
            }
            else if (_sessions.Snapshot.IsConnected)
            {
                ComposerHint = "Press Enter for a new line; click Send Text to send";
            }
        }
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetField(ref _connectionText, value);
    }

    public Brush ConnectionBrush
    {
        get => _connectionBrush;
        private set => SetField(ref _connectionBrush, value);
    }

    public string ConnectionDetail
    {
        get => _connectionDetail;
        private set => SetField(ref _connectionDetail, value);
    }

    public string ComposerHint
    {
        get => _composerHint;
        private set => SetField(ref _composerHint, value);
    }

    public string SettingsHint
    {
        get => _settingsHint;
        private set => SetField(ref _settingsHint, value);
    }

    public Visibility EmptyStateVisibility =>
        Timeline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool CanSendText() =>
        _sessions.Snapshot.IsConnected
        && !string.IsNullOrWhiteSpace(DraftText)
        && Encoding.UTF8.GetByteCount(DraftText) <= BobProtocol.MaxTextBytes;

    private bool CanChooseFiles() => _sessions.Snapshot.IsConnected;

    private Task ChooseReceiveDirectoryAsync()
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = ReceiveDirectory,
            Multiselect = false,
            Title = "Choose where received files are saved"
        };
        if (dialog.ShowDialog() != true)
        {
            return Task.CompletedTask;
        }

        try
        {
            _receiveDirectorySettings.SetReceiveDirectory(dialog.FolderName);
            OnPropertyChanged(nameof(ReceiveDirectory));
            SettingsHint = "New transfers will be saved in this folder.";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            SettingsHint = "BOB could not use that folder. Choose another folder.";
        }

        return Task.CompletedTask;
    }

    private Task OpenReceiveDirectoryAsync()
    {
        try
        {
            Directory.CreateDirectory(ReceiveDirectory);
            OpenWithShell(ReceiveDirectory);
            SettingsHint = "Receive folder opened.";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidOperationException)
        {
            SettingsHint = "BOB could not open the receive folder.";
        }

        return Task.CompletedTask;
    }

    private Task ChooseImagesAsync() => ChooseFilesAsync(
        TransferKind.Image,
        "Images|*.avif;*.bmp;*.gif;*.heic;*.jpeg;*.jpg;*.png;*.webp|All files|*.*");

    private Task ChooseFilesAsync() => ChooseFilesAsync(
        TransferKind.File,
        "All files|*.*");

    private async Task ChooseFilesAsync(TransferKind kind, string filter)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = true,
            Filter = filter,
            Title = kind == TransferKind.Image
                ? "Choose images to send"
                : "Choose files to send"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var queued = 0;
        foreach (var path in dialog.FileNames)
        {
            try
            {
                _transfers.EnqueueOutgoing(path, kind);
                queued++;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
            {
                ComposerHint = $"Could not queue {Path.GetFileName(path)}.";
            }
        }

        if (queued == 0)
        {
            return;
        }

        try
        {
            await _sessions.SendNextTransferOfferAsync(_transfers);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or System.Net.WebSockets.WebSocketException)
        {
            ComposerHint = "Could not offer the selected content. Check the connection.";
        }
    }

    private async Task SendTextAsync()
    {
        var text = DraftText;
        var textId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var item = new TimelineItemViewModel(
            textId,
            text,
            createdAt,
            outgoing: true,
            status: "Sending",
            peerName: _sessions.Snapshot.PeerName ?? "phone");

        Timeline.Add(item);
        _outgoing[textId] = item;
        OnPropertyChanged(nameof(EmptyStateVisibility));
        DraftText = string.Empty;

        try
        {
            await _sessions.SendTextAsync(textId, text, createdAt);
            if (_outgoing.ContainsKey(textId))
            {
                item.Status = "Awaiting confirmation";
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or IOException
            or System.Net.WebSockets.WebSocketException)
        {
            if (_outgoing.Remove(textId))
            {
                item.Status = "Failed to send";
                ComposerHint = "Could not send text. Check the connection and try again.";
            }
        }
    }

    private void OnConnectionChanged(object? sender, ConnectionSnapshot snapshot) =>
        Dispatch(() => ApplyConnectionSnapshot(snapshot));

    private void ApplyConnectionSnapshot(ConnectionSnapshot snapshot)
    {
        if (snapshot.Revision < _connectionRevision)
        {
            return;
        }

        _connectionRevision = snapshot.Revision;
        ConnectionDetail = snapshot.Detail;
        switch (snapshot.Phase)
        {
            case SessionPhase.Connected:
                ConnectionText = $"Phone connected · {snapshot.PeerName}";
                ConnectionBrush = ConnectedBrush;
                ComposerHint = "Press Enter for a new line; click Send Text to send";
                break;
            case SessionPhase.AwaitingHello:
                ConnectionText = "Phone is verifying";
                ConnectionBrush = WaitingBrush;
                ComposerHint = "Establishing a secure session";
                break;
            default:
                ConnectionText = "Waiting for phone";
                ConnectionBrush = WaitingBrush;
                ComposerHint = "Connect a phone to send text";
                break;
        }

        _sendTextCommand.RaiseCanExecuteChanged();
        _chooseImageCommand.RaiseCanExecuteChanged();
        _chooseFileCommand.RaiseCanExecuteChanged();
    }

    private void OnTextReceived(object? sender, IncomingText incoming) =>
        Dispatch(() =>
        {
            Timeline.Add(new TimelineItemViewModel(
                incoming.TextId,
                incoming.Text,
                incoming.CreatedAt,
                outgoing: false,
                status: "Received",
                peerName: incoming.PeerName));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        });

    private void OnTextAcknowledged(object? sender, Guid textId) =>
        Dispatch(() =>
        {
            if (_outgoing.TryGetValue(textId, out var item))
            {
                item.Status = "Delivered";
                _outgoing.Remove(textId);
            }
        });

    private void OnTransferChanged(object? sender, TransferSnapshot snapshot) =>
        Dispatch(() => ApplyTransferSnapshot(snapshot));

    private void ApplyTransferSnapshot(TransferSnapshot snapshot)
    {
        if (_transferRevisions.TryGetValue(snapshot.TransferId, out var currentRevision)
            && snapshot.Revision <= currentRevision)
        {
            return;
        }

        _transferRevisions[snapshot.TransferId] = snapshot.Revision;
        var detail = $"{(snapshot.Kind == TransferKind.Image ? "Image" : "File")} · "
            + (snapshot.Size is long size ? FormatBytes(size) : "Unknown size");
        if (!_transferItems.TryGetValue(snapshot.TransferId, out var item))
        {
            item = new TimelineItemViewModel(
                snapshot.TransferId,
                snapshot.Name,
                snapshot.CreatedAt,
                outgoing: snapshot.Direction == TransferDirection.WindowsToAndroid,
                status: TransferStatus(snapshot),
                peerName: _sessions.Snapshot.PeerName ?? "phone",
                detail: detail,
                kind: snapshot.Kind,
                localPath: snapshot.LocalPath,
                openImage: ShowImagePreview);
            _transferItems.Add(snapshot.TransferId, item);
            Timeline.Add(item);
            OnPropertyChanged(nameof(EmptyStateVisibility));
            return;
        }

        item.Detail = detail;
        item.Status = TransferStatus(snapshot);
        item.LocalPath = snapshot.LocalPath;
    }

    private async void ShowImagePreview(string path)
    {
        try
        {
            var image = await Task.Run(
                () => TimelineItemViewModel.LoadImagePreview(
                    path,
                    maximumDimension: 2_048));
            if (image is null)
            {
                ComposerHint = "BOB could not decode this image preview.";
                return;
            }

            var displayName = Path.GetFileName(path);
            var preview = new ImagePreviewWindow(
                string.IsNullOrWhiteSpace(displayName) ? "Image" : displayName,
                image);
            var owner = Application.Current?.MainWindow;
            if (owner is { IsVisible: true })
            {
                preview.Owner = owner;
            }

            preview.ShowDialog();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            ComposerHint = "BOB could not show this image preview.";
        }
    }

    private static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });

    private static string TransferStatus(TransferSnapshot snapshot) => snapshot.State switch
    {
        TransferState.Queued => "Queued",
        TransferState.Offered => "Waiting for phone",
        TransferState.Accepted => "Starting",
        TransferState.Transferring => snapshot.Size is long total
            ? $"Transferring · {FormatBytes(snapshot.BytesTransferred)} / {FormatBytes(total)}"
            : $"Transferring · {FormatBytes(snapshot.BytesTransferred)}",
        TransferState.Verifying => "Verifying",
        TransferState.Completed => "Completed",
        TransferState.Failed => $"Failed · {snapshot.ErrorMessage ?? snapshot.ErrorCode}",
        TransferState.Canceled => "Canceled",
        _ => snapshot.State.ToString()
    };

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

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
