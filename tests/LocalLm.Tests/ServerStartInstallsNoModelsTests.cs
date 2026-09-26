using System.Reflection;
using LocalLm.Core;
using LocalLm.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LocalLm.Tests;

public sealed class ServerStartInstallsNoModelsTests
{
    // Models are installed only when asked for: by the installer, `localai model pull` or
    // local_models_sync. A hosted service runs on every server start, asked or not.
    [Fact]
    public void The_locallm_services_start_nothing_on_their_own()
    {
        var services = new ServiceCollection()
            .AddLocalLm(DispatchProxy.Create<ILocalModelClient, UntouchedClient>());

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IHostedService));
    }

    public class UntouchedClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Composing the server must not call the model client.");
    }
}
