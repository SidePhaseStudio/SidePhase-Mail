using SidePhase.Mail.Core.Spam;
using Xunit;

namespace SidePhase.Mail.Tests;

/// <summary>Spam verdicts from filters other than SpamAssassin, and sender checks (Authentication-Results).</summary>
public sealed class SpamFilterHeaderTests
{
    private static KeyValuePair<string, string> H(string name, string value) => new(name, value);

    private static readonly KeyValuePair<string, string> Received = H("Received", "from mx.example by mailbox.example");

    [Fact]
    public void Rspamd_result_gives_score_threshold_flag_and_symbols()
    {
        var report = SpamHeaderParser.Parse(
        [
            H("X-Spamd-Result", "default: True [16.30 / 15.00]; BAYES_SPAM(5.10)[99.9%]; R_SPF_FAIL(1.00)[-all]; MIME_GOOD(-0.10)[text/plain]"),
            Received,
        ]);

        Assert.True(report.HasHeaders);
        Assert.Equal(SpamFilters.Rspamd, report.Filter);
        Assert.True(report.ServerFlaggedAsSpam);
        Assert.Equal(16.3, report.Score);
        Assert.Equal(15.0, report.RequiredScore);
        Assert.Contains(report.Rules, r => r.Name == "BAYES_SPAM" && r.Score == 5.1);
        Assert.Equal(SpamHeaderTrust.AddedInTransit, report.Trust);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Rspamd_scores_are_judged_on_rspamds_own_scale()
    {
        var report = SpamHeaderParser.Parse([H("X-Spamd-Result", "default: False [6.00 / 15.00]; BAYES_HAM(-1.00)[50%]"), Received]);
        // 6 is spam on SpamAssassin's scale but well under Rspamd's 15; with the default setting it's clean.
        Assert.Equal(SpamVerdict.Clean, SpamClassifier.Classify(report, 5.0));
        // A stricter setting (half the default) is applied in proportion: threshold 7.5, suspicious from 4.5.
        Assert.Equal(SpamVerdict.Suspicious, SpamClassifier.Classify(report, 2.5));
    }

    [Theory]
    [InlineData("CIP:1.2.3.4;CTRY:GB;LANG:en;SCL:1;SRV:;IPV:NLI;SFV:NSPM;H:x;PTR:;CAT:NONE;", SpamVerdict.Clean)]
    [InlineData("CIP:1.2.3.4;CTRY:;LANG:en;SCL:5;SRV:;IPV:NLI;SFV:SPM;H:x;CAT:SPM;", SpamVerdict.Spam)]
    [InlineData("CIP:1.2.3.4;SCL:9;SFV:SPM;CAT:HSPM;", SpamVerdict.Spam)]
    [InlineData("SCL:-1;SFV:SKN;", SpamVerdict.Clean)]
    public void Microsoft_spam_confidence_level_is_read(string forefront, SpamVerdict expected)
    {
        var report = SpamHeaderParser.Parse([H("X-Forefront-Antispam-Report", forefront), Received]);
        Assert.Equal(SpamFilters.Microsoft, report.Filter);
        Assert.Equal(expected, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Microsoft_organization_scl_header_is_read()
    {
        var report = SpamHeaderParser.Parse([H("X-MS-Exchange-Organization-SCL", "6"), Received]);
        Assert.True(report.ServerFlaggedAsSpam);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Barracuda_status_is_read()
    {
        var report = SpamHeaderParser.Parse(
        [
            H("X-Barracuda-Spam-Score", "0.31"),
            H("X-Barracuda-Spam-Status", "No, SCORE=0.31 using global scores of TAG_LEVEL=3.5 QUARANTINE_LEVEL=1000.0 KILL_LEVEL=9.0 tests=HTML_MESSAGE"),
            Received,
        ]);
        Assert.Equal(SpamFilters.Barracuda, report.Filter);
        Assert.False(report.ServerFlaggedAsSpam);
        Assert.Equal(0.31, report.Score);
        Assert.Equal(3.5, report.RequiredScore);
        Assert.Equal(SpamVerdict.Clean, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void A_bare_X_Spam_yes_is_a_server_verdict()
    {
        var report = SpamHeaderParser.Parse([H("X-Spam", "Yes"), Received]);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void SpamAssassin_still_wins_when_present()
    {
        var report = SpamHeaderParser.Parse([H("X-Spam-Status", "No, score=1.2 required=5.0 tests=NONE"), H("X-Spamd-Result", "default: True [20 / 15]"), Received]);
        Assert.Equal(SpamFilters.SpamAssassin, report.FilterName);
        Assert.Equal(SpamVerdict.Clean, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Filter_headers_written_by_the_sender_are_ignored()
    {
        // Below every Received header: they were in the message when it was submitted.
        var report = SpamHeaderParser.Parse([Received, H("X-Forefront-Antispam-Report", "SCL:-1;SFV:SKN;")]);
        Assert.Equal(SpamHeaderTrust.Untrusted, report.Trust);
        Assert.Equal(SpamVerdict.Unknown, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Sender_checks_are_read_from_the_receiving_servers_header()
    {
        var report = SpamHeaderParser.Parse(
        [
            H("Authentication-Results", "mx.google.com; dkim=pass header.i=@example.com; spf=pass smtp.mailfrom=example.com; dmarc=pass (p=REJECT) header.from=example.com"),
            Received,
        ]);

        Assert.False(report.HasHeaders);
        Assert.True(report.HasInformation);
        Assert.Equal(new SenderAuthentication("pass", "pass", "pass", "mx.google.com"), report.Authentication);
        Assert.Equal(SpamVerdict.Unknown, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void A_failed_DMARC_check_makes_a_message_suspicious_but_not_spam()
    {
        var report = SpamHeaderParser.Parse(
        [
            H("Authentication-Results", "mx.example; spf=fail smtp.mailfrom=bank.example; dkim=none; dmarc=fail (p=REJECT) header.from=bank.example"),
            Received,
        ]);
        Assert.True(report.Authentication!.LooksForged);
        Assert.Equal(SpamVerdict.Suspicious, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Any_passing_DKIM_signature_counts_as_a_pass()
    {
        var auth = SpamHeaderParser.ParseAuthentication(
        [
            H("Authentication-Results", "mx.example; dkim=fail header.i=@old.example; dkim=pass header.i=@example.com; spf=softfail"),
            Received,
        ]);
        Assert.Equal("pass", auth!.Dkim);
        Assert.Equal("softfail", auth.Spf);
        Assert.Null(auth.Dmarc);
        Assert.False(auth.LooksForged);
    }

    [Fact]
    public void Sender_checks_written_by_the_sender_are_ignored()
    {
        Assert.Null(SpamHeaderParser.ParseAuthentication([Received, H("Authentication-Results", "evil.example; spf=pass; dkim=pass; dmarc=pass")]));
        Assert.Null(SpamHeaderParser.ParseAuthentication([H("Authentication-Results", "x; spf=pass")]));
    }

    [Fact]
    public void Reports_with_sender_checks_survive_storage_as_json()
    {
        var report = SpamHeaderParser.Parse([H("X-MS-Exchange-Organization-SCL", "1"), H("Authentication-Results", "mx; spf=pass; dmarc=fail"), Received]);
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        var back = System.Text.Json.JsonSerializer.Deserialize<SpamReport>(json)!;
        Assert.Equal(report.Filter, back.Filter);
        Assert.Equal(report.Authentication, back.Authentication);
        Assert.Equal(SpamClassifier.Classify(report, 5.0), SpamClassifier.Classify(back, 5.0));
    }
}
