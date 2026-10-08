namespace CashPilot.Domain.Descriptions;

public sealed record Classification(string Category, string Item);

public enum ClassificationSource
{
    None = 0,
    /// <summary>Normalized description identical to one already taught.</summary>
    Exact = 1,
    /// <summary>Manually registered "contains" rule (e.g. 99FOOD).</summary>
    Contains = 2,
    /// <summary>Similar enough to a description already taught.</summary>
    Similarity = 3,
}

public sealed record ClassificationResult(Classification? Classification, double Confidence, ClassificationSource Source)
{
    public bool IsClassified => Classification is not null;
    public static ClassificationResult Unknown { get; } = new(null, 0, ClassificationSource.None);
}

/// <summary>
/// Layered classifier: exact match, "contains" rule, similarity (trigrams) and, if nothing fits,
/// returns "unknown" so the user can classify it. After the user decides, call <see cref="Learn"/>.
/// </summary>
public sealed class Classifier
{
    private readonly Dictionary<string, Classification> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _trigrams = new(StringComparer.Ordinal);
    private readonly List<(string Pattern, Classification Classification)> _contains = new();
    // Descriptions seen with different classifications (e.g. a generic "PAGTO ELETRON COBRANCA"): never guessed.
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

    /// <summary>Minimum similarity (0 to 1) to classify without asking.</summary>
    public double SimilarityThreshold { get; }

    public Classifier(double similarityThreshold = 0.8)
    {
        SimilarityThreshold = similarityThreshold;
    }

    public int LearnedCount => _exact.Count;

    /// <summary>Records that this description belongs to this category/item.</summary>
    public void Learn(string description, Classification classification)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        if (normalized.Length == 0) return;

        _exact[normalized] = classification;
        _trigrams[normalized] = Trigrams(normalized);
        _ambiguous.Remove(normalized); // an explicit decision settles the doubt
    }

    /// <summary>
    /// Records a classification seen in existing data (bulk history), as opposed to a decision by the user.
    /// If the same description was seen with a different classification, it becomes ambiguous and is not guessed.
    /// </summary>
    public void Observe(string description, Classification classification)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        if (normalized.Length == 0) return;

        if (_exact.TryGetValue(normalized, out var existing) && existing != classification)
            _ambiguous.Add(normalized);

        _exact[normalized] = classification;
        _trigrams[normalized] = Trigrams(normalized);
    }

    /// <summary>Restores a persisted ambiguity (only for descriptions already known).</summary>
    public void MarkAmbiguous(string description)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        if (_exact.ContainsKey(normalized)) _ambiguous.Add(normalized);
    }

    public bool IsAmbiguous(string description) =>
        _ambiguous.Contains(DescriptionNormalizer.Normalize(description));

    /// <summary>Manual rule: if the normalized description contains the pattern, use this classification.</summary>
    public void AddContainsRule(string pattern, Classification classification)
    {
        var normalized = DescriptionNormalizer.Normalize(pattern);
        if (normalized.Length == 0) return;

        _contains.Add((normalized, classification));
        // Longer (more specific) patterns first.
        _contains.Sort((a, b) => b.Pattern.Length.CompareTo(a.Pattern.Length));
    }

    public ClassificationResult Classify(string description)
    {
        var normalized = DescriptionNormalizer.Normalize(description);
        if (normalized.Length == 0) return ClassificationResult.Unknown;

        if (_exact.TryGetValue(normalized, out var exact))
        {
            return _ambiguous.Contains(normalized)
                ? ClassificationResult.Unknown
                : new ClassificationResult(exact, 1.0, ClassificationSource.Exact);
        }

        foreach (var (pattern, classification) in _contains)
        {
            if (normalized.Contains(pattern, StringComparison.Ordinal))
                return new ClassificationResult(classification, 1.0, ClassificationSource.Contains);
        }

        var target = Trigrams(normalized);
        string? best = null;
        var bestScore = 0.0;
        foreach (var (key, trigrams) in _trigrams)
        {
            if (_ambiguous.Contains(key)) continue;
            var score = Jaccard(target, trigrams);
            if (score > bestScore)
            {
                bestScore = score;
                best = key;
            }
        }

        if (best is not null && bestScore >= SimilarityThreshold)
            return new ClassificationResult(_exact[best], bestScore, ClassificationSource.Similarity);

        return ClassificationResult.Unknown;
    }

    private static HashSet<string> Trigrams(string text)
    {
        var padded = "  " + text + " ";
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 3 <= padded.Length; i++)
            set.Add(padded.Substring(i, 3));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return (double)intersection / union;
    }
}
