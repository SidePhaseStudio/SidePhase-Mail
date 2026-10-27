using Microsoft.Extensions.DependencyInjection;
using SidePhase.Mail.Core;
using SidePhase.Mail.Core.Abstractions;
using SidePhase.Mail.Infrastructure;
using SidePhase.Mail.Infrastructure.Live;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests.Mail;

public class ServiceRegistrationTests
{
    [Fact]
    public async Task Container_resolves_the_router_in_demo_mode_by_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "sp-di-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddSidePhaseMailCore().AddSidePhaseMailInfrastructure(new AppDataPaths(root));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await provider.GetRequiredService<ISettingsService>().LoadAsync();
        var mail = provider.GetRequiredService<IMailDataSource>();
        Assert.IsType<MailDataSourceRouter>(mail);
        Assert.True(mail.IsDemo);
        Assert.NotNull(provider.GetRequiredService<IAccountService>());
        Assert.Same(provider.GetRequiredService<LiveMailDataSource>(), provider.GetRequiredService<LiveMailDataSource>());
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
