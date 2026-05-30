namespace DebugBundle;

internal sealed class ProbeBuffer
{
    private readonly int _maxLabels;
    private readonly int _maxEntriesPerLabel;
    private readonly Dictionary<string, Queue<ProbeEntry>> _entries = new(StringComparer.Ordinal);

    public ProbeBuffer(int maxLabels, int maxEntriesPerLabel)
    {
        _maxLabels = maxLabels;
        _maxEntriesPerLabel = maxEntriesPerLabel;
    }

    public void Record(string label, object? data, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        if (!_entries.TryGetValue(label, out var queue))
        {
            if (_entries.Count >= _maxLabels)
            {
                return;
            }

            queue = new Queue<ProbeEntry>();
            _entries[label] = queue;
        }

        queue.Enqueue(new ProbeEntry
        {
            Label = label,
            OccurredAt = occurredAt,
            Data = data
        });

        while (queue.Count > _maxEntriesPerLabel)
        {
            queue.Dequeue();
        }
    }

    public IReadOnlyList<Dictionary<string, object?>> Snapshot()
    {
        return _entries.Values
            .SelectMany(queue => queue)
            .Select(entry => new Dictionary<string, object?>
            {
                ["label"] = entry.Label,
                ["occurred_at"] = entry.OccurredAt.ToString("O"),
                ["data"] = entry.Data
            })
            .ToArray();
    }

    private sealed class ProbeEntry
    {
        public string Label { get; set; } = string.Empty;
        public DateTimeOffset OccurredAt { get; set; }
        public object? Data { get; set; }
    }
}
