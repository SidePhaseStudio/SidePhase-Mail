namespace SidePhase.Mail.Core.Spam;

/// <summary>
/// Turns a <see cref="SpamReport"/> into a verdict using the user's configured threshold.
/// Classification never deletes anything; it only informs filtering and display.
/// </summary>
public static class SpamClassifier
{
    /// <summary>Fraction of the threshold at which a message is shown as "suspicious".</summary>
    public const double SuspiciousRatio = 0.6;

    public static SpamVerdict Classify(SpamReport report, double userThreshold)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.HasHeaders) return SpamVerdict.Unknown;

        // Respect an explicit server-side "yes".
        if (report.ServerFlaggedAsSpam == true) return SpamVerdict.Spam;

        if (report.Score is { } score)
        {
            if (score >= userThreshold) return SpamVerdict.Spam;
            if (score >= userThreshold * SuspiciousRatio && score > 0) return SpamVerdict.Suspicious;
            return SpamVerdict.Clean;
        }

        return report.ServerFlaggedAsSpam == false ? SpamVerdict.Clean : SpamVerdict.Unknown;
    }
}
