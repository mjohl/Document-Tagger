using System.Text.RegularExpressions;

/// <summary>A new scan held back because it looks like a document already in the library.</summary>
/// <param name="Reason"><c>identical</c> for a byte-for-byte copy, <c>similar</c> for a rescan with near-identical text.</param>
/// <param name="Status"><c>pending</c> while waiting for the user, <c>approved</c> once they chose to process it anyway.</param>
sealed record ScanReview(long Id, string SourcePath, string ContentHash, string? OcrText, long DuplicateOfId, double Similarity, string Reason, DateTimeOffset DetectedAt, string Status)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string IdenticalReason = "identical";
    public const string SimilarReason = "similar";

    public string FileName => Path.GetFileName(SourcePath);
    public bool IsIdentical => Reason == IdenticalReason;
}

sealed record DuplicateMatch(long DocumentId, double Similarity);

/// <summary>
/// Spots a rescan of a document that is already in the library by comparing OCR text.
/// Two scans of the same page never produce identical files or identical OCR, so the score averages the overlap of
/// the words and of adjacent word pairs: a misread word costs little, while the word order still has to match.
/// A separate check on the numbers stops a new monthly bill from the same company — same wording, different
/// dates and amounts — from looking like a duplicate of last month's. That check is deliberately looser than the
/// text score, because OCR often misreads a digit or two in an otherwise identical rescan.
/// </summary>
static partial class DuplicateDetector
{
    private const int MinimumWords = 15;
    private const int MinimumNumbers = 3;
    private const double MinimumNumberOverlap = 0.5;

    public static DuplicateMatch? FindBestMatch(string text, IEnumerable<(long Id, string Text)> candidates, double threshold)
    {
        var scan = Fingerprint.Create(text);
        if (scan.WordCount < MinimumWords) return null; // Too little text to judge reliably.

        DuplicateMatch? best = null;
        foreach (var (id, candidateText) in candidates)
        {
            // Jaccard similarity can never exceed the ratio of the two sizes, so skip documents of very different length cheaply.
            var lengthRatio = (double)Math.Min(text.Length, candidateText.Length) / Math.Max(1, Math.Max(text.Length, candidateText.Length));
            if (lengthRatio < threshold * 0.6) continue;

            var candidate = Fingerprint.Create(candidateText);
            if (candidate.WordCount < MinimumWords) continue;
            var similarity = (Jaccard(scan.Words, candidate.Words) + Jaccard(scan.WordPairs, candidate.WordPairs)) / 2;
            if (similarity < threshold) continue;
            if (scan.Numbers.Count >= MinimumNumbers && candidate.Numbers.Count >= MinimumNumbers && Jaccard(scan.Numbers, candidate.Numbers) < MinimumNumberOverlap) continue;
            if (best is null || similarity > best.Similarity) best = new DuplicateMatch(id, similarity);
        }
        return best;
    }

    private static double Jaccard<T>(HashSet<T> left, HashSet<T> right)
    {
        if (left.Count == 0 && right.Count == 0) return 1;
        var (small, large) = left.Count <= right.Count ? (left, right) : (right, left);
        var shared = small.Count(large.Contains);
        return (double)shared / (left.Count + right.Count - shared);
    }

    private sealed record Fingerprint(HashSet<string> Words, HashSet<string> WordPairs, HashSet<string> Numbers, int WordCount)
    {
        public static Fingerprint Create(string text)
        {
            var lower = text.ToLowerInvariant();
            // Single characters are mostly OCR noise (stray marks, broken letters), so they are ignored.
            var words = WordPattern().Matches(lower).Select(match => match.Value).Where(word => word.Length > 1).ToList();
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < words.Count; i++) pairs.Add(words[i - 1] + " " + words[i]);
            // Dates, amounts and references, compared on their digits only: "84.20" and "84,20" both become "8420".
            var numbers = NumberPattern().Matches(lower).Select(match => new string(match.Value.Where(char.IsDigit).ToArray()))
                .Where(digits => digits.Length >= 2).ToHashSet(StringComparer.Ordinal);
            return new Fingerprint(words.ToHashSet(StringComparer.Ordinal), pairs, numbers, words.Count);
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\d[\d.,/:-]*\d")]
    private static partial Regex NumberPattern();
}
