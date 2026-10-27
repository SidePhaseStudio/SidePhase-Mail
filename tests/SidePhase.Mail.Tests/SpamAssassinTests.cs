using SidePhase.Mail.Core.Spam;

namespace SidePhase.Mail.Tests;

public class SpamAssassinTests
{
    private static SpamReport Parse(params (string, string)[] headers) =>
        SpamAssassinHeaderParser.Parse(headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)));

    [Fact]
    public void No_headers_returns_none()
    {
        var report = Parse(("Subject", "Hi"));
        Assert.False(report.HasHeaders);
        Assert.Equal(SpamVerdict.Unknown, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Parses_status_header()
    {
        var report = Parse(("X-Spam-Status",
            "Yes, score=7.3 required=5.0 tests=BAYES_99,HTML_MESSAGE,URIBL_BLACK autolearn=no autolearn_force=no version=3.4.6"));

        Assert.True(report.HasHeaders);
        Assert.True(report.ServerFlaggedAsSpam);
        Assert.Equal(7.3, report.Score);
        Assert.Equal(5.0, report.RequiredScore);
        Assert.Equal(["BAYES_99", "HTML_MESSAGE", "URIBL_BLACK"], report.Tests);
        Assert.Equal("3.4.6", report.Version);
    }

    [Fact]
    public void Parses_folded_status_header()
    {
        var report = Parse(("x-spam-status", "No, score=-0.2 required=5.0 tests=DKIM_SIGNED,\r\n\tDKIM_VALID,SPF_PASS\r\n\tautolearn=ham version=3.4.2"));

        Assert.False(report.ServerFlaggedAsSpam);
        Assert.Equal(-0.2, report.Score);
        Assert.Equal(["DKIM_SIGNED", "DKIM_VALID", "SPF_PASS"], report.Tests);
        Assert.Equal(SpamVerdict.Clean, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Flag_header_marks_spam_even_with_low_score()
    {
        var report = Parse(("X-Spam-Flag", "YES"), ("X-Spam-Score", "2.1"));
        Assert.True(report.ServerFlaggedAsSpam);
        Assert.Equal(2.1, report.Score);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5));
    }

    [Fact]
    public void Level_header_is_used_when_no_score_is_available()
    {
        var report = Parse(("X-Spam-Level", "******"));
        Assert.Equal(6, report.Score);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5));
        Assert.Equal(SpamVerdict.Clean, SpamClassifier.Classify(report, 12));
    }

    [Theory]
    [InlineData(1.0, SpamVerdict.Clean)]
    [InlineData(3.5, SpamVerdict.Suspicious)]
    [InlineData(5.0, SpamVerdict.Spam)]
    [InlineData(12.0, SpamVerdict.Spam)]
    public void Classifies_against_user_threshold(double score, SpamVerdict expected)
    {
        var report = new SpamReport { HasHeaders = true, ServerFlaggedAsSpam = false, Score = score };
        Assert.Equal(expected, SpamClassifier.Classify(report, 5.0));
    }

    [Fact]
    public void Higher_user_threshold_relaxes_classification()
    {
        var report = Parse(("X-Spam-Status", "No, score=6.0 required=8.0 tests=none"));
        Assert.Empty(report.Tests);
        Assert.Equal(SpamVerdict.Spam, SpamClassifier.Classify(report, 5));
        Assert.Equal(SpamVerdict.Suspicious, SpamClassifier.Classify(report, 9));
    }
}
