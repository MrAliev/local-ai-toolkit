using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>Keep CLI ownership locally without changing MCP clients or shared broker policy.</summary>
internal static class CliBrokerJobs
{
    public static BrokerClient Create(string? runtimeRoot = null, ILocalRunObserver? observer = null, TimeSpan? deadline = null)
    {
        var root = runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot;
        return BrokerClientFactory.CreateDefault(root, observer, deadline,
            onSubmitted: new BrokerJobHandleStore(root).Save);
    }
}
