using Microsoft.Extensions.DependencyInjection;
using ShowroomBilling.Application.Settings;

namespace ShowroomBilling.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationLayer(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsStorageContract, SettingsStorageContract>();
        return services;
    }
}
