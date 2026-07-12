using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using Bob.Windows.Domain;
using Bob.Windows.Security;
using Bob.Windows.Transport;

namespace Bob.Windows.UI;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly Brush ConnectedBrush = Freeze("#276B45");
    private static readonly Brush WaitingBrush = Freeze("#7A4D00");
    private readonly SessionCoordinator _sessions;
    private readonly AsyncRelayCommand _sendTextCommand;
    private readonly Dictionary<Guid, TimelineItemViewModel> _outgoing = new();
    private string _draftText = string.Empty;
    private string _connectionText = "Waiting for phone";
    private Brush _connectionBrush = WaitingBrush;
    private string _composerHint = "Connect a phone to send text";

    public MainWindowViewModel(
        SessionCoordinator sessions,
        ServerIdentity identity)
    {
        _sessions = sessions;
        ServerDetails = $"HTTPS :{BobProtocol.DefaultPort} · {identity.CertificatePin[..10]}…";
        _sendTextCommand = new AsyncRelayCommand(SendTextAsync, CanSendText);

        sessions.ConnectionChanged += OnConnectionChanged;
        sessions.TextReceived += OnTextReceived;
        sessions.TextAcknowledged += OnTextAcknowledged;
        ApplyConnectionSnapshot(sessions.Snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = new();

    public string ServerDetails { get; }

    public ICommand SendTextCommand => _sendTextCommand;

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

    public string ComposerHint
    {
        get => _composerHint;
        private set => SetField(ref _composerHint, value);
    }

    public Visibility EmptyStateVisibility =>
        Timeline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool CanSendText() =>
        _sessions.Snapshot.IsConnected
        && !string.IsNullOrWhiteSpace(DraftText)
        && Encoding.UTF8.GetByteCount(DraftText) <= BobProtocol.MaxTextBytes;

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
        switch (snapshot.Phase)
        {
            case SessionPhase.Connected:
                ConnectionText = $"Connected · {snapshot.PeerName}";
                ConnectionBrush = ConnectedBrush;
                ComposerHint = "Press Enter for a new line; click Send Text to send";
                break;
            case SessionPhase.AwaitingHello:
                ConnectionText = "Verifying phone";
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
