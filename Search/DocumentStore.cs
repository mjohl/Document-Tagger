using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>A searchable passage of a document. Ordinal 0 is the document's metadata card.</summary>
sealed record ChunkRecord(long Id, long DocumentId, int Ordinal, string Text);

/// <summary>A chunk to store: <see cref="Text"/> goes into prompts; <see cref="IndexText"/> (with a title header) is what gets searched and embedded.</summary>
sealed record ChunkInput(string Text, string IndexText);

sealed record RankedChunk(long ChunkId, long DocumentId, double Score);

readonly record struct ChunkVector(long ChunkId, long DocumentId, float[] Vector);

/// <summary>Document metadata without the OCR text, for filtering and catalogues.</summary>
sealed record DocumentSummary(long Id, string Title, DateOnly? DocumentDate, string? Sender, string? Addressee, string? Category, List<string> Tags, string Summary);

sealed record DocumentFilter(DateOnly? From = null, DateOnly? To = null, string? Sender = null, string? Addressee = null, string? Category = null)
{
    public bool IsEmpty => From is null && To is null && Sender is null && Addressee is null && Category is null;
}

sealed record IndexStatus(int Documents, int Indexed, int Embedded, string EmbeddingModel)
{
    public string Message => string.IsNullOrWhiteSpace(EmbeddingModel)
        ? $"Search index: {Indexed:N0} of {Documents:N0} documents indexed for keyword search (no embedding model configured)."
        : $"Search index: {Embedded:N0} of {Documents:N0} documents ready for semantic search with {EmbeddingModel}.";
}

sealed class DocumentStore(TaggerSettings settings)
{
    private readonly object vectorGate = new();
    private (string Model, IReadOnlyList<ChunkVector> Vectors)? vectorCache;

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path.Combine(settings.DatabaseDirectory, "documents.db") }.ToString();

    public void Initialize()
    {
        using var db = Open();
        Execute(db, """CREATE TABLE IF NOT EXISTS documents (id INTEGER PRIMARY KEY, source_path TEXT NOT NULL UNIQUE, content_hash TEXT NULL, original_file_name TEXT NOT NULL, file_path TEXT NOT NULL, title TEXT NOT NULL, document_date TEXT NULL, sender TEXT NULL, addressee TEXT NULL, tags_json TEXT NOT NULL, keywords_json TEXT NOT NULL, summary TEXT NOT NULL, ocr_text TEXT NOT NULL, processed_at TEXT NOT NULL);""");
        EnsureColumn(db, "content_hash", "TEXT NULL");
        EnsureColumn(db, "category", "TEXT NULL");
        BackfillContentHashes(db);
        BackfillCategories(db);

        // The old documents_fts table was recreated empty on every start and never queried. Chunks replace it.
        Execute(db, """
            DROP TABLE IF EXISTS documents_fts;
            CREATE INDEX IF NOT EXISTS ix_documents_date ON documents(document_date);
            CREATE TABLE IF NOT EXISTS chunks (id INTEGER PRIMARY KEY, document_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, text TEXT NOT NULL, embedding BLOB NULL, embedding_model TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks(document_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(text, document_id UNINDEXED, tokenize='porter unicode61 remove_diacritics 2');
            """);
    }

    public bool HasSource(string sourcePath)
    {
        var contentHash = ComputeContentHash(sourcePath);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM documents WHERE source_path=$source OR content_hash=$hash)";
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$hash", contentHash);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public long Upsert(DocumentRecord r)
    {
        using var db = Open();
        if (r.Id == 0)
        {
            using var insert = Command(db, "INSERT INTO documents(source_path,content_hash,original_file_name,file_path,title,document_date,sender,addressee,tags_json,keywords_json,summary,ocr_text,processed_at,category) VALUES($source,$hash,$original,$file,$title,$date,$sender,$addressee,$tags,$keywords,$summary,$ocr,$processed,$category); SELECT last_insert_rowid();", r);
            return (long)(insert.ExecuteScalar() ?? 0L);
        }

        // COALESCE keeps the stored hash when the original scan has since been moved or deleted.
        using var update = Command(db, "UPDATE documents SET source_path=$source,content_hash=COALESCE($hash,content_hash),original_file_name=$original,file_path=$file,title=$title,document_date=$date,sender=$sender,addressee=$addressee,tags_json=$tags,keywords_json=$keywords,summary=$summary,ocr_text=$ocr,processed_at=$processed,category=$category WHERE id=$id;", r);
        update.Parameters.AddWithValue("$id", r.Id);
        update.ExecuteNonQuery();
        return r.Id;
    }

    public DocumentRecord? Get(long id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT * FROM documents WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<string> GetTags() => Search(new DocumentQuery()).SelectMany(x => x.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

    public IReadOnlyList<DocumentRecord> Search(DocumentQuery q)
    {
        using var db = Open(); using var command = db.CreateCommand(); var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.Name)) { where.Add("(title LIKE $name OR sender LIKE $name OR addressee LIKE $name)"); command.Parameters.AddWithValue("$name", $"%{q.Name}%"); }
        if (!string.IsNullOrWhiteSpace(q.Content)) { where.Add("(ocr_text LIKE $content OR keywords_json LIKE $content OR summary LIKE $content)"); command.Parameters.AddWithValue("$content", $"%{q.Content}%"); }
        if (!string.IsNullOrWhiteSpace(q.Tag)) { where.Add("tags_json LIKE $tag"); command.Parameters.AddWithValue("$tag", $"%{q.Tag}%"); }
        if (!string.IsNullOrWhiteSpace(q.From)) { where.Add("document_date >= $from"); command.Parameters.AddWithValue("$from", q.From); }
        if (!string.IsNullOrWhiteSpace(q.To)) { where.Add("document_date <= $to"); command.Parameters.AddWithValue("$to", q.To); }
        command.CommandText = "SELECT * FROM documents" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY COALESCE(document_date, processed_at) DESC, id DESC";
        using var reader = command.ExecuteReader(); var records = new List<DocumentRecord>(); while (reader.Read()) records.Add(Read(reader)); return records;
    }

    public int CountDocuments()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM documents";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Documents matching structured filters, newest first, without loading OCR text.</summary>
    public IReadOnlyList<DocumentSummary> FindDocuments(DocumentFilter filter)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        var where = new List<string>();
        if (filter.From is { } from) { where.Add("document_date >= $from"); command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); }
        if (filter.To is { } to) { where.Add("document_date <= $to"); command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); }
        if (filter.Sender is { } sender)
        {
            // Senders are often written differently in the question ("HMRC") and the metadata, so titles and tags count too.
            where.Add("(sender LIKE $sender ESCAPE '\\' OR title LIKE $sender ESCAPE '\\' OR tags_json LIKE $sender ESCAPE '\\' OR keywords_json LIKE $sender ESCAPE '\\')");
            command.Parameters.AddWithValue("$sender", Like(sender));
        }
        if (filter.Addressee is { } addressee) { where.Add("addressee LIKE $addressee ESCAPE '\\'"); command.Parameters.AddWithValue("$addressee", Like(addressee)); }
        if (filter.Category is { } category) { where.Add("category = $category COLLATE NOCASE"); command.Parameters.AddWithValue("$category", category); }
        command.CommandText = "SELECT id,title,document_date,sender,addressee,category,tags_json,summary FROM documents"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY COALESCE(document_date, processed_at) DESC, id DESC";
        using var reader = command.ExecuteReader();
        var results = new List<DocumentSummary>();
        while (reader.Read())
        {
            results.Add(new DocumentSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : DateOnly.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
                reader.GetString(7)));
        }
        return results;
    }

    /// <summary>Full-text (BM25) search over chunks, optionally limited to some documents. Lower BM25 is better, so results come back best first.</summary>
    public IReadOnlyList<RankedChunk> SearchChunks(string matchQuery, IReadOnlyCollection<long>? documentIds, int limit)
    {
        if (string.IsNullOrWhiteSpace(matchQuery) || documentIds is { Count: 0 }) return [];
        using var db = Open();
        using var command = db.CreateCommand();
        var scope = documentIds is null ? "" : $" AND c.document_id IN ({string.Join(',', documentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        command.CommandText = $"SELECT c.id, c.document_id, bm25(chunks_fts) AS score FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.rowid WHERE chunks_fts MATCH $query{scope} ORDER BY score LIMIT $limit";
        command.Parameters.AddWithValue("$query", matchQuery);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<RankedChunk>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) results.Add(new RankedChunk(reader.GetInt64(0), reader.GetInt64(1), -reader.GetDouble(2)));
        }
        catch (SqliteException) { return []; }
        return results;
    }

    public IReadOnlyDictionary<long, ChunkRecord> GetChunks(IEnumerable<long> chunkIds)
    {
        var ids = chunkIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, ChunkRecord>();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT id, document_id, ordinal, text FROM chunks WHERE id IN ({string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        return ReadChunks(command).ToDictionary(chunk => chunk.Id);
    }

    public IReadOnlyList<ChunkRecord> GetDocumentChunks(long documentId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id, document_id, ordinal, text FROM chunks WHERE document_id=$id ORDER BY ordinal";
        command.Parameters.AddWithValue("$id", documentId);
        return ReadChunks(command);
    }

    /// <summary>The metadata-card chunks of the given documents, in the order given.</summary>
    public IReadOnlyList<ChunkRecord> GetMetadataChunks(IReadOnlyList<long> documentIds)
    {
        if (documentIds.Count == 0) return [];
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT id, document_id, ordinal, text FROM chunks WHERE ordinal = 0 AND document_id IN ({string.Join(',', documentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        var byDocument = ReadChunks(command).ToDictionary(chunk => chunk.DocumentId);
        return documentIds.Where(byDocument.ContainsKey).Select(id => byDocument[id]).ToList();
    }

    /// <summary>Replaces a document's chunks, their full-text entries and their embeddings in one transaction.</summary>
    public void ReplaceChunks(long documentId, IReadOnlyList<ChunkInput> chunks, float[][]? embeddings, string? embeddingModel)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var delete = db.CreateCommand())
        {
            delete.CommandText = "DELETE FROM chunks_fts WHERE rowid IN (SELECT id FROM chunks WHERE document_id=$id); DELETE FROM chunks WHERE document_id=$id;";
            delete.Parameters.AddWithValue("$id", documentId);
            delete.ExecuteNonQuery();
        }

        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO chunks(document_id, ordinal, text, embedding, embedding_model) VALUES($document, $ordinal, $text, $embedding, $model); SELECT last_insert_rowid();";
        var document = insert.Parameters.Add("$document", SqliteType.Integer);
        var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
        var text = insert.Parameters.Add("$text", SqliteType.Text);
        var embedding = insert.Parameters.Add("$embedding", SqliteType.Blob);
        var model = insert.Parameters.Add("$model", SqliteType.Text);

        using var fts = db.CreateCommand();
        fts.CommandText = "INSERT INTO chunks_fts(rowid, text, document_id) VALUES($id, $text, $document);";
        var ftsId = fts.Parameters.Add("$id", SqliteType.Integer);
        var ftsText = fts.Parameters.Add("$text", SqliteType.Text);
        fts.Parameters.AddWithValue("$document", documentId);

        for (var i = 0; i < chunks.Count; i++)
        {
            document.Value = documentId;
            ordinal.Value = i;
            text.Value = chunks[i].Text;
            embedding.Value = embeddings is null ? DBNull.Value : MemoryMarshal.AsBytes(embeddings[i].AsSpan()).ToArray();
            model.Value = embeddings is null ? DBNull.Value : embeddingModel;
            var id = (long)(insert.ExecuteScalar() ?? 0L);

            ftsId.Value = id;
            ftsText.Value = chunks[i].IndexText;
            fts.ExecuteNonQuery();
        }
        transaction.Commit();
        lock (vectorGate) vectorCache = null;
    }

    /// <summary>All stored embeddings for a model, cached in memory until the index changes.</summary>
    public IReadOnlyList<ChunkVector> GetVectors(string embeddingModel)
    {
        lock (vectorGate)
        {
            if (vectorCache is { } cached && cached.Model == embeddingModel) return cached.Vectors;
        }

        var vectors = new List<ChunkVector>();
        using (var db = Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT id, document_id, embedding FROM chunks WHERE embedding IS NOT NULL AND embedding_model=$model";
            command.Parameters.AddWithValue("$model", embeddingModel);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bytes = (byte[])reader.GetValue(2);
                vectors.Add(new ChunkVector(reader.GetInt64(0), reader.GetInt64(1), MemoryMarshal.Cast<byte, float>(bytes).ToArray()));
            }
        }

        lock (vectorGate) vectorCache = (embeddingModel, vectors);
        return vectors;
    }

    /// <summary>Documents with no chunks yet, or (when embeddings are enabled) chunks missing an embedding from the configured model.</summary>
    public IReadOnlyList<long> GetDocumentsNeedingIndex(string embeddingModel)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(embeddingModel)
            ? "SELECT d.id FROM documents d WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id) ORDER BY d.id"
            : "SELECT d.id FROM documents d WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id) OR EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id AND (c.embedding IS NULL OR c.embedding_model IS NOT $model)) ORDER BY d.id";
        command.Parameters.AddWithValue("$model", embeddingModel ?? "");
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public IndexStatus GetIndexStatus(string embeddingModel)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM documents),
              (SELECT COUNT(DISTINCT document_id) FROM chunks),
              (SELECT COUNT(*) FROM documents d WHERE EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id)
                 AND NOT EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id AND (c.embedding IS NULL OR c.embedding_model IS NOT $model)))
            """;
        command.Parameters.AddWithValue("$model", embeddingModel ?? "");
        using var reader = command.ExecuteReader();
        reader.Read();
        return new IndexStatus(reader.GetInt32(0), reader.GetInt32(1), string.IsNullOrWhiteSpace(embeddingModel) ? 0 : reader.GetInt32(2), embeddingModel ?? "");
    }

    private SqliteConnection Open() { var db = new SqliteConnection(ConnectionString); db.Open(); return db; }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection db, string column, string definition)
    {
        using (var columns = db.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(documents);";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        Execute(db, $"ALTER TABLE documents ADD COLUMN {column} {definition};");
    }

    private void BackfillCategories(SqliteConnection db)
    {
        var updates = new List<(long Id, string Category)>();
        using (var select = db.CreateCommand())
        {
            select.CommandText = "SELECT id, file_path FROM documents WHERE category IS NULL;";
            using var reader = select.ExecuteReader();
            while (reader.Read()) updates.Add((reader.GetInt64(0), CategoryFromPath(reader.GetString(1))));
        }
        foreach (var (id, category) in updates)
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE documents SET category=$category WHERE id=$id;";
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Processed files live in ProcessedDirectory\&lt;category&gt;\&lt;year&gt;, so older rows can recover their category from the path.</summary>
    private string CategoryFromPath(string filePath)
    {
        try
        {
            var relative = Path.GetRelativePath(settings.ProcessedDirectory, filePath);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
            return relative.StartsWith("..", StringComparison.Ordinal) ? "Unsorted" : DocumentCategories.Normalize(first);
        }
        catch (ArgumentException) { return "Unsorted"; }
    }

    private void BackfillContentHashes(SqliteConnection db)
    {
        var updates = new List<(long Id, string Hash)>();
        using (var select = db.CreateCommand())
        {
            select.CommandText = "SELECT id, source_path, file_path FROM documents WHERE content_hash IS NULL;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var sourcePath = reader.GetString(1);
                var filePath = reader.GetString(2);
                var path = File.Exists(sourcePath) ? sourcePath : filePath;
                if (File.Exists(path)) updates.Add((reader.GetInt64(0), ComputeContentHash(path)));
            }
        }

        foreach (var update in updates)
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE documents SET content_hash=$hash WHERE id=$id;";
            command.Parameters.AddWithValue("$hash", update.Hash);
            command.Parameters.AddWithValue("$id", update.Id);
            command.ExecuteNonQuery();
        }
    }

    private static string ComputeContentHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Like(string value) => "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static List<ChunkRecord> ReadChunks(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var chunks = new List<ChunkRecord>();
        while (reader.Read()) chunks.Add(new ChunkRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));
        return chunks;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, DocumentRecord r)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$source", r.SourcePath);
        c.Parameters.AddWithValue("$hash", File.Exists(r.SourcePath) ? ComputeContentHash(r.SourcePath) : (object)DBNull.Value);
        c.Parameters.AddWithValue("$original", r.OriginalFileName);
        c.Parameters.AddWithValue("$file", r.FilePath);
        c.Parameters.AddWithValue("$title", r.Title);
        c.Parameters.AddWithValue("$date", (object?)r.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        c.Parameters.AddWithValue("$sender", (object?)r.Sender ?? DBNull.Value);
        c.Parameters.AddWithValue("$addressee", (object?)r.Addressee ?? DBNull.Value);
        c.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(r.Tags));
        c.Parameters.AddWithValue("$keywords", JsonSerializer.Serialize(r.Keywords));
        c.Parameters.AddWithValue("$summary", r.Summary);
        c.Parameters.AddWithValue("$ocr", r.OcrText);
        c.Parameters.AddWithValue("$processed", r.ProcessedAt.ToString("O"));
        c.Parameters.AddWithValue("$category", (object?)r.Category ?? DBNull.Value);
        return c;
    }

    private static DocumentRecord Read(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("source_path")),
        r.GetString(r.GetOrdinal("original_file_name")),
        r.GetString(r.GetOrdinal("file_path")),
        r.GetString(r.GetOrdinal("title")),
        r.IsDBNull(r.GetOrdinal("document_date")) ? null : DateOnly.Parse(r.GetString(r.GetOrdinal("document_date")), CultureInfo.InvariantCulture),
        r.IsDBNull(r.GetOrdinal("sender")) ? null : r.GetString(r.GetOrdinal("sender")),
        r.IsDBNull(r.GetOrdinal("addressee")) ? null : r.GetString(r.GetOrdinal("addressee")),
        JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("tags_json"))) ?? new(),
        JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("keywords_json"))) ?? new(),
        r.GetString(r.GetOrdinal("summary")),
        r.GetString(r.GetOrdinal("ocr_text")),
        DateTimeOffset.Parse(r.GetString(r.GetOrdinal("processed_at")), CultureInfo.InvariantCulture),
        r.IsDBNull(r.GetOrdinal("category")) ? null : r.GetString(r.GetOrdinal("category")));
}
