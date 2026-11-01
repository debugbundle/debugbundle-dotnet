using System.Globalization;
using System.Runtime.CompilerServices;

namespace DebugBundle;

// Runs only on the existing deferred exception worker. New graph inspection uses
// base native getters and bounded aggregate indexing, without virtual formatters,
// frame locals, object fields, symbol lookup or a fabricated capture/throw stack.
internal sealed class ExceptionEvidence
{
    private sealed class ReferenceComparer : IEqualityComparer<Exception>
    {
        internal static readonly ReferenceComparer Instance = new();
        public bool Equals(Exception? left, Exception? right) => ReferenceEquals(left, right);
        public int GetHashCode(Exception value) => RuntimeHelpers.GetHashCode(value);
    }
    private readonly List<object?> _nodes = new();
    private readonly Dictionary<Exception, int> _ids = new(ReferenceComparer.Instance);
    private readonly Dictionary<int, int> _heights = new();
    private readonly HashSet<Exception> _active = new(ReferenceComparer.Instance);
    private readonly bool _fatal;
    private Exception? _retainedInner;
    private string? _retainedInnerMessage;
    private bool _truncated, _unsafe;
    private int _frames;
    private ExceptionEvidence(bool fatal) { _fatal = fatal; }
    internal static Dictionary<string, object?> Collect(Exception exception, string retainedMessage, bool fatal, string? retainedInnerMessage = null)
    {
        var collector = new ExceptionEvidence(fatal);
        collector._retainedInner = exception.InnerException;
        collector._retainedInnerMessage = retainedInnerMessage;
        try { collector.Add(exception, retainedMessage, 0); }
        catch
        {
            collector._nodes.Clear(); collector._unsafe = true;
            collector._nodes.Add(new Dictionary<string, object?> { ["id"] = 0, ["name"] = Clip(exception.GetType().FullName ?? exception.GetType().Name, 256), ["message"] = Clip(retainedMessage, 2048), ["stack"] = null, ["trace_origin"] = "unavailable", ["parsing_status"] = "unavailable", ["frames"] = new List<object?>(), ["relationships"] = new List<object?>() });
        }
        var graph = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["runtime"] = "dotnet",
            ["root_id"] = 0,
            ["nodes"] = collector._nodes,
            ["truncated"] = collector._truncated,
            ["unavailable_reason"] = collector._unsafe ? "unsafe" : null
        };
        Fit(graph, fatal ? 3500 : 31000);
        return graph;
    }
    private int? Add(Exception exception, string? retainedMessage, int depth)
    {
        if (depth > 4 || _active.Contains(exception)) { _truncated = true; return null; }
        if (_ids.TryGetValue(exception, out var shared))
        {
            if (depth + _heights[shared] <= 4) return shared;
            _truncated = true; return null;
        }
        if (_nodes.Count >= (_fatal ? 2 : 8)) { _truncated = true; return null; }
        var id = _nodes.Count; _ids[exception] = id; _heights[id] = 0; _active.Add(exception);
        try
        {
            if (retainedMessage == null && ReferenceEquals(exception, _retainedInner)) retainedMessage = _retainedInnerMessage;
            var name = exception.GetType().FullName ?? exception.GetType().Name;
            var message = retainedMessage ?? ExceptionProjection.Message(exception);
            if (retainedMessage == null && !ExceptionProjection.IsBaseGetter(exception, nameof(Exception.Message))) _unsafe = true;
            string? trace = null;
            if (ExceptionProjection.IsBaseGetter(exception, nameof(Exception.StackTrace)))
            {
                try { trace = exception.StackTrace; } catch { _unsafe = true; }
            }
            else _unsafe = true;
            var safeTrace = trace == null ? null : Clip(trace, 16384);
            var maximum = _fatal ? Math.Max(0, 8 - _frames) : 32;
            var frames = ParseFrames(safeTrace, maximum, out var status, out var clippedFrames);
            _frames += frames.Count;
            var clippedName = Clip(name, 256); var clippedMessage = Clip(message, 2048);
            if (clippedName.Length != name.Length || clippedMessage.Length != message.Length || safeTrace?.Length != trace?.Length || clippedFrames) _truncated = true;
            var node = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = clippedName,
                ["message"] = clippedMessage,
                ["stack"] = status == "unparsed" ? safeTrace : null,
                ["trace_origin"] = string.IsNullOrEmpty(trace) ? "unavailable" : "throw",
                ["parsing_status"] = clippedFrames ? "truncated" : status,
                ["frames"] = frames,
                ["relationships"] = new List<object?>()
            };
            _nodes.Add(node);
            var edges = (List<object?>)node["relationships"]!;
            if (exception is AggregateException aggregate)
            {
                var children = aggregate.InnerExceptions;
                if (children.Count > 5) _truncated = true;
                for (var index = 0; index < Math.Min(5, children.Count); index++) Link(children[index], "aggregate");
            }
            else if (exception.InnerException is { } cause) Link(cause, "cause");
            return id;

            void Link(Exception child, string kind)
            {
                var target = Add(child, null, depth + 1); if (target == null) return;
                if (edges.Cast<Dictionary<string, object?>>().Any(edge => (int)edge["target_id"]! == target && (string)edge["kind"]! == kind)) return;
                edges.Add(new Dictionary<string, object?> { ["kind"] = kind, ["target_id"] = target.Value });
                _heights[id] = Math.Max(_heights[id], _heights[target.Value] + 1);
            }
        }
        finally { _active.Remove(exception); }
    }
    private static List<object?> ParseFrames(string? trace, int maximum, out string status, out bool truncated)
    {
        var frames = new List<object?>(); status = string.IsNullOrEmpty(trace) ? "unavailable" : "unparsed"; truncated = false;
        if (string.IsNullOrEmpty(trace)) return frames;
        foreach (var raw in trace!.Split(new[] { '\n' }, 130))
        {
            var line = raw.Trim(); if (!line.StartsWith("at ", StringComparison.Ordinal)) continue;
            var text = line.Substring(3); var arguments = text.IndexOf('('); if (arguments <= 0) continue;
            if (frames.Count >= maximum) { truncated = true; break; }
            var function = Clip(text.Substring(0, arguments), 256);
            string? file = null; int? number = null;
            var source = text.LastIndexOf(" in ", StringComparison.Ordinal);
            if (source > arguments)
            {
                var location = text.Substring(source + 4); var suffix = location.LastIndexOf(":line ", StringComparison.Ordinal);
                if (suffix > 0 && int.TryParse(location.Substring(suffix + 6), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                { file = Clip(location.Substring(0, suffix), 2048); number = parsed; }
            }
            var classification = function.StartsWith("DebugBundle.", StringComparison.Ordinal) ? "sdk" :
                function.StartsWith("System.", StringComparison.Ordinal) || function.StartsWith("Microsoft.", StringComparison.Ordinal) ? "framework" : "unknown";
            frames.Add(new Dictionary<string, object?> { ["function"] = function, ["file"] = file, ["line"] = number, ["column"] = null, ["classification"] = classification });
            status = "parsed";
        }
        return frames;
    }
    internal static string Clip(string value, int maximum)
    {
        var bytes = 0; var index = 0;
        while (index < value.Length && index < maximum)
        {
            var letter = value[index]; var pair = char.IsHighSurrogate(letter) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);
            var cost = letter <= 127 ? 1 : letter <= 2047 ? 2 : pair ? 4 : 3;
            if (bytes + cost > maximum) break;
            bytes += cost; index += pair ? 2 : 1;
        }
        return index == value.Length ? value : value.Substring(0, index);
    }
    internal static void Fit(Dictionary<string, object?> graph, int maximum)
    {
        var nodes = (List<object?>)graph["nodes"]!;
        while (AcceptingJsonSize.Size(graph) > maximum)
        {
            graph["truncated"] = true;
            var changed = false;
            foreach (var node in nodes.Cast<Dictionary<string, object?>>())
                if (node["stack"] != null) { node["stack"] = null; changed = true; }
            if (changed) continue;
            if (nodes.Count > 1)
            {
                nodes.RemoveAt(nodes.Count - 1);
                foreach (var node in nodes.Cast<Dictionary<string, object?>>())
                    ((List<object?>)node["relationships"]!).RemoveAll(edge => (int)((Dictionary<string, object?>)edge!)["target_id"]! >= nodes.Count);
                continue;
            }
            var root = (Dictionary<string, object?>)nodes[0]!; var frames = (List<object?>)root["frames"]!;
            if (frames.Count > 0) { frames.RemoveAt(frames.Count - 1); root["parsing_status"] = "truncated"; continue; }
            var message = (string)root["message"]!;
            if (message.Length > 0) { root["message"] = Clip(message, message.Length / 2); continue; }
            return;
        }
    }
}
