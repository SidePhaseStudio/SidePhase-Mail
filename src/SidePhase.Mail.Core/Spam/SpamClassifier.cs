namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// Turns a <see cref="SpamReport"/> into a verdict using the user's configured threshold.
/// Classification never deletes anything; it only informs filtering and display.
/// </summary>
public static class SpamClassifier
{
    /// <summary>Fraction of the threshold at which a message is shown as "suspicious".</summary>
    public const double SuspiciousRatio = 0.6;

    /// <summary>The default threshold, in SpamAssassin points; other filters' scales are adjusted relative to it.</summary>
    public const double DefaultThreshold = 5.0;

    public static SpamVerdict Classify(SpamReport report, double userThreshold)
    {
        ArgumentNullException.ThrowIfNull(report);

        var verdict = ClassifyFilter(report, userThreshold);

        // A From address that fails the sender checks is worth a warning even when the filter let it through.
        if (verdict is SpamVerdict.Unknown or SpamVerdict.Clean && report.Authentication?.LooksForged == true) return SpamVerdict.Suspicious;
        return verdict;
    }

    private static SpamVerdict ClassifyFilter(SpamReport report, double userThreshold)
    {
        // Missing headers prove nothing, and headers the sender wrote themselves are ignored.
        if (!report.IsUsable) return SpamVerdict.Unknown;

        // Respect an explicit server-side "yes".
        if (report.ServerFlaggedAsSpam == true) return SpamVerdict.Spam;

        if (report.Score is { } score)
        {
            var threshold = ThresholdFor(report, userThreshold);
            if (score >= threshold) return SpamVerdict.Spam;
            if (score >= threshold * SuspiciousRatio && score > 0) return SpamVerdict.Suspicious;
            return SpamVerdict.Clean;
        }

        return report.ServerFlaggedAsSpam == false ? SpamVerdict.Clean : SpamVerdict.Unknown;
    }

    /// <summary>
    /// The threshold on this report's own scale. The user's setting is in SpamAssassin points; other filters use
    /// their reported threshold, made stricter or looser in the same proportion as the user's setting.
    /// </summary>
    public static double ThresholdFor(SpamReport report, double userThreshold) =>
        report.FilterName == SpamFilters.SpamAssassin || report.RequiredScore is not { } required || required <= 0
            ? userThreshold
            : required * userThreshold / DefaultThreshold;
}
