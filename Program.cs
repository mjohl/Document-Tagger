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
    const int pageSize = 50;
    var query = DocumentQuery.Parse(request.Query);
    var colours = LabelColours.Effective(store.GetChosenColours());
    string results;
    if (query.IsCalendar)
    {
        // Open on the current month if it has documents, otherwise on the latest month that does.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var month = query.MonthStart ?? (store.GetLatestCalendarDate(query, query.ByScanDate) is { } latest && latest < thisMonth ? new DateOnly(latest.Year, latest.Month, 1) : thisMonth);
        query = query with { Month = month.ToString("yyyy-MM", CultureInfo.InvariantCulture) };
        var firstShown = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));
        var lastOfMonth = month.AddMonths(1).AddDays(-1);
        var lastShown = lastOfMonth.AddDays((7 - (int)lastOfMonth.DayOfWeek) % 7);
        results = CalendarView(query, month, firstShown, lastShown, store.ListDocumentsBetween(query, firstShown, lastShown, query.ByScanDate), colours, today);
    }
    else
    {
        var (items, total) = store.ListDocuments(query, query.Page, pageSize);
        results = ListView(query, items, total, pageSize, colours);
    }
    // Suggest tags used more than once plus the categories in use; one-off tags would bury the useful ones.
    var suggestions = store.GetCategoryCounts().Select(category => category.Label).Concat(store.GetTagCounts(null, 80, 2).Select(tag => tag.Label))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    return Results.Content(PageShell("Documents", HomePage(query, results, suggestions, store.CountReviews(ScanReview.Pending))), "text/html");
});

app.MapGet("/labels", (HttpRequest request, DocumentStore store) =>
{
    var search = request.Query["q"].ToString().Trim();
    var colours = LabelColours.Effective(store.GetChosenColours());
    var tags = store.GetTagCounts(search.Length == 0 ? null : search, 100);
    return Results.Content(PageShell("Colours", LabelsPage(store.GetCategoryCounts(), tags, colours, search)), "text/html");
});

app.MapPost("/labels", async (HttpRequest request, DocumentStore store) =>
{
    var form = await request.ReadFormAsync();
    var label = form["label"].ToString().Trim();
    var colour = form["colour"].ToString();
    var fromScript = request.Headers["X-Requested-With"] == "fetch";
    if (label.Length is 0 or > 80 || !LabelColours.IsValid(colour))
        return fromScript ? Results.BadRequest(new { error = "Choose a colour from the palette." }) : Results.Redirect("/labels");
    store.SetColour(label, colour);
    return fromScript ? Results.Json(new { label, colour }) : Results.Redirect($"/labels?q={Uri.EscapeDataString(form["q"].ToString())}");
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
    StartProcessing(status, scopeFactory);
    return Results.Redirect("/processing");
});

app.MapGet("/reviews", (HttpRequest request, DocumentStore store) =>
{
    var reviews = store.GetReviews(ScanReview.Pending)
        .Where(review => File.Exists(review.SourcePath))
        .Select(review => (Review: review, Existing: store.Get(review.DuplicateOfId)))
        .ToList();
    return Results.Content(PageShell("Possible duplicates", ReviewsPage(reviews, request.Query["message"], Path.Combine(settings.ScansDirectory, DocumentProcessor.DuplicatesFolderName))), "text/html");
});

app.MapGet("/reviews/{id:long}/file", (long id, DocumentStore store) =>
{
    var review = store.GetReview(id);
    return review is null || !File.Exists(review.SourcePath)
        ? Results.NotFound()
        : Results.File(review.SourcePath, "application/pdf", enableRangeProcessing: true);
});

app.MapPost("/reviews/{id:long}/discard", (long id, DocumentProcessor processor) =>
    Results.Redirect($"/reviews?message={Uri.EscapeDataString(processor.DiscardDuplicate(id))}"));

app.MapPost("/reviews/{id:long}/process", (long id, DocumentStore store, ProcessingStatus status, IServiceScopeFactory scopeFactory) =>
{
    var review = store.GetReview(id);
    if (review is null || !File.Exists(review.SourcePath))
        return Results.Redirect($"/reviews?message={Uri.EscapeDataString("That scan is no longer waiting for review.")}");
    store.SetReviewStatus(id, ScanReview.Approved);
    // A run already in progress picks up approved scans before it finishes.
    return StartProcessing(status, scopeFactory)
        ? Results.Redirect("/processing")
        : Results.Redirect($"/reviews?message={Uri.EscapeDataString($"{review.FileName} was added to the processing run that is already in progress.")}");
});

app.MapGet("/processing", (OllamaModelSelection models) => Results.Content(PageShell("Processing scans", ProcessingPage(models.CurrentModel)), "text/html"));
app.MapGet("/api/processing", (ProcessingStatus status, DocumentStore store) =>
{
    var snapshot = status.Snapshot();
    return Results.Json(new
    {
        snapshot.Running, snapshot.Completed, snapshot.Total, snapshot.Message,
        snapshot.File, snapshot.Step, snapshot.Detail, snapshot.Page, snapshot.Pages, snapshot.StepFraction, snapshot.Results,
        index = store.GetIndexStatus(settings.EmbeddingModel).Message,
        reviews = store.CountReviews(ScanReview.Pending)
    });
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

app.MapPost("/documents/{id:long}/reprocess", (long id, DocumentStore store, ProcessingStatus status, IServiceScopeFactory scopeFactory) =>
{
    var document = store.Get(id);
    if (document is null) return Results.NotFound("Document not found.");
    StartReprocessing(document, status, scopeFactory);
    return Results.Redirect("/processing");
});

app.MapGet("/documents/{id:long}", (long id, HttpRequest request, DocumentStore store) =>
{
    var document = store.Get(id);
    return document is null
        ? Results.NotFound("Document not found")
        : Results.Content(PageShell(document.Title, DetailPage(document, request.Query["message"], LabelColours.Effective(store.GetChosenColours()))), "text/html");
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

static string TagChip(string tag, IReadOnlyDictionary<string, string> colours) =>
    $"<a class='tag{LabelColours.ChipClass(LabelColours.For(tag, colours))}' href='/?tag={Uri.EscapeDataString(tag)}'>{H(tag)}</a>";

static string CategoryChip(string? category, IReadOnlyDictionary<string, string> colours) => category is null ? "" :
    $"<a class='tag category{LabelColours.ChipClass(LabelColours.For(category, colours))}' href='/?tag={Uri.EscapeDataString(category)}' title='Category'>{H(category)}</a>";

/// <summary>The home page filters as hidden fields, so a form that changes one setting keeps the rest.</summary>
static string HiddenFilters(DocumentQuery query, params string[] except)
{
    var fields = new (string Name, string? Value)[] { ("name", query.Name), ("content", query.Content), ("tag", query.Tag), ("from", query.From), ("to", query.To), ("scanned", query.Scanned), ("view", query.IsCalendar ? "calendar" : null), ("by", query.ByScanDate ? "scanned" : null), ("month", query.IsCalendar ? query.Month : null) };
    return string.Join("", fields.Where(field => !string.IsNullOrWhiteSpace(field.Value) && !except.Contains(field.Name)).Select(field => $"<input type='hidden' name='{field.Name}' value='{A(field.Value)}'>"));
}

static string ListView(DocumentQuery query, IReadOnlyList<DocumentListItem> documents, int total, int pageSize, IReadOnlyDictionary<string, string> colours)
{
    // The title goes in a data attribute rather than inline JavaScript, so OCR-derived titles cannot break out of the script.
    // Column classes and data-labels let the same table render as cards on narrow screens.
    var rows = string.Join("", documents.Select(d =>
    {
        var date = d.DocumentDate is { } documentDate
            ? H(documentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : $"—{(d.ScannedDate is { } scanned ? $"<small>Scanned {scanned:yyyy-MM-dd}</small>" : "")}";
        return $"<tr class='doc-row{LabelColours.AccentClass(LabelColours.ForDocument(d.Tags, d.Category, colours))}'><td class='col-name'><a href='/documents/{d.Id}'>{H(d.Title)}</a><small>{H(d.OriginalFileName)}</small></td>" +
            $"<td class='col-date' data-label='Date'>{date}</td><td class='col-to col-people' data-label='To'>{H(d.Addressee ?? "—")}</td><td class='col-from col-people' data-label='From'>{H(d.Sender ?? "—")}</td>" +
            $"<td class='col-tags'>{CategoryChip(d.Category, colours)}{string.Join("", d.Tags.Select(tag => TagChip(tag, colours)))}</td>" +
            $"<td class='col-summary'>{H(Trim(d.Summary, 130))}</td><td class='col-ai' data-label='Actions'><div class='document-actions'>" +
            $"<button type='button' class='document-chat' data-id='{d.Id}' data-title='{A(d.Title)}'><svg class='action-icon' viewBox='0 0 24 24' aria-hidden='true' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'><path d='M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z'/></svg><span>Ask AI</span></button>" +
            $"<form method='post' action='/documents/{d.Id}/reprocess'><button type='submit' class='document-reprocess'><svg class='action-icon' viewBox='0 0 24 24' aria-hidden='true' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'><path d='M20 11a8.1 8.1 0 0 0-14.6-4L4 9'/><path d='M4 5v4h4'/><path d='M4 13a8.1 8.1 0 0 0 14.6 4L20 15'/><path d='M20 19v-4h-4'/></svg><span>Reprocess</span></button></form>" +
            "</div></td></tr>";
    }));
    if (rows.Length == 0) rows = $"<tr><td colspan='7' class='empty'>{(query.HasFilters ? "No documents match your search." : "No documents yet. Put scans in the scans folder and click Process scans.")}</td></tr>";

    var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
    var page = Math.Min(query.Page, pages);
    var first = total == 0 ? 0 : (page - 1) * pageSize + 1;
    var count = total == 0 ? "0 documents" : $"Showing {first:N0}–{first + documents.Count - 1:N0} of {total:N0} document{(total == 1 ? "" : "s")}";
    string PageLink(int target, string text, string rel) => target < 1 || target > pages
        ? $"<span class='button disabled' aria-disabled='true'>{text}</span>"
        : $"<a class='button' rel='{rel}' href='{A((query with { Page = target }).Url())}'>{text}</a>";
    var pager = pages <= 1 ? "" :
        $"<nav class='pager' aria-label='Pages'>{PageLink(1, "« First", "first")}{PageLink(page - 1, "‹ Previous", "prev")}<span class='pager-status'>Page {page:N0} of {pages:N0}</span>{PageLink(page + 1, "Next ›", "next")}{PageLink(pages, "Last »", "last")}</nav>";
    return $"""
        <p class='count'>{count}</p>
        <div class='table-wrap'><table><thead><tr><th>Name</th><th>Date</th><th>To</th><th>From</th><th>Category and tags</th><th>Summary</th><th><span class='visually-hidden'>Actions</span></th></tr></thead><tbody>{rows}</tbody></table></div>
        {pager}
        """;
}

static string CalendarView(DocumentQuery query, DateOnly month, DateOnly firstShown, DateOnly lastShown, IReadOnlyList<DocumentListItem> documents, IReadOnlyDictionary<string, string> colours, DateOnly today)
{
    const int visiblePerDay = 3;
    const int expandablePerDay = 20;
    DateOnly? DayOf(DocumentListItem d) => query.ByScanDate ? d.ScannedDate : d.DocumentDate ?? d.ScannedDate;
    var byDay = documents.Where(d => DayOf(d) is not null).GroupBy(d => DayOf(d)!.Value).ToDictionary(group => group.Key, group => group.ToList());
    var inMonth = documents.Count(d => DayOf(d) is { } day && day.Year == month.Year && day.Month == month.Month);
    var undated = query.ByScanDate ? 0 : documents.Count(d => d.DocumentDate is null && DayOf(d) is { } day && day.Year == month.Year && day.Month == month.Month);
    var monthName = month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    string Entry(DocumentListItem d)
    {
        var fallback = !query.ByScanDate && d.DocumentDate is null;
        var tooltip = $"{d.Title}{(d.Sender is null ? "" : " · from " + d.Sender)}{(fallback ? " · no document date, shown on its scan date" : "")}";
        return $"<li><a class='cal-doc{LabelColours.AccentClass(LabelColours.ForDocument(d.Tags, d.Category, colours))}' href='/documents/{d.Id}' title='{A(tooltip)}'>{(fallback ? "<span class='cal-flag' aria-label='Scan date'>◷</span>" : "")}{H(d.Title)}</a></li>";
    }

    var cells = new StringBuilder();
    for (var day = firstShown; day <= lastShown; day = day.AddDays(1))
    {
        var entries = byDay.GetValueOrDefault(day) ?? [];
        var classes = "cal-day" + (day.Month != month.Month ? " other-month" : "") + (day == today ? " today" : "") + (entries.Count > 0 ? " has-docs" : "");
        // A few extra documents open in place; a busy day (such as a first big import) links to the list for that day instead.
        var dayList = query.ByScanDate
            ? query with { View = "list", Page = 1, Scanned = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : query with { View = "list", Page = 1, From = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), To = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        var hidden = entries.Count - visiblePerDay;
        var more = hidden <= 0 ? ""
            : hidden <= expandablePerDay
                ? $"<details class='cal-more'><summary>+{hidden} more</summary><ul class='cal-docs'>{string.Join("", entries.Skip(visiblePerDay).Select(Entry))}</ul></details>"
                : $"<a class='cal-more-link' href='{A(dayList.Url())}'>+{hidden:N0} more</a>";
        cells.Append($"<div class='{classes}'><div class='cal-date'><span class='cal-dow'>{day.ToString("ddd", CultureInfo.InvariantCulture)}</span><span class='cal-num'>{day.Day}</span><span class='cal-mon'>{day.ToString("MMM", CultureInfo.InvariantCulture)}</span></div>");
        if (entries.Count > 0) cells.Append($"<ul class='cal-docs'>{string.Join("", entries.Take(visiblePerDay).Select(Entry))}</ul>{more}");
        cells.Append("</div>");
    }

    string MonthLink(DateOnly target) => A((query with { Month = target.ToString("yyyy-MM", CultureInfo.InvariantCulture) }).Url());
    string BasisLink(string by, string text) => $"<a href='{A((query with { By = by }).Url())}'{((by == "scanned") == query.ByScanDate ? " class='active' aria-current='true'" : "")}>{text}</a>";
    var weekdays = string.Join("", new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }.Select(name => $"<div class='cal-weekday'>{name}</div>"));
    var summary = $"{inMonth:N0} document{(inMonth == 1 ? "" : "s")} in {monthName}" +
        (undated > 0 ? $" · {undated} without a document date {(undated == 1 ? "is" : "are")} shown on {(undated == 1 ? "its" : "their")} scan date (◷)" : "");
    return $"""
        <div class='calendar-bar'>
          <div class='cal-nav'><a class='button' href='{MonthLink(month.AddMonths(-1))}' aria-label='Previous month'>‹</a><h2>{monthName}</h2><a class='button' href='{MonthLink(month.AddMonths(1))}' aria-label='Next month'>›</a></div>
          <form class='cal-jump' method='get'>{HiddenFilters(query, "month")}<label class='visually-hidden' for='cal-month'>Go to month</label><input id='cal-month' type='month' name='month' value='{A(query.Month)}' onchange='this.form.requestSubmit()'><a class='button' href='{MonthLink(new DateOnly(today.Year, today.Month, 1))}'>Today</a></form>
          <div class='segmented' aria-label='Place documents by'>{BasisLink("document", "Document date")}{BasisLink("scanned", "Scan date")}</div>
        </div>
        <p class='count'>{summary}</p>
        <div class='calendar'>{weekdays}{cells}</div>
        {(inMonth == 0 ? $"<p class='cal-empty'>No documents in {monthName}.</p>" : "")}
        """;
}

static string HomePage(DocumentQuery query, string results, IReadOnlyList<string> suggestions, int pendingReviews)
{
    var message = query.Message is { Length: > 0 } ? $"<div class='notice'>{H(query.Message)}</div>" : "";
    if (pendingReviews > 0)
        message += $"<div class='notice review-notice' role='status'><span>{(pendingReviews == 1 ? "1 new scan looks like a document" : $"{pendingReviews} new scans look like documents")} you already have.</span><a class='button' href='/reviews'>Review {(pendingReviews == 1 ? "it" : "them")}</a></div>";
    if (query.ScannedOn is { } scannedOn)
        message += $"<div class='notice filter-notice' role='status'><span>Showing documents scanned on {scannedOn.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.</span><a href='{A((query with { Scanned = null, Page = 1 }).Url())}'>Show all</a></div>";
    var options = string.Join("", suggestions.Select(tag => $"<option value='{A(tag)}'></option>"));
    var activeFilters = new[] { query.Name, query.Content, query.Tag, query.From, query.To }.Count(value => !string.IsNullOrWhiteSpace(value));
    var filterCount = activeFilters == 0 ? "" : $"<span class='filter-count'>{activeFilters}</span>";
    var listUrl = A((query with { View = "list", Page = 1 }).Url());
    var calendarUrl = A((query with { View = "calendar", Page = 1 }).Url());
    var clearUrl = A(new DocumentQuery(View: query.View, By: query.By, Month: query.IsCalendar ? query.Month : null).Url());
    return $$"""
        <div class='hero'><div><p class='eyebrow'>LOCAL DOCUMENT LIBRARY</p><h1>Scanned documents</h1><p>OCR, tags and search stay on this computer.</p></div></div>{{message}}
        <div class='home-tools' aria-label='Library tools'>
          <button id='archive-toggle' class='primary panel-toggle' type='button' aria-expanded='false' aria-controls='archive-panel'><svg class='panel-toggle-icon' viewBox='0 0 24 24' aria-hidden='true' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'><path d='M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z'/></svg><span>Ask Ollama</span></button>
          <button id='filter-toggle' class='primary panel-toggle' type='button' aria-expanded='{{(activeFilters > 0 ? "true" : "false")}}' aria-controls='filter-panel'><svg class='panel-toggle-icon' viewBox='0 0 24 24' aria-hidden='true' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'><path d='M4 21v-7'/><path d='M4 10V3'/><path d='M12 21v-9'/><path d='M12 8V3'/><path d='M20 21v-5'/><path d='M20 12V3'/><path d='M2 14h4'/><path d='M10 8h4'/><path d='M18 16h4'/></svg><span>Filter</span>{{filterCount}}</button>
          <form method='post' action='/process'><button class='primary process-button' type='submit'>Process scans</button></form>
        </div>
        <details class='chat-panel collapsible-panel' id='archive-panel' aria-labelledby='archive-toggle'><summary>Ask your archive</summary><div class='chat-panel-content'>
        <div class='chat-intro'><p class='eyebrow'>ASK YOUR ARCHIVE</p><h2>What do you need to find?</h2><p class='chat-help'>Ask about dates, people, payments or renewals, or ask for lists and counts such as “list all bills from 2025”. Follow-up questions use the conversation so far.</p></div>
        <div id='chat-thread' class='chat-thread' aria-live='polite'></div>
        <form id='chat-form' class='chat-form'><label class='visually-hidden' for='chat-question'>Question</label><textarea id='chat-question' rows='2' placeholder='e.g. When does my car insurance renew?' required></textarea>
        <div class='chat-buttons'><button class='primary' type='submit'>Ask Ollama</button><button type='button' id='chat-reset' class='button hidden'>New conversation</button></div></form></div></details>
        <details class='filter-panel collapsible-panel' id='filter-panel'{{(activeFilters > 0 ? " open" : "")}} aria-labelledby='filter-toggle'><summary>Search and filter</summary>
        <form class='filters' method='get'>{{HiddenFilters(query, "name", "content", "tag", "from", "to")}}
        <label class='filter-wide'>Name<input name='name' value='{{A(query.Name)}}' placeholder='Title, sender or addressee'></label>
        <label class='filter-wide'>Text<input name='content' value='{{A(query.Content)}}' placeholder='Words in the OCR text'></label>
        <label>Tag or category<input name='tag' value='{{A(query.Tag)}}' list='tag-options' placeholder='e.g. Bills' autocomplete='off'><datalist id='tag-options'>{{options}}</datalist></label>
        <label>From date<input name='from' type='date' value='{{A(query.From)}}'></label>
        <label>To date<input name='to' type='date' value='{{A(query.To)}}'></label>
        <div class='filter-actions'><button>Search</button><a class='clear' href='{{clearUrl}}'>Clear</a></div></form></details>
        <div class='view-bar'>
          <nav class='segmented' aria-label='View'><a href='{{listUrl}}'{{(query.IsCalendar ? "" : " class='active' aria-current='page'")}}>List</a><a href='{{calendarUrl}}'{{(query.IsCalendar ? " class='active' aria-current='page'" : "")}}>Calendar</a></nav>
          <a class='button colours-link' href='/labels'>Colours</a>
        </div>
        {{results}}
        <dialog id='document-chat' class='document-chat-dialog' aria-labelledby='document-chat-title'>
        <div class='dialog-head'><div><p class='eyebrow'>DOCUMENT ASSISTANT</p><h2 id='document-chat-title'>Document</h2></div><form method='dialog' class='dialog-close'><button aria-label='Close'>×</button></form></div>
        <div id='document-chat-thread' class='chat-thread' aria-live='polite'></div>
        <form id='document-chat-form' class='chat-form'><input type='hidden' id='document-chat-id'><label class='visually-hidden' for='document-chat-question'>Question about this document</label><textarea id='document-chat-question' rows='2' placeholder='Ask for an explanation, key dates, amounts or actions.' required></textarea><div class='chat-buttons'><button class='primary' type='submit'>Ask</button><button type='button' id='document-chat-reset' class='button hidden'>New conversation</button></div></form></dialog>
        """ + ChatScript();
}

static string LabelsPage(IReadOnlyList<(string Label, int Count)> categories, IReadOnlyList<(string Label, int Count)> tags, IReadOnlyDictionary<string, string> colours, string search)
{
    var row = 0;
    string Row((string Label, int Count) item, bool isCategory)
    {
        row++;
        var current = LabelColours.For(item.Label, colours);
        var swatches = string.Join("", LabelColours.Palette.Select(swatch => (Name: swatch.Name, Title: swatch.Title)).Prepend((Name: LabelColours.None, Title: "No colour")).Select(option =>
            $"<label class='swatch swatch-{option.Name}' title='{option.Title}'><input type='radio' name='colour' value='{option.Name}' aria-label='{option.Title}'{((current ?? LabelColours.None) == option.Name ? " checked" : "")}><span></span></label>"));
        return $"""
            <form class='label-row' method='post' action='/labels'>
              <input type='hidden' name='label' value='{A(item.Label)}'><input type='hidden' name='q' value='{A(search)}'>
              <div class='label-name'><span class='tag{(isCategory ? " category" : "")}{LabelColours.ChipClass(current)}' data-preview>{H(item.Label)}</span><a class='label-count' href='/?tag={Uri.EscapeDataString(item.Label)}'>{item.Count:N0} document{(item.Count == 1 ? "" : "s")}</a></div>
              <fieldset class='swatches'><legend class='visually-hidden'>Colour for {H(item.Label)}</legend>{swatches}</fieldset>
              <noscript><button type='submit'>Save</button></noscript>
            </form>
            """;
    }
    var categoryRows = categories.Count == 0 ? "<p class='hint'>No documents have a category yet.</p>" : string.Join("", categories.Select(category => Row(category, true)));
    var tagRows = tags.Count == 0 ? $"<p class='hint'>{(search.Length > 0 ? $"No tags contain “{H(search)}”." : "No tags yet.")}</p>" : string.Join("", tags.Select(tag => Row(tag, false)));
    return $$"""
        <p><a class='back' href='/'>← All documents</a></p>
        <div class='edit-heading'><p class='eyebrow'>LIBRARY SETTINGS</p><h1>Colours</h1>
        <p>Colour categories and tags so different kinds of document stand out in the list and calendar. A document takes the colour of its first coloured tag, otherwise its category’s colour.</p></div>
        <p id='label-status' class='label-status' role='status'></p>
        <section><h2>Categories</h2><div class='label-list'>{{categoryRows}}</div></section>
        <section><div class='section-head'><h2>Tags</h2>
          <form class='label-search' method='get'><label class='visually-hidden' for='label-q'>Find a tag</label><input id='label-q' name='q' value='{{A(search)}}' placeholder='Find a tag'><button>Find</button></form></div>
          <p class='hint'>{{(search.Length > 0 ? $"Tags containing “{H(search)}”, most used first." : "The 100 most used tags. Search to find others.")}}</p>
          <div class='label-list'>{{tagRows}}</div></section>
        <script>
        // Each colour choice saves straight away; the preview chip changes to match.
        const status = document.getElementById('label-status');
        for (const form of document.querySelectorAll('.label-row')) {
          form.addEventListener('change', async event => {
            if (event.target.name !== 'colour') return;
            const label = form.elements.label.value;
            try {
              const response = await fetch(form.action, {method: 'POST', headers: {'X-Requested-With': 'fetch'}, body: new URLSearchParams(new FormData(form))});
              if (!response.ok) throw new Error();
              const preview = form.querySelector('[data-preview]');
              preview.className = preview.className.replace(/\bc-[a-z]+\b/g, '').trim();
              if (event.target.value !== 'none') preview.classList.add('c-' + event.target.value);
              status.textContent = `Saved the colour for “${label}”.`;
            } catch {
              status.textContent = `Could not save the colour for “${label}”. Try again.`;
            }
          });
        }
        </script>
        """;
}

static string ChatScript() => """
    <script>
    const touchKeyboard = matchMedia('(pointer: coarse)').matches;
    for (const toggle of document.querySelectorAll('.panel-toggle')) {
      const panel = document.getElementById(toggle.getAttribute('aria-controls'));
      const stateKey = 'document-tagger-panel:' + panel.id;
      try {
        const savedState = localStorage.getItem(stateKey);
        if (savedState !== null) panel.open = savedState === 'open';
      } catch (error) {
        console.warn('Could not restore the ' + panel.id + ' panel state.', error);
      }
      const updateState = () => {
        toggle.setAttribute('aria-expanded', String(panel.open));
        try {
          localStorage.setItem(stateKey, panel.open ? 'open' : 'closed');
        } catch (error) {
          console.warn('Could not save the ' + panel.id + ' panel state.', error);
        }
      };
      toggle.addEventListener('click', () => {
        panel.open = !panel.open;
        updateState();
      });
      panel.addEventListener('toggle', updateState);
      updateState();
    }
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

static bool StartProcessing(ProcessingStatus status, IServiceScopeFactory scopeFactory)
{
    if (!status.TryBegin()) return false;
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<DocumentProcessor>().ProcessPendingAsync(status.Report, status.AddResult);
            status.Complete(result);
        }
        catch (Exception ex) { status.Fail($"Processing stopped: {ex.Message}"); }
    });
    return true;
}

static void StartReprocessing(DocumentRecord document, ProcessingStatus status, IServiceScopeFactory scopeFactory)
{
    if (!status.TryBegin()) return;
    _ = Task.Run(async () =>
    {
        var fileName = document.OriginalFileName;
        status.Report(new ProcessingUpdate(0, 1, $"Preparing to reprocess {fileName}…", fileName,
            new ScanProgress(ScanSteps.Render, "Preparing the document…")));
        try
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();
            var result = await processor.ReprocessAsync(document.Id, scan =>
                status.Report(new ProcessingUpdate(0, 1, $"Reprocessing {fileName}: {scan.Message}", fileName, scan)));
            status.AddResult(result);
            status.Report(new ProcessingUpdate(1, 1, result.Detail));
            if (result.Outcome == "saved") status.Complete($"Reprocessed {fileName}.");
            else status.Fail(result.Detail);
        }
        catch (Exception ex)
        {
            var failure = new ProcessingResult(fileName, "failed", $"Reprocessing failed: {ex.Message}");
            status.AddResult(failure);
            status.Report(new ProcessingUpdate(1, 1, failure.Detail));
            status.Fail(failure.Detail);
        }
    });
}

static string ReviewsPage(IReadOnlyList<(ScanReview Review, DocumentRecord? Existing)> items, string? message, string duplicatesFolder)
{
    var notice = string.IsNullOrWhiteSpace(message) ? "" : $"<div class='notice' role='status'>{H(message)}</div>";
    var cards = string.Join("", items.Select(item =>
    {
        var (review, existing) = item;
        var existingLink = existing is null
            ? "a document that is no longer in the library"
            : $"<a href='/documents/{existing.Id}'>{H(existing.Title)}</a>{(existing.DocumentDate.HasValue ? $" ({existing.DocumentDate.Value:dd MMM yyyy})" : "")}";
        var verdict = review.IsIdentical
            ? $"This file is identical to the scan of {existingLink}."
            : $"Its text is <strong>{Math.Round(review.Similarity * 100)}%</strong> the same as {existingLink}.";
        var compare = review.IsIdentical || existing is null || review.OcrText is null ? "" : $"""
            <details class='compare'><summary>Compare the text</summary><div class='compare-grid'>
              <p class='eyebrow'>NEW SCAN</p><p class='eyebrow'>ALREADY IN LIBRARY</p>
              <div class='compare-lines'>{CompareTextLines(review.OcrText, existing.OcrText)}</div>
            </div></details>
            """;
        return $"""
            <section class='review-card'>
              <p class='eyebrow'>{(review.IsIdentical ? "IDENTICAL FILE" : "POSSIBLE DUPLICATE")}</p>
              <h2>{H(review.FileName)}</h2>
              <p class='review-verdict'>{verdict}</p>
              <p class='review-links'><a href='/reviews/{review.Id}/file' target='_blank'>Open the new scan</a>{(existing is null ? "" : $"<a href='/files/{existing.Id}' target='_blank'>Open the existing PDF</a>")}</p>
              {compare}
              <div class='review-actions'>
                <form method='post' action='/reviews/{review.Id}/discard'><button class='primary' type='submit'>Discard scan</button></form>
                <form method='post' action='/reviews/{review.Id}/process'><button class='button' type='submit'>Process anyway</button></form>
              </div>
              <p class='hint review-found'>Found {review.DetectedAt.ToLocalTime():dd MMM yyyy HH:mm}</p>
            </section>
            """;
    }));
    if (items.Count == 0) cards = "<section class='review-empty'><p>No scans are waiting for review.</p><p><a class='button' href='/'>Back to documents</a></p></section>";
    return $"""
        <p><a class='back' href='/'>← All documents</a></p>{notice}
        <div class='edit-heading'><p class='eyebrow'>BEFORE PROCESSING</p><h1>Possible duplicates</h1>
        <p>These scans look like documents already in your library, so they were not processed. Discarding a scan moves it to <code>{H(duplicatesFolder)}</code>; nothing is deleted.</p></div>
        <div class='review-list'>{cards}</div>
        """;
}

static string CompareTextLines(string newText, string libraryText)
{
    static string[] GetLines(string text) => Trim(text, 2500)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToArray();
    static string NormalizeLine(string line) => string.Concat(line.Where(character => !char.IsWhiteSpace(character)));

    var newLines = GetLines(newText);
    var libraryLines = GetLines(libraryText);
    var normalizedNewLines = newLines.Select(NormalizeLine).ToArray();
    var normalizedLibraryLines = libraryLines.Select(NormalizeLine).ToArray();
    var common = new int[newLines.Length + 1, libraryLines.Length + 1];
    for (var i = newLines.Length - 1; i >= 0; i--)
    {
        for (var j = libraryLines.Length - 1; j >= 0; j--)
        {
            common[i, j] = string.Equals(normalizedNewLines[i], normalizedLibraryLines[j], StringComparison.Ordinal)
                ? common[i + 1, j + 1] + 1
                : Math.Max(common[i + 1, j], common[i, j + 1]);
        }
    }

    var html = new StringBuilder();
    var changedNew = new List<string>();
    var changedLibrary = new List<string>();
    void AppendRow(string? newLine, string? libraryLine, bool mismatch)
    {
        html.Append("<div class='compare-row'>");
        AppendCell(newLine, "NEW SCAN", mismatch);
        AppendCell(libraryLine, "ALREADY IN LIBRARY", mismatch);
        html.Append("</div>");
    }

    void AppendCell(string? line, string label, bool mismatch)
    {
        html.Append("<div class='compare-line");
        if (mismatch) html.Append(" mismatch");
        if (line is null) html.Append(" missing");
        html.Append("' data-label='").Append(label).Append('\'');
        if (line is null) html.Append(" aria-hidden='true'></div>");
        else html.Append('>').Append(H(line)).Append("</div>");
    }

    void FlushChanges()
    {
        var rows = Math.Max(changedNew.Count, changedLibrary.Count);
        for (var row = 0; row < rows; row++)
            AppendRow(row < changedNew.Count ? changedNew[row] : null,
                row < changedLibrary.Count ? changedLibrary[row] : null, mismatch: true);
        changedNew.Clear();
        changedLibrary.Clear();
    }

    var newIndex = 0;
    var libraryIndex = 0;
    while (newIndex < newLines.Length || libraryIndex < libraryLines.Length)
    {
        if (newIndex < newLines.Length && libraryIndex < libraryLines.Length &&
            string.Equals(normalizedNewLines[newIndex], normalizedLibraryLines[libraryIndex], StringComparison.Ordinal))
        {
            FlushChanges();
            AppendRow(newLines[newIndex++], libraryLines[libraryIndex++], mismatch: false);
        }
        else if (newIndex < newLines.Length &&
            (libraryIndex == libraryLines.Length || common[newIndex + 1, libraryIndex] >= common[newIndex, libraryIndex + 1]))
        {
            changedNew.Add(newLines[newIndex++]);
        }
        else
        {
            changedLibrary.Add(libraryLines[libraryIndex++]);
        }
    }
    FlushChanges();
    return html.ToString();
}

static string DetailPage(DocumentRecord d, string? message, IReadOnlyDictionary<string, string> colours)
{
    var notice = string.IsNullOrWhiteSpace(message) ? "" : $"<div class='notice'>{H(message)}</div>";
    var metadata = $"<dl><dt>Document date</dt><dd>{H(d.DocumentDate?.ToString("dd MMMM yyyy") ?? "Not detected")}</dd>" +
        $"<dt>From</dt><dd>{H(d.Sender ?? "Not detected")}</dd><dt>Addressed to</dt><dd>{H(d.Addressee ?? "Not detected")}</dd>" +
        $"<dt>Category</dt><dd>{(d.Category is null ? "Not set" : CategoryChip(d.Category, colours))}</dd>" +
        $"<dt>Tags</dt><dd>{string.Join(" ", d.Tags.Select(tag => TagChip(tag, colours)))}</dd>" +
        $"<dt>Keywords</dt><dd>{H(string.Join(", ", d.Keywords))}</dd>" +
        $"<dt>Scanned</dt><dd>{H(d.ScannedAt?.ToString("dd MMM yyyy HH:mm") ?? "Unknown")}</dd>" +
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
    <div class='scan-stage'>
      <div class='doc-anim' id='doc-anim' data-step='idle' aria-hidden='true'>
        <div class='doc-sheet doc-under'></div>
        <div class='doc-sheet doc-front'><span class='doc-line'></span><span class='doc-line'></span><span class='doc-line'></span><span class='doc-line'></span><span class='doc-line'></span><span class='doc-line'></span><span class='doc-line'></span><span class='doc-beam'></span></div>
        <span class='doc-badge' id='doc-badge'></span>
        <span class='doc-pages hidden' id='doc-pages'></span>
      </div>
      <div class='scan-info'>
        <p class='scan-file' id='scan-file'>Preparing your scan queue…</p>
        <p class='scan-detail' id='scan-detail' aria-live='polite'></p>
        <ol class='scan-steps' id='scan-steps' aria-label='Processing steps'>
          <li data-step='render'>Render</li><li data-step='ocr'>Read text</li><li data-step='check'>Duplicates</li><li data-step='analyse'>Analyse</li><li data-step='save'>Save</li>
        </ol>
      </div>
    </div>
    <div class='progress' role='progressbar' aria-label='Overall progress' aria-valuemin='0' aria-valuemax='100'><div id='bar'></div></div>
    <div class='progress-meta'><span id='counter' class='count'></span><span id='percent' class='count'></span></div>
    <p id='status' class='run-summary hidden' role='status'></p>
    <ul id='results' class='run-results' aria-label='Files in this run'></ul>
    <p class='hint' id='leave-hint'>You can leave this page; processing carries on in the background.</p>
    <div id='complete' class='actions hidden'><a id='review-link' class='primary hidden' href='/reviews'></a><a id='library-link' class='primary' href='/'>View document library</a></div>
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
    // Share of one document's processing time taken by each step, so the bar moves steadily within a document.
    const steps = ['render', 'ocr', 'check', 'analyse', 'save'];
    const weights = {render: 0.15, ocr: 0.45, check: 0.05, analyse: 0.30, save: 0.05};
    const badges = {check: '≈', analyse: '✦', save: '↓', done: '✓'};
    const resultIcons = {saved: '✓', duplicate: '!', failed: '✕'};
    let shownResults = 0;
    function documentFraction(step, fraction) {
      let done = 0;
      for (const name of steps) {
        if (name === step) return done + weights[name] * Math.min(1, Math.max(0, fraction || 0));
        done += weights[name];
      }
      return 0;
    }
    function showResults(results) {
      const list = document.getElementById('results');
      for (const result of results.slice(shownResults)) {
        const item = document.createElement('li');
        item.className = result.outcome;
        const icon = document.createElement('span');
        icon.className = 'icon';
        icon.textContent = resultIcons[result.outcome] || '•';
        const text = document.createElement('div');
        const name = document.createElement('strong');
        name.textContent = result.file;
        const detail = document.createElement('span');
        detail.className = 'detail';
        detail.textContent = result.detail;
        text.append(name, detail);
        item.append(icon, text);
        if (result.documentId || result.outcome === 'duplicate') {
          const link = document.createElement('a');
          link.href = result.documentId ? '/documents/' + result.documentId : '/reviews';
          link.textContent = result.documentId ? 'Open' : 'Review';
          item.append(link);
        }
        list.append(item);
      }
      shownResults = results.length;
    }
    function showStep(state) {
      const step = state.running ? (state.step || 'idle') : (shownResults > 0 ? 'done' : 'idle');
      const animation = document.getElementById('doc-anim');
      animation.dataset.step = step;
      animation.classList.toggle('multi', state.pages > 1);
      document.getElementById('doc-badge').textContent = badges[step] || '';
      const pages = document.getElementById('doc-pages');
      const onPages = state.running && (step === 'render' || step === 'ocr') && state.page > 0;
      pages.textContent = onPages ? (state.pages > 0 ? `Page ${state.page} of ${state.pages}` : `Page ${state.page}`) : '';
      pages.classList.toggle('hidden', !onPages);
      const current = steps.indexOf(step);
      for (const item of document.querySelectorAll('#scan-steps li')) {
        const index = steps.indexOf(item.dataset.step);
        item.classList.toggle('done', step === 'done' || (current >= 0 && index < current));
        item.classList.toggle('active', index === current);
      }
    }
    async function refreshProgress() {
      try {
        const state = await fetch('/api/processing', {cache: 'no-store'}).then(r => r.json());
        if (state.index) document.getElementById('index-status').textContent = state.index;
        showResults(state.results || []);
        showStep(state);
        const total = state.total || 0;
        const working = state.running && state.file && state.completed < total;
        document.getElementById('scan-file').textContent = state.running ? (state.file || state.message) : 'Finished';
        document.getElementById('scan-detail').textContent = state.running ? (working ? state.detail || 'Preparing the document…' : '') : '';
        const summary = document.getElementById('status');
        summary.textContent = state.running ? '' : state.message;
        summary.classList.toggle('hidden', state.running);
        const progress = document.querySelector('.progress');
        const percent = total ? Math.min(100, ((state.completed + (working ? documentFraction(state.step, state.stepFraction) : 0)) / total) * 100) : (state.running ? 0 : 100);
        progress.classList.toggle('indeterminate', state.running && !total);
        progress.setAttribute('aria-valuenow', Math.round(percent));
        const bar = document.getElementById('bar');
        if (state.running && !total) bar.style.removeProperty('width');
        else bar.style.width = Math.max(2, percent) + '%';
        document.getElementById('counter').textContent = !total ? '' : state.running
          ? `Document ${Math.min(state.completed + 1, total)} of ${total}`
          : `${total} document${total === 1 ? '' : 's'} handled`;
        document.getElementById('percent').textContent = total ? `${Math.floor(percent)}%` : '';
        document.getElementById('leave-hint').classList.toggle('hidden', !state.running);
        if (!state.running) {
          // Duplicates need a decision, so reviewing them becomes the main action and the library link steps back.
          const reviewLink = document.getElementById('review-link');
          if (state.reviews > 0) {
            reviewLink.textContent = state.reviews === 1 ? 'Review 1 possible duplicate' : `Review ${state.reviews} possible duplicates`;
            reviewLink.classList.remove('hidden');
            document.getElementById('library-link').className = 'button';
          }
          document.getElementById('complete').classList.remove('hidden');
          clearInterval(timer);
        }
      } catch { document.getElementById('status').textContent = 'Waiting for the local service…'; }
    }
    const timer = setInterval(refreshProgress, 700); refreshProgress();
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
    :root{color-scheme:dark;--bg:#090d18;--panel:#12192a;--panel-2:#182135;--line:#2a3550;--text:#f0f4ff;--muted:#99a7c2;--accent:#7c8cff;--accent-2:#5eead4;--danger:#ff9ca8}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(ellipse at 12% -10%,#233a68 0,transparent 31rem),radial-gradient(ellipse at 85% 0,#203953 0,transparent 30rem),var(--bg);color:var(--text);font:15px/1.55 Inter,Segoe UI,Arial,sans-serif}body:before{content:'';position:fixed;inset:0;pointer-events:none;background-image:linear-gradient(rgba(255,255,255,.018) 1px,transparent 1px),linear-gradient(90deg,rgba(255,255,255,.018) 1px,transparent 1px);background-size:32px 32px;mask-image:linear-gradient(to bottom,black,transparent 70%)}main{max-width:1240px;margin:auto;padding:52px 28px 80px;position:relative}.site-mark{display:flex;gap:11px;align-items:center;color:#d9e1fa;font-weight:700;text-decoration:none;letter-spacing:-.02em;margin-bottom:45px}.site-mark:before{content:'✦';display:grid;place-items:center;width:30px;height:30px;border-radius:9px;background:linear-gradient(135deg,var(--accent),var(--accent-2));color:#0b1020;box-shadow:0 7px 25px rgba(94,234,212,.23)}h1{font-size:clamp(2.25rem,5vw,4.1rem);letter-spacing:-.055em;line-height:1.02;margin:.12em 0 .2em}h2{font-size:1.08rem;margin:0 0 20px}.hero,.detail-head{display:flex;justify-content:space-between;gap:24px;align-items:flex-start;margin-bottom:34px}.eyebrow{letter-spacing:.14em;font-weight:700;color:var(--accent-2);font-size:.7rem;margin:0}.hero p:not(.eyebrow),.detail-head>div>p,.edit-heading p:not(.eyebrow){color:var(--muted);margin:8px 0;max-width:580px}.primary,.button,button{border:1px solid var(--line);background:rgba(255,255,255,.03);padding:10px 15px;border-radius:9px;color:var(--text);font:inherit;font-weight:650;cursor:pointer;text-decoration:none;display:inline-block;transition:.18s ease}.primary{border-color:transparent;background:linear-gradient(135deg,var(--accent),#6672e9);box-shadow:0 10px 30px rgba(92,109,239,.22)}.primary:hover{filter:brightness(1.1);transform:translateY(-1px)}.button:hover,button:not(.primary):hover{border-color:#65759a;background:rgba(255,255,255,.07)}.filters{display:flex;flex-wrap:wrap;gap:10px;padding:13px;background:rgba(18,25,42,.8);backdrop-filter:blur(18px);border:1px solid var(--line);border-radius:13px;box-shadow:0 16px 40px rgba(0,0,0,.17)}input,select,textarea{width:100%;border:1px solid var(--line);border-radius:8px;padding:10px 11px;font:inherit;color:var(--text);background:#0c1322;outline:none;transition:border .18s,box-shadow .18s}input:focus,select:focus,textarea:focus{border-color:var(--accent);box-shadow:0 0 0 3px rgba(124,140,255,.17)}select{width:auto;min-width:145px}input[name=name],input[name=content]{flex:1;min-width:210px}.filters input{width:auto;min-width:135px}.clear,.back{color:var(--accent-2);padding:10px;text-decoration:none}.count{color:var(--muted);font-size:.87rem;margin:18px 2px}.table-wrap{overflow:auto;background:rgba(18,25,42,.8);border:1px solid var(--line);border-radius:13px;box-shadow:0 18px 46px rgba(0,0,0,.17)}table{width:100%;border-collapse:collapse;min-width:900px}th,td{padding:17px;text-align:left;border-bottom:1px solid rgba(42,53,80,.8);vertical-align:top}tbody tr:last-child td{border-bottom:0}tbody tr{transition:background .18s}tbody tr:hover{background:rgba(124,140,255,.06)}th{color:var(--muted);font-size:.69rem;letter-spacing:.1em;text-transform:uppercase;font-weight:700}td a{color:#dce3ff;font-weight:700;text-decoration:none}td a:hover{color:var(--accent-2)}small{display:block;color:var(--muted);margin-top:3px}.tag{display:inline-block;background:rgba(94,234,212,.1);border:1px solid rgba(94,234,212,.17);border-radius:99px;padding:2px 8px;margin:1px;color:#a4f5e7;font-size:.79rem}.empty{text-align:center;color:var(--muted);padding:48px}.notice{background:rgba(94,234,212,.1);border:1px solid rgba(94,234,212,.25);padding:12px 15px;margin:0 0 22px;border-radius:9px;color:#baf9ee}.actions{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.actions form{margin:0}.metadata,section,.processing,.edit-form{background:rgba(18,25,42,.82);backdrop-filter:blur(16px);border:1px solid var(--line);padding:26px;border-radius:13px;margin:22px 0;box-shadow:0 18px 46px rgba(0,0,0,.14)}.processing{max-width:650px;margin:80px auto}    .chat-panel-content{display:flex;flex-direction:column;gap:16px}.chat-help{color:var(--muted);margin:0}.chat-form{display:flex;gap:10px;align-items:end}.chat-form textarea{min-height:58px;resize:vertical}.chat-form button{white-space:nowrap}.chat-answer{min-height:48px;padding:14px 16px;color:#dbe3f7;background:rgba(7,12,25,.62);border:1px solid var(--line);border-radius:9px;white-space:pre-wrap}.document-chat{padding:7px 10px;font-size:.82rem;white-space:nowrap}.document-chat-dialog{width:min(620px,calc(100% - 28px));padding:28px;background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:14px;box-shadow:0 24px 80px rgba(0,0,0,.48)}.document-chat-dialog::backdrop{background:rgba(2,5,12,.76);backdrop-filter:blur(4px)}.document-chat-dialog h2{margin:6px 0 20px}.document-chat-dialog textarea{margin:0 0 12px;resize:vertical}.dialog-close{float:right}.dialog-close button{font-size:1.4rem;line-height:1;padding:4px 10px}.progress{height:10px;background:#0a1020;border-radius:99px;overflow:hidden;margin:24px 0 10px}.progress>div{height:100%;width:4%;background:linear-gradient(90deg,var(--accent),var(--accent-2));border-radius:99px;transition:width .35s ease}.hint{color:var(--muted)}.hidden{display:none}dl{display:grid;grid-template-columns:170px 1fr;gap:13px 20px;margin:0}dt{font-weight:650;color:var(--muted)}dd{margin:0}pre{white-space:pre-wrap;font:13px/1.65 ui-monospace,Consolas,monospace;margin:0;color:#cbd5ed}.edit-heading{margin-bottom:28px}.edit-form{max-width:920px}.form-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px 18px;margin-bottom:18px}.edit-form label{display:block;font-size:.84rem;font-weight:700;color:#ccd5ec;margin:17px 0}.form-grid label{margin:0}.edit-form label span{font-size:.75rem;color:var(--muted);font-weight:500;margin-left:4px}.edit-form input,.edit-form textarea{display:block;margin-top:7px}.edit-form textarea{resize:vertical}.ocr-input{font:13px/1.5 ui-monospace,Consolas,monospace;color:#cbd5ed}@media(max-width:650px){main{padding:30px 16px}.site-mark{margin-bottom:28px}.hero,.detail-head{display:block}.hero form{margin-top:20px}.form-grid{grid-template-columns:1fr}dl{grid-template-columns:1fr;gap:4px}dd{margin-bottom:13px}.filters input,.filters select{width:100%;flex:auto}}
    .progress.indeterminate>div{width:35%;animation:progress-slide 1.4s ease-in-out infinite}@keyframes progress-slide{from{transform:translateX(-110%)}to{transform:translateX(310%)}}
    .model-settings{border-top:1px solid var(--line);margin-top:30px;padding-top:20px}.model-settings>p:not(.eyebrow){margin:8px 0}.model-picker{display:grid;gap:7px;margin-top:18px}.model-picker label{font-size:.84rem;color:var(--muted)}.model-picker>div{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:10px}.model-picker select{width:100%;min-width:0}
    .site-header{display:flex;justify-content:space-between;align-items:center;margin-bottom:45px}.site-mark{margin-bottom:0}.sign-in{max-width:460px;margin:8vh auto}.sign-in h1{font-size:clamp(2.25rem,5vw,3.2rem)}.sign-in>p:not(.eyebrow){margin:8px 0 24px}.sign-in label{display:block;margin:18px 0;font-weight:650}.sign-in input{display:block;margin-top:7px}.sign-in .primary{width:100%;margin-top:5px}@media(max-width:650px){.site-header{margin-bottom:28px}}
    :root{--bg:#071120;--panel:rgba(15,30,58,.64);--panel-2:rgba(25,48,86,.58);--line:rgba(156,190,245,.2);--text:#eef4ff;--muted:#a6b8d7;--accent:#4f8cff;--accent-2:#8bc5ff}
    body{background:radial-gradient(ellipse at 12% -10%,rgba(53,111,225,.42) 0,transparent 34rem),radial-gradient(ellipse at 88% 2%,rgba(37,87,166,.38) 0,transparent 32rem),linear-gradient(145deg,#071120,#0a1630 48%,#07101f);background-attachment:fixed}
    main{max-width:1680px;padding:18px 32px 48px}
    .site-header{position:sticky;top:0;z-index:10;display:flex;justify-content:space-between;align-items:center;width:100%;margin:0;padding:10px clamp(16px,3vw,42px);border:0;border-bottom:1px solid rgba(175,204,255,.16);border-radius:0;background:rgba(7,17,36,.88);backdrop-filter:blur(22px) saturate(145%);box-shadow:0 10px 28px rgba(0,7,24,.2),inset 0 1px rgba(255,255,255,.04)}
    .site-header form{margin:0}.site-header .button{padding:8px 14px}
    .site-mark:before{box-shadow:0 7px 25px rgba(75,139,255,.34)}
    .eyebrow{color:#8fc7ff}
    .hero{align-items:center;padding:0 0 8px;margin-bottom:0;border-bottom:0}
    .primary{background:linear-gradient(135deg,#4f8cff,#4269e8);box-shadow:0 10px 30px rgba(54,113,255,.25)}
    .process-button{background:linear-gradient(135deg,#28b978,#168456);box-shadow:0 9px 24px rgba(27,156,102,.22)}.process-button:hover{background:linear-gradient(135deg,#32ca88,#1b9b66)}
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
    @media(max-width:650px){main{padding-top:14px}.site-header{padding:9px 14px}.hero{padding-bottom:4px}}
    .chat-thread{display:grid;gap:16px;align-content:start}
    .chat-turn{display:grid;gap:8px}.chat-question{margin:0;justify-self:end;max-width:85%;padding:9px 14px;border-radius:14px 14px 4px 14px;background:rgba(79,140,255,.18);border:1px solid rgba(139,190,255,.28);color:#e3efff;white-space:pre-wrap}
    .chat-answer.rendered{white-space:normal}.chat-answer.rendered>:first-child{margin-top:0}.chat-answer.rendered>:last-child{margin-bottom:0}.chat-answer p{margin:0 0 10px}.chat-answer ul,.chat-answer ol{margin:0 0 10px;padding-left:22px}.chat-answer li{margin:3px 0}.chat-answer h4{margin:12px 0 6px;font-size:.95rem}
    .chat-answer .cite{color:#9ed0ff;text-decoration:none;font-size:.82em;font-weight:650;white-space:nowrap}.chat-answer .cite:hover{text-decoration:underline}.chat-placeholder{color:var(--muted)}.chat-error{border-color:rgba(255,156,168,.45);color:#ffd2d8}
    .chat-sources{display:flex;flex-wrap:wrap;gap:6px;align-items:center;font-size:.8rem}.chat-sources:empty{display:none}.chat-sources span{color:var(--muted);font-weight:650;letter-spacing:.06em;text-transform:uppercase;font-size:.68rem;margin-right:2px}.chat-sources a{color:#c4e0ff;text-decoration:none;padding:3px 9px;border-radius:99px;background:rgba(79,140,255,.12);border:1px solid rgba(139,190,255,.25)}.chat-sources a:hover{background:rgba(79,140,255,.22)}
    .chat-warning{margin:0;font-size:.8rem;color:#ffd9a0}
    .review-notice{display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap;background:rgba(255,190,90,.1);border-color:rgba(255,200,120,.35);color:#ffe2b8}.review-notice .button{padding:7px 13px}
    .review-list{display:grid;gap:18px}.review-card{margin:0}.review-card h2{margin:4px 0 8px;overflow-wrap:anywhere}.review-card .eyebrow{color:#ffcf8a}
    .review-verdict{margin:0 0 10px}.review-verdict a,.review-links a{color:#9ed0ff}.review-links{display:flex;flex-wrap:wrap;gap:8px 18px;margin:0 0 14px;font-size:.9rem}
    .compare{margin:0 0 16px}.compare>summary{cursor:pointer;color:var(--muted);font-weight:650;font-size:.9rem;margin-bottom:10px}.compare-grid{display:grid;grid-template-columns:1fr 1fr;gap:0 14px;max-height:340px;overflow:auto;padding:0 8px 8px;border:1px solid rgba(156,190,245,.16);border-radius:10px;background:rgba(6,17,37,.34)}.compare-grid>.eyebrow{position:sticky;top:0;z-index:1;margin:0;padding:9px 10px 6px;background:rgba(13,29,59,.96)}.compare-lines{grid-column:1/-1;display:grid;gap:3px;min-width:0}.compare-row{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:14px;min-width:0}.compare-line{min-width:0;margin:0;padding:7px 10px;overflow-wrap:anywhere;white-space:pre-wrap;border-radius:6px;background:rgba(6,17,37,.32);color:#cbd5ed;font:12px/1.6 ui-monospace,Consolas,monospace}.compare-line.mismatch{background:rgba(255,120,140,.16);box-shadow:inset 3px 0 rgba(255,156,168,.85);color:#ffe0e4}.compare-line.missing{background:rgba(255,120,140,.07);outline:1px dashed rgba(255,156,168,.35)}
    .review-actions{display:flex;gap:10px;flex-wrap:wrap}.review-actions form{margin:0}.review-found{margin:12px 0 0;font-size:.8rem}.review-empty{text-align:center;color:var(--muted)}
    .scan-stage{display:grid;grid-template-columns:auto minmax(0,1fr);gap:28px;align-items:center;margin:26px 0 24px}
    .doc-anim{position:relative;width:96px;height:124px;margin:4px 8px 28px 4px}
    .doc-sheet{position:absolute;inset:0;border-radius:7px;background:linear-gradient(180deg,#f4f7ff,#dbe4f6);box-shadow:0 14px 34px rgba(0,8,30,.45)}
    .doc-under{background:linear-gradient(180deg,#c9d5ee,#aebfe0);transform:translate(10px,-8px) rotate(5deg);opacity:0;transition:opacity .3s}.doc-anim.multi .doc-under{opacity:.8}
    .doc-front{overflow:hidden;padding:20px 13px 0;display:flex;flex-direction:column;gap:8px;clip-path:polygon(0 0,calc(100% - 18px) 0,100% 18px,100% 100%,0 100%)}
    .doc-front:before{content:'';position:absolute;top:0;right:0;width:18px;height:18px;background:linear-gradient(to bottom left,transparent 50%,#a9b9db 50%)}
    .doc-line{display:block;height:6px;border-radius:3px;background:#b2c0dc;transform-origin:left center}.doc-line:nth-child(1){width:58%;height:8px;background:#7387b3}.doc-line:nth-child(2){width:92%}.doc-line:nth-child(3){width:80%}.doc-line:nth-child(4){width:96%}.doc-line:nth-child(5){width:70%}.doc-line:nth-child(6){width:88%}.doc-line:nth-child(7){width:44%}
    .doc-line:nth-child(2){animation-delay:.1s}.doc-line:nth-child(3){animation-delay:.2s}.doc-line:nth-child(4){animation-delay:.3s}.doc-line:nth-child(5){animation-delay:.4s}.doc-line:nth-child(6){animation-delay:.5s}.doc-line:nth-child(7){animation-delay:.6s}
    .doc-beam{position:absolute;left:0;right:0;top:-24px;height:22px;background:linear-gradient(180deg,transparent,rgba(79,140,255,.6) 60%,rgba(140,190,255,.95) 64%,transparent 70%);opacity:0}
    .doc-badge{position:absolute;right:-14px;bottom:-12px;width:36px;height:36px;border-radius:50%;display:grid;place-items:center;font-weight:800;font-size:1rem;color:#fff;background:linear-gradient(135deg,#4f8cff,#4269e8);box-shadow:0 8px 22px rgba(30,80,200,.45);transform:scale(0);transition:transform .25s ease}.doc-badge:not(:empty){transform:scale(1)}
    .doc-pages{position:absolute;left:50%;bottom:-32px;transform:translateX(-50%);white-space:nowrap;font-size:.74rem;font-weight:700;padding:2px 9px;border-radius:99px;background:rgba(79,140,255,.18);border:1px solid rgba(139,190,255,.32);color:#c4e0ff}
    .doc-anim[data-step=idle]{animation:doc-float 3s ease-in-out infinite}
    .doc-anim[data-step=render] .doc-line{animation:doc-line-draw 1.6s ease-in-out infinite}
    .doc-anim[data-step=ocr] .doc-beam{animation:doc-scan 1.8s linear infinite}.doc-anim[data-step=ocr] .doc-line{background:#a3b6dd}.doc-anim[data-step=ocr] .doc-line:nth-child(1){background:#6a80b0}
    .doc-anim[data-step=check] .doc-under{opacity:.9;animation:doc-compare 1.1s ease-in-out infinite alternate}
    .doc-anim[data-step=analyse] .doc-line{background:linear-gradient(90deg,#b2c0dc 0%,#6ea7ff 50%,#b2c0dc 100%);background-size:220% 100%;animation:doc-shimmer 1.5s linear infinite}.doc-anim[data-step=analyse] .doc-badge{animation:doc-pulse 1.4s ease-in-out infinite}
    .doc-anim[data-step=save] .doc-front{animation:doc-drop .9s ease-in-out infinite alternate}
    .doc-anim[data-step=done] .doc-badge{background:linear-gradient(135deg,#34c98e,#1f9c6c);box-shadow:0 8px 22px rgba(20,140,90,.45)}
    @keyframes doc-float{50%{transform:translateY(-5px)}}
    @keyframes doc-line-draw{0%{transform:scaleX(0)}45%,100%{transform:scaleX(1)}}
    @keyframes doc-scan{0%{top:-24px;opacity:1}100%{top:124px;opacity:1}}
    @keyframes doc-compare{to{transform:translate(-16px,-4px) rotate(-6deg)}}
    @keyframes doc-shimmer{from{background-position:120% 0}to{background-position:-120% 0}}
    @keyframes doc-pulse{50%{transform:scale(1.15)}}
    @keyframes doc-drop{to{transform:translateY(6px)}}
    @keyframes step-pulse{50%{box-shadow:0 0 0 5px rgba(79,140,255,.25)}}
    .scan-file{font-weight:700;font-size:1.05rem;margin:0;overflow-wrap:anywhere}.scan-detail{color:var(--muted);margin:4px 0 0;min-height:1.55em}
    .scan-steps{list-style:none;display:flex;flex-wrap:wrap;gap:6px;padding:0;margin:14px 0 0;counter-reset:step}
    .scan-steps li{counter-increment:step;display:flex;align-items:center;gap:6px;font-size:.76rem;font-weight:650;color:var(--muted);padding:3px 10px 3px 4px;border-radius:99px;border:1px solid rgba(156,190,245,.18);transition:.2s}
    .scan-steps li:before{content:counter(step);width:18px;height:18px;border-radius:50%;display:grid;place-items:center;font-size:.66rem;background:rgba(156,190,245,.14)}
    .scan-steps li.active{color:#e3efff;border-color:rgba(139,190,255,.6);background:rgba(79,140,255,.16)}.scan-steps li.active:before{background:#4f8cff;color:#fff;animation:step-pulse 1.4s ease-in-out infinite}
    .scan-steps li.done{color:#a8dcc4}.scan-steps li.done:before{content:'✓';background:rgba(94,234,160,.2);color:#8ff0c0}
    .progress-meta{display:flex;justify-content:space-between;gap:12px}.progress-meta .count{margin:0 2px 14px}
    .run-summary{margin:0 0 12px;font-weight:650}
    .run-results{list-style:none;padding:0;margin:0 0 6px;display:grid;gap:6px;max-height:280px;overflow:auto}.run-results:empty{display:none}
    .run-results li{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:10px;align-items:start;padding:8px 11px;border-radius:10px;background:rgba(6,17,37,.42);border:1px solid rgba(156,190,245,.12);font-size:.86rem}
    .run-results .icon{width:20px;height:20px;border-radius:50%;display:grid;place-items:center;font-size:.7rem;font-weight:800;margin-top:1px}
    .run-results .saved .icon{background:rgba(94,234,160,.2);color:#8ff0c0}.run-results .duplicate .icon{background:rgba(255,190,90,.2);color:#ffcf8a}.run-results .failed .icon{background:rgba(255,120,140,.2);color:#ffb3bf}
    .run-results strong{display:block;overflow-wrap:anywhere}.run-results .detail{display:block;color:var(--muted);overflow-wrap:anywhere}.run-results a{color:#9ed0ff;text-decoration:none;font-weight:650}.run-results .failed .detail{color:#ffc4cd}
    @media(prefers-reduced-motion:reduce){.doc-anim,.doc-anim *,.scan-steps li:before,.progress>div{animation:none!important}.doc-anim[data-step=ocr] .doc-beam{opacity:1;top:50%}}
    @media(max-width:650px){.scan-stage{grid-template-columns:1fr;justify-items:center;text-align:center;gap:10px}.scan-steps{justify-content:center}}
    code{font:13px ui-monospace,Consolas,monospace;background:rgba(6,17,37,.6);padding:1px 5px;border-radius:5px;overflow-wrap:anywhere}
    @media(max-width:650px){.compare-grid{grid-template-columns:1fr;max-height:420px}.compare-grid>.eyebrow{display:none}.compare-lines{grid-column:1}.compare-row{grid-template-columns:1fr;gap:4px;padding-bottom:8px;border-bottom:1px solid rgba(156,190,245,.12)}.compare-line:before{content:attr(data-label);display:block;margin-bottom:3px;color:var(--muted);font-size:.62rem;font-weight:700;letter-spacing:.1em}.review-actions{display:grid;grid-template-columns:1fr}.review-actions button{width:100%}.review-notice .button{width:100%;text-align:center}}
    /* Layout for desktop and mobile */
    body{background-attachment:scroll;background-color:#07101f}html{background:#07101f}
    .visually-hidden{position:absolute;width:1px;height:1px;margin:-1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
    :root{--input-size:15px}input,select,textarea,.edit-form input,.edit-form textarea{font-weight:400;font-size:var(--input-size)}
    :focus-visible{outline:2px solid #8bc5ff;outline-offset:2px}input:focus-visible,select:focus-visible,textarea:focus-visible{outline:none}
    .home-tools{display:flex;align-items:center;flex-wrap:wrap;gap:12px;margin:12px 0 10px}.home-tools form{margin:0}.panel-toggle{display:inline-flex;align-items:center;justify-content:center;gap:10px;min-width:118px;padding:10px 16px;font-size:1rem}.panel-toggle-icon{width:19px;height:19px;flex:none}.panel-toggle:after{content:'';width:8px;height:8px;margin-left:4px;border-right:2px solid currentColor;border-bottom:2px solid currentColor;transform:rotate(45deg);transition:transform .18s}.panel-toggle[aria-expanded=true]:after{transform:rotate(225deg)}.filter-count{min-width:20px;padding:1px 6px;border-radius:99px;background:rgba(255,255,255,.2);font-size:.76rem;text-align:center}
    .chat-intro h2{margin:4px 0 6px}
    .chat-thread{max-height:min(62vh,680px);overflow-y:auto;overscroll-behavior:contain;padding-right:2px}
    .chat-panel .chat-thread:has(>.chat-placeholder){display:none}
    .chat-form{display:flex;gap:10px;align-items:flex-end}.chat-form textarea{flex:1;min-height:52px;max-height:40vh;resize:vertical;margin:0}
    .chat-buttons{display:flex;gap:8px;flex-shrink:0}.chat-buttons button{white-space:nowrap}
    .dialog-head{display:flex;justify-content:space-between;align-items:flex-start;gap:12px;margin-bottom:16px}.dialog-head h2{margin:4px 0 0;overflow-wrap:anywhere}.dialog-close{float:none;margin:0}
    .document-chat-dialog[open]{display:flex;flex-direction:column;max-height:min(88vh,880px)}
    .document-chat-dialog .chat-thread{flex:1 1 auto;max-height:none;min-height:60px;margin-bottom:14px}.document-chat-dialog .chat-form{flex-shrink:0}
    .collapsible-panel>summary{display:none}
    .chat-panel{display:block;padding:0;margin:0 0 14px}
    .chat-panel-content{padding:20px}
    .filter-panel{margin:0 0 14px;padding:0;border:1px solid rgba(171,201,250,.2);border-radius:16px;background:rgba(13,28,57,.48);backdrop-filter:blur(24px) saturate(150%);box-shadow:0 22px 60px rgba(0,5,20,.2)}
    .filter-panel .filters{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:0;padding:14px;border:0;border-radius:inherit;background:none;box-shadow:none;backdrop-filter:none}
    .filters label{display:grid;gap:5px;font-size:.72rem;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:var(--muted);min-width:0}
    .filter-panel .filters input,.filter-panel .filters select{width:100%;min-width:0;flex:none;text-transform:none;letter-spacing:normal;font-size:var(--input-size);font-weight:400}
    .filter-wide,.filter-actions{grid-column:1/-1}.filter-actions{display:flex;gap:6px;align-items:center}.filter-actions button{min-width:110px}
    .col-date{white-space:nowrap}.col-name{min-width:190px}.col-people{min-width:110px}.col-summary{min-width:220px;color:#cfdaef}.col-ai{text-align:right;white-space:nowrap}.col-name a{overflow-wrap:anywhere}
    .document-actions{display:flex;flex-direction:column;gap:7px;width:120px;margin-left:auto}.document-actions form{margin:0}.document-actions button{display:flex;align-items:center;justify-content:center;gap:7px;width:100%;padding:7px 8px;font-size:.78rem;white-space:nowrap}.action-icon{width:15px;height:15px;flex:none}
    .section-head{display:flex;justify-content:space-between;align-items:baseline;gap:12px;flex-wrap:wrap}.section-head h2{margin:0 0 14px}.section-head span{color:var(--muted);font-size:.8rem}
    .ocr-text{max-height:min(70vh,760px);overflow:auto;padding:14px 16px;border:1px solid rgba(156,190,245,.16);border-radius:10px;background:rgba(6,17,37,.48)}
    @media(min-width:801px){.filter-panel .filters{grid-template-columns:minmax(0,1.5fr) minmax(0,1.5fr) minmax(0,1fr) minmax(0,.9fr) minmax(0,.9fr) auto;align-items:end;padding:18px}.filter-wide,.filter-actions{grid-column:auto}}
    @media(max-width:800px){
      .table-wrap{overflow:visible;background:none;border:0;box-shadow:none;backdrop-filter:none;border-radius:0}
      table{min-width:0}thead{display:none}table,tbody,td{display:block}tbody{display:grid;gap:12px}
      tbody tr{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 120px;grid-template-areas:'name name ai' 'date from to' 'tags tags tags' 'summary summary summary';gap:12px;padding:16px;border:1px solid rgba(171,201,250,.2);border-radius:16px;background:linear-gradient(135deg,rgba(49,83,139,.24),rgba(12,27,54,.68));box-shadow:0 14px 40px rgba(0,5,20,.22)}
      tbody tr:hover{background:linear-gradient(135deg,rgba(49,83,139,.32),rgba(12,27,54,.72))}
      td{padding:0;border:0;min-width:0;overflow-wrap:anywhere}.col-name{grid-area:name;min-width:0;font-size:1.02rem}.col-date{grid-area:date}.col-from{grid-area:from}.col-to{grid-area:to}.col-tags{grid-area:tags}.col-summary{grid-area:summary;min-width:0}.col-ai{grid-area:ai;align-self:start}.col-people{min-width:0}
      td[data-label]{font-size:.88rem}td[data-label]:before{content:attr(data-label);display:block;font-size:.64rem;font-weight:700;letter-spacing:.1em;text-transform:uppercase;color:var(--muted);margin-bottom:2px}
      .col-tags:empty,.col-summary:empty{display:none}td.empty{grid-column:1/-1;padding:32px 0}
    }
    @media(max-width:650px){
      main{padding:16px 14px 48px}.site-header{margin-bottom:22px}
      h1{font-size:clamp(1.9rem,9vw,2.5rem);overflow-wrap:anywhere}
      .hero{padding-bottom:18px;margin-bottom:22px}.hero form,.hero form button{width:100%}.hero form button{margin-top:4px}
      .chat-panel,.metadata,section,.processing,.edit-form,.sign-in{padding:18px;border-radius:18px}.chat-panel{padding:0}.chat-panel-content{padding:18px}.processing{margin:24px auto}
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
    a.tag{text-decoration:none;font-weight:500;transition:filter .15s}a.tag:hover{filter:brightness(1.25)}
    .tag.category{font-weight:700}.tag.category:before{content:'';display:inline-block;width:6px;height:6px;border-radius:50%;background:currentColor;margin:0 5px 1px 0;vertical-align:middle}
    .col-tags{min-width:200px}.doc-row td.col-name{box-shadow:inset 3px 0 0 var(--accent,transparent)}
    .view-bar{display:flex;justify-content:space-between;align-items:center;gap:12px;margin:12px 0 0}
    .segmented{display:inline-flex;padding:3px;border-radius:11px;background:rgba(6,17,37,.55);border:1px solid rgba(156,190,245,.18)}
    .segmented a{padding:7px 14px;border-radius:8px;color:var(--muted);text-decoration:none;font-weight:650;font-size:.88rem;white-space:nowrap}
    .segmented a.active{background:rgba(79,140,255,.25);color:#e3efff;box-shadow:inset 0 0 0 1px rgba(139,190,255,.4)}.segmented a:not(.active):hover{color:var(--text)}
    .button.disabled{opacity:.4;cursor:default;pointer-events:none}
    .pager{display:flex;flex-wrap:wrap;justify-content:center;align-items:center;gap:8px;margin:18px 0}.pager-status{color:var(--muted);padding:0 8px}
    .calendar-bar{display:flex;flex-wrap:wrap;align-items:center;gap:12px 18px;margin:18px 0 0}
    .cal-nav{display:flex;align-items:center;gap:10px}.cal-nav h2{margin:0;min-width:9.5em;text-align:center;font-size:1.25rem}.cal-nav .button{padding:5px 13px;font-size:1.15rem;line-height:1.3}
    .cal-jump{display:flex;gap:8px;align-items:center}.cal-jump input{width:auto;padding:7px 10px}.calendar-bar .segmented{margin-left:auto}
    .calendar{display:grid;grid-template-columns:repeat(7,minmax(0,1fr));gap:1px;background:rgba(156,190,245,.13);border:1px solid rgba(171,201,250,.2);border-radius:16px;overflow:hidden}
    .cal-weekday{background:rgba(9,22,47,.88);padding:9px 10px;font-size:.68rem;font-weight:700;letter-spacing:.1em;text-transform:uppercase;color:var(--muted)}
    .cal-day{background:rgba(11,25,52,.85);min-height:118px;padding:8px;display:flex;flex-direction:column;gap:5px;min-width:0}
    .cal-day.other-month{background:rgba(6,15,33,.88)}.cal-day.other-month .cal-num,.cal-day.other-month .cal-docs{opacity:.5}
    .cal-date{display:flex;align-items:baseline;gap:6px}.cal-dow,.cal-mon{display:none}
    .cal-num{font-size:.85rem;font-weight:700;color:var(--muted);width:26px;height:26px;display:grid;place-items:center;border-radius:50%}.cal-day.today .cal-num{background:#4f8cff;color:#fff}
    .cal-docs{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:minmax(0,1fr);gap:4px;min-width:0}
    .cal-doc{display:block;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;padding:3px 7px;border-radius:6px;font-size:.78rem;font-weight:600;text-decoration:none;color:var(--accent-text,#dbe5f7);background:var(--accent-soft,rgba(156,190,245,.12));border-left:3px solid var(--accent,rgba(156,190,245,.45))}
    .cal-doc:hover{filter:brightness(1.25)}.cal-flag{margin-right:4px;opacity:.85}
    .filter-notice{display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap}.filter-notice a{color:#9ed0ff;font-weight:650}
    .cal-more-link{font-size:.75rem;color:#9ed0ff;font-weight:650;text-decoration:none;padding:1px 2px}.cal-more-link:hover{text-decoration:underline}
    .cal-more>summary{cursor:pointer;font-size:.75rem;color:#9ed0ff;font-weight:650;list-style:none;padding:1px 2px}.cal-more>summary::-webkit-details-marker{display:none}.cal-more[open]>summary{margin-bottom:4px}
    .cal-empty{display:none}
    @media(max-width:800px){
      .calendar-bar .segmented{margin-left:0}
      .calendar{display:block;background:none;border:0;border-radius:0;overflow:visible}
      .cal-weekday,.cal-day:not(.has-docs),.cal-day.other-month{display:none}
      .cal-day{display:grid;grid-template-columns:52px minmax(0,1fr);gap:4px 12px;min-height:0;padding:12px 14px;margin-bottom:8px;border-radius:14px;border:1px solid rgba(171,201,250,.18)}
      .cal-date{flex-direction:column;align-items:center;gap:0;grid-row:span 2}.cal-dow,.cal-mon{display:block;font-size:.64rem;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--muted)}.cal-num{font-size:1.15rem;color:var(--text);width:34px;height:34px}
      .cal-doc{white-space:normal;font-size:.88rem;padding:7px 10px}
      .cal-empty{display:block;text-align:center;color:var(--muted);padding:28px 0}
      .doc-row{border-left:4px solid var(--accent,rgba(171,201,250,.2))}.doc-row td.col-name{box-shadow:none}
    }
    @media(max-width:650px){.cal-nav{width:100%;justify-content:space-between}.cal-nav h2{min-width:0}.cal-jump{flex:1}.cal-jump input{flex:1}.pager .button{padding:8px 11px}}
    .label-status{min-height:1.4em;color:#a8dcc4;margin:0 0 4px}
    .label-list{display:grid}
    .label-row{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:8px 16px;padding:10px 2px;border-bottom:1px solid rgba(156,190,245,.1)}.label-row:last-child{border-bottom:0}
    .label-name{display:flex;align-items:center;gap:10px;min-width:0;flex-wrap:wrap}.label-count{color:var(--muted);font-size:.8rem;text-decoration:none;white-space:nowrap}.label-count:hover{color:#9ed0ff}
    .swatches{display:flex;flex-wrap:wrap;gap:5px;border:0;margin:0;padding:0;min-width:0}
    .swatch{position:relative;display:inline-grid;cursor:pointer}.swatch input{position:absolute;inset:0;margin:0;opacity:0;cursor:pointer}
    .swatch span{width:22px;height:22px;border-radius:50%;background:var(--swatch);box-shadow:inset 0 0 0 1px rgba(255,255,255,.18);transition:transform .12s}
    .swatch:hover span{transform:scale(1.12)}.swatch input:checked+span{box-shadow:0 0 0 2px #071120,0 0 0 4px #fff}.swatch input:focus-visible+span{outline:2px solid #8bc5ff;outline-offset:4px}
    .swatch-none span{background:repeating-linear-gradient(45deg,#26324d 0 3px,#4a5a80 3px 6px)}
    .label-search{display:flex;gap:8px}.label-search input{width:min(260px,52vw)}
    @media(pointer:coarse){.swatch span{width:28px;height:28px}.swatches{gap:7px}}
    __PALETTE__
    </style></head><body><header class='site-header'><a class='site-mark' href='/'>Document Tagger</a>__ACCOUNT__</header><main>__BODY__</main></body></html>
    """.Replace("__TITLE__", H(title))
    .Replace("__ACCOUNT__", signedIn ? "<form method='post' action='/sign-out'><button class='button' type='submit'>Sign out</button></form>" : "")
    .Replace("__PALETTE__", LabelColours.Css)
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
    /// <summary>Folder containing Tesseract's eng.traineddata. Leave empty to find it next to tesseract.exe automatically.</summary>
    public string TessdataPath { get; set; } = "";

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
    /// <summary>How similar (0–1) a new scan's text must be to an existing document before it is held back as a possible duplicate.</summary>
    public double DuplicateThreshold { get; set; } = 0.75;
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
    private readonly List<ProcessingResult> results = [];
    private ProcessingSnapshot state = new(false, 0, 0, "Nothing is currently being processed.");

    public bool TryBegin()
    {
        lock (gate)
        {
            if (state.Running) return false;
            results.Clear();
            state = new ProcessingSnapshot(true, 0, 0, "Preparing the scan queue…");
            return true;
        }
    }
    public void Report(ProcessingUpdate update)
    {
        lock (gate)
            state = new ProcessingSnapshot(true, update.Completed, update.Total, update.Message, update.File, update.Scan?.Step, update.Scan?.Message,
                update.Scan?.Page ?? 0, update.Scan?.Pages ?? 0, update.Scan?.Fraction ?? 0);
    }
    public void AddResult(ProcessingResult result) { lock (gate) results.Add(result); }
    public void Complete(string message) { lock (gate) state = state with { Running = false, Message = message, File = null, Step = null, Detail = null }; }
    public void Fail(string message) { lock (gate) state = state with { Running = false, Message = message, File = null, Step = null, Detail = null }; }
    public ProcessingSnapshot Snapshot() { lock (gate) return state with { Results = results.ToList() }; }
}

sealed class DocumentProcessor(DocumentStore store, OcrService ocr, OllamaClient ollama, DocumentIndexer indexer, TaggerSettings settings)
{
    public const string DuplicatesFolderName = "Duplicates";

    private sealed record ScanWork(string Path, ScanReview? ApprovedReview);

    public async Task<string> ProcessPendingAsync(Action<ProcessingUpdate>? progress = null, Action<ProcessingResult>? onResult = null)
    {
        progress?.Invoke(new ProcessingUpdate(0, 0, "Looking for PDFs in the scans folder…"));
        if (!Directory.Exists(settings.ScansDirectory)) return $"Scans folder was not found: {settings.ScansDirectory}";

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0; var total = 0; var saved = 0; var flagged = 0; var errors = new List<string>();
        // Repeat while new work appears, so a scan approved with "Process anyway" during this run is picked up before it ends.
        for (var round = 0; round < 20; round++)
        {
            var work = FindWork(handled);
            if (work.Count == 0) break;
            total += work.Count;
            progress?.Invoke(new ProcessingUpdate(completed, total, round == 0 ? $"Found {total} new PDF{(total == 1 ? "" : "s")}." : $"Added {work.Count} approved scan{(work.Count == 1 ? "" : "s")}."));
            foreach (var item in work)
            {
                handled.Add(item.Path);
                var name = Path.GetFileName(item.Path);
                progress?.Invoke(new ProcessingUpdate(completed, total, $"Processing {name}: preparing document…", name));
                try
                {
                    var documentId = await ProcessScanAsync(item, scan => progress?.Invoke(new ProcessingUpdate(completed, total, $"Processing {name}: {scan.Message}", name, scan)));
                    completed++;
                    if (documentId is null)
                    {
                        flagged++;
                        onResult?.Invoke(new ProcessingResult(name, "duplicate", "Looks like a document you already have. Waiting for your review."));
                    }
                    else
                    {
                        saved++;
                        onResult?.Invoke(new ProcessingResult(name, "saved", "Saved and indexed.", documentId));
                    }
                    progress?.Invoke(new ProcessingUpdate(completed, total, documentId is null ? $"{name} looks like a duplicate and is waiting for your review." : $"Saved {name}."));
                }
                catch (Exception ex)
                {
                    completed++;
                    errors.Add($"{name}: {ex.Message}");
                    onResult?.Invoke(new ProcessingResult(name, "failed", ex.Message));
                    progress?.Invoke(new ProcessingUpdate(completed, total, $"Could not process {name}: {ex.Message}"));
                }
            }
        }

        var waiting = store.CountReviews(ScanReview.Pending);
        if (total == 0)
            return waiting == 0 ? "No new PDFs found in the scans folder." : $"No new PDFs found. {Plural(waiting, "possible duplicate")} {(waiting == 1 ? "is" : "are")} waiting for your review.";
        var message = $"Processed {Plural(saved, "document")}.";
        if (flagged > 0) message += $" {Plural(flagged, "scan")} looked like {(flagged == 1 ? "a duplicate" : "duplicates")} and {(flagged == 1 ? "needs" : "need")} your review.";
        if (errors.Count > 0) message += $" {errors.Count} failed. {string.Join(" ", errors)}";
        return message;
    }

    /// <summary>New scans in the folder, plus scans the user approved after a duplicate warning. Scans waiting for review are skipped.</summary>
    private List<ScanWork> FindWork(HashSet<string> handled)
    {
        var reviews = new Dictionary<string, ScanReview>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in store.GetReviews())
        {
            // Forget reviews whose scan was removed or moved outside the app.
            if (File.Exists(review.SourcePath)) reviews[review.SourcePath] = review;
            else store.DeleteReview(review.Id);
        }

        var work = new List<ScanWork>();
        foreach (var file in Directory.EnumerateFiles(settings.ScansDirectory, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            if (handled.Contains(file) || store.IsProcessedSource(file)) continue;
            if (reviews.TryGetValue(file, out var review))
            {
                // A file replaced under the same name is a different scan, so it is checked again from scratch.
                if (!string.Equals(review.ContentHash, DocumentStore.ComputeContentHash(file), StringComparison.Ordinal)) store.DeleteReview(review.Id);
                else if (review.Status == ScanReview.Approved) { work.Add(new ScanWork(file, review)); continue; }
                else continue;
            }
            work.Add(new ScanWork(file, null));
        }
        return work;
    }

    /// <summary>Processes one scan and returns the new document's id, or holds the scan for review and returns null when it looks like a document already in the library.</summary>
    private async Task<long?> ProcessScanAsync(ScanWork item, Action<ScanProgress> progress)
    {
        var hash = DocumentStore.ComputeContentHash(item.Path);
        if (item.ApprovedReview is null && store.FindDocumentByContentHash(hash) is long identicalTo)
        {
            progress(new ScanProgress(ScanSteps.Check, "this file is identical to a document you already have.", Fraction: 1));
            store.SaveReview(new ScanReview(0, item.Path, hash, null, identicalTo, 1, ScanReview.IdenticalReason, DateTimeOffset.UtcNow, ScanReview.Pending));
            return null;
        }

        // An approved rescan keeps the OCR text from when it was flagged, so it is not read twice.
        var text = item.ApprovedReview?.OcrText ?? await ocr.ReadPdfAsync(item.Path, progress);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("OCR returned no readable text.");
        if (item.ApprovedReview is null)
        {
            progress(new ScanProgress(ScanSteps.Check, "checking for duplicates…", Fraction: 0.5));
            if (DuplicateDetector.FindBestMatch(text, store.GetDuplicateCandidates(), settings.DuplicateThreshold) is { } match)
            {
                store.SaveReview(new ScanReview(0, item.Path, hash, text, match.DocumentId, match.Similarity, ScanReview.SimilarReason, DateTimeOffset.UtcNow, ScanReview.Pending));
                return null;
            }
        }

        var id = await ProcessFileAsync(item.Path, null, progress, text);
        if (item.ApprovedReview is not null) store.DeleteReview(item.ApprovedReview.Id);
        return id;
    }

    /// <summary>Moves a duplicate scan out of the scans folder into its Duplicates subfolder, where it is never picked up again.</summary>
    public string DiscardDuplicate(long reviewId)
    {
        var review = store.GetReview(reviewId);
        if (review is null) return "That scan is no longer waiting for review.";
        var folder = Path.Combine(settings.ScansDirectory, DuplicatesFolderName);
        try
        {
            if (File.Exists(review.SourcePath))
            {
                Directory.CreateDirectory(folder);
                File.Move(review.SourcePath, AvailablePath(folder, review.FileName, null));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not move {review.FileName}: {ex.Message}";
        }
        store.DeleteReview(review.Id);
        return $"Discarded {review.FileName}. It was moved to {folder}.";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public async Task<ProcessingResult> ReprocessAsync(long id, Action<ScanProgress>? progress = null)
    {
        var existing = store.Get(id);
        if (existing is null) return new ProcessingResult($"Document {id}", "failed", "Document not found.");
        try
        {
            var reprocessedId = await ProcessFileAsync(existing.SourcePath, id, progress);
            return new ProcessingResult(existing.OriginalFileName, "saved", "Document reprocessed and indexed.", reprocessedId);
        }
        catch (Exception ex)
        {
            return new ProcessingResult(existing.OriginalFileName, "failed", $"Reprocessing failed: {ex.Message}");
        }
    }

    private async Task<long> ProcessFileAsync(string sourcePath, long? existingId, Action<ScanProgress>? progress, string? ocrText = null)
    {
        var text = ocrText ?? await ocr.ReadPdfAsync(sourcePath, progress);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("OCR returned no readable text.");
        var fileName = Path.GetFileName(sourcePath);
        // Ollama gives no progress until it writes, so the step creeps forward with elapsed time and never reaches the end on its own.
        var elapsed = Stopwatch.StartNew();
        double AnalysisFraction() => 0.95 * (1 - Math.Exp(-elapsed.Elapsed.TotalSeconds / 45));
        string Elapsed() => elapsed.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        progress?.Invoke(new ScanProgress(ScanSteps.Analyse, "Ollama is reading the text to find the title, date, sender and tags…"));
        var analysisTask = ollama.AnalyseAsync(text, fileName, count =>
            progress?.Invoke(new ScanProgress(ScanSteps.Analyse, $"Ollama is writing the metadata ({count:N0} characters, {Elapsed()} elapsed)…", Fraction: AnalysisFraction())));
        if (progress is not null)
        {
            while (await Task.WhenAny(analysisTask, Task.Delay(TimeSpan.FromSeconds(2))) != analysisTask)
                progress(new ScanProgress(ScanSteps.Analyse, $"Ollama is analysing the document ({Elapsed()} elapsed)…", Fraction: AnalysisFraction()));
        }
        var analysis = await analysisTask;
        progress?.Invoke(new ScanProgress(ScanSteps.Save, "saving the tagged PDF and metadata…", Fraction: 0.2));
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
        progress?.Invoke(new ScanProgress(ScanSteps.Save, "indexing the text for search…", Fraction: 0.6));
        await indexer.IndexAsync(record with { Id = id });
        return id;
    }

    private static string AvailablePath(string directory, string fileName, string? current) { var path = Path.Combine(directory, fileName); var n = 2; while (File.Exists(path) && !string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)} ({n++}).pdf"); return path; }
    private static string SafeName(string value) { var invalid = Path.GetInvalidFileNameChars(); var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.'); return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned[..Math.Min(cleaned.Length, 120)]; }
}

sealed class OcrService(TaggerSettings settings)
{
    public async Task<string> ReadPdfAsync(string pdfPath, Action<ScanProgress>? progress = null)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "document-tagger", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            var prefix = Path.Combine(scratch, "page");
            var expected = await GetPageCountAsync(pdfPath);
            progress?.Invoke(new ScanProgress(ScanSteps.Render, expected > 0 ? $"rendering {expected} page{(expected == 1 ? "" : "s")}…" : "rendering PDF pages…", 0, expected));
            // pdftoppm renders every page in one run, so progress comes from watching the page images appear.
            var seen = 0;
            await RunAsync(ToolPath(settings.PdfToPpmPath, "pdftoppm"), $"-r {settings.RenderDpi} -png {Q(pdfPath)} {Q(prefix)}", scratch, "PDF rendering", () =>
            {
                var rendered = Directory.EnumerateFiles(scratch, "page-*.png").Count();
                if (rendered == seen) return;
                seen = rendered;
                var total = Math.Max(expected, rendered);
                progress?.Invoke(new ScanProgress(ScanSteps.Render, expected > 0 ? $"rendering page {rendered} of {total}…" : $"rendering page {rendered}…", rendered, expected, expected > 0 ? (rendered - 1) / (double)total : 0));
            });
            var pages = Directory.EnumerateFiles(scratch, "page-*.png").OrderBy(x => x).ToList();
            if (pages.Count == 0) throw new InvalidOperationException("No PDF pages were rendered.");
            var output = new StringBuilder();
            var tesseract = ToolPath(settings.TesseractPath, "tesseract");
            var tessdata = FindTessdata(tesseract);
            var languageData = tessdata is null ? "" : $" --tessdata-dir {Q(tessdata)}";
            for (var i = 0; i < pages.Count; i++)
            {
                var baseName = Path.Combine(scratch, Path.GetFileNameWithoutExtension(pages[i]));
                progress?.Invoke(new ScanProgress(ScanSteps.Ocr, $"reading page {i + 1} of {pages.Count} with OCR…", i + 1, pages.Count, i / (double)pages.Count));
                try
                {
                    await RunAsync(tesseract, $"{Q(pages[i])} {Q(baseName)}{languageData} -l eng", scratch, "Tesseract OCR");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("traineddata", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("load any languages", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(tessdata is null
                        ? "Tesseract could not find its English language data (eng.traineddata). Set TessdataPath in appsettings.json to the folder that contains it, usually a 'tessdata' folder in the Tesseract installation."
                        : $"Tesseract could not load eng.traineddata from {tessdata}. Download it from https://github.com/tesseract-ocr/tessdata into that folder, or set TessdataPath in appsettings.json.");
                }
                var result = baseName + ".txt"; if (File.Exists(result)) output.AppendLine(await File.ReadAllTextAsync(result));
            }
            return output.ToString().Trim();
        }
        finally { try { Directory.Delete(scratch, true); } catch { } }
    }

    /// <summary>The PDF's page count from poppler's pdfinfo (installed next to pdftoppm), or 0 when it cannot be read.</summary>
    private async Task<int> GetPageCountAsync(string pdfPath)
    {
        var pdftoppm = ToolPath(settings.PdfToPpmPath, "pdftoppm");
        var folder = Path.GetDirectoryName(pdftoppm);
        var pdfinfo = string.IsNullOrEmpty(folder) ? "pdfinfo" : Path.Combine(folder, "pdfinfo" + Path.GetExtension(pdftoppm));
        try
        {
            using var process = Process.Start(new ProcessStartInfo(pdfinfo, Q(pdfPath)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return 0;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            var pages = Regex.Match(await output, @"^Pages:\s+(\d+)", RegexOptions.Multiline);
            return pages.Success ? int.Parse(pages.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { return 0; }
    }

    /// <summary>
    /// The folder holding Tesseract's language data, passed as --tessdata-dir. Builds that are unzipped rather than installed
    /// (for example a source build with the data in ..\share\tessdata) do not find it on their own, and the app runs Tesseract
    /// from a temporary folder, so it is searched for next to tesseract.exe. Null leaves Tesseract to its own default.
    /// </summary>
    private string? FindTessdata(string tesseract)
    {
        if (!string.IsNullOrWhiteSpace(settings.TessdataPath)) return Path.TrimEndingDirectorySeparator(settings.TessdataPath);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TESSDATA_PREFIX"))) return null;
        var folder = Path.GetDirectoryName(tesseract);
        if (string.IsNullOrEmpty(folder)) return null;
        return new[] { Path.Combine(folder, "tessdata"), Path.Combine(folder, "..", "share", "tessdata"), Path.Combine(folder, "..", "tessdata") }
            .Select(Path.GetFullPath)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "eng.traineddata")));
    }

    /// <summary>Accepts either the executable or the folder that contains it.</summary>
    private static string ToolPath(string configured, string executable) =>
        Directory.Exists(configured) ? Path.Combine(configured, executable + ".exe") : configured;

    private static async Task RunAsync(string program, string arguments, string workingDirectory, string step, Action? whileRunning = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException($"Could not start {program}.");
            // Both streams are drained while the tool runs; a full, unread pipe would make it hang.
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            var exited = process.WaitForExitAsync();
            while (await Task.WhenAny(exited, Task.Delay(250)) != exited) whileRunning?.Invoke();
            whileRunning?.Invoke();
            if (process.ExitCode != 0) throw new InvalidOperationException($"{step} failed. {(await error).Trim()}");
        }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException($"{step} requires '{program}'. Install it or set its full path in appsettings.json."); }
    }
    private static string Q(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

record DocumentRecord(long Id, string SourcePath, string OriginalFileName, string FilePath, string Title, DateOnly? DocumentDate, string? Sender, string? Addressee, List<string> Tags, List<string> Keywords, string Summary, string OcrText, DateTimeOffset ProcessedAt, string? Category = null, DateTime? ScannedAt = null);
sealed record ChatRequest(string? Question, long? DocumentId, List<ChatTurn>? History);
record DocumentAnalysis(string? Title, DateOnly? DocumentDate, string? Sender, string? Addressee, string? Category, List<string>? Tags, List<string>? Keywords, string? Summary);
static class ScanSteps
{
    public const string Render = "render";
    public const string Ocr = "ocr";
    public const string Check = "check";
    public const string Analyse = "analyse";
    public const string Save = "save";
}

/// <summary>Where one scan is in processing. <see cref="Fraction"/> is how far through the current step it is (0–1).</summary>
record ScanProgress(string Step, string Message, int Page = 0, int Pages = 0, double Fraction = 0);
record ProcessingUpdate(int Completed, int Total, string Message, string? File = null, ScanProgress? Scan = null);
/// <summary>The outcome for one file in a run: <c>saved</c>, <c>duplicate</c> or <c>failed</c>.</summary>
record ProcessingResult(string File, string Outcome, string Detail, long? DocumentId = null);
record ProcessingSnapshot(bool Running, int Completed, int Total, string Message, string? File = null, string? Step = null, string? Detail = null, int Page = 0, int Pages = 0, double StepFraction = 0, IReadOnlyList<ProcessingResult>? Results = null);
/// <summary>
/// The home page's state, all held in the URL so any view can be bookmarked or shared: filters, list or calendar view,
/// the list page, and the calendar month and whether it places documents by document date or scan date.
/// </summary>
record DocumentQuery(string? Name = null, string? Content = null, string? Tag = null, string? From = null, string? To = null, string? Message = null,
    string View = "list", int Page = 1, string? Month = null, string By = "document", string? Scanned = null)
{
    public bool IsCalendar => View == "calendar";
    public bool ByScanDate => By == "scanned";
    public bool HasFilters => new[] { Name, Content, Tag, From, To, Scanned }.Any(value => !string.IsNullOrWhiteSpace(value));
    /// <summary>A "scanned on this day" filter, set by the calendar's link to a busy day's documents.</summary>
    public DateOnly? ScannedOn => DateOnly.TryParseExact(Scanned, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>The first day of the requested month, from a <c>yyyy-MM</c> value.</summary>
    public DateOnly? MonthStart => DateOnly.TryParseExact(Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month : null;

    public static DocumentQuery Parse(IQueryCollection q) => new(
        q["name"].ToString(), q["content"].ToString(), q["tag"].ToString(), q["from"].ToString(), q["to"].ToString(), q["message"].ToString(),
        q["view"].ToString() == "calendar" ? "calendar" : "list",
        int.TryParse(q["page"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1,
        q["month"].ToString() is { Length: > 0 } month ? month : null,
        q["by"].ToString() == "scanned" ? "scanned" : "document",
        q["scanned"].ToString() is { Length: > 0 } scanned ? scanned : null);

    /// <summary>A home page link with this state; one-off values (the message) are dropped, and defaults are left out to keep URLs short.</summary>
    public string Url()
    {
        var parts = new List<string>();
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{key}={Uri.EscapeDataString(value)}"); }
        Add("name", Name); Add("content", Content); Add("tag", Tag); Add("from", From); Add("to", To); Add("scanned", Scanned);
        if (IsCalendar) { Add("view", View); Add("month", Month); if (ByScanDate) Add("by", By); }
        else if (Page > 1) Add("page", Page.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "/" : "/?" + string.Join("&", parts);
    }
}

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
