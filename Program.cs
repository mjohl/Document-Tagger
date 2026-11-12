using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.RegularExpressions;

SQLitePCL.Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
var settings = builder.Configuration.GetSection("DocumentTagger").Get<TaggerSettings>() ?? new();
Directory.CreateDirectory(settings.DatabaseDirectory);
Directory.CreateDirectory(settings.ProcessedDirectory);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<OllamaModelSelection>();
builder.Services.AddHttpClient<OllamaClient>(client =>
{
    client.BaseAddress = new Uri(settings.OllamaUrl);
    client.Timeout = TimeSpan.FromMinutes(8);
});
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<OcrService>();
builder.Services.AddSingleton<ProcessingStatus>();
builder.Services.AddScoped<DocumentProcessor>();
builder.Services.AddScoped<DocumentIndexer>();
builder.Services.AddScoped<ChatRetriever>();
builder.Services.AddHostedService<IndexBackfillService>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto);
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/sign-in";
        options.Cookie.Name = "DocumentTagger.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<DocumentStore>().Initialize();

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/sign-in", (string? returnUrl) =>
    Results.Content(PageShell("Sign in", SignInPage(null, AuthenticationCredentials.GetLocalReturnUrl(returnUrl)), false), "text/html"))
    .AllowAnonymous();

app.MapPost("/sign-in", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var returnUrl = AuthenticationCredentials.GetLocalReturnUrl(form["returnUrl"].ToString());
    if (!AuthenticationCredentials.Verify(form["username"].ToString(), form["password"].ToString()))
    {
        return (IResult)Results.Content(
            PageShell("Sign in", SignInPage("The username or password is incorrect.", returnUrl), false),
            "text/html",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, AuthenticationCredentials.UserName)],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
    return Results.LocalRedirect(returnUrl);
}).AllowAnonymous();

app.MapPost("/sign-out", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/sign-in");
});

app.MapGet("/", (HttpRequest request, DocumentStore store) =>
{
    var query = DocumentQuery.Parse(request.Query);
    return Results.Content(PageShell("Documents", SearchPage(store.Search(query), query, store.GetTags())), "text/html");
});

app.MapPost("/api/chat", async (HttpContext http, ChatRequest request, DocumentStore store, ChatRetriever retriever, OllamaClient ollama) =>
{
    var question = request.Question?.Trim();
    if (string.IsNullOrWhiteSpace(question)) return Results.BadRequest(new { error = "Ask a question about your documents." });
    if (question.Length > 2000) question = question[..2000];
    DocumentRecord? document = null;
    if (request.DocumentId is long id)
    {
        document = store.Get(id);
        if (document is null) return Results.NotFound(new { error = "Document not found." });
    }
    var history = ChatHistory.Sanitize(request.History);
    var cancellationToken = http.RequestAborted;
    http.Response.ContentType = "application/x-ndjson; charset=utf-8";
    http.Response.Headers.CacheControl = "no-cache";
    Task Send(object value) => WriteChatEventAsync(http.Response, value, cancellationToken);
    Task Status(string message) => Send(new { type = "status", message });
    try
    {
        var budget = ollama.GetContextBudgetTokens(question, history);
        var context = document is null
            ? await retriever.RetrieveArchiveAsync(question, history, budget, Status, cancellationToken)
            : await retriever.RetrieveDocumentAsync(document, question, history, budget, Status, cancellationToken);
        await Status(context.Status);
        var result = await ollama.StreamAnswerAsync(question, history, context.Text, text => Send(new { type = "token", text }), cancellationToken);
        if (result.LikelyTruncated)
            await Send(new { type = "debug", message = $"The prompt filled the model's {settings.ChatContextTokens:N0}-token context window, so some document text may have been cut. Lower MaxContextChunks or raise ChatContextTokens." });
        var answer = result.Text.Trim();
        if (answer.Length == 0) answer = "Ollama returned an empty answer.";
        await Send(new { type = "sources", sources = context.SourcesFor(answer).Select(source => new { id = source.Id, title = source.Title, date = source.Date }) });
        await Send(new { type = "complete", answer, answerHtml = Markdown(answer) });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    catch (Exception ex)
    {
        await Send(new { type = "error", message = ex.Message });
    }
    return Results.Empty;
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

app.MapGet("/processing", (OllamaModelSelection models) => Results.Content(PageShell("Processing scans", ProcessingPage(models.CurrentModel)), "text/html"));
app.MapGet("/api/processing", (ProcessingStatus status, DocumentStore store) =>
{
    var snapshot = status.Snapshot();
    return Results.Json(new { snapshot.Running, snapshot.Completed, snapshot.Total, snapshot.Message, index = store.GetIndexStatus(settings.EmbeddingModel).Message });
});
app.MapGet("/api/ollama/models", async (OllamaClient ollama, OllamaModelSelection selection, CancellationToken cancellationToken) =>
{
    try
    {
        var models = await ollama.GetAvailableModelsAsync(cancellationToken);
        return Results.Json(new OllamaModelsResponse(selection.CurrentModel, models));
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("GetOllamaModels")
.WithSummary("List installed Ollama models and the currently selected model")
.Produces<OllamaModelsResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/ollama/model", async (SelectOllamaModelRequest request, OllamaClient ollama, OllamaModelSelection selection, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Model))
        return Results.BadRequest(new { error = "Choose an installed Ollama model." });

    IReadOnlyList<string> available;
    try
    {
        available = await ollama.GetAvailableModelsAsync(cancellationToken);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var model = request.Model.Trim();
    if (!available.Contains(model, StringComparer.Ordinal))
        return Results.BadRequest(new { error = "That model is not available in Ollama." });

    selection.Select(model);
    return Results.Json(new SelectOllamaModelResponse(model));
})
.WithName("SelectOllamaModel")
.WithSummary("Select an installed Ollama model for future requests")
.Produces<SelectOllamaModelResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

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

app.MapPost("/documents/{id:long}/edit", async (long id, HttpRequest request, DocumentStore store, DocumentIndexer indexer) =>
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
    await indexer.IndexAsync(updated, cancellationToken: request.HttpContext.RequestAborted);
    return Results.Redirect($"/documents/{id}?message={Uri.EscapeDataString("Metadata saved.")}");
});

app.MapGet("/files/{id:long}", (long id, DocumentStore store) =>
{
    var document = store.Get(id);
    return document is null || !File.Exists(document.FilePath)
        ? Results.NotFound()
        : Results.File(document.FilePath, "application/pdf", enableRangeProcessing: true);
});

app.MapGet("/health", async (OllamaClient ollama, DocumentStore store) =>
    Results.Json(new { ollama = await ollama.HealthAsync(), index = store.GetIndexStatus(settings.EmbeddingModel) }));

app.Run();

static string SearchPage(IReadOnlyList<DocumentRecord> documents, DocumentQuery query, IReadOnlyList<string> tags)
{
    var message = query.Message is { Length: > 0 } ? $"<div class='notice'>{H(query.Message)}</div>" : "";
    var options = string.Join("", tags.Select(t => $"<option value='{A(t)}' {(query.Tag == t ? "selected" : "")}>{H(t)}</option>"));
    // The title goes in a data attribute rather than inline JavaScript, so OCR-derived titles cannot break out of the script.
    // Column classes and data-labels let the same table render as cards on narrow screens.
    var rows = string.Join("", documents.Select(d =>
        $"<tr><td class='col-name'><a href='/documents/{d.Id}'>{H(d.Title)}</a><small>{H(d.OriginalFileName)}</small></td>" +
        $"<td class='col-date' data-label='Date'>{H(d.DocumentDate?.ToString("yyyy-MM-dd") ?? "—")}</td><td class='col-to col-people' data-label='To'>{H(d.Addressee ?? "—")}</td><td class='col-from col-people' data-label='From'>{H(d.Sender ?? "—")}</td>" +
        $"<td class='col-tags'>{string.Join(" ", d.Tags.Select(t => $"<span class='tag'>{H(t)}</span>"))}</td>" +
        $"<td class='col-summary'>{H(Trim(d.Summary, 130))}</td><td class='col-ai'><button type='button' class='document-chat' data-id='{d.Id}' data-title='{A(d.Title)}'>Ask AI</button></td></tr>"));
    if (rows.Length == 0) rows = "<tr><td colspan='7' class='empty'>No processed documents match your search.</td></tr>";
    var activeFilters = new[] { query.Name, query.Content, query.Tag, query.From, query.To }.Count(value => !string.IsNullOrWhiteSpace(value));
    var filterSummary = activeFilters == 0 ? "Search and filter" : $"Search and filter <span class='filter-count'>{activeFilters} active</span>";
    return $$"""
        <div class='hero'><div><p class='eyebrow'>LOCAL DOCUMENT LIBRARY</p><h1>Scanned documents</h1><p>OCR, tags and search stay on this computer.</p></div>
        <form method='post' action='/process'><button class='primary'>Process scans</button></form></div>{{message}}
        <section class='chat-panel'><div class='chat-intro'><p class='eyebrow'>ASK YOUR ARCHIVE</p><h2>What do you need to find?</h2><p class='chat-help'>Ask about dates, people, payments or renewals, or ask for lists and counts such as “list all bills from 2025”. Follow-up questions use the conversation so far.</p></div>
        <div id='chat-thread' class='chat-thread' aria-live='polite'></div>
        <form id='chat-form' class='chat-form'><label class='visually-hidden' for='chat-question'>Question</label><textarea id='chat-question' rows='2' placeholder='e.g. When does my car insurance renew?' required></textarea>
        <div class='chat-buttons'><button class='primary' type='submit'>Ask Ollama</button><button type='button' id='chat-reset' class='button hidden'>New conversation</button></div></form></section>
        <details class='filter-panel'{{(activeFilters > 0 ? " open" : "")}}><summary>{{filterSummary}}</summary>
        <form class='filters' method='get'>
        <label class='filter-wide'>Name<input name='name' value='{{A(query.Name)}}' placeholder='Title, sender or addressee'></label>
        <label class='filter-wide'>Text<input name='content' value='{{A(query.Content)}}' placeholder='Words in the OCR text'></label>
        <label>Tag<select name='tag'><option value=''>All tags</option>{{options}}</select></label>
        <label>From date<input name='from' type='date' value='{{A(query.From)}}'></label>
        <label>To date<input name='to' type='date' value='{{A(query.To)}}'></label>
        <div class='filter-actions'><button>Search</button><a class='clear' href='/'>Clear</a></div></form></details>
        <p class='count'>{{documents.Count}} document{{(documents.Count == 1 ? "" : "s")}}</p>
        <div class='table-wrap'><table><thead><tr><th>Name</th><th>Date</th><th>To</th><th>From</th><th>Tags</th><th>Summary</th><th><span class='visually-hidden'>Actions</span></th></tr></thead><tbody>{{rows}}</tbody></table></div>
        <dialog id='document-chat' class='document-chat-dialog' aria-labelledby='document-chat-title'>
        <div class='dialog-head'><div><p class='eyebrow'>DOCUMENT ASSISTANT</p><h2 id='document-chat-title'>Document</h2></div><form method='dialog' class='dialog-close'><button aria-label='Close'>×</button></form></div>
        <div id='document-chat-thread' class='chat-thread' aria-live='polite'></div>
        <form id='document-chat-form' class='chat-form'><input type='hidden' id='document-chat-id'><label class='visually-hidden' for='document-chat-question'>Question about this document</label><textarea id='document-chat-question' rows='2' placeholder='Ask for an explanation, key dates, amounts or actions.' required></textarea><div class='chat-buttons'><button class='primary' type='submit'>Ask</button><button type='button' id='document-chat-reset' class='button hidden'>New conversation</button></div></form></dialog>
        """ + ChatScript();
}

static string ChatScript() => """
    <script>
    const touchKeyboard = matchMedia('(pointer: coarse)').matches;
    const filterPanel = document.querySelector('.filter-panel');
    if (filterPanel && matchMedia('(min-width: 801px)').matches) filterPanel.open = true;
    async function askChat(payload, output, sources) {
      let receivedAnswer = false;
      let complete = null;
      try {
        const response = await fetch('/api/chat', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload)});
        if (!response.ok) {
          let detail = 'The local model could not answer.';
          try { const data = await response.json(); detail = data.detail || data.error || detail; } catch (ignored) { }
          throw new Error(detail);
        }
        if (!response.body) throw new Error('This browser does not support streamed responses.');
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        function handleLine(line) {
          if (!line) return;
          const message = JSON.parse(line);
          if (message.type === 'status') { if (!receivedAnswer) output.textContent = message.message; }
          else if (message.type === 'token') {
            if (!receivedAnswer) { output.textContent = ''; receivedAnswer = true; }
            output.textContent += message.text;
          } else if (message.type === 'debug') {
            const warning = document.createElement('p');
            warning.className = 'chat-warning';
            warning.textContent = message.message;
            sources.before(warning);
          } else if (message.type === 'sources') renderSources(sources, message.sources);
          else if (message.type === 'complete') {
            output.innerHTML = message.answerHtml;
            output.classList.add('rendered');
            complete = message.answer;
          } else if (message.type === 'error') throw new Error(message.message);
        }
        while (true) {
          const result = await reader.read();
          buffer += decoder.decode(result.value || new Uint8Array(), {stream: !result.done});
          let newline;
          while ((newline = buffer.indexOf('\n')) >= 0) {
            handleLine(buffer.slice(0, newline));
            buffer = buffer.slice(newline + 1);
          }
          if (result.done) break;
        }
        if (buffer) handleLine(buffer);
        if (complete === null) throw new Error('The local model ended the response unexpectedly.');
        return complete;
      } catch (error) {
        output.textContent = receivedAnswer ? output.textContent + '\n\n' + error.message : error.message;
        output.classList.add('chat-error');
        return null;
      }
    }
    function renderSources(element, sources) {
      element.replaceChildren();
      if (!sources || sources.length === 0) return;
      const label = document.createElement('span');
      label.textContent = 'Sources';
      element.append(label);
      for (const source of sources) {
        const link = document.createElement('a');
        link.href = '/documents/' + source.id;
        link.textContent = source.title + (source.date ? ' · ' + source.date : '');
        element.append(link);
      }
    }
    function setupChat(options) {
      const button = options.form.querySelector('button[type=submit]');
      let history = [];
      function placeholder() {
        const element = document.createElement('div');
        element.className = 'chat-answer chat-placeholder';
        element.textContent = options.emptyText;
        return element;
      }
      function clear() {
        history = [];
        options.thread.replaceChildren(placeholder());
        options.reset.classList.add('hidden');
        options.input.value = '';
        button.disabled = false;
      }
      // Keep the newest text in view while an answer streams, unless the reader has scrolled up to re-read.
      let followLatest = true;
      const scrollToLatest = () => { if (followLatest) options.thread.scrollTop = options.thread.scrollHeight; };
      options.thread.addEventListener('scroll', () => {
        followLatest = options.thread.scrollHeight - options.thread.scrollTop - options.thread.clientHeight < 48;
      });
      new MutationObserver(scrollToLatest).observe(options.thread, {childList: true, subtree: true, characterData: true});
      options.reset.addEventListener('click', () => { clear(); options.input.focus(); });
      // Enter sends on a physical keyboard; on touch keyboards it adds a new line and the button sends.
      options.input.addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.shiftKey && !touchKeyboard) { event.preventDefault(); options.form.requestSubmit(); }
      });
      options.form.addEventListener('submit', async event => {
        event.preventDefault();
        const question = options.input.value.trim();
        if (!question || button.disabled) return;
        options.thread.querySelector('.chat-placeholder')?.remove();
        const turn = document.createElement('div');
        turn.className = 'chat-turn';
        const asked = document.createElement('p');
        asked.className = 'chat-question';
        asked.textContent = question;
        const answer = document.createElement('div');
        answer.className = 'chat-answer';
        answer.textContent = 'Connecting to the local model…';
        const sources = document.createElement('div');
        sources.className = 'chat-sources';
        turn.append(asked, answer, sources);
        followLatest = true;
        options.thread.append(turn);
        options.input.value = '';
        button.disabled = true;
        const payload = {question: question, history: history};
        const documentId = options.documentId();
        if (documentId) payload.documentId = Number(documentId);
        const reply = await askChat(payload, answer, sources);
        if (reply !== null) {
          history = history.concat([{role: 'user', content: question}, {role: 'assistant', content: reply}]).slice(-6);
          options.reset.classList.remove('hidden');
        }
        button.disabled = false;
        if (!touchKeyboard) options.input.focus();
      });
      clear();
      return {clear: clear};
    }
    setupChat({
      form: document.getElementById('chat-form'),
      input: document.getElementById('chat-question'),
      thread: document.getElementById('chat-thread'),
      reset: document.getElementById('chat-reset'),
      emptyText: 'Your answer will appear here.',
      documentId: () => null
    });
    const documentDialog = document.getElementById('document-chat');
    const documentChat = setupChat({
      form: document.getElementById('document-chat-form'),
      input: document.getElementById('document-chat-question'),
      thread: document.getElementById('document-chat-thread'),
      reset: document.getElementById('document-chat-reset'),
      emptyText: 'Ask a question to get more information about this document.',
      documentId: () => document.getElementById('document-chat-id').value
    });
    document.addEventListener('click', event => {
      const trigger = event.target.closest('.document-chat');
      if (!trigger) return;
      document.getElementById('document-chat-id').value = trigger.dataset.id;
      document.getElementById('document-chat-title').textContent = trigger.dataset.title;
      documentChat.clear();
      documentDialog.showModal();
      if (!touchKeyboard) document.getElementById('document-chat-question').focus();
    });
    </script>
    """;

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
        <section><div class='section-head'><h2>OCR text</h2><span>{d.OcrText.Length:N0} characters</span></div><pre class='ocr-text' tabindex='0'>{H(d.OcrText)}</pre></section>
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

static string ProcessingPage(string currentModel) => """
    <div class='processing'><p class='eyebrow'>LOCAL DOCUMENT LIBRARY</p><h1>Processing scans</h1>
    <p id='status' aria-live='polite'>Preparing your scan queue…</p><div class='progress' role='progressbar' aria-label='Document processing progress'><div id='bar'></div></div>
    <p id='counter' class='count'></p><p class='hint'>Keep this page open while documents are being read, tagged and saved.</p>
    <div id='complete' class='actions hidden'><a class='primary' href='/'>View document library</a></div>
    <footer class='model-settings'>
      <p class='eyebrow'>OLLAMA MODEL</p>
      <p>Currently using <strong id='current-model'>__CURRENT_MODEL__</strong></p>
      <div class='model-picker'><label for='model-select'>Choose an installed model</label>
        <div><select id='model-select' disabled><option>Loading available models…</option></select>
        <button id='model-save' type='button' disabled>Use model</button></div>
      </div>
      <p id='model-status' class='hint' role='status'>Changes apply to new Ollama requests.</p>
      <p id='index-status' class='hint'></p>
    </footer></div>
    <script>
    async function refreshProgress() {
      try {
        const state = await fetch('/api/processing', {cache: 'no-store'}).then(r => r.json());
        document.getElementById('status').textContent = state.message;
        if (state.index) document.getElementById('index-status').textContent = state.index;
        const total = state.total || 0;
        const progress = document.querySelector('.progress');
        const indeterminate = state.running && (!total || state.completed < total);
        progress.classList.toggle('indeterminate', indeterminate);
        const percent = total ? Math.round((state.completed / total) * 100) : (state.running ? 8 : 100);
        const bar = document.getElementById('bar');
        if (indeterminate) bar.style.removeProperty('width');
        else bar.style.width = Math.min(100, percent) + '%';
        document.getElementById('counter').textContent = total ? `${state.completed} of ${total} document${total === 1 ? '' : 's'} handled` : '';
        if (!state.running) { document.getElementById('complete').classList.remove('hidden'); clearInterval(timer); }
      } catch { document.getElementById('status').textContent = 'Waiting for the local service…'; }
    }
    const timer = setInterval(refreshProgress, 800); refreshProgress();
    const modelSelect = document.getElementById('model-select');
    const modelSave = document.getElementById('model-save');
    const modelStatus = document.getElementById('model-status');
    async function loadOllamaModels() {
      try {
        const response = await fetch('/api/ollama/models', {cache: 'no-store'});
        const data = await response.json();
        if (!response.ok) throw new Error(data.detail || data.error || 'Could not load Ollama models.');
        document.getElementById('current-model').textContent = data.currentModel;
        modelSelect.replaceChildren(...data.models.map(model => {
          const option = document.createElement('option');
          option.value = model;
          option.textContent = model;
          return option;
        }));
        if (data.models.includes(data.currentModel)) modelSelect.value = data.currentModel;
        modelSelect.disabled = data.models.length === 0;
        modelSave.disabled = data.models.length === 0;
        modelStatus.textContent = data.models.length ? 'Changes apply to new Ollama requests.' : 'No installed Ollama models were found.';
      } catch (error) {
        modelSelect.replaceChildren(new Option('Could not load available models', ''));
        modelSelect.disabled = true;
        modelSave.disabled = true;
        modelStatus.textContent = error.message;
      }
    }
    modelSave.addEventListener('click', async () => {
      modelSave.disabled = true;
      modelStatus.textContent = 'Saving model selection…';
      try {
        const response = await fetch('/api/ollama/model', {
          method: 'POST',
          headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({model: modelSelect.value})
        });
        const data = await response.json();
        if (!response.ok) throw new Error(data.detail || data.error || 'Could not select that Ollama model.');
        document.getElementById('current-model').textContent = data.model;
        modelStatus.textContent = 'Model saved. New Ollama requests will use ' + data.model + '.';
      } catch (error) {
        modelStatus.textContent = error.message;
      } finally {
        modelSave.disabled = modelSelect.disabled;
      }
    });
    loadOllamaModels();
    </script>
    """.Replace("__CURRENT_MODEL__", H(currentModel));

static string PageShell(string title, string body, bool signedIn = true) => """
    <!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta name='theme-color' content='#071120'>
    <title>__TITLE__ · Document Tagger</title><style>
    :root{color-scheme:dark;--bg:#090d18;--panel:#12192a;--panel-2:#182135;--line:#2a3550;--text:#f0f4ff;--muted:#99a7c2;--accent:#7c8cff;--accent-2:#5eead4;--danger:#ff9ca8}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(ellipse at 12% -10%,#233a68 0,transparent 31rem),radial-gradient(ellipse at 85% 0,#203953 0,transparent 30rem),var(--bg);color:var(--text);font:15px/1.55 Inter,Segoe UI,Arial,sans-serif}body:before{content:'';position:fixed;inset:0;pointer-events:none;background-image:linear-gradient(rgba(255,255,255,.018) 1px,transparent 1px),linear-gradient(90deg,rgba(255,255,255,.018) 1px,transparent 1px);background-size:32px 32px;mask-image:linear-gradient(to bottom,black,transparent 70%)}main{max-width:1240px;margin:auto;padding:52px 28px 80px;position:relative}.site-mark{display:flex;gap:11px;align-items:center;color:#d9e1fa;font-weight:700;text-decoration:none;letter-spacing:-.02em;margin-bottom:45px}.site-mark:before{content:'✦';display:grid;place-items:center;width:30px;height:30px;border-radius:9px;background:linear-gradient(135deg,var(--accent),var(--accent-2));color:#0b1020;box-shadow:0 7px 25px rgba(94,234,212,.23)}h1{font-size:clamp(2.25rem,5vw,4.1rem);letter-spacing:-.055em;line-height:1.02;margin:.12em 0 .2em}h2{font-size:1.08rem;margin:0 0 20px}.hero,.detail-head{display:flex;justify-content:space-between;gap:24px;align-items:flex-start;margin-bottom:34px}.eyebrow{letter-spacing:.14em;font-weight:700;color:var(--accent-2);font-size:.7rem;margin:0}.hero p:not(.eyebrow),.detail-head>div>p,.edit-heading p:not(.eyebrow){color:var(--muted);margin:8px 0;max-width:580px}.primary,.button,button{border:1px solid var(--line);background:rgba(255,255,255,.03);padding:10px 15px;border-radius:9px;color:var(--text);font:inherit;font-weight:650;cursor:pointer;text-decoration:none;display:inline-block;transition:.18s ease}.primary{border-color:transparent;background:linear-gradient(135deg,var(--accent),#6672e9);box-shadow:0 10px 30px rgba(92,109,239,.22)}.primary:hover{filter:brightness(1.1);transform:translateY(-1px)}.button:hover,button:not(.primary):hover{border-color:#65759a;background:rgba(255,255,255,.07)}.filters{display:flex;flex-wrap:wrap;gap:10px;padding:13px;background:rgba(18,25,42,.8);backdrop-filter:blur(18px);border:1px solid var(--line);border-radius:13px;box-shadow:0 16px 40px rgba(0,0,0,.17)}input,select,textarea{width:100%;border:1px solid var(--line);border-radius:8px;padding:10px 11px;font:inherit;color:var(--text);background:#0c1322;outline:none;transition:border .18s,box-shadow .18s}input:focus,select:focus,textarea:focus{border-color:var(--accent);box-shadow:0 0 0 3px rgba(124,140,255,.17)}select{width:auto;min-width:145px}input[name=name],input[name=content]{flex:1;min-width:210px}.filters input{width:auto;min-width:135px}.clear,.back{color:var(--accent-2);padding:10px;text-decoration:none}.count{color:var(--muted);font-size:.87rem;margin:18px 2px}.table-wrap{overflow:auto;background:rgba(18,25,42,.8);border:1px solid var(--line);border-radius:13px;box-shadow:0 18px 46px rgba(0,0,0,.17)}table{width:100%;border-collapse:collapse;min-width:900px}th,td{padding:17px;text-align:left;border-bottom:1px solid rgba(42,53,80,.8);vertical-align:top}tbody tr:last-child td{border-bottom:0}tbody tr{transition:background .18s}tbody tr:hover{background:rgba(124,140,255,.06)}th{color:var(--muted);font-size:.69rem;letter-spacing:.1em;text-transform:uppercase;font-weight:700}td a{color:#dce3ff;font-weight:700;text-decoration:none}td a:hover{color:var(--accent-2)}small{display:block;color:var(--muted);margin-top:3px}.tag{display:inline-block;background:rgba(94,234,212,.1);border:1px solid rgba(94,234,212,.17);border-radius:99px;padding:2px 8px;margin:1px;color:#a4f5e7;font-size:.79rem}.empty{text-align:center;color:var(--muted);padding:48px}.notice{background:rgba(94,234,212,.1);border:1px solid rgba(94,234,212,.25);padding:12px 15px;margin:0 0 22px;border-radius:9px;color:#baf9ee}.actions{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.actions form{margin:0}.metadata,section,.processing,.edit-form{background:rgba(18,25,42,.82);backdrop-filter:blur(16px);border:1px solid var(--line);padding:26px;border-radius:13px;margin:22px 0;box-shadow:0 18px 46px rgba(0,0,0,.14)}.processing{max-width:650px;margin:80px auto}.chat-panel{display:flex;flex-direction:column;gap:16px}.chat-help{color:var(--muted);margin:0}.chat-form{display:flex;gap:10px;align-items:end}.chat-form textarea{min-height:58px;resize:vertical}.chat-form button{white-space:nowrap}.chat-answer{min-height:48px;padding:14px 16px;color:#dbe3f7;background:rgba(7,12,25,.62);border:1px solid var(--line);border-radius:9px;white-space:pre-wrap}.document-chat{padding:7px 10px;font-size:.82rem;white-space:nowrap}.document-chat-dialog{width:min(620px,calc(100% - 28px));padding:28px;background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:14px;box-shadow:0 24px 80px rgba(0,0,0,.48)}.document-chat-dialog::backdrop{background:rgba(2,5,12,.76);backdrop-filter:blur(4px)}.document-chat-dialog h2{margin:6px 0 20px}.document-chat-dialog textarea{margin:0 0 12px;resize:vertical}.dialog-close{float:right}.dialog-close button{font-size:1.4rem;line-height:1;padding:4px 10px}.progress{height:10px;background:#0a1020;border-radius:99px;overflow:hidden;margin:24px 0 10px}.progress>div{height:100%;width:4%;background:linear-gradient(90deg,var(--accent),var(--accent-2));border-radius:99px;transition:width .35s ease}.hint{color:var(--muted)}.hidden{display:none}dl{display:grid;grid-template-columns:170px 1fr;gap:13px 20px;margin:0}dt{font-weight:650;color:var(--muted)}dd{margin:0}pre{white-space:pre-wrap;font:13px/1.65 ui-monospace,Consolas,monospace;margin:0;color:#cbd5ed}.edit-heading{margin-bottom:28px}.edit-form{max-width:920px}.form-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px 18px;margin-bottom:18px}.edit-form label{display:block;font-size:.84rem;font-weight:700;color:#ccd5ec;margin:17px 0}.form-grid label{margin:0}.edit-form label span{font-size:.75rem;color:var(--muted);font-weight:500;margin-left:4px}.edit-form input,.edit-form textarea{display:block;margin-top:7px}.edit-form textarea{resize:vertical}.ocr-input{font:13px/1.5 ui-monospace,Consolas,monospace;color:#cbd5ed}@media(max-width:650px){main{padding:30px 16px}.site-mark{margin-bottom:28px}.hero,.detail-head{display:block}.hero form{margin-top:20px}.form-grid{grid-template-columns:1fr}dl{grid-template-columns:1fr;gap:4px}dd{margin-bottom:13px}.filters input,.filters select{width:100%;flex:auto}}
    .progress.indeterminate>div{width:35%;animation:progress-slide 1.4s ease-in-out infinite}@keyframes progress-slide{from{transform:translateX(-110%)}to{transform:translateX(310%)}}
    .model-settings{border-top:1px solid var(--line);margin-top:30px;padding-top:20px}.model-settings>p:not(.eyebrow){margin:8px 0}.model-picker{display:grid;gap:7px;margin-top:18px}.model-picker label{font-size:.84rem;color:var(--muted)}.model-picker>div{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:10px}.model-picker select{width:100%;min-width:0}
    .site-header{display:flex;justify-content:space-between;align-items:center;margin-bottom:45px}.site-mark{margin-bottom:0}.sign-in{max-width:460px;margin:8vh auto}.sign-in h1{font-size:clamp(2.25rem,5vw,3.2rem)}.sign-in>p:not(.eyebrow){margin:8px 0 24px}.sign-in label{display:block;margin:18px 0;font-weight:650}.sign-in input{display:block;margin-top:7px}.sign-in .primary{width:100%;margin-top:5px}@media(max-width:650px){.site-header{margin-bottom:28px}}
    :root{--bg:#071120;--panel:rgba(15,30,58,.64);--panel-2:rgba(25,48,86,.58);--line:rgba(156,190,245,.2);--text:#eef4ff;--muted:#a6b8d7;--accent:#4f8cff;--accent-2:#8bc5ff}
    body{background:radial-gradient(ellipse at 12% -10%,rgba(53,111,225,.42) 0,transparent 34rem),radial-gradient(ellipse at 88% 2%,rgba(37,87,166,.38) 0,transparent 32rem),linear-gradient(145deg,#071120,#0a1630 48%,#07101f);background-attachment:fixed}
    main{max-width:1320px;padding-top:36px}
    .site-header{position:relative;padding:13px 17px;border:1px solid rgba(175,204,255,.16);border-radius:16px;background:rgba(9,22,47,.48);backdrop-filter:blur(22px) saturate(145%);box-shadow:0 14px 42px rgba(0,7,24,.25),inset 0 1px rgba(255,255,255,.06)}
    .site-mark:before{box-shadow:0 7px 25px rgba(75,139,255,.34)}
    .eyebrow{color:#8fc7ff}
    .hero{align-items:center;padding:8px 0 24px;border-bottom:1px solid rgba(156,190,245,.14)}
    .primary{background:linear-gradient(135deg,#4f8cff,#4269e8);box-shadow:0 10px 30px rgba(54,113,255,.25)}
    .button:hover,button:not(.primary):hover{border-color:rgba(139,190,255,.65);background:rgba(95,150,240,.12)}
    .chat-panel,.metadata,section,.processing,.edit-form,.filters,.table-wrap,.document-chat-dialog,.sign-in{background:linear-gradient(135deg,rgba(49,83,139,.24),rgba(12,27,54,.68));border:1px solid rgba(171,201,250,.2);backdrop-filter:blur(24px) saturate(150%);box-shadow:0 22px 60px rgba(0,5,20,.29),inset 0 1px rgba(255,255,255,.07)}
    .chat-panel,.metadata,.processing,.edit-form,.sign-in{border-radius:20px}
    .filters,.table-wrap{border-radius:16px}
    .filters{background:rgba(13,28,57,.48)}
    .table-wrap{background:rgba(11,25,52,.52)}
    th,td{border-bottom-color:rgba(156,190,245,.13)}
    tbody tr:hover{background:rgba(79,140,255,.1)}
    td a:hover,.clear,.back{color:#9ed0ff}
    .tag{background:rgba(79,140,255,.14);border-color:rgba(139,190,255,.28);color:#c4e0ff}
    .notice{background:rgba(54,115,220,.16);border-color:rgba(130,183,255,.3);color:#d2e6ff}
    input,select,textarea{background:rgba(7,19,41,.62);border-color:rgba(156,190,245,.2)}
    input:focus,select:focus,textarea:focus{border-color:#6eaeff;box-shadow:0 0 0 3px rgba(79,140,255,.2)}
    .chat-answer{background:rgba(6,17,37,.48);border-color:rgba(156,190,245,.2)}
    .document-chat-dialog{background:rgba(13,29,59,.82)}
    .document-chat-dialog::backdrop{background:rgba(2,8,22,.72);backdrop-filter:blur(8px)}
    .progress{background:rgba(4,14,32,.7)}
    .progress>div{background:linear-gradient(90deg,#4f8cff,#9bd1ff)}
    @media(max-width:650px){main{padding-top:24px}.site-header{padding:10px 12px;margin-bottom:24px}.hero{padding-bottom:18px}}
    .chat-thread{display:grid;gap:16px;align-content:start}
    .chat-turn{display:grid;gap:8px}.chat-question{margin:0;justify-self:end;max-width:85%;padding:9px 14px;border-radius:14px 14px 4px 14px;background:rgba(79,140,255,.18);border:1px solid rgba(139,190,255,.28);color:#e3efff;white-space:pre-wrap}
    .chat-answer.rendered{white-space:normal}.chat-answer.rendered>:first-child{margin-top:0}.chat-answer.rendered>:last-child{margin-bottom:0}.chat-answer p{margin:0 0 10px}.chat-answer ul,.chat-answer ol{margin:0 0 10px;padding-left:22px}.chat-answer li{margin:3px 0}.chat-answer h4{margin:12px 0 6px;font-size:.95rem}
    .chat-answer .cite{color:#9ed0ff;text-decoration:none;font-size:.82em;font-weight:650;white-space:nowrap}.chat-answer .cite:hover{text-decoration:underline}.chat-placeholder{color:var(--muted)}.chat-error{border-color:rgba(255,156,168,.45);color:#ffd2d8}
    .chat-sources{display:flex;flex-wrap:wrap;gap:6px;align-items:center;font-size:.8rem}.chat-sources:empty{display:none}.chat-sources span{color:var(--muted);font-weight:650;letter-spacing:.06em;text-transform:uppercase;font-size:.68rem;margin-right:2px}.chat-sources a{color:#c4e0ff;text-decoration:none;padding:3px 9px;border-radius:99px;background:rgba(79,140,255,.12);border:1px solid rgba(139,190,255,.25)}.chat-sources a:hover{background:rgba(79,140,255,.22)}
    .chat-warning{margin:0;font-size:.8rem;color:#ffd9a0}
    /* Layout for desktop and mobile */
    body{background-attachment:scroll;background-color:#07101f}html{background:#07101f}
    .visually-hidden{position:absolute;width:1px;height:1px;margin:-1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
    :root{--input-size:15px}input,select,textarea,.edit-form input,.edit-form textarea{font-weight:400;font-size:var(--input-size)}
    :focus-visible{outline:2px solid #8bc5ff;outline-offset:2px}input:focus-visible,select:focus-visible,textarea:focus-visible{outline:none}
    .chat-intro h2{margin:4px 0 6px}
    .chat-thread{max-height:min(62vh,680px);overflow-y:auto;overscroll-behavior:contain;padding-right:2px}
    .chat-panel .chat-thread:has(>.chat-placeholder){display:none}
    .chat-form{display:flex;gap:10px;align-items:flex-end}.chat-form textarea{flex:1;min-height:52px;max-height:40vh;resize:vertical;margin:0}
    .chat-buttons{display:flex;gap:8px;flex-shrink:0}.chat-buttons button{white-space:nowrap}
    .dialog-head{display:flex;justify-content:space-between;align-items:flex-start;gap:12px;margin-bottom:16px}.dialog-head h2{margin:4px 0 0;overflow-wrap:anywhere}.dialog-close{float:none;margin:0}
    .document-chat-dialog[open]{display:flex;flex-direction:column;max-height:min(88vh,880px)}
    .document-chat-dialog .chat-thread{flex:1 1 auto;max-height:none;min-height:60px;margin-bottom:14px}.document-chat-dialog .chat-form{flex-shrink:0}
    .filter-panel{margin:22px 0 0;border:1px solid rgba(171,201,250,.2);border-radius:16px;background:rgba(13,28,57,.48);backdrop-filter:blur(24px) saturate(150%);box-shadow:0 22px 60px rgba(0,5,20,.2)}
    .filter-panel>summary{list-style:none;cursor:pointer;padding:14px 18px;font-weight:650;display:flex;align-items:center;gap:10px;border-radius:16px}.filter-panel>summary::-webkit-details-marker{display:none}
    .filter-panel>summary:after{content:'';margin-left:auto;width:8px;height:8px;border-right:2px solid var(--muted);border-bottom:2px solid var(--muted);transform:rotate(45deg) translateY(-3px);transition:transform .18s}.filter-panel[open]>summary:after{transform:rotate(225deg) translateY(-1px)}
    .filter-count{font-size:.75rem;font-weight:650;color:#c4e0ff;background:rgba(79,140,255,.18);border:1px solid rgba(139,190,255,.3);border-radius:99px;padding:1px 8px}
    .filter-panel .filters{display:grid;grid-template-columns:1fr 1fr;gap:12px;padding:2px 16px 16px;border:0;border-radius:0;background:none;box-shadow:none;backdrop-filter:none}
    .filters label{display:grid;gap:5px;font-size:.72rem;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);min-width:0}
    .filter-panel .filters input,.filter-panel .filters select{width:100%;min-width:0;flex:none;text-transform:none;letter-spacing:normal;font-size:var(--input-size);font-weight:400}
    .filter-wide,.filter-actions{grid-column:1/-1}.filter-actions{display:flex;gap:6px;align-items:center}.filter-actions button{min-width:110px}
    .col-date{white-space:nowrap}.col-name{min-width:190px}.col-people{min-width:110px}.col-summary{min-width:220px;color:#cfdaef}.col-ai{text-align:right;white-space:nowrap}.col-name a{overflow-wrap:anywhere}
    .section-head{display:flex;justify-content:space-between;align-items:baseline;gap:12px;flex-wrap:wrap}.section-head h2{margin:0 0 14px}.section-head span{color:var(--muted);font-size:.8rem}
    .ocr-text{max-height:min(70vh,760px);overflow:auto;padding:14px 16px;border:1px solid rgba(156,190,245,.16);border-radius:10px;background:rgba(6,17,37,.48)}
    @media(min-width:801px){.filter-panel>summary{display:none}.filter-panel .filters{grid-template-columns:minmax(0,1.5fr) minmax(0,1.5fr) minmax(0,1fr) minmax(0,.9fr) minmax(0,.9fr) auto;align-items:end;padding:14px}.filter-wide,.filter-actions{grid-column:auto}}
    @media(max-width:800px){
      .table-wrap{overflow:visible;background:none;border:0;box-shadow:none;backdrop-filter:none;border-radius:0}
      table{min-width:0}thead{display:none}table,tbody,td{display:block}tbody{display:grid;gap:12px}
      tbody tr{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));grid-template-areas:'name name ai' 'date from to' 'tags tags tags' 'summary summary summary';gap:12px;padding:16px;border:1px solid rgba(171,201,250,.2);border-radius:16px;background:linear-gradient(135deg,rgba(49,83,139,.24),rgba(12,27,54,.68));box-shadow:0 14px 40px rgba(0,5,20,.22)}
      tbody tr:hover{background:linear-gradient(135deg,rgba(49,83,139,.32),rgba(12,27,54,.72))}
      td{padding:0;border:0;min-width:0;overflow-wrap:anywhere}.col-name{grid-area:name;min-width:0;font-size:1.02rem}.col-date{grid-area:date}.col-from{grid-area:from}.col-to{grid-area:to}.col-tags{grid-area:tags}.col-summary{grid-area:summary;min-width:0}.col-ai{grid-area:ai;align-self:start}.col-people{min-width:0}
      td[data-label]{font-size:.88rem}td[data-label]:before{content:attr(data-label);display:block;font-size:.64rem;font-weight:700;letter-spacing:.1em;text-transform:uppercase;color:var(--muted);margin-bottom:2px}
      .col-tags:empty,.col-summary:empty{display:none}td.empty{grid-column:1/-1;padding:32px 0}
    }
    @media(max-width:650px){
      main{padding:16px 14px 48px}.site-header{margin-bottom:22px}
      h1{font-size:clamp(1.9rem,9vw,2.5rem);overflow-wrap:anywhere}
      .hero{padding-bottom:18px;margin-bottom:22px}.hero form,.hero form button{width:100%}.hero form button{margin-top:4px}
      .chat-panel,.metadata,section,.processing,.edit-form,.sign-in{padding:18px;border-radius:18px}.processing{margin:24px auto}
      .chat-form{flex-direction:column;align-items:stretch}.chat-buttons .primary{flex:1}
      .chat-question{max-width:92%}
      .filter-panel .filters{grid-template-columns:1fr}
      .detail-head .actions{display:grid;grid-template-columns:1fr 1fr;margin-top:18px}.detail-head .actions>*{text-align:center}.detail-head .actions .primary{grid-column:1/-1}.detail-head .actions form button{width:100%}
      .ocr-text{max-height:60vh;font-size:12px;padding:12px}
      .edit-form .actions{position:sticky;bottom:0;z-index:1;margin:0 -18px -18px;padding:12px 18px calc(12px + env(safe-area-inset-bottom));background:rgba(9,22,47,.92);backdrop-filter:blur(14px);border-top:1px solid rgba(156,190,245,.16);border-radius:0 0 18px 18px}.edit-form .actions>*{flex:1;text-align:center}
      .document-chat-dialog{width:100%;max-width:none;height:100%;max-height:none;margin:0;padding:16px 16px calc(14px + env(safe-area-inset-bottom));border-radius:0;border:0}
      .document-chat-dialog[open]{max-height:none;height:100dvh}
    }
    /* 16px stops iOS Safari zooming into a field when it is tapped. */
    @media(max-width:800px),(pointer:coarse){:root{--input-size:16px}.ocr-input{font-size:16px}}
    @media(pointer:coarse){.primary,.button,button{min-height:44px}.dialog-close button{min-width:44px}}
    </style></head><body><main><header class='site-header'><a class='site-mark' href='/'>Document Tagger</a>__ACCOUNT__</header>__BODY__</main></body></html>
    """.Replace("__TITLE__", H(title))
    .Replace("__ACCOUNT__", signedIn ? "<form method='post' action='/sign-out'><button class='button' type='submit'>Sign out</button></form>" : "")
    .Replace("__BODY__", body);

static string H(string value) => System.Net.WebUtility.HtmlEncode(value);
static async Task WriteChatEventAsync(HttpResponse response, object value, CancellationToken cancellationToken)
{
    await response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}
// A small, safe Markdown subset for model answers: headings, bullet and numbered lists, bold, and [#id] citations as links.
// Every line is HTML-encoded before any markup is added.
static string Markdown(string value)
{
    var html = new StringBuilder();
    var paragraph = new List<string>();
    string? listTag = null;
    void FlushParagraph()
    {
        if (paragraph.Count == 0) return;
        html.Append("<p>").Append(string.Join("<br>", paragraph)).Append("</p>");
        paragraph.Clear();
    }
    void CloseList()
    {
        if (listTag is null) return;
        html.Append("</").Append(listTag).Append('>');
        listTag = null;
    }
    foreach (var rawLine in value.Replace("\r\n", "\n").Split('\n'))
    {
        var line = rawLine.Trim();
        if (line.Length == 0) { FlushParagraph(); CloseList(); continue; }
        var heading = Regex.Match(line, @"^#{1,6}\s+(.+)$");
        if (heading.Success)
        {
            FlushParagraph(); CloseList();
            html.Append("<h4>").Append(MarkdownInline(heading.Groups[1].Value)).Append("</h4>");
            continue;
        }
        var bullet = Regex.Match(line, @"^[-*•]\s+(.+)$");
        var numbered = Regex.Match(line, @"^\d{1,3}[.)]\s+(.+)$");
        if (bullet.Success || numbered.Success)
        {
            FlushParagraph();
            var tag = bullet.Success ? "ul" : "ol";
            if (listTag != tag) { CloseList(); html.Append('<').Append(tag).Append('>'); listTag = tag; }
            html.Append("<li>").Append(MarkdownInline((bullet.Success ? bullet : numbered).Groups[1].Value)).Append("</li>");
            continue;
        }
        CloseList();
        paragraph.Add(MarkdownInline(line));
    }
    FlushParagraph();
    CloseList();
    return html.ToString();
}
static string MarkdownInline(string text)
{
    var bold = Regex.Replace(H(text), @"\*\*(.+?)\*\*", "<strong>$1</strong>");
    return Regex.Replace(bold, @"\[(#\d+(?:\s*[,;]\s*#?\d+)*)\]", match =>
        string.Join(" ", Regex.Matches(match.Groups[1].Value, @"\d+").Select(id => $"<a class='cite' href='/documents/{id.Value}'>[#{id.Value}]</a>")));
}
static string A(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");
static string Trim(string value, int maximum) => value.Length <= maximum ? value : value[..maximum].TrimEnd() + "…";
static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
static List<string> Terms(string value) => value.Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

static string SignInPage(string? message, string returnUrl)
{
    var notice = message is null ? "" : $"<div class='notice' role='alert'>{H(message)}</div>";
    return $"""
        <section class='sign-in'>
          <p class='eyebrow'>PRIVATE DOCUMENT LIBRARY</p>
          <h1>Welcome back</h1>
          <p class='hint'>Sign in to access your documents and tools.</p>
          {notice}
          <form method='post' action='/sign-in'>
            <input type='hidden' name='returnUrl' value='{A(returnUrl)}'>
            <label>Username<input name='username' value='{A(AuthenticationCredentials.UserName)}' autocomplete='username' required autofocus></label>
            <label>Password<input type='password' name='password' autocomplete='current-password' required></label>
            <button class='primary' type='submit'>Sign in</button>
          </form>
        </section>
        """;
}

sealed class AuthenticationCredentials
{
    private static readonly byte[] PasswordSalt = Convert.FromBase64String("bhxIfwazfKUEk/lmTCURqw==");
    private static readonly byte[] PasswordHash = Convert.FromBase64String("yFg6Qu2uWxBNUXB+x+yopozMfqZ9Wnm1F0RmkFdzQAs=");

    public const string UserName = "Castle";

    public static bool Verify(string username, string password)
    {
        var candidate = password.Length <= 256 ? password : "";
        var candidateHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(candidate),
            PasswordSalt,
            600_000,
            HashAlgorithmName.SHA256,
            PasswordHash.Length);
        return username.Equals(UserName, StringComparison.Ordinal) &&
            password.Length <= 256 &&
            CryptographicOperations.FixedTimeEquals(candidateHash, PasswordHash);
    }

    public static string GetLocalReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith('/') &&
        !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Any(char.IsControl) &&
        Uri.TryCreate(value, UriKind.Relative, out _)
            ? value
            : "/";
}

sealed class TaggerSettings
{
    public string ScansDirectory { get; set; } = @"C:\Users\pharl\Scans";
    public string ProcessedDirectory { get; set; } = @"C:\Users\pharl\Scans\Processed";
    public string DatabaseDirectory { get; set; } = "App_Data";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen2.5:7b";
    public string PdfToPpmPath { get; set; } = "pdftoppm";
    public string TesseractPath { get; set; } = "C:\\Program Files\\Tesseract-OCR\\tesseract.exe";
    public int RenderDpi { get; set; } = 300;

    /// <summary>Ollama model used to embed document chunks for semantic search. Leave empty for keyword search only.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    /// <summary>Ollama context window (num_ctx) for chat. Keep it equal to AnalysisContextTokens so Ollama does not reload the model between requests.</summary>
    public int ChatContextTokens { get; set; } = 8192;
    /// <summary>Ollama context window (num_ctx) for metadata extraction.</summary>
    public int AnalysisContextTokens { get; set; } = 8192;
    public double ChatTemperature { get; set; } = 0.2;
    /// <summary>Approximate characters per OCR chunk in the search index.</summary>
    public int ChunkSize { get; set; } = 1200;
    public int ChunkOverlap { get; set; } = 200;
    /// <summary>The most document passages sent to the model for one archive question.</summary>
    public int MaxContextChunks { get; set; } = 8;
    /// <summary>How long Ollama keeps models loaded after a request.</summary>
    public string KeepAlive { get; set; } = "10m";
}

sealed class OllamaModelSelection(TaggerSettings settings)
{
    private readonly object gate = new();
    private readonly string modelPath = Path.Combine(settings.DatabaseDirectory, "ollama-model.txt");
    private string currentModel = LoadModel(Path.Combine(settings.DatabaseDirectory, "ollama-model.txt"), settings.OllamaModel);

    public string CurrentModel { get { lock (gate) return currentModel; } }

    public void Select(string model)
    {
        lock (gate)
        {
            var temporaryPath = modelPath + ".tmp";
            File.WriteAllText(temporaryPath, model);
            File.Move(temporaryPath, modelPath, true);
            currentModel = model;
        }
    }

    private static string LoadModel(string path, string fallback)
    {
        if (!File.Exists(path)) return fallback;
        var savedModel = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(savedModel) ? fallback : savedModel;
    }
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

sealed class DocumentProcessor(DocumentStore store, OcrService ocr, OllamaClient ollama, DocumentIndexer indexer, TaggerSettings settings)
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
        var fileName = Path.GetFileName(sourcePath);
        stage?.Invoke($"Ollama is starting metadata analysis for {fileName}…");
        var analysisTask = ollama.AnalyseAsync(text, fileName, count =>
            stage?.Invoke($"Received {count:N0} characters of metadata from Ollama for {fileName}…"));
        if (stage is not null)
        {
            var elapsed = Stopwatch.StartNew();
            while (await Task.WhenAny(analysisTask, Task.Delay(TimeSpan.FromSeconds(5))) != analysisTask)
                stage($"Ollama is still analysing {fileName} ({elapsed.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)} elapsed)…");
        }
        var analysis = await analysisTask;
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
        var record = new DocumentRecord(existingId ?? 0, sourcePath, Path.GetFileName(sourcePath), destination, title, analysis.DocumentDate, analysis.Sender, analysis.Addressee, tags, analysis.Keywords ?? new(), analysis.Summary ?? "", text, DateTimeOffset.UtcNow, category);
        var id = store.Upsert(record);
        DocumentMetadataWriter.Write(record with { Id = id });
        stage?.Invoke("indexing for search…");
        await indexer.IndexAsync(record with { Id = id });
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

record DocumentRecord(long Id, string SourcePath, string OriginalFileName, string FilePath, string Title, DateOnly? DocumentDate, string? Sender, string? Addressee, List<string> Tags, List<string> Keywords, string Summary, string OcrText, DateTimeOffset ProcessedAt, string? Category = null);
sealed record ChatRequest(string? Question, long? DocumentId, List<ChatTurn>? History);
record DocumentAnalysis(string? Title, DateOnly? DocumentDate, string? Sender, string? Addressee, string? Category, List<string>? Tags, List<string>? Keywords, string? Summary);
record ProcessingUpdate(int Completed, int Total, string Message);
record ProcessingSnapshot(bool Running, int Completed, int Total, string Message);
record DocumentQuery(string? Name = null, string? Content = null, string? Tag = null, string? From = null, string? To = null, string? Message = null) { public static DocumentQuery Parse(IQueryCollection q) => new(q["name"].ToString(), q["content"].ToString(), q["tag"].ToString(), q["from"].ToString(), q["to"].ToString(), q["message"].ToString()); }

/// <summary>Lists the installed Ollama models and the model currently selected by the application.</summary>
sealed record OllamaModelsResponse(string CurrentModel, IReadOnlyList<string> Models);

/// <summary>Selects an installed Ollama model for future requests.</summary>
sealed record SelectOllamaModelRequest(string? Model);

/// <summary>Confirms the Ollama model selected for future requests.</summary>
sealed record SelectOllamaModelResponse(string Model);

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
        File.WriteAllText(Path.ChangeExtension(record.FilePath, ".metadata.json"), JsonSerializer.Serialize(new { record.Title, documentDate = record.DocumentDate?.ToString("yyyy-MM-dd"), record.Sender, record.Addressee, record.Category, record.Tags, record.Keywords, record.Summary, source = record.SourcePath }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
