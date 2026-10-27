using Microsoft.Extensions.DependencyInjection;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Infrastructure.Demo;
using SidePhase.Mail.Infrastructure.Settings;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage and the mail data source. Milestone 1 uses <see cref="DemoMailDataSource"/>; Milestone 2
    /// replaces it with the SQLite cache + MailKit engine behind the same <see cref="IMailDataSource"/> interface.
    /// </summary>
    public static IServiceCollection AddSidePhaseMailInfrastructure(this IServiceCollection services, AppDataPaths? paths = null)
    {
        services.AddSingleton(paths ?? new AppDataPaths());
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IMailDataSource, DemoMailDataSource>();
        return services;
    }
}
