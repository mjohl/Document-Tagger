# Document Tagger

Document Tagger is a private, localhost-only C# web app for organizing scanned PDFs. It watches a scans folder, renders each PDF page, runs OCR, asks a local Ollama model to extract metadata, and saves a cleaned copy into a categorized folder structure while keeping the original untouched.

It stores searchable metadata in SQLite and writes a readable `.metadata.json` sidecar beside each processed PDF.

## What it does

- Scans a configured folder for new PDF files
- Renders each page to images with `pdftoppm`
- Extracts text with Tesseract OCR
- Sends the OCR text to Ollama for title, sender/addressee, date, category, tags, keywords, and summary
- Copies the document into `Processed/<category>/<year>/` using a date-based filename
- Keeps a local SQLite index for searching and browsing
- Lets you edit metadata manually and reprocess an individual document

## Prerequisites

- .NET SDK 10.0.401
- [Poppler for Windows](https://github.com/oschwartz10612/poppler-windows/releases) so `pdftoppm` is available on PATH, or set `PdfToPpmPath` in `appsettings.json`
- [Tesseract OCR for Windows](https://github.com/UB-Mannheim/tesseract/wiki) so `tesseract.exe` is available on PATH, or set `TesseractPath` in `appsettings.json`
- Ollama running locally, with the model configured in `appsettings.json`

Example:

```powershell
ollama pull llama3.1
```

## Configuration

The app settings live in `appsettings.json` under the `DocumentTagger` section.

```json
{
  "DocumentTagger": {
    "ScansDirectory": "C:\\Users\\pharl\\Scans",
    "ProcessedDirectory": "C:\\Users\\pharl\\Scans\\Processed",
    "DatabaseDirectory": "App_Data",
    "OllamaUrl": "http://localhost:11434",
    "OllamaModel": "llama3.1:8b",
    "PdfToPpmPath": "pdftoppm",
    "TesseractPath": "C:\\Program Files\\Tesseract-OCR\\tesseract.exe",
    "RenderDpi": 300
  }
}
```

Key settings:

- `ScansDirectory`: the input folder that is searched for new PDFs
- `ProcessedDirectory`: destination for completed documents
- `DatabaseDirectory`: SQLite database folder; the app stores `documents.db` there
- `OllamaUrl` and `OllamaModel`: local Ollama endpoint and model used for extraction
- `PdfToPpmPath` and `TesseractPath`: OCR/render executables
- `RenderDpi`: page rendering resolution for OCR quality

## Run

```powershell
dotnet run
```

Open the local URL printed by ASP.NET (typically `http://localhost:5000` or similar) in a browser.

## How the app works

From the home page, click **Process scans** to process every unindexed PDF in the scans directory. The originals are never modified or moved.

Completed PDFs are saved into:

```text
C:\Users\pharl\Scans\Processed\<category>\<year>\
```

Examples of categories include `Bills`, `Legal`, `Medical`, `Appointments`, and `Government`.

Each file is renamed to a date-based pattern such as:

```text
2026-08-12 - TV Licence - Permissions and Limitations.pdf
```

If multiple documents share the same date and title, the app adds a numeric suffix.

## Search and metadata editing

The interface supports searching by:

- title
- sender or addressee
- tag
- document date range
- keywords
- summary text
- OCR text content

Each processed document has a detail page where you can:

- open the PDF
- edit title, sender, addressee, date, tags, keywords, summary, and OCR text
- run **Reprocess** to re-run OCR and metadata extraction for that file

## Local data files

The app keeps the working index in:

```text
App_Data\documents.db
```

It also writes a sidecar metadata file next to each processed PDF:

```text
filename.metadata.json
```

These files are intentionally local-only and are not synced anywhere.

## Notes

- The app is designed for a single-person local workflow and does not require internet access beyond Ollama on `localhost`
- If you change OCR settings, model settings, or extraction prompts, use **Reprocess** for affected documents
- If `pdftoppm` or Tesseract are not on PATH, set their full path in `appsettings.json`
