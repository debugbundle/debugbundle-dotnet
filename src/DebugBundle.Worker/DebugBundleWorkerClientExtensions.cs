namespace DebugBundle.Worker;

public static class DebugBundleWorkerClientExtensions
{
    public static async Task CaptureOperationAsync(
        this IDebugBundleClient client,
        string operationName,
        Func<DebugBundleOperationContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (operation == null)
        {
            return;
        }

        var operationContext = new DebugBundleOperationContext(operationName);
        using var scope = client.BeginScope(new Dictionary<string, object?> { ["operation"] = operationContext.OperationName });
        try
        {
            await operation(operationContext, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            client.CaptureException(exception, operationContext.Snapshot());
            throw;
        }
    }

    public static async Task<T> CaptureOperationAsync<T>(
        this IDebugBundleClient client,
        string operationName,
        Func<DebugBundleOperationContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (operation == null)
        {
            return default!;
        }

        var operationContext = new DebugBundleOperationContext(operationName);
        using var scope = client.BeginScope(new Dictionary<string, object?> { ["operation"] = operationContext.OperationName });
        try
        {
            return await operation(operationContext, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            client.CaptureException(exception, operationContext.Snapshot());
            throw;
        }
    }
}
