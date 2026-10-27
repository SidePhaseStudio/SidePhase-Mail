using SidePhase.Mail.Core.Settings;
using SidePhase.Mail.Infrastructure.Settings;
using SidePhase.Mail.Infrastructure.Storage;

namespace SidePhase.Mail.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidephase-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Json_store_round_trips_settings()
    {
        var store = new JsonSettingsStore(new AppDataPaths(_root));
        var settings = new AppSettings
        {
            Theme = AppTheme.SolidLight,
            AccentColor = "#3FB6A8",
            Density = ListDensity.Compact,
            SpamThreshold = 7.5,
            SenderRules = [new SenderRule(SenderRuleKind.BlockedDomain, "spam.example", Samples.Now)],
        };
        settings.Signatures[Guid.Empty] = "Cheers,\nAlex";

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(AppTheme.SolidLight, loaded.Theme);
        Assert.Equal("#3FB6A8", loaded.AccentColor);
        Assert.Equal(ListDensity.Compact, loaded.Density);
        Assert.Equal(7.5, loaded.SpamThreshold);
        Assert.Equal("spam.example", Assert.Single(loaded.SenderRules).Value);
        Assert.Equal("Cheers,\nAlex", loaded.Signatures[Guid.Empty]);
        Assert.Contains("\"theme\": \"SolidLight\"", await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
    }

    [Fact]
    public async Task Missing_file_returns_null()
    {
        var store = new JsonSettingsStore(new AppDataPaths(_root));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Corrupt_file_falls_back_to_defaults_and_is_preserved()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(file, "{ this is not json");

        var service = new SettingsService(new JsonSettingsStore(new AppDataPaths(_root)));
        await service.LoadAsync();

        Assert.Equal(AppTheme.DarkGlass, service.Current.Theme);
        Assert.True(File.Exists(file + ".corrupt"));
    }

    [Fact]
    public async Task Update_raises_event_and_saves()
    {
        var store = new InMemorySettingsStore();
        var service = new SettingsService(store);
        AppSettings? received = null;
        service.SettingsChanged += (_, e) => received = e.Settings;

        service.Update(s => s.Density = ListDensity.Compact);
        await service.FlushAsync();

        Assert.Equal(ListDensity.Compact, received?.Density);
        Assert.Equal(ListDensity.Compact, store.Stored?.Density);
    }

    [Fact]
    public void Snapshots_are_isolated_from_later_updates()
    {
        var service = new SettingsService(new InMemorySettingsStore());
        var before = service.Current;
        service.Update(s => s.SenderRules.Add(new SenderRule(SenderRuleKind.BlockedSender, "a@b.example", Samples.Now)));
        Assert.Empty(before.SenderRules);
        Assert.Single(service.Current.SenderRules);
    }

    [Fact]
    public void Normalize_repairs_invalid_values()
    {
        var settings = new AppSettings
        {
            AccentColor = "purple",
            SpamThreshold = -3,
            SyncIntervalMinutes = 0,
            Theme = (AppTheme)99,
            SenderRules =
            [
                new SenderRule(SenderRuleKind.BlockedSender, "a@b.example", Samples.Now),
                new SenderRule(SenderRuleKind.BlockedSender, "A@B.example", Samples.Now),
                new SenderRule(SenderRuleKind.BlockedSender, " ", Samples.Now),
            ],
        };
        settings.Normalize();
        Assert.Equal(AppSettings.DefaultAccent, settings.AccentColor);
        Assert.Equal(5.0, settings.SpamThreshold);
        Assert.Equal(5, settings.SyncIntervalMinutes);
        Assert.Equal(AppTheme.DarkGlass, settings.Theme);
        Assert.Single(settings.SenderRules);
    }
}
