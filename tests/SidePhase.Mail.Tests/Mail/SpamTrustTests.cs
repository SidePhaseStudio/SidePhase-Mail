using MimeKit;
using SidePhase.Mail.Core.Spam;
using SidePhase.Mail.Infrastructure.Mail;

namespace SidePhase.Mail.Tests.Mail;

public class SpamTrustTests
{
    private static SpamReport Parse(params (string, string)[] headers) =>
        SpamAssassinHeaderParser.Parse(headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)));

    private const string Report = """
         
        	* 3.5 BAYES_99 BODY: Bayes spam probability is 99 to 100%
        	*      [score: 1.0000]
        	*  2.0 URIBL_BLACK Contains an URL listed in the URIBL blacklist
        	* -0.0 SPF_PASS SPF: sender matches SPF record
        """;

    [Fact]
    public void Headers_added_by_a_receiving_server_are_trusted()
    {
        var report = Parse(
            ("Received", "from mx.provider.example by mailbox.provider.example"),
            ("X-Spam-Status", "Yes, score=8.0 required=5.0"),
            ("Received", "from sender.example by mx.provider.example"));
        Assert.Equal(SpamHeaderTrust.AddedInTransit, report.Trust);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Headers_written_by_the_sender_are_ignored()
    {
        // A spammer pre-fills "No" below every Received header, i.e. in the message as submitted.
        var report = Parse(
            ("Received", "from sender.example by mx.provider.example"),
            ("Subject", "You won"),
            ("X-Spam-Status", "No, score=-5.0 required=5.0"),
            ("X-Spam-Flag", "NO"));
        Assert.Equal(SpamHeaderTrust.Untrusted, report.Trust);
        Assert.False(report.IsUsable);
        Assert.Equal(SpamVerdict.Unknown, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Without_received_headers_the_origin_is_unknown_but_usable()
    {
        var report = Parse(("X-Spam-Status", "Yes, score=9.1 required=5.0"));
        Assert.Equal(SpamHeaderTrust.Unknown, report.Trust);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Missing_headers_are_not_treated_as_safe()
    {
        var report = Parse(("Received", "from a by b"), ("Subject", "Hello"));
        Assert.False(report.HasHeaders);
        Assert.Equal(SpamVerdict.Unknown, SpamClassifier.Classify(report, 5));
        Assert.NotEqual(SpamVerdict.Clean, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Topmost_status_wins_and_conflicts_are_reported()
    {
        var report = Parse(
            ("X-Spam-Status", "Yes, score=6.2 required=5.0"),
            ("Received", "from x by y"),
            ("X-Spam-Status", "No, score=0.0 required=5.0"));
        Assert.True(report.ServerFlaggedAsSpam);
        Assert.Equal(6.2, report.Score);
        Assert.True(report.HasConflictingHeaders);
    }

    [Fact]
    public void Report_rules_are_parsed_folded_or_unfolded()
    {
        var folded = SpamAssassinHeaderParser.ParseReport(Report);
        Assert.Equal(3, folded.Count);
        Assert.Equal(new SpamRule(3.5, "BAYES_99", "BODY: Bayes spam probability is 99 to 100%"), folded[0]);
        Assert.Equal(-0.0, folded[2].Score);
        Assert.Equal("SPF_PASS", folded[2].Name);

        var unfolded = SpamAssassinHeaderParser.ParseReport(Report.Replace("\n", " "));
        Assert.Equal(["BAYES_99", "URIBL_BLACK", "SPF_PASS"], unfolded.Select(r => r.Name));
    }

    [Fact]
    public void Report_alone_supplies_score_and_required()
    {
        var report = Parse(("X-Spam-Report", """
            Content analysis details:   (6.1 points, 5.0 required)

             pts rule name              description
            ---- ---------------------- --------------------------------------------------
             4.1 BAYES_95               BODY: Bayes spam probability is 95 to 99%
             2.0 RDNS_NONE              Delivered to internal network by a host with no rDNS
            """));
        Assert.Equal(6.1, report.Score);
        Assert.Equal(5.0, report.RequiredScore);
        Assert.Equal(["BAYES_95", "RDNS_NONE"], report.Tests);
    }

    [Fact]
    public void Mime_headers_keep_the_report_layout_and_order()
    {
        var message = MimeMessage.Load(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(
            "Received: from mx by mailbox\r\n" +
            "X-Spam-Status: Yes, score=5.5 required=5.0\r\n" +
            "X-Spam-Report: \r\n\t* 3.5 BAYES_99 BODY: Bayes\r\n\t* 2.0 URIBL_BLACK URL listed\r\n" +
            "Received: from sender by mx\r\n" +
            "Subject: test\r\n\r\nbody\r\n")));
        var report = MessageBodyExtractor.ReadSpamHeaders(message.Headers);
        Assert.Equal(SpamHeaderTrust.AddedInTransit, report.Trust);
        Assert.Equal(2, report.Rules.Count);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5));
    }

    [Theory]
    [InlineData(4.9, 5.0, SpamVerdict.Suspicious)]
    [InlineData(5.0, 5.0, SpamVerdict.Spam)]
    [InlineData(5.0, 8.0, SpamVerdict.Suspicious)]
    [InlineData(1.0, 8.0, SpamVerdict.Clean)]
    [InlineData(-2.0, 5.0, SpamVerdict.Clean)]
    public void Threshold_controls_the_verdict(double score, double threshold, SpamVerdict expected)
    {
        var report = Parse(("X-Spam-Score", score.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(expected, SpamClassifier.Classify(report, threshold));
    }
}
