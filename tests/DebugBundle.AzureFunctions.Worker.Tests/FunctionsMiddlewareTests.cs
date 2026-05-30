using System.Collections.Immutable;
using System.Collections;
using DebugBundle.AzureFunctions;
using Microsoft.Azure.Functions.Worker;

namespace DebugBundle.AzureFunctions.Worker.Tests;

public sealed class FunctionsMiddlewareTests
{
    [Fact]
    public async Task Invoke_Captures_Successful_Function_Invocation_Metadata()
    {
        var client = new FakeClient();
        var middleware = new DebugBundleFunctionsMiddleware(client);
        var context = new TestFunctionContext();

        await middleware.Invoke(context, _ => Task.CompletedTask);

        var request = Assert.Single(client.Requests);
        Assert.Equal("AZURE_FUNCTION", request.Request.Method);
        Assert.Equal("ProcessOrder", request.Request.Path);
        Assert.Equal(200, request.Response.StatusCode);
        Assert.Equal("timerTrigger", request.Context!["trigger_type"]);
        Assert.DoesNotContain("tenant-secret", request.Context.Values.Select(value => value?.ToString()));
    }

    [Fact]
    public async Task Invoke_Captures_Exception_And_Rethrows()
    {
        var client = new FakeClient();
        var middleware = new DebugBundleFunctionsMiddleware(client);
        var context = new TestFunctionContext();
        var exception = new InvalidOperationException("failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.Invoke(context, _ => throw exception));

        Assert.Same(exception, thrown);
        var captured = Assert.Single(client.Exceptions);
        Assert.Equal("ProcessOrder", captured.Context!["function_name"]);
        Assert.Equal("inv-123", captured.Context["invocation_id"]);
        var request = Assert.Single(client.Requests);
        Assert.Equal(500, request.Response.StatusCode);
    }

    private sealed class TestFunctionContext : FunctionContext
    {
        private IServiceProvider _services = new EmptyServiceProvider();
        private IDictionary<object, object> _items = new Dictionary<object, object>();

        public override string InvocationId => "inv-123";
        public override string FunctionId => "fn-123";
        public override TraceContext TraceContext { get; } = new TestTraceContext();
        public override BindingContext BindingContext { get; } = new TestBindingContext();
        public override RetryContext RetryContext { get; } = new TestRetryContext();
        public override IServiceProvider InstanceServices { get => _services; set => _services = value; }
        public override FunctionDefinition FunctionDefinition { get; } = new TestFunctionDefinition();
        public override IDictionary<object, object> Items { get => _items; set => _items = value; }
        public override IInvocationFeatures Features { get; } = new TestInvocationFeatures();
        public override CancellationToken CancellationToken => CancellationToken.None;
    }

    private sealed class TestFunctionDefinition : FunctionDefinition
    {
        public override ImmutableArray<FunctionParameter> Parameters => ImmutableArray<FunctionParameter>.Empty;
        public override string PathToAssembly => "/app/functions.dll";
        public override string EntryPoint => "Functions.ProcessOrder.Run";
        public override string Id => "fn-123";
        public override string Name => "ProcessOrder";
        public override IImmutableDictionary<string, BindingMetadata> InputBindings { get; } =
            new Dictionary<string, BindingMetadata>
            {
                ["timer"] = new TestBindingMetadata("timer", "timerTrigger", BindingDirection.In),
                ["payload"] = new TestBindingMetadata("payload", "queue", BindingDirection.In)
            }.ToImmutableDictionary(StringComparer.Ordinal);

        public override IImmutableDictionary<string, BindingMetadata> OutputBindings { get; } =
            new Dictionary<string, BindingMetadata>
            {
                ["output"] = new TestBindingMetadata("output", "queue", BindingDirection.Out)
            }.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private sealed class TestBindingMetadata : BindingMetadata
    {
        public TestBindingMetadata(string name, string type, BindingDirection direction)
        {
            Name = name;
            Type = type;
            Direction = direction;
        }

        public override string Name { get; }
        public override string Type { get; }
        public override BindingDirection Direction { get; }
    }

    private sealed class TestBindingContext : BindingContext
    {
        public override IReadOnlyDictionary<string, object?> BindingData { get; } = new Dictionary<string, object?>
        {
            ["tenant_id"] = "tenant-secret",
            ["delivery_count"] = 2
        };
    }

    private sealed class TestTraceContext : TraceContext
    {
        public override string TraceParent => "00-abcdefabcdefabcdefabcdefabcdef12-abcdefabcdef1234-01";
        public override string TraceState => "state";
    }

    private sealed class TestRetryContext : RetryContext
    {
        public override int RetryCount => 1;
        public override int MaxRetryCount => 5;
    }

    private sealed class TestInvocationFeatures : IInvocationFeatures
    {
        private readonly Dictionary<Type, object> _features = new();

        public void Set<T>(T instance) => _features[typeof(T)] = instance!;
        public T? Get<T>() => _features.TryGetValue(typeof(T), out var value) ? (T)value : default;
        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => _features.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
