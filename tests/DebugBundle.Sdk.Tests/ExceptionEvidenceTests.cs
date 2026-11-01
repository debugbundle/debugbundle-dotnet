using DebugBundle;

namespace DebugBundle.Sdk.Tests;

public sealed class ExceptionEvidenceTests
{
    [Fact]
    public void NativeInnerAndAggregateRelationshipsRetainThrowFramesWithoutArguments()
    {
        Exception leaf;
        try { throw new ArgumentException("fictional leaf"); } catch (Exception error) { leaf = error; }
        var aggregate = new AggregateException("fictional aggregate", leaf, new InvalidOperationException("second"));
        var graph = ExceptionEvidence.Collect(aggregate, "retained root message", false);
        var nodes = Assert.IsType<List<object?>>(graph["nodes"]);
        Assert.Equal(3, nodes.Count);
        var root = Assert.IsType<Dictionary<string, object?>>(nodes[0]);
        Assert.Equal("retained root message", root["message"]);
        Assert.Equal(2, Assert.IsType<List<object?>>(root["relationships"]).Count);
        var child = Assert.IsType<Dictionary<string, object?>>(nodes[1]);
        Assert.Equal("throw", child["trace_origin"]);
        var frame = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<List<object?>>(child["frames"])[0]);
        Assert.DoesNotContain("(", Assert.IsType<string>(frame["function"]));
        Assert.Contains(nameof(NativeInnerAndAggregateRelationshipsRetainThrowFramesWithoutArguments), Assert.IsType<string>(frame["function"]));
    }
    [Fact]
    public void SharedNativeErrorsAreDeduplicatedAndFatalMetadataIsSmaller()
    {
        var leaf = new Exception("shared");
        var graph = ExceptionEvidence.Collect(new AggregateException("root", leaf, leaf), "root", false);
        var nodes = Assert.IsType<List<object?>>(graph["nodes"]);
        Assert.Equal(2, nodes.Count);
        Assert.Single(Assert.IsType<List<object?>>(Assert.IsType<Dictionary<string, object?>>(nodes[0])["relationships"]));
        var fatal = ExceptionEvidence.Collect(new AggregateException(Enumerable.Range(0, 20).Select(index => new Exception(new string('x', 2048)))), "fatal", true);
        Assert.True(Assert.IsType<List<object?>>(fatal["nodes"]).Count <= 2);
        Assert.True(AcceptingJsonSize.Size(fatal) <= 3500);
        Assert.True(Assert.IsType<bool>(fatal["truncated"]));
    }
    private sealed class ForeignStack : Exception
    {
        internal int Calls;
        public override string? StackTrace { get { Calls++; return "must not inspect"; } }
    }
    [Fact]
    public void NewGraphInspectionDoesNotInvokeOverriddenStackGetters()
    {
        var error = new ForeignStack();
        var graph = ExceptionEvidence.Collect(error, "already retained", false);
        Assert.Equal(0, error.Calls);
        Assert.Equal("unsafe", graph["unavailable_reason"]);
        var root = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(graph["nodes"])));
        Assert.Equal("unavailable", root["trace_origin"]);
    }
}
