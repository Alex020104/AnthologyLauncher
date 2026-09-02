namespace Anthology.Launcher;

/// <summary>
/// Coordinates launcher operations that must not be interrupted by a native
/// window close. Restart shutdown is an explicit, short-lived exception.
/// </summary>
public sealed class LauncherOperationGate
{
    private readonly object _sync = new();
    private int _activeTransfers;
    private bool _restartShutdownAuthorized;

    public bool ShouldBlockWindowClose
    {
        get
        {
            lock (_sync)
            {
                return _activeTransfers > 0 && !_restartShutdownAuthorized;
            }
        }
    }

    public IDisposable EnterTransfer()
    {
        lock (_sync)
        {
            if (_activeTransfers == 0)
            {
                _restartShutdownAuthorized = false;
            }

            _activeTransfers++;
        }

        return new TransferLease(this);
    }

    public void AuthorizeRestartShutdown()
    {
        lock (_sync)
        {
            _restartShutdownAuthorized = true;
        }
    }

    public void RevokeRestartShutdownAuthorization()
    {
        lock (_sync)
        {
            _restartShutdownAuthorized = false;
        }
    }

    private void ExitTransfer()
    {
        lock (_sync)
        {
            if (_activeTransfers == 0)
            {
                return;
            }

            _activeTransfers--;
            if (_activeTransfers == 0)
            {
                _restartShutdownAuthorized = false;
            }
        }
    }

    private sealed class TransferLease(LauncherOperationGate owner) : IDisposable
    {
        private LauncherOperationGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitTransfer();
    }
}
