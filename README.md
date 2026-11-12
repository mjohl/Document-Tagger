# Document Tagger

A private, localhost-only C# web app for organizing scanned PDFs. It watches a source folder, renders each page, runs OCR, asks a local Ollama model to extract metadata, and stores the processed document in a categorized folder structure without altering the original.

The app keeps a local SQLite search index and also writes a readable `.metadata.json` sidecar beside each processed PDF.

## Features

- Finds new PDFs in a configured scans folder
- Renders PDF pages to images with `pdftoppm`
- Extracts OCR text with Tesseract
- Uses local Ollama to infer metadata such as title, sender/addressee, date, category, tags, keywords, and summary
- Streams chat answers into the browser as Ollama generates them, with progress feedback while it starts
- Copies documents into a date- and category-based folder structure
- Provides a browser UI for searching, reviewing, editing, and reprocessing documents
- Requires the Castle account to sign in before using the site

## Prerequisites

- .NET SDK 10.0.401
- [Poppler for Windows](https://github.com/oschwartz10612/poppler-windows/releases) so `pdftoppm` is available on PATH, or set `PdfToPpmPath` in `appsettings.json`
- [Tesseract OCR for Windows](https://github.com/UB-Mannheim/tesseract/wiki) so `tesseract.exe` is available on PATH, or set `TesseractPath` in `appsettings.json`
- Ollama running locally with a model available for the app settings

Install the default model used by the app:

```powershell
ollama pull llama3.1
```

## Configuration

The settings live in `appsettings.json` under the `DocumentTagger` section:

```json
{
  "DocumentTagger": {
    "ScansDirectory": "C:\\Users\\pharl\\Scans",
    "ProcessedDirectory": "C:\\Users\\pharl\\Scans\\Processed",
    "DatabaseDirectory": "App_Data",
    "OllamaUrl": "http://localhost:11434",
    "OllamaModel": "llama3.1:8b",
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
- `OllamaUrl` and `OllamaModel`: local Ollama endpoint and model name
- `PdfToPpmPath` and `TesseractPath`: full paths to the render and OCR executables (the Windows service cannot rely on your interactive PATH)
- `RenderDpi`: OCR rendering resolution

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

## Search and metadata editing

The web UI can search by:

- title
- sender or addressee
- tags
- document date
- keywords
- summary text
- OCR text

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
