using DebugBundle.AzureFunctions;
using Microsoft.Azure.Functions.Worker.Builder;

namespace DebugBundle.AzureFunctions.Worker.Tests;

public sealed class FunctionsWorkerApplicationBuilderExtensionTests
{
    [Fact]
    public void UseDebugBundle_Registers_Middleware_On_The_Real_Worker_Builder()
    {
        var builder = FunctionsApplication.CreateBuilder(Array.Empty<string>());

        Assert.Same(builder, builder.UseDebugBundle());
    }

    [Fact]
    public void UseDebugBundle_Rejects_A_Null_Builder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FunctionsWorkerApplicationBuilderExtensions.UseDebugBundle(null!));
    }
}
