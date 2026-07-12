namespace Bob.Windows.Domain;

public enum SessionPhase
{
    Offline,
    AwaitingHello,
    Connected
}

public sealed class SessionStateMachine
{
    public SessionPhase Phase { get; private set; } = SessionPhase.Offline;

    public void BeginHandshake() => Transition(SessionPhase.AwaitingHello);

    public void CompleteHandshake() => Transition(SessionPhase.Connected);

    public void Reset() => Phase = SessionPhase.Offline;

    public static bool IsAllowed(SessionPhase from, SessionPhase to) =>
        (from, to) switch
        {
            (SessionPhase.Offline, SessionPhase.AwaitingHello) => true,
            (SessionPhase.AwaitingHello, SessionPhase.Connected) => true,
            (_, SessionPhase.Offline) => true,
            _ => false
        };

    private void Transition(SessionPhase target)
    {
        if (!IsAllowed(Phase, target))
        {
            throw new InvalidOperationException(
                $"Invalid session transition: {Phase} -> {target}.");
        }

        Phase = target;
    }
}
