using Hangfire.Common;
using Hangfire.Server;

namespace DebugBundle.Hangfire;

public sealed class DebugBundleHangfireFilter : IServerFilter
{
    private readonly IDebugBundleClient _client;

    public DebugBundleHangfireFilter()
        : this(new StaticDebugBundleClient())
    {
    }

    public DebugBundleHangfireFilter(IDebugBundleClient client)
    {
        _client = client;
    }

    public void OnPerforming(PerformingContext context)
    {
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context?.Exception == null)
        {
            return;
        }

        try
        {
            _client.CaptureException(context.Exception, BuildContext(context));
        }
        catch
        {
            // SDK integrations are fail-open and must never affect job failure handling.
        }
    }

    private static Dictionary<string, object?> BuildContext(PerformedContext context)
    {
        var job = context.BackgroundJob?.Job;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["framework"] = "hangfire",
            ["job_id"] = context.BackgroundJob?.Id,
            ["job_type"] = job?.Type.FullName,
            ["job_method"] = job?.Method.Name,
            ["queue"] = GetQueue(context),
            ["retry_count"] = TryGetJobParameter<int?>(context, "RetryCount"),
            ["canceled"] = context.Canceled,
            ["argument_summaries"] = SummarizeArguments(job)
        };

        return values;
    }

    private static string? GetQueue(PerformedContext context)
    {
        if (context.Items.TryGetValue("Queue", out var queue) && queue is string queueName)
        {
            return queueName;
        }

        return null;
    }

    private static T? TryGetJobParameter<T>(PerformedContext context, string name)
    {
        try
        {
            return context.GetJobParameter<T>(name);
        }
        catch
        {
            return default;
        }
    }

    private static IReadOnlyList<Dictionary<string, object?>> SummarizeArguments(Job? job)
    {
        if (job?.Args == null || job.Args.Count == 0)
        {
            return Array.Empty<Dictionary<string, object?>>();
        }

        var summaries = new List<Dictionary<string, object?>>(job.Args.Count);
        for (var i = 0; i < job.Args.Count; i++)
        {
            var argument = job.Args[i];
            summaries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = i,
                ["type"] = argument?.GetType().FullName,
                ["kind"] = ArgumentKind(argument)
            });
        }

        return summaries;
    }

    private static string ArgumentKind(object? value)
    {
        return value switch
        {
            null => "null",
            string => "string",
            Array => "array",
            System.Collections.ICollection => "collection",
            _ when value.GetType().IsPrimitive => "primitive",
            _ => "object"
        };
    }

}
