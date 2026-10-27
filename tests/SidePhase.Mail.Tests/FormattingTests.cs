using System.Collections.ObjectModel;
using System.Globalization;
using SidePhase.Mail.Core.Collections;
using SidePhase.Mail.Core.Formatting;
using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Tests;

public class FormattingTests
{
    private static readonly CultureInfo UkCulture = CultureInfo.GetCultureInfo("en-GB");

    [Theory]
    [InlineData("Sarah Chen", "sarah@x.example", "SC")]
    [InlineData("Jean-Luc Picard", "jl@x.example", "JP")]
    [InlineData("madonna", "m@x.example", "M")]
    [InlineData(null, "support.team@x.example", "ST")]
    [InlineData("Jane Doe (Finance)", "j@x.example", "JD")]
    [InlineData("", "", "?")]
    public void Initials(string? name, string address, string expected) =>
        Assert.Equal(expected, AvatarHelper.Initials(name, address));

    [Fact]
    public void Avatar_colour_is_stable_and_in_range()
    {
        var a = AvatarHelper.ColorIndex("Jane@Example.com");
        var b = AvatarHelper.ColorIndex("jane@example.com ");
        Assert.Equal(a, b);
        Assert.InRange(a, 0, AvatarHelper.PaletteSize - 1);
    }

    [Fact]
    public void List_dates()
    {
        var now = Samples.Now;
        Assert.Equal("14:00", DateDisplayFormatter.ForList(now.AddMinutes(-30), now, UkCulture));
        Assert.Equal("Yesterday", DateDisplayFormatter.ForList(now.AddDays(-1), now, UkCulture));
        Assert.Equal("Sat", DateDisplayFormatter.ForList(now.AddDays(-3), now, UkCulture));
        Assert.Equal("12 Mar", DateDisplayFormatter.ForList(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero), now, UkCulture));
        Assert.Equal("Mar 12", DateDisplayFormatter.ForList(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero), now, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("12/03/2024", DateDisplayFormatter.ForList(new DateTimeOffset(2024, 3, 12, 9, 0, 0, TimeSpan.Zero), now, UkCulture));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(2_481_337, "2.4 MB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    public void File_sizes(long bytes, string expected) =>
        Assert.Equal(expected, FileSizeFormatter.Format(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Accent_fill_meets_contrast_for_white_text()
    {
        var accent = ColorMath.ParseHex("#8B7CFF");
        var palette = ColorMath.DeriveAccent(accent, darkTheme: true);
        Assert.True(ColorMath.ContrastRatio(palette.Fill, new Rgb(255, 255, 255)) >= 4.5);
        Assert.True(ColorMath.ContrastRatio(palette.Text, ColorMath.ParseHex("#151820")) >= 4.5);

        var light = ColorMath.DeriveAccent(ColorMath.ParseHex("#F5C542"), darkTheme: false);
        Assert.True(ColorMath.ContrastRatio(light.Text, ColorMath.ParseHex("#F7F8FB")) >= 4.5);
    }

    [Theory]
    [InlineData("#8B7CFF", true)]
    [InlineData("8b7cff", true)]
    [InlineData("#FF8B7CFF", true)]
    [InlineData("#GGGGGG", false)]
    [InlineData("#123", false)]
    [InlineData(null, false)]
    public void Hex_parsing(string? hex, bool valid) => Assert.Equal(valid, ColorMath.TryParseHex(hex, out _));

    [Fact]
    public void Collection_sync_reorders_inserts_and_removes_in_place()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var d = new object();
        var target = new ObservableCollection<object> { a, b, c };
        var events = 0;
        target.CollectionChanged += (_, _) => events++;

        target.SyncTo([c, a, d]);

        Assert.Equal([c, a, d], target);
        Assert.True(events <= 3, $"Expected at most 3 changes, got {events}");
    }

    [Fact]
    public void Collection_sync_with_identical_lists_does_nothing()
    {
        var items = new[] { new object(), new object() };
        var target = new ObservableCollection<object>(items);
        var events = 0;
        target.CollectionChanged += (_, _) => events++;
        target.SyncTo(items);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Email_address_helpers()
    {
        var address = new EmailAddress(" Jane ", "Jane@Mail.Example.COM");
        Assert.Equal("Jane", address.DisplayName);
        Assert.Equal("mail.example.com", address.Domain);
        Assert.True(address.IsSameMailbox(new EmailAddress(null, "jane@mail.example.com")));
    }
}
