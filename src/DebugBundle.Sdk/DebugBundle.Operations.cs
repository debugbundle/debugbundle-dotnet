namespace DebugBundle;

public static partial class DebugBundle
{
    public static DebugBundleOperationScope BeginOperation(DebugBundleOperationOptions? options = null) => Current?.BeginOperation(options) ?? DebugBundleOperationScope.None();
    public static T RunWithOperationContext<T>(DebugBundleOperationOptions options, Func<T> callback) => DebugBundleOperationExtensions.RunWithOperationContext(Current, options, callback);
    public static void RunWithOperationContext(DebugBundleOperationOptions options, Action callback) => DebugBundleOperationExtensions.RunWithOperationContext(Current, options, callback);
    public static Task<T> RunWithOperationContextAsync<T>(DebugBundleOperationOptions options, Func<Task<T>> callback) => DebugBundleOperationExtensions.RunWithOperationContextAsync(Current, options, callback);
    public static Task RunWithOperationContextAsync(DebugBundleOperationOptions options, Func<Task> callback) => DebugBundleOperationExtensions.RunWithOperationContextAsync(Current, options, callback);
}
