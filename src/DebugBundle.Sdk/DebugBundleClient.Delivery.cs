namespace DebugBundle;

public sealed partial class DebugBundleClient
{
    private TaskCompletionSource<bool>? _senderCompletion;
    private bool _senderRunning;
    private bool _sendAgain;
    private int _explicitFlushWaiters;
    private const int MaxExplicitFlushWaiters = 64;

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (_sync)
        {
            if (_disposed || _explicitFlushWaiters >= MaxExplicitFlushWaiters) return;
            _explicitFlushWaiters++;
            try
            {
                RequestFlushLocked();
                pending = _senderCompletion!.Task;
            }
            catch
            {
                _explicitFlushWaiters--;
                return;
            }
        }

        try
        {
            var timeout = Task.Delay(_options.RequestTimeout, cancellationToken);
            var completed = await Task.WhenAny(pending, timeout).ConfigureAwait(false);
            if (completed == timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            await pending.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) _explicitFlushWaiters--;
        }
    }

    private void RequestFlushLocked()
    {
        if (_disposed) return;
        _sendAgain = true;
        if (_senderRunning) return;

        _senderRunning = true;
        _senderCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(DrainSenderAsync);
    }

    private async Task DrainSenderAsync()
    {
        try
        {
            while (true)
            {
                lock (_sync)
                {
                    if (!_sendAgain || _disposed)
                    {
                        _senderRunning = false;
                        _senderCompletion?.TrySetResult(true);
                        return;
                    }
                    _sendAgain = false;
                }
                await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                _senderRunning = false;
                _senderCompletion?.TrySetResult(false);
            }
        }
    }
}
