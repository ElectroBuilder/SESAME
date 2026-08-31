namespace Sesame.Services.Mii;

/// <summary>Single-owner gate used by the UI and exit/disconnect guards.</summary>
public sealed class MiiOperationLock
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) != 0;
    public bool CanStartMutation => !IsActive;
    public bool CanDisconnect => !IsActive;
    public bool CanClose => !IsActive;

    public bool TryBegin()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return false;
        StateChanged?.Invoke(true);
        return true;
    }

    public void End()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
            throw new InvalidOperationException("No Mii operation owns the lock.");
        StateChanged?.Invoke(false);
    }

    public event Action<bool>? StateChanged;
}
