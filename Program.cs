using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("DocumentTagger").Get<TaggerSettings>() ?? new();
Directory.CreateDirectory(settings.DatabaseDirectory);
Directory.CreateDirectory(settings.ProcessedDirectory);

builder.Services.AddSingleton(settings);
builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.BaseAddress = new Uri(settings.OllamaUrl);
    client.Timeout = TimeSpan.FromMinutes(8);
});
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<OcrService>();
builder.Services.AddSingleton<ProcessingStatus>();
builder.Services.AddScoped<DocumentProcessor>();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<DocumentStore>().Initialize();

app.MapGet("/", (HttpRequest request, DocumentStore store) =>
{
    var query = DocumentQuery.Parse(request.Query);
    return Results.Content(PageShell("Documents", SearchPage(store.Search(query), query, store.GetTags())), "text/html");
});

app.MapPost("/process", (ProcessingStatus status, IServiceScopeFactory scopeFactory) =>
{
    if (!status.TryBegin()) return Results.Redirect("/processing");
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<DocumentProcessor>().ProcessPendingAsync(status.Report);
            status.Complete(result);
        }
        catch (Exception ex) { status.Fail($"Processing stopped: {ex.Message}"); }
    });
    return Results.Redirect("/processing");
});

app.MapGet("/processing", () => Results.Content(PageShell("Processing scans", ProcessingPage()), "text/html"));
app.MapGet("/api/processing", (ProcessingStatus status) => Results.Json(status.Snapshot()));

app.MapPost("/documents/{id:long}/reprocess", async (long id, DocumentProcessor processor) =>
{
    var result = await processor.ReprocessAsync(id);
    return Results.Redirect($"/documents/{id}?message={Uri.EscapeDataString(result)}");
});

app.MapGet("/documents/{id:long}", (long id, HttpRequest request, DocumentStore store) =>
{
    var document = store.Get(id);
    return document is null
        ? Results.NotFound("Document not found")
        : Results.Content(PageShell(document.Title, DetailPage(document, request.Query["message"])), "text/html");
});

app.MapGet("/documents/{id:long}/edit", (long id, HttpRequest request, DocumentStore store) =>
{
    var document = store.Get(id);
    return document is null
        ? Results.NotFound("Document not found")
        : Results.Content(PageShell($"Edit {document.Title}", EditPage(document, request.Query["message"])), "text/html");
});

app.MapPost("/documents/{id:long}/edit", async (long id, HttpRequest request, DocumentStore store) =>
{
    var existing = store.Get(id);
    if (existing is null) return Results.NotFound("Document not found");

    var form = await request.ReadFormAsync();
    var title = form["title"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(title)) return Results.Redirect($"/documents/{id}/edit?message={Uri.EscapeDataString("A title is required.")}");
    var dateText = form["documentDate"].ToString();
    if (!string.IsNullOrWhiteSpace(dateText) && !DateOnly.TryParse(dateText, out _)) return Results.Redirect($"/documents/{id}/edit?message={Uri.EscapeDataString("Enter a valid document date.")}");
    var updated = existing with
    {
        Title = title,
        DocumentDate = DateOnly.TryParse(dateText, out var date) ? date : null,
        Addressee = EmptyToNull(form["addressee"].ToString()),
        Sender = EmptyToNull(form["sender"].ToString()),
        Tags = Terms(form["tags"].ToString()),
        Keywords = Terms(form["keywords"].ToString()),
        Summary = form["summary"].ToString().Trim(),
        OcrText = form["ocrText"].ToString().Trim()
    };
    store.Upsert(updated);
    DocumentMetadataWriter.Write(updated);
    return Results.Redirect($"/documents/{id}?message={Uri.EscapeDataString("Metadata saved.")}");
});

app.MapGet("/files/{id:long}", (long id, DocumentStore store) =>
{
    var document = store.Get(id);
    return document is null || !File.Exists(document.FilePath)
        ? Results.NotFound()
        : Results.File(document.FilePath, "application/pdf", enableRangeProcessing: true);
});

app.MapGet("/health", async (OllamaClient ollama) => Results.Json(await ollama.HealthAsync()));

app.Run();

static string SearchPage(IReadOnlyList<DocumentRecord> documents, DocumentQuery query, IReadOnlyList<string> tags)
{
    var message = query.Message is { Length: > 0 } ? $"<div class='notice'>{H(query.Message)}</div>" : "";
    var options = string.Join("", tags.Select(t => $"<option value='{A(t)}' {(query.Tag == t ? "selected" : "")}>{H(t)}</option>"));
    var rows = string.Join("", documents.Select(d =>
        $"<tr><td><a href='/documents/{d.Id}'>{H(d.Title)}</a><small>{H(d.OriginalFileName)}</small></td>" +
        $"<td>{H(d.DocumentDate?.ToString("yyyy-MM-dd") ?? "—")}</td><td>{H(d.Addressee ?? "—")}</td><td>{H(d.Sender ?? "—")}</td>" +
        $"<td>{string.Join(" ", d.Tags.Select(t => $"<span class='tag'>{H(t)}</span>"))}</td>" +
        $"<td>{H(Trim(d.Summary, 130))}</td></tr>"));
    if (rows.Length == 0) rows = "<tr><td colspan='6' class='empty'>No processed documents match your search.</td></tr>";
    return $"""
        <div class='hero'><div><p class='eyebrow'>LOCAL DOCUMENT LIBRARY</p><h1>Scanned documents</h1><p>OCR, tags and search stay on this computer.</p></div>
        <form method='post' action='/process'><button class='primary'>Process scans</button></form></div>{message}
        <form class='filters' method='get'><input name='name' value='{A(query.Name)}' placeholder='Title, sender or addressee'> 
        <input name='content' value='{A(query.Content)}' placeholder='Search OCR text'>
        <select name='tag'><option value=''>All tags</option>{options}</select>
        <input name='from' type='date' value='{A(query.From)}'><input name='to' type='date' value='{A(query.To)}'>
        <button>Search</button><a class='clear' href='/'>Clear</a></form>
        <p class='count'>{documents.Count} document{(documents.Count == 1 ? "" : "s")}</p>
        <div class='table-wrap'><table><thead><tr><th>Name</th><th>Date</th><th>To</th><th>From</th><th>Tags</th><th>Summary</th></tr></thead><tbody>{rows}</tbody></table></div>
        """;
}

static string DetailPage(DocumentRecord d, string? message)
{
    var notice = string.IsNullOrWhiteSpace(message) ? "" : $"<div class='notice'>{H(message)}</div>";
    var metadata = $"<dl><dt>Document date</dt><dd>{H(d.DocumentDate?.ToString("dd MMMM yyyy") ?? "Not detected")}</dd>" +
        $"<dt>From</dt><dd>{H(d.Sender ?? "Not detected")}</dd><dt>Addressed to</dt><dd>{H(d.Addressee ?? "Not detected")}</dd>" +
        $"<dt>Tags</dt><dd>{string.Join(" ", d.Tags.Select(t => $"<span class='tag'>{H(t)}</span>"))}</dd>" +
        $"<dt>Keywords</dt><dd>{H(string.Join(", ", d.Keywords))}</dd>" +
        $"<dt>Processed</dt><dd>{H(d.ProcessedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm"))}</dd></dl>";
    return $"""
        <p><a class='back' href='/'>← All documents</a></p>{notice}
        <div class='detail-head'><div><p class='eyebrow'>PROCESSED DOCUMENT</p><h1>{H(d.Title)}</h1><p>{H(d.Summary)}</p></div><div class='actions'><a class='primary' href='/files/{d.Id}' target='_blank'>Open PDF</a><a class='button' href='/documents/{d.Id}/edit'>Edit metadata</a><form method='post' action='/documents/{d.Id}/reprocess'><button>Reprocess</button></form></div></div>
        <section class='metadata'><h2>Metadata</h2>{metadata}</section>
        <section><h2>OCR text</h2><pre>{H(d.OcrText)}</pre></section>
        """;
}

static string EditPage(DocumentRecord d, string? message) => $"""
    <p><a class='back' href='/documents/{d.Id}'>← Back to document</a></p>
    <div class='edit-heading'><p class='eyebrow'>EDIT DOCUMENT</p><h1>Correct metadata</h1><p>Changes update the local search index and the metadata file alongside the PDF.</p></div>{(string.IsNullOrWhiteSpace(message) ? "" : $"<div class='notice'>{H(message)}</div>")}
    <form class='edit-form' method='post' action='/documents/{d.Id}/edit'>
      <div class='form-grid'><label>Title<input required name='title' value='{A(d.Title)}'></label><label>Document date<input name='documentDate' type='date' value='{A(d.DocumentDate?.ToString("yyyy-MM-dd"))}'></label>
      <label>To<input name='addressee' value='{A(d.Addressee)}' placeholder='Person or organisation'></label><label>From<input name='sender' value='{A(d.Sender)}' placeholder='Person or organisation'></label>
      <label>Tags <span>Separate with commas</span><input name='tags' value='{A(string.Join(", ", d.Tags))}' placeholder='e.g. Finance, Invoice'></label><label>Keywords <span>Separate with commas</span><input name='keywords' value='{A(string.Join(", ", d.Keywords))}' placeholder='e.g. account number, renewal'></label></div>
      <label>Summary<textarea name='summary' rows='4' placeholder='A concise description of the document'>{H(d.Summary)}</textarea></label>
      <label>OCR text <span>This is fully searchable</span><textarea class='ocr-input' name='ocrText' rows='16'>{H(d.OcrText)}</textarea></label>
      <div class='actions'><button class='primary' type='submit'>Save changes</button><a class='button' href='/documents/{d.Id}'>Cancel</a></div>
    </form>
    """;

static string ProcessingPage() => """
    <div class='processing'><p class='eyebrow'>LOCAL DOCUMENT LIBRARY</p><h1>Processing scans</h1>
    <p id='status' aria-live='polite'>Preparing your scan queue…</p><div class='progress'><div id='bar'></div></div>
    <p id='counter' class='count'></p><p class='hint'>Keep this page open while documents are being read, tagged and saved.</p>
    <div id='complete' class='actions hidden'><a class='primary' href='/'>View document library</a></div></div>
    <script>
    async function refreshProgress() {
      try {
        const state = await fetch('/api/processing', {cache: 'no-store'}).then(r => r.json());
        document.getElementById('status').textContent = state.message;
        const total = state.total || 0;
        const percent = total ? Math.round((state.completed / total) * 100) : (state.running ? 8 : 100);
        document.getElementById('bar').style.width = Math.min(100, percent) + '%';
        document.getElementById('counter').textContent = total ? `${state.completed} of ${total} document${total === 1 ? '' : 's'} handled` : '';
        if (!state.running) { document.getElementById('complete').classList.remove('hidden'); clearInterval(timer); }
      } catch { document.getElementById('status').textContent = 'Waiting for the local service…'; }
    }
    const timer = setInterval(refreshProgress, 800); refreshProgress();
    </script>
    """;

static string PageShell(string title, string body) => """
    <!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
    <title>__TITLE__ · Document Tagger</title><style>
    :root{color-scheme:dark;--bg:#090d18;--panel:#12192a;--panel-2:#182135;--line:#2a3550;--text:#f0f4ff;--muted:#99a7c2;--accent:#7c8cff;--accent-2:#5eead4;--danger:#ff9ca8}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(ellipse at 12% -10%,#233a68 0,transparent 31rem),radial-gradient(ellipse at 85% 0,#203953 0,transparent 30rem),var(--bg);color:var(--text);font:15px/1.55 Inter,Segoe UI,Arial,sans-serif}body:before{content:'';position:fixed;inset:0;pointer-events:none;background-image:linear-gradient(rgba(255,255,255,.018) 1px,transparent 1px),linear-gradient(90deg,rgba(255,255,255,.018) 1px,transparent 1px);background-size:32px 32px;mask-image:linear-gradient(to bottom,black,transparent 70%)}main{max-width:1240px;margin:auto;padding:52px 28px 80px;position:relative}.site-mark{display:flex;gap:11px;align-items:center;color:#d9e1fa;font-weight:700;text-decoration:none;letter-spacing:-.02em;margin-bottom:45px}.site-mark:before{content:'✦';display:grid;place-items:center;width:30px;height:30px;border-radius:9px;background:linear-gradient(135deg,var(--accent),var(--accent-2));color:#0b1020;box-shadow:0 7px 25px rgba(94,234,212,.23)}h1{font-size:clamp(2.25rem,5vw,4.1rem);letter-spacing:-.055em;line-height:1.02;margin:.12em 0 .2em}h2{font-size:1.08rem;margin:0 0 20px}.hero,.detail-head{display:flex;justify-content:space-between;gap:24px;align-items:flex-start;margin-bottom:34px}.eyebrow{letter-spacing:.14em;font-weight:700;color:var(--accent-2);font-size:.7rem;margin:0}.hero p:not(.eyebrow),.detail-head>div>p,.edit-heading p:not(.eyebrow){color:var(--muted);margin:8px 0;max-width:580px}.primary,.button,button{border:1px solid var(--line);background:rgba(255,255,255,.03);padding:10px 15px;border-radius:9px;color:var(--text);font:inherit;font-weight:650;cursor:pointer;text-decoration:none;display:inline-block;transition:.18s ease}.primary{border-color:transparent;background:linear-gradient(135deg,var(--accent),#6672e9);box-shadow:0 10px 30px rgba(92,109,239,.22)}.primary:hover{filter:brightness(1.1);transform:translateY(-1px)}.button:hover,button:not(.primary):hover{border-color:#65759a;background:rgba(255,255,255,.07)}.filters{display:flex;flex-wrap:wrap;gap:10px;padding:13px;background:rgba(18,25,42,.8);backdrop-filter:blur(18px);border:1px solid var(--line);border-radius:13px;box-shadow:0 16px 40px rgba(0,0,0,.17)}input,select,textarea{width:100%;border:1px solid var(--line);border-radius:8px;padding:10px 11px;font:inherit;color:var(--text);background:#0c1322;outline:none;transition:border .18s,box-shadow .18s}input:focus,select:focus,textarea:focus{border-color:var(--accent);box-shadow:0 0 0 3px rgba(124,140,255,.17)}select{width:auto;min-width:145px}input[name=name],input[name=content]{flex:1;min-width:210px}.filters input{width:auto;min-width:135px}.clear,.back{color:var(--accent-2);padding:10px;text-decoration:none}.count{color:var(--muted);font-size:.87rem;margin:18px 2px}.table-wrap{overflow:auto;background:rgba(18,25,42,.8);border:1px solid var(--line);border-radius:13px;box-shadow:0 18px 46px rgba(0,0,0,.17)}table{width:100%;border-collapse:collapse;min-width:900px}th,td{padding:17px;text-align:left;border-bottom:1px solid rgba(42,53,80,.8);vertical-align:top}tbody tr:last-child td{border-bottom:0}tbody tr{transition:background .18s}tbody tr:hover{background:rgba(124,140,255,.06)}th{color:var(--muted);font-size:.69rem;letter-spacing:.1em;text-transform:uppercase;font-weight:700}td a{color:#dce3ff;font-weight:700;text-decoration:none}td a:hover{color:var(--accent-2)}small{display:block;color:var(--muted);margin-top:3px}.tag{display:inline-block;background:rgba(94,234,212,.1);border:1px solid rgba(94,234,212,.17);border-radius:99px;padding:2px 8px;margin:1px;color:#a4f5e7;font-size:.79rem}.empty{text-align:center;color:var(--muted);padding:48px}.notice{background:rgba(94,234,212,.1);border:1px solid rgba(94,234,212,.25);padding:12px 15px;margin:0 0 22px;border-radius:9px;color:#baf9ee}.actions{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.actions form{margin:0}.metadata,section,.processing,.edit-form{background:rgba(18,25,42,.82);backdrop-filter:blur(16px);border:1px solid var(--line);padding:26px;border-radius:13px;margin:22px 0;box-shadow:0 18px 46px rgba(0,0,0,.14)}.processing{max-width:650px;margin:80px auto}.progress{height:10px;background:#0a1020;border-radius:99px;overflow:hidden;margin:24px 0 10px}.progress>div{height:100%;width:4%;background:linear-gradient(90deg,var(--accent),var(--accent-2));border-radius:99px;transition:width .35s ease}.hint{color:var(--muted)}.hidden{display:none}dl{display:grid;grid-template-columns:170px 1fr;gap:13px 20px;margin:0}dt{font-weight:650;color:var(--muted)}dd{margin:0}pre{white-space:pre-wrap;font:13px/1.65 ui-monospace,Consolas,monospace;margin:0;color:#cbd5ed}.edit-heading{margin-bottom:28px}.edit-form{max-width:920px}.form-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px 18px;margin-bottom:18px}.edit-form label{display:block;font-size:.84rem;font-weight:700;color:#ccd5ec;margin:17px 0}.form-grid label{margin:0}.edit-form label span{font-size:.75rem;color:var(--muted);font-weight:500;margin-left:4px}.edit-form input,.edit-form textarea{display:block;margin-top:7px}.edit-form textarea{resize:vertical}.ocr-input{font:13px/1.5 ui-monospace,Consolas,monospace;color:#cbd5ed}@media(max-width:650px){main{padding:30px 16px}.site-mark{margin-bottom:28px}.hero,.detail-head{display:block}.hero form{margin-top:20px}.form-grid{grid-template-columns:1fr}dl{grid-template-columns:1fr;gap:4px}dd{margin-bottom:13px}.filters input,.filters select{width:100%;flex:auto}}
    </style></head><body><main><a class='site-mark' href='/'>Document Tagger</a>__BODY__</main></body></html>
    """.Replace("__TITLE__", H(title)).Replace("__BODY__", body);

static string H(string value) => System.Net.WebUtility.HtmlEncode(value);
static string A(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..maximum].TrimEnd() + "…";
static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
static List<string> Terms(string value) => value.Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

sealed class TaggerSettings
{
    public string ScansDirectory { get; set; } = @"C:\Users\pharl\Scans";
    public string ProcessedDirectory { get; set; } = @"C:\Users\pharl\Scans\Processed";
    public string DatabaseDirectory { get; set; } = "App_Data";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.1:8b";
    public string PdfToPpmPath { get; set; } = "pdftoppm";
    public string TesseractPath { get; set; } = "C:\\Program Files\\Tesseract-OCR\\tesseract.exe";
    public int RenderDpi { get; set; } = 300;
}

sealed class ProcessingStatus
{
    private readonly object gate = new();
    private ProcessingSnapshot state = new(false, 0, 0, "Nothing is currently being processed.");

    public bool TryBegin()
    {
        lock (gate)
        {
            if (state.Running) return false;
            state = new ProcessingSnapshot(true, 0, 0, "Preparing the scan queue…");
            return true;
        }
    }
    public void Report(ProcessingUpdate update) { lock (gate) state = new ProcessingSnapshot(true, update.Completed, update.Total, update.Message); }
    public void Complete(string message) { lock (gate) state = state with { Running = false, Message = message }; }
    public void Fail(string message) { lock (gate) state = state with { Running = false, Message = message }; }
    public ProcessingSnapshot Snapshot() { lock (gate) return state; }
}

sealed class DocumentProcessor(DocumentStore store, OcrService ocr, OllamaClient ollama, TaggerSettings settings)
{
    public async Task<string> ProcessPendingAsync(Action<ProcessingUpdate>? progress = null)
    {
        progress?.Invoke(new ProcessingUpdate(0, 0, "Looking for PDFs in the scans folder…"));
        if (!Directory.Exists(settings.ScansDirectory)) return $"Scans folder was not found: {settings.ScansDirectory}";
        var files = Directory.EnumerateFiles(settings.ScansDirectory, "*.pdf", SearchOption.TopDirectoryOnly).ToList();
        var pending = files.Where(path => !store.HasSource(path)).ToList();
        if (pending.Count == 0) return "No new PDFs found in the scans folder.";
        var completed = 0; var errors = new List<string>(); var total = pending.Count;
        progress?.Invoke(new ProcessingUpdate(completed, total, $"Found {total} new PDF{(total == 1 ? "" : "s")}."));
        foreach (var file in pending)
        {
            var name = Path.GetFileName(file);
            progress?.Invoke(new ProcessingUpdate(completed, total, $"Processing {name}: preparing document…"));
            try
            {
                await ProcessFileAsync(file, null, stage => progress?.Invoke(new ProcessingUpdate(completed, total, $"Processing {name}: {stage}")));
                completed++;
                progress?.Invoke(new ProcessingUpdate(completed, total, $"Saved {name}."));
            }
            catch (Exception ex)
            {
                completed++;
                errors.Add($"{name}: {ex.Message}");
                progress?.Invoke(new ProcessingUpdate(completed, total, $"Could not process {name}: {ex.Message}"));
            }
        }
        return errors.Count == 0 ? $"Processed {completed} document(s)." : $"Processed {completed}; {errors.Count} failed. {string.Join(" ", errors)}";
    }

    public async Task<string> ReprocessAsync(long id)
    {
        var existing = store.Get(id);
        if (existing is null) return "Document not found.";
        try { await ProcessFileAsync(existing.SourcePath, id, null); return "Document reprocessed."; }
        catch (Exception ex) { return $"Reprocessing failed: {ex.Message}"; }
    }

    private async Task ProcessFileAsync(string sourcePath, long? existingId, Action<string>? stage)
    {
        var text = await ocr.ReadPdfAsync(sourcePath, stage);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("OCR returned no readable text.");
        stage?.Invoke("asking Ollama to identify the document and extract metadata…");
        var analysis = await ollama.AnalyseAsync(text, Path.GetFileName(sourcePath));
        stage?.Invoke("saving tagged PDF and metadata…");
        var title = string.IsNullOrWhiteSpace(analysis.Title) ? Path.GetFileNameWithoutExtension(sourcePath) : analysis.Title;
        var category = DocumentCategories.Normalize(analysis.Category);
        var date = analysis.DocumentDate ?? DateOnly.FromDateTime(File.GetCreationTime(sourcePath));
        var destinationDirectory = Path.Combine(settings.ProcessedDirectory, category, date.Year.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(destinationDirectory);
        var existing = existingId is null ? null : store.Get(existingId.Value);
        var destination = AvailablePath(destinationDirectory, SafeName($"{date:yyyy-MM-dd} - {title}") + ".pdf", existing?.FilePath);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.Copy(sourcePath, destination, true);
        if (existing is not null && !string.Equals(Path.GetFullPath(existing.FilePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(existing.FilePath)) File.Delete(existing.FilePath);
            var oldMetadata = Path.ChangeExtension(existing.FilePath, ".metadata.json");
            if (File.Exists(oldMetadata)) File.Delete(oldMetadata);
        }
        // The category is deliberately for the folder path only. Tags remain rich, document-specific search labels.
        var tags = DocumentTags.Build(analysis.Tags, analysis.Keywords, title, analysis.Sender, category);
        var record = new DocumentRecord(existingId ?? 0, sourcePath, Path.GetFileName(sourcePath), destination, title, analysis.DocumentDate, analysis.Sender, analysis.Addressee, tags, analysis.Keywords ?? new(), analysis.Summary ?? "", text, DateTimeOffset.UtcNow);
        var id = store.Upsert(record);
        DocumentMetadataWriter.Write(record with { Id = id });
    }

    private static string AvailablePath(string directory, string fileName, string? current) { var path = Path.Combine(directory, fileName); var n = 2; while (File.Exists(path) && !string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)} ({n++}).pdf"); return path; }
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars(); var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.'); return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned[..Math.Min(cleaned.Length, 120)]; }
}

sealed class OcrService(TaggerSettings settings)
{
    public async Task<string> ReadPdfAsync(string pdfPath, Action<string>? stage = null)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "document-tagger", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            var prefix = Path.Combine(scratch, "page");
            stage?.Invoke("rendering PDF pages…");
            await RunAsync(settings.PdfToPpmPath, $"-r {settings.RenderDpi} -png {Q(pdfPath)} {Q(prefix)}", scratch, "PDF rendering");
            var pages = Directory.EnumerateFiles(scratch, "page-*.png").OrderBy(x => x).ToList();
            if (pages.Count == 0) throw new InvalidOperationException("No PDF pages were rendered.");
            var output = new StringBuilder();
            foreach (var page in pages)
            {
                var baseName = Path.Combine(scratch, Path.GetFileNameWithoutExtension(page));
                stage?.Invoke($"reading page {pages.IndexOf(page) + 1} of {pages.Count} with OCR…");
                await RunAsync(settings.TesseractPath, $"{Q(page)} {Q(baseName)} -l eng", scratch, "Tesseract OCR");
                var result = baseName + ".txt"; if (File.Exists(result)) output.AppendLine(await File.ReadAllTextAsync(result));
            }
            return output.ToString().Trim();
        }
        finally { try { Directory.Delete(scratch, true); } catch { } }
    }
    private static async Task RunAsync(string program, string arguments, string workingDirectory, string step)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false }) ?? throw new InvalidOperationException($"Could not start {program}.");
            var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"{step} failed. {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException($"{step} requires '{program}'. Install it or set its full path in appsettings.json."); }
    }
    private static string Q(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

sealed class OllamaClient(HttpClient client, TaggerSettings settings)
{
    public async Task<object> HealthAsync()
    {
        try { using var response = await client.GetAsync("/api/tags"); return new { available = response.IsSuccessStatusCode, model = settings.OllamaModel }; }
        catch (Exception ex) { return new { available = false, model = settings.OllamaModel, error = ex.Message }; }
    }
    public async Task<DocumentAnalysis> AnalyseAsync(string text, string originalName)
    {
        var prompt = $"""Extract metadata from this OCR text. Return only valid JSON with exactly: title (string), documentDate (YYYY-MM-DD or null), sender (string or null), addressee (string or null), category (one exact value from: Appointments, Banking, Bills, Correspondence, Education, Employment, Government, Insurance, Legal, Medical, Property, Receipts, Subscriptions, Tax, Travel, Utilities, Unsorted), tags (array of 3-8 specific searchable labels), keywords (array of 8-15 useful search terms), summary (string, max 300 chars). Category is ONLY the broad folder group; do not put it in tags. Tags must capture specific organisations, schemes, document types, subjects, services, or actions found in the document. For example, a TV Licensing letter could have tags such as TV Licensing, Television Licence, Licence Fee, Permissions, and Conditions; a bail notice could have Court, Bail, Hearing, and Grant of Bail. The category must be broad, not a company name or document type. Choose Bills for invoices, statements, payment requests and account notices; Legal for legal matters and official legal notices; Medical for health, clinical and dental documents; Appointments for bookings, reminders and schedules. Do not invent facts; use null where unknown. Original filename: {originalName}. OCR text:\n{text[..Math.Min(text.Length, 24000)]}""";
        using var response = await client.PostAsJsonAsync("/api/chat", new { model = settings.OllamaModel, stream = false, format = "json", messages = new[] { new { role = "user", content = prompt } } });
        var raw = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama ({settings.OllamaModel}) rejected the request: {raw}");
        using var envelope = JsonDocument.Parse(raw);
        var content = envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}";
        var analysis = JsonSerializer.Deserialize<DocumentAnalysis>(content, JsonOptions) ?? throw new InvalidOperationException("Ollama returned empty metadata.");
        return analysis with { Tags = analysis.Tags?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(), Keywords = analysis.Keywords?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new() };
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

sealed class DocumentStore(TaggerSettings settings)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path.Combine(settings.DatabaseDirectory, "documents.db") }.ToString();
    public void Initialize()
    {
        using var db = Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = """CREATE TABLE IF NOT EXISTS documents (id INTEGER PRIMARY KEY, source_path TEXT NOT NULL UNIQUE, content_hash TEXT NULL, original_file_name TEXT NOT NULL, file_path TEXT NOT NULL, title TEXT NOT NULL, document_date TEXT NULL, sender TEXT NULL, addressee TEXT NULL, tags_json TEXT NOT NULL, keywords_json TEXT NOT NULL, summary TEXT NOT NULL, ocr_text TEXT NOT NULL, processed_at TEXT NOT NULL);""";
            command.ExecuteNonQuery();
        }

        var hasContentHash = false;
        using (var columns = db.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(documents);";
            using var reader = columns.ExecuteReader();
            while (reader.Read()) hasContentHash |= string.Equals(reader.GetString(1), "content_hash", StringComparison.OrdinalIgnoreCase);
        }

        if (!hasContentHash)
        {
            using var addColumn = db.CreateCommand();
            addColumn.CommandText = "ALTER TABLE documents ADD COLUMN content_hash TEXT NULL;";
            addColumn.ExecuteNonQuery();
        }

        BackfillContentHashes(db);

        using (var inspect = db.CreateCommand())
        {
            inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='documents_fts';";
            var existingSql = inspect.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existingSql))
            {
                using var drop = db.CreateCommand();
                drop.CommandText = "DROP TABLE documents_fts;";
                drop.ExecuteNonQuery();
            }
        }

        using (var create = db.CreateCommand())
        {
            create.CommandText = """CREATE VIRTUAL TABLE documents_fts USING fts5(title, sender, addressee, tags_json, keywords_json, ocr_text, content='documents', content_rowid='id');""";
            create.ExecuteNonQuery();
        }
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
        using var db = Open(); using var tx = db.BeginTransaction();
        long id;
        if (r.Id == 0)
        {
            using var insert = Command(db, "INSERT INTO documents(source_path,content_hash,original_file_name,file_path,title,document_date,sender,addressee,tags_json,keywords_json,summary,ocr_text,processed_at) VALUES($source,$hash,$original,$file,$title,$date,$sender,$addressee,$tags,$keywords,$summary,$ocr,$processed); SELECT last_insert_rowid();", r);
            id = (long)(insert.ExecuteScalar() ?? 0L);
        }
        else
        {
            id = r.Id;
            using var update = Command(db, "UPDATE documents SET source_path=$source,content_hash=$hash,original_file_name=$original,file_path=$file,title=$title,document_date=$date,sender=$sender,addressee=$addressee,tags_json=$tags,keywords_json=$keywords,summary=$summary,ocr_text=$ocr,processed_at=$processed WHERE id=$id;", r);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }

        if (r.Id != 0)
        {
            using var clear = db.CreateCommand();
            clear.CommandText = "DELETE FROM documents_fts WHERE rowid=$id;";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }

        using var fts = db.CreateCommand();
        fts.CommandText = "INSERT INTO documents_fts(rowid,title,sender,addressee,tags_json,keywords_json,ocr_text) VALUES($id,$title,$sender,$addressee,$tags,$keywords,$ocr);";
        fts.Parameters.AddWithValue("$id", id);
        fts.Parameters.AddWithValue("$title", r.Title);
        fts.Parameters.AddWithValue("$sender", r.Sender ?? "");
        fts.Parameters.AddWithValue("$addressee", r.Addressee ?? "");
        fts.Parameters.AddWithValue("$tags", string.Join(' ', r.Tags));
        fts.Parameters.AddWithValue("$keywords", string.Join(' ', r.Keywords));
        fts.Parameters.AddWithValue("$ocr", r.OcrText);
        fts.ExecuteNonQuery();
        tx.Commit(); return id;
    }
    public DocumentRecord? Get(long id) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM documents WHERE id=$id"; command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return reader.Read() ? Read(reader) : null; }
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
    private SqliteConnection Open() { var db = new SqliteConnection(ConnectionString); db.Open(); return db; }
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
    private static SqliteCommand Command(SqliteConnection db, string sql, DocumentRecord r) { var c = db.CreateCommand(); c.CommandText = sql; c.Parameters.AddWithValue("$source", r.SourcePath); c.Parameters.AddWithValue("$hash", ComputeContentHash(r.SourcePath)); c.Parameters.AddWithValue("$original", r.OriginalFileName); c.Parameters.AddWithValue("$file", r.FilePath); c.Parameters.AddWithValue("$title", r.Title); c.Parameters.AddWithValue("$date", (object?)r.DocumentDate?.ToString("yyyy-MM-dd") ?? DBNull.Value); c.Parameters.AddWithValue("$sender", (object?)r.Sender ?? DBNull.Value); c.Parameters.AddWithValue("$addressee", (object?)r.Addressee ?? DBNull.Value); c.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(r.Tags)); c.Parameters.AddWithValue("$keywords", JsonSerializer.Serialize(r.Keywords)); c.Parameters.AddWithValue("$summary", r.Summary); c.Parameters.AddWithValue("$ocr", r.OcrText); c.Parameters.AddWithValue("$processed", r.ProcessedAt.ToString("O")); return c; }
    private static DocumentRecord Read(SqliteDataReader r) => new(r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("source_path")), r.GetString(r.GetOrdinal("original_file_name")), r.GetString(r.GetOrdinal("file_path")), r.GetString(r.GetOrdinal("title")), r.IsDBNull(r.GetOrdinal("document_date")) ? null : DateOnly.Parse(r.GetString(r.GetOrdinal("document_date"))), r.IsDBNull(r.GetOrdinal("sender")) ? null : r.GetString(r.GetOrdinal("sender")), r.IsDBNull(r.GetOrdinal("addressee")) ? null : r.GetString(r.GetOrdinal("addressee")), JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("tags_json"))) ?? new(), JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("keywords_json"))) ?? new(), r.GetString(r.GetOrdinal("summary")), r.GetString(r.GetOrdinal("ocr_text")), DateTimeOffset.Parse(r.GetString(r.GetOrdinal("processed_at"))));
}

record DocumentRecord(long Id, string SourcePath, string OriginalFileName, string FilePath, string Title, DateOnly? DocumentDate, string? Sender, string? Addressee, List<string> Tags, List<string> Keywords, string Summary, string OcrText, DateTimeOffset ProcessedAt);
record DocumentAnalysis(string? Title, DateOnly? DocumentDate, string? Sender, string? Addressee, string? Category, List<string>? Tags, List<string>? Keywords, string? Summary);
record ProcessingUpdate(int Completed, int Total, string Message);
record ProcessingSnapshot(bool Running, int Completed, int Total, string Message);
record DocumentQuery(string? Name = null, string? Content = null, string? Tag = null, string? From = null, string? To = null, string? Message = null) { public static DocumentQuery Parse(IQueryCollection q) => new(q["name"].ToString(), q["content"].ToString(), q["tag"].ToString(), q["from"].ToString(), q["to"].ToString(), q["message"].ToString()); }

static class DocumentCategories
{
    private static readonly string[] Choices = ["Appointments", "Banking", "Bills", "Correspondence", "Education", "Employment", "Government", "Insurance", "Legal", "Medical", "Property", "Receipts", "Subscriptions", "Tax", "Travel", "Utilities", "Unsorted"];
    public static string Normalize(string? category) => Choices.FirstOrDefault(choice => string.Equals(choice, category?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Unsorted";
    public static bool IsCategory(string value) => Choices.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}

static class DocumentTags
{
    private static readonly HashSet<string> StopWords = ["and", "the", "for", "with", "from", "that", "this", "your", "you", "are", "has", "have", "was", "will", "into", "about", "notice", "letter", "document"];

    public static List<string> Build(IEnumerable<string>? proposed, IEnumerable<string>? keywords, string title, string? sender, string category)
    {
        var tags = new List<string>();
        AddRange(tags, proposed);

        // A model can occasionally under-deliver on tags. Use its recognised keywords and title terms as
        // a local safety net, so every document stays usefully discoverable without leaking the folder category.
        if (tags.Count < 3) AddRange(tags, keywords);
        if (tags.Count < 3) Add(tags, title);
        if (tags.Count < 3 && !string.IsNullOrWhiteSpace(sender)) Add(tags, sender);
        if (tags.Count < 3)
        {
            foreach (var word in title.Split([' ', '-', '/', ':', ',', '(', ')'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 2 && !StopWords.Contains(word.ToLowerInvariant())) Add(tags, word);
                if (tags.Count >= 3) break;
            }
        }
        return tags.Where(tag => !DocumentCategories.IsCategory(tag) && !string.Equals(tag, category, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
    }

    private static void AddRange(List<string> target, IEnumerable<string>? values)
    {
        if (values is null) return;
        foreach (var value in values) Add(target, value);
    }

    private static void Add(List<string> target, string? value)
    {
        var tag = value?.Trim();
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 80 || DocumentCategories.IsCategory(tag)) return;
        if (!target.Contains(tag, StringComparer.OrdinalIgnoreCase)) target.Add(tag);
    }
}

static class DocumentMetadataWriter
{
    public static void Write(DocumentRecord record)
    {
        if (!File.Exists(record.FilePath)) return;
        File.WriteAllText(Path.ChangeExtension(record.FilePath, ".metadata.json"), JsonSerializer.Serialize(new { record.Title, documentDate = record.DocumentDate?.ToString("yyyy-MM-dd"), record.Sender, record.Addressee, record.Tags, record.Keywords, record.Summary, source = record.SourcePath }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
