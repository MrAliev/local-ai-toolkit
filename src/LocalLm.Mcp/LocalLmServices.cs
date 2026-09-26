using LocalLm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LocalLm.Mcp;

public static class LocalLmServices
{
    public static IServiceCollection AddLocalLm(this IServiceCollection services, ILocalModelClient client)
    {
        services.AddSingleton(client);
        services.AddSingleton<LocalTasks>();
        services.AddSingleton<ModelManagementTasks>();
        return services;
    }
}
