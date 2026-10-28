using SidePhase.Mail.Core.Formatting;

namespace SidePhase.Mail.Tests;

public class AttachmentSafetyTests
{
    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SCRIPT.PS1", true)]
    [InlineData("budget.xlsm", true)]
    [InlineData("disk.iso", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("trailing.exe. ", true)]
    [InlineData("report.pdf", false)]
    [InlineData("photo.JPG", false)]
    [InlineData("noextension", false)]
    [InlineData(null, false)]
    public void Recognises_files_that_can_run_code(string? name, bool dangerous) =>
        Assert.Equal(dangerous, AttachmentSafety.IsDangerous(name));

    [Fact]
    public void Flags_names_disguised_as_documents()
    {
        Assert.True(AttachmentSafety.HasMisleadingName("invoice.pdf.exe"));
        Assert.True(AttachmentSafety.HasMisleadingName("photo      .scr"));
        Assert.False(AttachmentSafety.HasMisleadingName("installer.exe"));
        Assert.False(AttachmentSafety.HasMisleadingName("archive.tar.gz"));
    }

    [Theory]
    [InlineData("..\\..\\Windows\\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("a<b>c:d|e?f*.txt", "a_b_c_d_e_f_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("...", "attachment")]
    [InlineData("  ", "attachment")]
    [InlineData("name. . .", "name")]
    [InlineData("cod‮txt.exe", "cod_txt.exe")]
    public void Makes_names_safe_to_save(string input, string expected) =>
        Assert.Equal(expected, AttachmentSafety.SafeFileName(input));

    [Fact]
    public void Long_names_are_shortened_but_keep_their_extension()
    {
        var safe = AttachmentSafety.SafeFileName(new string('a', 400) + ".pdf");
        Assert.True(safe.Length <= 180);
        Assert.EndsWith(".pdf", safe);
    }

    [Fact]
    public void Never_reuses_an_existing_name()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "report.pdf", "report (2).pdf" };
        Assert.Equal("Report (3).pdf", AttachmentSafety.UniqueName("Report.pdf", taken.Contains));
        Assert.Equal("new.pdf", AttachmentSafety.UniqueName("new.pdf", taken.Contains));
    }
}
