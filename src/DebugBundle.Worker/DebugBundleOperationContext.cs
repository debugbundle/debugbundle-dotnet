namespace DebugBundle.Worker;

public sealed class DebugBundleOperationContext
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public string OperationName { get; }

    public DebugBundleOperationContext(string operationName)
    {
        OperationName = operationName;
        _values["operation"] = operationName;
    }

    public void Set(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (value == null)
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value;
    }

    public IDictionary<string, object?> Snapshot() => new Dictionary<string, object?>(_values, StringComparer.Ordinal);
}
