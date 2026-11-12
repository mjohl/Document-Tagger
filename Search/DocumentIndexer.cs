using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Splits documents into searchable chunks and stores them with their embeddings.</summary>
sealed class DocumentIndexer(DocumentStore store, OllamaClient ollama, TaggerSettings settings, ILogger<DocumentIndexer> logger)
{
    /// <summary>Rebuilds a document's chunks. Returns false when embedding failed; the chunks are still stored for keyword search.</summary>
    public async Task<bool> IndexAsync(DocumentRecord record, bool embed = true, CancellationToken cancellationToken = default)
    {
        var chunks = DocumentChunker.Build(record, settings.ChunkSize, settings.ChunkOverlap);
        float[][]? vectors = null;
        if (embed && !string.IsNullOrWhiteSpace(settings.EmbeddingModel))
        {
            try
            {
                vectors = await ollama.EmbedAsync(chunks.Select(chunk => chunk.IndexText).ToList(), isQuery: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not embed document {Id} with {Model}; it is indexed for keyword search only: {Error}", record.Id, settings.EmbeddingModel, ex.Message);
            }
        }
        store.ReplaceChunks(record.Id, chunks, vectors, vectors is null ? null : settings.EmbeddingModel);
        return vectors is not null;
    }
}

static partial class DocumentChunker
{
    /// <summary>
    /// Chunk 0 is a metadata card (title, date, people, tags, summary), so questions about who, when and what
    /// match a document even when its OCR text never repeats those words. The OCR text follows in overlapping chunks.
    /// </summary>
    public static List<ChunkInput> Build(DocumentRecord document, int size, int overlap)
    {
        size = Math.Max(400, size);
        overlap = Math.Clamp(overlap, 0, size / 2);
        var date = document.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated";
        var card = string.Join("\n",
            $"Title: {document.Title}",
            $"Date: {date}",
            $"From: {document.Sender ?? "unknown"}",
            $"To: {document.Addressee ?? "unknown"}",
            $"Category: {document.Category ?? "Unsorted"}",
            $"Tags: {string.Join(", ", document.Tags)}",
            $"Keywords: {string.Join(", ", document.Keywords)}",
            $"Summary: {document.Summary}");
        var header = $"{document.Title} ({date}{(string.IsNullOrWhiteSpace(document.Sender) ? "" : ", from " + document.Sender)})";

        var chunks = new List<ChunkInput> { new(card, card) };
        foreach (var body in Split(Normalize(document.OcrText), size, overlap))
            chunks.Add(new ChunkInput(body, header + "\n" + body));
        return chunks;
    }

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(line => SpaceRuns().Replace(line, " ").TrimEnd());
        return BlankLineRuns().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    private static IEnumerable<string> Split(string text, int size, int overlap)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + size);
            if (end < text.Length) end = BreakPoint(text, start, end, size);
            var piece = text[start..end].Trim();
            if (piece.Length > 0) yield return piece;
            if (end >= text.Length) yield break;

            // Step back by the overlap, then forward to the start of a word.
            var next = Math.Max(end - overlap, start + 1);
            while (next < end && !char.IsWhiteSpace(text[next - 1])) next++;
            start = next;
        }
    }

    /// <summary>Prefers to end a chunk at a paragraph, then a sentence, then a line, then a word.</summary>
    private static int BreakPoint(string text, int start, int end, int size)
    {
        var minimum = start + size / 2;
        foreach (var separator in new[] { "\n\n", ". ", ".\n", "\n", " " })
        {
            var at = text.LastIndexOf(separator, end - 1, end - minimum, StringComparison.Ordinal);
            if (at >= minimum) return at + separator.Length;
        }
        return end;
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRuns();
}

/// <summary>Indexes documents processed before the chunk index existed, and retries embeddings that failed (for example while Ollama was down).</summary>
sealed class IndexBackfillService(IServiceScopeFactory scopes, TaggerSettings settings, ILogger<IndexBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Search index backfill failed."); }
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DocumentStore>();
        var indexer = scope.ServiceProvider.GetRequiredService<DocumentIndexer>();
        var pending = store.GetDocumentsNeedingIndex(settings.EmbeddingModel);
        if (pending.Count == 0) return;

        logger.LogInformation("Indexing {Count} document(s) for search.", pending.Count);
        var embed = true;
        var indexed = 0;
        foreach (var id in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = store.Get(id);
            if (document is null) continue;
            // After the first embedding failure, keep building keyword chunks but stop calling Ollama until the next run.
            if (!await indexer.IndexAsync(document, embed, cancellationToken) && embed)
            {
                embed = false;
                logger.LogWarning("Embeddings are unavailable; documents are being indexed for keyword search only and will be retried in 10 minutes.");
            }
            indexed++;
        }
        logger.LogInformation("Search index backfill finished: {Count} document(s) indexed.", indexed);
    }
}
