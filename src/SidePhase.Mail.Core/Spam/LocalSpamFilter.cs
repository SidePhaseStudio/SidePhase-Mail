using SidePhase.Mail.Core.Models;

namespace SidePhase.Mail.Core.Spam;

/// <summary>Word counts learned from messages the user marked as junk or not junk. Stored on this PC only.</summary>
public interface ISpamLearningStore
{
    Task<SpamModel> LoadAsync(CancellationToken ct = default);

    /// <summary>What a message was last trained as (so re-marking it moves its words rather than counting twice).</summary>
    Task<(bool IsSpam, IReadOnlyList<string> Tokens)?> GetTrainingAsync(Guid accountId, string identityKey, CancellationToken ct = default);

    /// <summary>Records a training example atomically, undoing <paramref name="previous"/> first if given.</summary>
    Task ApplyAsync(Guid accountId, string identityKey, bool isSpam, IReadOnlyList<string> tokens, (bool IsSpam, IReadOnlyList<string> Tokens)? previous, CancellationToken ct = default);

    Task ResetAsync(CancellationToken ct = default);
}

public sealed class SpamModel
{
    public Dictionary<string, (int Spam, int Ham)> Tokens { get; } = new(StringComparer.Ordinal);
    public int SpamMessages { get; set; }
    public int HamMessages { get; set; }
}

/// <summary>The local filter's opinion of one message.</summary>
/// <param name="Probability">0 = certainly fine, 1 = certainly junk.</param>
/// <param name="Evidence">The words that weighed most, for the explanation.</param>
public sealed record LocalSpamResult(double Probability, IReadOnlyList<string> Evidence, int LearnedFrom)
{
    public bool LikelyJunk => Probability >= LocalSpamFilter.JunkThreshold;
    public bool LikelyFine => Probability <= 0.2;
}

/// <summary>
/// A small naive-Bayes filter (Robinson's word probabilities combined with Fisher's method, as in SpamBayes) that
/// learns only from messages you mark Junk or Not junk. It runs entirely on this PC and looks only at what's already
/// cached: the sender's domain, the subject and the preview. It never moves or deletes anything by itself; the app
/// only shows its opinion, and optionally lists messages it's very sure about under Junk.
/// </summary>
public sealed class LocalSpamFilter(ISpamLearningStore store)
{
    /// <summary>It gives no opinion until it has seen this many of each kind.</summary>
    public const int MinimumExamples = 5;

    public const double JunkThreshold = 0.9;

    private const int MaxEvidence = 15;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SpamModel? _model;

    public event EventHandler? Changed;

    public int SpamExamples => _model?.SpamMessages ?? 0;
    public int HamExamples => _model?.HamMessages ?? 0;
    public bool IsReady => SpamExamples >= MinimumExamples && HamExamples >= MinimumExamples;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_model is not null) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _model ??= await store.LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Learns from a message the user explicitly marked. Marking the same message the same way again changes nothing.</summary>
    public async Task TrainAsync(MessageSummary message, bool isSpam, CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        var identity = IdentityOf(message);
        var tokens = Tokenize(message);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = await store.GetTrainingAsync(message.AccountId, identity, ct).ConfigureAwait(false);
            if (previous is { } p && p.IsSpam == isSpam) return;
            await store.ApplyAsync(message.AccountId, identity, isSpam, tokens, previous, ct).ConfigureAwait(false);

            // Copy, change and swap, so readers on other threads always see a complete model.
            var model = Clone(_model!);
            if (previous is { } old) Count(model, old.Tokens, old.IsSpam, -1);
            Count(model, tokens, isSpam, +1);
            _model = model;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await store.ResetAsync(ct).ConfigureAwait(false);
            _model = new SpamModel();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The filter's opinion, or null while it hasn't learned enough (or hasn't loaded yet).</summary>
    public LocalSpamResult? Classify(MessageSummary message)
    {
        var model = _model;
        if (model is null || model.SpamMessages < MinimumExamples || model.HamMessages < MinimumExamples) return null;

        var scored = new List<(string Token, double F)>();
        foreach (var token in Tokenize(message))
        {
            if (!model.Tokens.TryGetValue(token, out var counts)) continue;
            var b = (double)counts.Spam / model.SpamMessages;
            var g = (double)counts.Ham / model.HamMessages;
            if (b + g == 0) continue;
            var p = b / (b + g);
            var n = counts.Spam + counts.Ham;
            const double s = 1.0, x = 0.5;
            var f = (s * x + n * p) / (s + n);
            scored.Add((token, Math.Clamp(f, 0.01, 0.99)));
        }

        var evidence = scored.Where(t => Math.Abs(t.F - 0.5) > 0.1)
            .OrderByDescending(t => Math.Abs(t.F - 0.5))
            .Take(MaxEvidence)
            .ToList();
        if (evidence.Count == 0) return new LocalSpamResult(0.5, [], model.SpamMessages + model.HamMessages);

        var h = InverseChiSquare(-2 * evidence.Sum(t => Math.Log(t.F)), 2 * evidence.Count);
        var sp = InverseChiSquare(-2 * evidence.Sum(t => Math.Log(1 - t.F)), 2 * evidence.Count);
        var probability = (1 + h - sp) / 2;
        var shown = evidence.Where(t => probability >= 0.5 ? t.F > 0.5 : t.F < 0.5).Take(5).Select(t => Describe(t.Token)).ToList();
        return new LocalSpamResult(probability, shown, model.SpamMessages + model.HamMessages);
    }

    /// <summary>The words a message is judged on: sender domain, subject and preview (all already on this PC).</summary>
    public static IReadOnlyList<string> Tokenize(MessageSummary message)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var domain = message.From.Domain;
        if (domain.Length > 0) tokens.Add("from:" + domain);
        if (message.HasAttachments) tokens.Add("has:attachment");
        foreach (var word in Words(message.Subject)) tokens.Add("subject:" + word);
        foreach (var word in Words(message.Preview)) tokens.Add(word);
        return tokens.Take(300).ToList();
    }

    private static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var current = new System.Text.StringBuilder();
        foreach (var c in text + " ")
        {
            if (char.IsLetterOrDigit(c) || c is '$' or '€' or '£')
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (current.Length is >= 3 and <= 24) yield return current.ToString();
            current.Clear();
        }
    }

    private static string Describe(string token) =>
        token.StartsWith("from:", StringComparison.Ordinal) ? "sender " + token[5..]
        : token.StartsWith("subject:", StringComparison.Ordinal) ? "“" + token[8..] + "” in the subject"
        : token == "has:attachment" ? "an attachment"
        : "“" + token + "”";

    private static string IdentityOf(MessageSummary message) => message.MessageIdHeader?.Trim() is { Length: > 0 } id ? id : message.Id.ToString("N");

    private static SpamModel Clone(SpamModel source)
    {
        var copy = new SpamModel { SpamMessages = source.SpamMessages, HamMessages = source.HamMessages };
        foreach (var (token, counts) in source.Tokens) copy.Tokens[token] = counts;
        return copy;
    }

    private static void Count(SpamModel model, IReadOnlyList<string> tokens, bool isSpam, int delta)
    {
        if (isSpam) model.SpamMessages = Math.Max(0, model.SpamMessages + delta);
        else model.HamMessages = Math.Max(0, model.HamMessages + delta);
        foreach (var token in tokens)
        {
            model.Tokens.TryGetValue(token, out var counts);
            counts = isSpam ? (Math.Max(0, counts.Spam + delta), counts.Ham) : (counts.Spam, Math.Max(0, counts.Ham + delta));
            if (counts.Spam == 0 && counts.Ham == 0) model.Tokens.Remove(token);
            else model.Tokens[token] = counts;
        }
    }

    /// <summary>Survival function of the chi-square distribution with an even number of degrees of freedom.</summary>
    internal static double InverseChiSquare(double chi, int degreesOfFreedom)
    {
        var m = chi / 2;
        var sum = Math.Exp(-m);
        var term = sum;
        for (var i = 1; i < degreesOfFreedom / 2; i++)
        {
            term *= m / i;
            sum += term;
        }

        return Math.Min(sum, 1.0);
    }
}
