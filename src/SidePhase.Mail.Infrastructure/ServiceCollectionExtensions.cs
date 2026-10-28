using Microsoft.Extensions.DependencyInjection;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Core.Scheduling;
using SidePhase.Mail.Infrastructure.Accounts;
using SidePhase.Mail.Infrastructure.Demo;
using SidePhase.Mail.Infrastructure.Live;
using SidePhase.Mail.Infrastructure.Mail;
using SidePhase.Mail.Infrastructure.OAuth;
using SidePhase.Mail.Infrastructure.Security;
using SidePhase.Mail.Infrastructure.Settings;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage, credentials, the MailKit engine and the mail data source. <see cref="IMailDataSource"/> is
    /// a router that serves either the demonstration mailbox or real accounts, depending on settings.
    /// </summary>
    public static IServiceCollection AddSidePhaseMailInfrastructure(this IServiceCollection services, AppDataPaths? paths = null)
    {
        services.AddSingleton(paths ?? new AppDataPaths());
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();

        // Secrets: DPAPI-protected files on Windows. Nothing secret is ever written to SQLite or settings.json.
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ISecretStore, FileSecretStore>();

        // Only for OAuth token endpoints and autoconfig lookups; mail itself goes over IMAP/SMTP.
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IBrowserLauncher, ShellBrowserLauncher>();
        services.AddSingleton<OAuthClient>();
        services.AddSingleton<OAuthConfiguration>();
        services.AddSingleton<CredentialProvider>();
        services.AddSingleton<AccountDiscovery>();

        services.AddSingleton<MailDatabase>();
        services.AddSingleton<MailCache>();
        services.AddSingleton<MailConnector>();
        services.AddSingleton<DemoMailDataSource>();
        services.AddSingleton<LiveMailDataSource>();
        services.AddSingleton<MailDataSourceRouter>();
        services.AddSingleton<IMailDataSource>(sp => sp.GetRequiredService<MailDataSourceRouter>());
        services.AddSingleton<IAccountService, AccountService>();

        // Outbox: Undo Send and Schedule send. Runs only while SidePhase Mail is open.
        services.AddSingleton<IOutboxStore, SqliteOutboxStore>();
        services.AddSingleton<IOutboxAttachmentStore, FileOutboxAttachmentStore>();
        services.AddSingleton<IOutgoingTransport, OutboxTransport>();
        services.AddSingleton(sp => new OutboxService(
            sp.GetRequiredService<IOutboxStore>(),
            sp.GetRequiredService<IOutgoingTransport>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IOutboxAttachmentStore>()));
        return services;
    }
}
