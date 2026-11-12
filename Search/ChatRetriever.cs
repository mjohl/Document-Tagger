using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>What the model understood the question to ask for: search text, hard filters and the kind of answer.</summary>
sealed record SearchPlan(string SearchText, DateOnly? DateFrom, DateOnly? DateTo, string? Sender, string? Addressee, string? Category, IReadOnlyList<string> Tags, string Intent)
{
    private static readonly HashSet<string> Intents = ["lookup", "list", "count", "summarize"];

    public DocumentFilter Filter => new(DateFrom, DateTo, Sender, Addressee, Category);

    public static SearchPlan Fallback(string question) => new(question, null, null, null, null, null, [], "lookup");

    public static SearchPlan Parse(string json, string question)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Fallback(question);

        var from = OllamaClient.ParseDate(OllamaClient.Text(root, "dateFrom"));
        var to = OllamaClient.ParseDate(OllamaClient.Text(root, "dateTo"));
        if (from > to) (from, to) = (to, from);
        var category = OllamaClient.Text(root, "category");
        var intent = OllamaClient.Text(root, "intent")?.ToLowerInvariant();
        return new SearchPlan(
            OllamaClient.Text(root, "searchText") ?? question,
            from,
            to,
            Name(OllamaClient.Text(root, "sender")),
            Name(OllamaClient.Text(root, "addressee")),
            category is not null && DocumentCategories.IsCategory(category) ? DocumentCategories.Normalize(category) : null,
            OllamaClient.List(root, "tags").Take(5).ToList(),
            intent is not null && Intents.Contains(intent) ? intent : "lookup");
    }

    public string DescribeFilter()
    {
        var parts = new List<string>();
        if (DateFrom is not null || DateTo is not null)
            parts.Add($"dated {DateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "any time"} to {DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "now"}");
        if (Sender is not null) parts.Add($"from or about \"{Sender}\"");
        if (Addressee is not null) parts.Add($"addressed to \"{Addressee}\"");
        if (Category is not null) parts.Add($"category {Category}");
        return string.Join("; ", parts);
    }

    private static string? Name(string? value) => value is { Length: >= 2 } ? value : null;
}

sealed record SourceDocument(long Id, string Title, string? Date);

/// <summary>The archive context for one question, plus the documents it mentions so citations can be linked.</summary>
sealed record RetrievedContext(string Text, string Status, IReadOnlyDictionary<long, SourceDocument> Documents, IReadOnlyList<long> ExcerptDocumentIds)
{
    /// <summary>The documents the answer cited, or the documents excerpts came from when it cited none.</summary>
    public IReadOnlyList<SourceDocument> SourcesFor(string answer)
    {
        var cited = Regex.Matches(answer, @"#(\d+)")
            .Select(match => long.TryParse(match.Groups[1].Value, out var id) ? id : 0)
            .Where(Documents.ContainsKey)
            .Distinct()
            .ToList();
        var ids = cited.Count > 0 ? cited : ExcerptDocumentIds.Take(5).ToList();
        return ids.Select(id => Documents[id]).ToList();
    }
}

/// <summary>
/// Finds the passages a question needs: structured filters (dates, sender, category) narrow the documents,
/// then keyword (BM25) and semantic (embedding) rankings are fused, and everything is packed into the token budget.
/// </summary>
sealed class ChatRetriever(DocumentStore store, DocumentIndexer indexer, OllamaClient ollama, TaggerSettings settings, ILogger<ChatRetriever> logger)
{
    private const int CandidatePool = 40;
    private const int MaxChunksPerDocument = 3;
    private const int RrfK = 60;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "any", "are", "as", "at", "be", "by", "can", "did", "do", "does", "for", "from", "get", "give", "got", "had", "has", "have",
        "how", "i", "if", "in", "is", "it", "its", "me", "my", "of", "on", "or", "our", "please", "show", "so", "tell", "than", "that", "the", "their",
        "them", "there", "these", "this", "those", "to", "was", "we", "were", "what", "when", "where", "which", "who", "whom", "why", "will", "with",
        "you", "your", "all", "about", "document", "documents", "list", "many", "much", "find"
    };

    public async Task<RetrievedContext> RetrieveArchiveAsync(string question, IReadOnlyList<ChatTurn> history, int budgetTokens, Func<string, Task> status, CancellationToken cancellationToken)
    {
        var total = store.CountDocuments();
        if (total == 0)
            return new RetrievedContext("No documents have been indexed yet.", "The archive is empty.", new Dictionary<long, SourceDocument>(), []);

        await status("Understanding your question…");
        var plan = await ollama.ExtractSearchPlanAsync(question, history, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        logger.LogInformation("Search plan: {Plan}", plan);

        await status($"Searching {total:N0} document{(total == 1 ? "" : "s")}…");
        var (candidates, filtered, note) = FindCandidates(plan);
        var documents = candidates.ToDictionary(candidate => candidate.Id);
        IReadOnlyCollection<long>? scope = filtered ? documents.Keys.ToHashSet() : null;

        var query = string.Join(" ", plan.Tags.Prepend(plan.SearchText));
        var ranked = await RankAsync(query, scope, cancellationToken);
        if (ranked.Count == 0 && filtered)
        {
            // Nothing in the filtered set matched the words, so fall back to the newest matching documents' metadata cards.
            ranked = store.GetMetadataChunks(candidates.Take(settings.MaxContextChunks).Select(c => c.Id).ToList())
                .Select((chunk, index) => new RankedChunk(chunk.Id, chunk.DocumentId, -index)).ToList();
        }

        var context = new StringBuilder();
        context.AppendLine(filtered
            ? $"Search summary: exactly {candidates.Count:N0} of the {total:N0} documents in the archive match the filters ({plan.DescribeFilter()})."
            : $"Search summary: the whole archive of {total:N0} documents was searched; the most relevant documents are below.");
        if (note is not null) context.AppendLine(note);

        var wantsCatalogue = plan.Intent is "list" or "count" or "summarize" || (filtered && candidates.Count > settings.MaxContextChunks);
        if (wantsCatalogue)
        {
            // Filtered: catalogue every matching document. Unfiltered: catalogue the best-ranked documents.
            var catalogueDocuments = filtered
                ? candidates
                : ranked.Select(chunk => chunk.DocumentId).Distinct().Where(documents.ContainsKey).Select(id => documents[id]).Take(30).ToList();
            AppendCatalogue(context, catalogueDocuments, filtered ? candidates.Count : catalogueDocuments.Count, budgetTokens * 4 / 10);
        }

        var excerptBudget = budgetTokens - TokenEstimate.Of(context.ToString());
        var excerpts = SelectExcerpts(ranked, excerptBudget, settings.MaxContextChunks);
        AppendExcerpts(context, excerpts, documents);

        var excerptDocuments = excerpts.Select(chunk => chunk.DocumentId).Distinct().ToList();
        var statusMessage = filtered
            ? $"Found {candidates.Count:N0} matching document{(candidates.Count == 1 ? "" : "s")}; reading {excerpts.Count} passage{(excerpts.Count == 1 ? "" : "s")}…"
            : $"Reading {excerpts.Count} relevant passage{(excerpts.Count == 1 ? "" : "s")} from {excerptDocuments.Count} document{(excerptDocuments.Count == 1 ? "" : "s")}…";
        return new RetrievedContext(context.ToString().TrimEnd(), statusMessage, documents.ToDictionary(pair => pair.Key, pair => Source(pair.Value)), excerptDocuments);
    }

    public async Task<RetrievedContext> RetrieveDocumentAsync(DocumentRecord document, string question, IReadOnlyList<ChatTurn> history, int budgetTokens, Func<string, Task> status, CancellationToken cancellationToken)
    {
        var chunks = store.GetDocumentChunks(document.Id);
        if (chunks.Count == 0)
        {
            await status("Indexing this document for search…");
            await indexer.IndexAsync(document, cancellationToken: cancellationToken);
            chunks = store.GetDocumentChunks(document.Id);
        }

        var selected = chunks.ToList();
        if (chunks.Sum(chunk => TokenEstimate.Of(chunk.Text) + 12) > budgetTokens)
        {
            // Too long to send whole: keep the metadata card, then the passages that best match the question.
            await status("This is a long document; finding the passages that answer your question…");
            var previousQuestion = history.LastOrDefault(turn => turn.Role == "user")?.Content;
            var ranked = await RankAsync($"{question} {previousQuestion}", new HashSet<long> { document.Id }, cancellationToken);
            var byId = chunks.ToDictionary(chunk => chunk.Id);
            var order = chunks.Where(chunk => chunk.Ordinal == 0)
                .Concat(ranked.Where(hit => byId.ContainsKey(hit.ChunkId)).Select(hit => byId[hit.ChunkId]))
                .Concat(chunks)
                .DistinctBy(chunk => chunk.Id);
            selected = [];
            var used = 0;
            foreach (var chunk in order)
            {
                var cost = TokenEstimate.Of(chunk.Text) + 12;
                if (used + cost > budgetTokens) continue;
                selected.Add(chunk);
                used += cost;
            }
            selected = selected.OrderBy(chunk => chunk.Ordinal).ToList();
        }

        var source = Source(document);
        var context = new StringBuilder();
        context.AppendLine($"The user is asking about one document: [#{document.Id}] {document.Title}.");
        context.AppendLine(selected.Count < chunks.Count
            ? $"It is long, so only the {selected.Count - 1} of {chunks.Count - 1} text passages most relevant to the question are included."
            : "Its full metadata and OCR text are included.");
        foreach (var chunk in selected)
        {
            context.AppendLine();
            context.AppendLine(chunk.Ordinal == 0 ? $"=== [#{document.Id}] metadata ===" : $"=== [#{document.Id}] text, part {chunk.Ordinal} of {chunks.Count - 1} ===");
            context.AppendLine(chunk.Text);
        }
        var statusMessage = selected.Count < chunks.Count ? $"Reading {selected.Count - 1} relevant passages…" : "Reading the document…";
        return new RetrievedContext(context.ToString().TrimEnd(), statusMessage, new Dictionary<long, SourceDocument> { [document.Id] = source }, [document.Id]);
    }

    /// <summary>Applies the plan's filters, relaxing them rather than returning nothing when they match no document.</summary>
    private (IReadOnlyList<DocumentSummary> Candidates, bool Filtered, string? Note) FindCandidates(SearchPlan plan)
    {
        var filter = plan.Filter;
        if (filter.IsEmpty) return (store.FindDocuments(filter), false, null);

        var matches = store.FindDocuments(filter);
        if (matches.Count > 0) return (matches, true, null);

        var datesOnly = new DocumentFilter(filter.From, filter.To);
        if (!datesOnly.IsEmpty && datesOnly != filter)
        {
            matches = store.FindDocuments(datesOnly);
            if (matches.Count > 0)
            {
                var relaxed = plan with { Sender = null, Addressee = null, Category = null };
                return (matches, true, $"Note: no document matched every filter ({plan.DescribeFilter()}), so only the date range ({relaxed.DescribeFilter()}) was applied. Check the catalogue and excerpts for the documents the question means.");
            }
        }
        return (store.FindDocuments(new DocumentFilter()), false, $"Note: no document matched the filters ({plan.DescribeFilter()}), so the whole archive was searched instead.");
    }

    /// <summary>Reciprocal-rank fusion of BM25 keyword hits and embedding similarity, best first.</summary>
    private async Task<IReadOnlyList<RankedChunk>> RankAsync(string query, IReadOnlyCollection<long>? scope, CancellationToken cancellationToken)
    {
        var keyword = store.SearchChunks(FtsQuery(query), scope, CandidatePool);
        IReadOnlyList<RankedChunk> semantic = [];
        if (!string.IsNullOrWhiteSpace(settings.EmbeddingModel))
        {
            try
            {
                var vectors = store.GetVectors(settings.EmbeddingModel);
                if (vectors.Count > 0)
                {
                    var queryVector = (await ollama.EmbedAsync([query], isQuery: true, cancellationToken))[0];
                    semantic = vectors
                        .Where(vector => scope is null || scope.Contains(vector.DocumentId))
                        .Select(vector => new RankedChunk(vector.ChunkId, vector.DocumentId, Dot(queryVector, vector.Vector)))
                        .OrderByDescending(hit => hit.Score)
                        .Take(CandidatePool)
                        .ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Semantic search is unavailable, so only keyword search was used: {Error}", ex.Message);
            }
        }

        var fused = new Dictionary<long, (long DocumentId, double Score)>();
        foreach (var list in new[] { keyword, semantic })
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var hit = list[rank];
                var score = 1.0 / (RrfK + rank + 1);
                fused[hit.ChunkId] = fused.TryGetValue(hit.ChunkId, out var existing) ? (hit.DocumentId, existing.Score + score) : (hit.DocumentId, score);
            }
        }
        return fused.Select(pair => new RankedChunk(pair.Key, pair.Value.DocumentId, pair.Value.Score)).OrderByDescending(hit => hit.Score).ToList();
    }

    private List<ChunkRecord> SelectExcerpts(IReadOnlyList<RankedChunk> ranked, int budgetTokens, int maxChunks)
    {
        var texts = store.GetChunks(ranked.Take(CandidatePool * 2).Select(hit => hit.ChunkId));
        var perDocument = new Dictionary<long, int>();
        var selected = new List<ChunkRecord>();
        var used = 0;
        foreach (var hit in ranked)
        {
            if (selected.Count >= maxChunks) break;
            if (!texts.TryGetValue(hit.ChunkId, out var chunk)) continue;
            if (perDocument.GetValueOrDefault(hit.DocumentId) >= MaxChunksPerDocument) continue;
            var cost = TokenEstimate.Of(chunk.Text) + 30;
            if (used + cost > budgetTokens) continue;
            selected.Add(chunk);
            perDocument[hit.DocumentId] = perDocument.GetValueOrDefault(hit.DocumentId) + 1;
            used += cost;
        }
        return selected;
    }

    private static void AppendCatalogue(StringBuilder context, IReadOnlyList<DocumentSummary> documents, int matchCount, int budgetTokens)
    {
        var lines = new List<string>();
        var used = 0;
        foreach (var document in documents)
        {
            var tags = document.Tags.Count == 0 ? "" : " | tags: " + string.Join(", ", document.Tags.Take(4));
            var line = $"[#{document.Id}] {Date(document.DocumentDate)} | {document.Title} | from: {document.Sender ?? "unknown"} | to: {document.Addressee ?? "unknown"} | {document.Category ?? "Unsorted"}{tags}";
            var cost = TokenEstimate.Of(line) + 1;
            if (used + cost > budgetTokens) break;
            lines.Add(line);
            used += cost;
        }
        context.AppendLine();
        context.AppendLine(lines.Count < matchCount
            ? $"Document catalogue (newest first; showing {lines.Count} of {matchCount} — say that the list is incomplete):"
            : $"Document catalogue (newest first; all {matchCount}):");
        foreach (var line in lines) context.AppendLine(line);
    }

    private static void AppendExcerpts(StringBuilder context, IReadOnlyList<ChunkRecord> excerpts, IReadOnlyDictionary<long, DocumentSummary> documents)
    {
        context.AppendLine();
        if (excerpts.Count == 0)
        {
            context.AppendLine("Document excerpts: none of the documents contain text matching the question.");
            return;
        }
        context.AppendLine("Document excerpts (most relevant first):");
        foreach (var group in excerpts.GroupBy(chunk => chunk.DocumentId))
        {
            var heading = documents.TryGetValue(group.Key, out var document)
                ? $"=== [#{document.Id}] {document.Title} | {Date(document.DocumentDate)} | from: {document.Sender ?? "unknown"} | to: {document.Addressee ?? "unknown"} ==="
                : $"=== [#{group.Key}] ===";
            context.AppendLine();
            context.AppendLine(heading);
            foreach (var chunk in group.OrderBy(chunk => chunk.Ordinal))
            {
                if (group.Count() > 1) context.AppendLine(chunk.Ordinal == 0 ? "(metadata)" : $"(text, part {chunk.Ordinal})");
                context.AppendLine(chunk.Text);
            }
        }
    }

    /// <summary>An FTS5 query that ORs the question's meaningful words; each is quoted so user text cannot inject FTS syntax.</summary>
    private static string FtsQuery(string text) =>
        string.Join(" OR ", Regex.Matches(text, @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .Where(word => word.Length >= 2 && !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .Select(word => $"\"{word}\""));

    private static double Dot(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        var sum = 0.0;
        for (var i = 0; i < length; i++) sum += left[i] * right[i];
        return sum;
    }

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated";
    private static SourceDocument Source(DocumentSummary document) => new(document.Id, document.Title, document.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    private static SourceDocument Source(DocumentRecord document) => new(document.Id, document.Title, document.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
