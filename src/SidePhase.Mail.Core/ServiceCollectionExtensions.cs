using Microsoft.Extensions.DependencyInjection;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Services;
using SidePhase.Mail.Core.Settings;

namespace SidePhase.Mail.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers UI-independent services. An <see cref="ISettingsStore"/> and <see cref="IMailDataSource"/> must be added separately.</summary>
    public static IServiceCollection AddSidePhaseMailCore(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISenderRulesService, SenderRulesService>();
        services.AddSingleton<JunkPolicy>();
        services.AddSingleton<NotificationPolicy>();
        services.AddSingleton<SendTheBuggerBackService>();
        services.AddSingleton<AutoJunkService>();
        return services;
    }
}
