# Document Tagger

A private, localhost-only C# web app for organizing scanned PDFs. It watches a source folder, renders each page, runs OCR, asks a local Ollama model to extract metadata, and stores the processed document in a categorized folder structure without altering the original.

The app keeps a local SQLite search index and also writes a readable `.metadata.json` sidecar beside each processed PDF.

## Features

- Finds new PDFs in a configured scans folder
- Renders PDF pages to images with `pdftoppm`
- Extracts OCR text with Tesseract
- Uses local Ollama to infer metadata such as title, sender/addressee, date, category, tags, keywords, and summary
- Answers questions about the whole archive: a hybrid search index (SQLite FTS5 keywords plus Ollama embeddings) finds the relevant passages, dates, senders and categories in a question become filters, and list and count questions get the exact matching documents
- Streams chat answers into the browser as Ollama generates them, with follow-up questions, progress feedback, and linked source documents
- Lists installed Ollama models on the processing page and saves the selected model for future app requests
- Copies documents into a date- and category-based folder structure
- Provides a browser UI for searching, reviewing, editing, and reprocessing documents
- Requires the Castle account to sign in before using the site

## Prerequisites

- .NET SDK 10.0.401
- [Poppler for Windows](https://github.com/oschwartz10612/poppler-windows/releases) so `pdftoppm` is available on PATH, or set `PdfToPpmPath` in `appsettings.json`
- [Tesseract OCR for Windows](https://github.com/UB-Mannheim/tesseract/wiki) so `tesseract.exe` is available on PATH, or set `TesseractPath` in `appsettings.json`
- Ollama 0.5 or later, with a chat model and an embedding model installed

Install the recommended models:

```powershell
ollama pull qwen2.5:7b        # chat and metadata; better at instructions and JSON than llama3.1:8b
ollama pull nomic-embed-text  # small embedding model for semantic search
```

Select the chat model on the processing page (or set `OllamaModel`). On a 6 GB GPU such as a GTX 1060, set these system environment variables and restart Ollama. They roughly halve the memory the 8K context needs, so it fits next to a 7B model:

```powershell
setx OLLAMA_FLASH_ATTENTION 1 /M
setx OLLAMA_KV_CACHE_TYPE q8_0 /M
```

If answers are too slow, `qwen2.5:3b` or `gemma3:4b` are faster.

## Configuration

The settings live in `appsettings.json` under the `DocumentTagger` section:

```json
{
  "DocumentTagger": {
    "ScansDirectory": "C:\\Users\\pharl\\Scans",
    "ProcessedDirectory": "C:\\Users\\pharl\\Scans\\Processed",
    "DatabaseDirectory": "App_Data",
    "OllamaUrl": "http://localhost:11434",
    "OllamaModel": "qwen2.5:7b",
    "EmbeddingModel": "nomic-embed-text",
    "ChatContextTokens": 8192,
    "AnalysisContextTokens": 8192,
    "ChatTemperature": 0.2,
    "ChunkSize": 1200,
    "ChunkOverlap": 200,
    "MaxContextChunks": 8,
    "KeepAlive": "10m",
    "DuplicateThreshold": 0.75,
    "PdfToPpmPath": "C:\\path\\to\\Poppler\\Library\\bin\\pdftoppm.exe",
    "TesseractPath": "C:\\Program Files\\Tesseract-OCR\\tesseract.exe",
    "RenderDpi": 300
  }
}
```

Common settings:

- `ScansDirectory`: where new PDFs are discovered
- `ProcessedDirectory`: where processed PDFs are saved
- `DatabaseDirectory`: folder for the SQLite database (`documents.db`)
- `OllamaUrl` and `OllamaModel`: local Ollama endpoint and initial model; the processing page can select any installed model, saved in `DatabaseDirectory\ollama-model.txt` and used for future app requests
- `PdfToPpmPath` and `TesseractPath`: full paths to the render and OCR executables, or the folders that contain them (the Windows service cannot rely on your interactive PATH). Poppler's `pdfinfo`, installed next to `pdftoppm`, is used to show "page X of Y" while processing
- `TessdataPath`: the folder containing Tesseract's `eng.traineddata`. Usually leave it empty: the app looks for `tessdata` next to `tesseract.exe`, including the `..\share\tessdata` layout of an unzipped source build. Set it if OCR fails with "Error opening data file eng.traineddata"
- `RenderDpi`: OCR rendering resolution
- `EmbeddingModel`: Ollama model for semantic search; leave empty for keyword search only. Changing it re-embeds every document in the background
- `ChatContextTokens` and `AnalysisContextTokens`: the Ollama context window (`num_ctx`) for chat and for metadata extraction. Prompts are sized to fit, so nothing is silently cut. Keep the two equal, or Ollama reloads the model when it switches between them
- `ChatTemperature`: lower values give more literal, factual answers
- `ChunkSize`, `ChunkOverlap`: size of the OCR passages in the search index, in characters
- `MaxContextChunks`: the most passages sent to the model for one archive question
- `KeepAlive`: how long Ollama keeps models loaded between requests
- `DuplicateThreshold`: how similar (0 to 1) a new scan's text must be to an existing document before it is held back as a possible duplicate. Raise it if different documents are flagged; lower it if rescans slip through

## How the chat finds answers

1. Every document is split into a metadata card plus overlapping passages of OCR text. They are indexed for keyword search (SQLite FTS5 with stemming) and embedded for semantic search. Documents processed before this index existed are indexed in the background after startup. Progress shows on the processing page and at `/health`.
2. The model first turns the question into a search plan: a standalone query (so follow-ups work), plus any date range, sender, addressee, category and intent (lookup, list, count or summarize).
3. The filters narrow the documents. If nothing matches, they are relaxed instead of returning nothing. Keyword and semantic rankings are then merged, at most three passages per document.
4. For list and count questions, the model also gets the exact number of matching documents and a one-line catalogue of them.
5. The prompt is packed to fit the context window, with the question last, and the answer cites documents as `[#id]`, which become links.

**Ask AI** on a single document sends the whole document when it fits, or otherwise its metadata and the passages most relevant to the question.

After upgrading from an earlier version, use **Reprocess** on documents whose metadata looks wrong. Earlier versions did not size prompts to Ollama's context window, so the extraction rules were often cut off.

## Run

```powershell
dotnet run
```

Then open `http://localhost:5248`.
Sign in with the Castle account to access the document library. The sign-in session expires after eight hours.

## Run as a Windows service

The app binds only to `http://localhost:5248`, suitable as the origin for a local Cloudflare Tunnel. Configure the tunnel hostname `castledocs.crazyl0v3.icu` to forward to `http://localhost:5248`. The app honors the tunnel's forwarded HTTPS scheme so authentication cookies are Secure for public HTTPS requests while local HTTP development remains usable.

Open an elevated PowerShell window in the repository and run:

```powershell
.\install-service.ps1
```

The installer publishes the app under `%LOCALAPPDATA%\DocumentTagger\Service`, preserves the existing SQLite database on first install, prompts for the current Windows account password, and registers an automatic-start Windows service. Running as the same account lets the app access that account's scans folders and local Ollama service. To check or stop the service:

```powershell
Get-Service DocumentTagger
Stop-Service DocumentTagger
```

To apply app or dependency path changes to an existing service, run the installer again from an elevated PowerShell window. It stops the service during publishing, preserves its service account and database, and starts it with the updated configuration.

## Workflow

From the home page, click **Process scans** to process every new PDF in the scans folder. The original file is left untouched.

The processing page shows the file being worked on, its current step (render, read text, check for duplicates, analyse, save), and which page is being rendered or read. It also lists the result for each file. Processing continues in the background if you leave the page.

### Duplicate scans

Before a scan is sent to Ollama, it is checked against the library:

- **Identical file**: a byte-for-byte copy of a scan that was already processed.
- **Possible duplicate**: a new scan whose OCR text closely matches an existing document, such as the same letter scanned twice. Both the wording and the numbers have to match, so next month's bill from the same company is not flagged.

A flagged scan is not processed. The home page and the processing page link to **Possible duplicates**, where you can open both PDFs, compare their text, and choose:

- **Discard scan**: moves the file to a `Duplicates` folder inside the scans folder. Nothing is deleted, and files there are never picked up again.
- **Process anyway**: processes it as a new document, reusing the OCR text that was already read.

Processed documents are saved under:

```text
C:\Users\pharl\Scans\Processed\<category>\<year>\
```

Typical categories include `Bills`, `Legal`, `Medical`, `Appointments`, and `Government`.

Files are renamed with a date prefix, for example:

```text
2026-08-12 - TV Licence - Permissions and Limitations.pdf
```

If the same date and title occur multiple times, the app adds a numeric suffix.

## Browsing, search and metadata editing

The home page has two views, and the filters apply to both:

- **List**: newest first, 50 documents per page. Each row's left edge shows the document's colour.
- **Calendar**: a month grid with each document under its document date. Switch to **Scan date** to place documents by when they were scanned instead. A document with no document date appears on its scan date, marked ◷. On a phone the calendar becomes a list of the days that have documents. Days with many documents link to the list for that day.

The scan date is the scan file's last-modified time, which is when the scanner wrote it. Documents processed before this was recorded use the original scan file's time if it still exists, otherwise the time they were processed.

The web UI can search by:

- title
- sender or addressee
- tag or category (with suggestions as you type; clicking any tag or category chip also filters by it)
- document date
- keywords
- summary text
- OCR text

### Colours

The **Colours** page assigns a colour to any category or tag. Categories start with defaults, such as Bills amber, Legal red, Medical green and Tax violet. You can change them or switch them off. Tags have no colour until you choose one. A document takes the colour of its first coloured tag, otherwise its category's colour. The colour appears on its chips, its row in the list and its calendar entry.

Each processed document can be opened for:

- viewing the PDF
- editing metadata fields such as title, sender, addressee, tags, keywords, summary, and OCR text
- running **Reprocess** to refresh OCR and metadata extraction

## Local data

The app stores its index in:

```text
App_Data\documents.db
```

It also writes a metadata sidecar next to each processed PDF:

```text
filename.metadata.json
```

This is intended for local use only and is not a cloud sync system.

## Notes

- This is designed for a single-user local workflow and does not depend on a remote service beyond Ollama on localhost.
- If you change model or OCR settings, re-run **Reprocess** on affected documents.
- If `pdftoppm` or Tesseract are not on PATH, configure their full paths in `appsettings.json`.
