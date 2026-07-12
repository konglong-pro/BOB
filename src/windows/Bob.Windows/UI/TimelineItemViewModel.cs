using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Bob.Windows.UI;

public sealed class TimelineItemViewModel : INotifyPropertyChanged
{
    private string _status;

    public TimelineItemViewModel(
        Guid textId,
        string text,
        DateTimeOffset createdAt,
        bool outgoing,
        string status,
        string peerName)
    {
        TextId = textId;
        Text = text;
        CreatedAt = createdAt;
        Outgoing = outgoing;
        _status = status;
        PeerName = peerName;
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
