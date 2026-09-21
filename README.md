# Document Tagger

A private, localhost-only C# web app for organizing scanned PDFs. It watches a source folder, renders each page, runs OCR, asks a local Ollama model to extract metadata, and stores the processed document in a categorized folder structure without altering the original.

The app keeps a local SQLite search index and also writes a readable `.metadata.json` sidecar beside each processed PDF.

## Features

- Finds new PDFs in a configured scans folder
- Renders PDF pages to images with `pdftoppm`
- Extracts OCR text with Tesseract
- Uses local Ollama to infer metadata such as title, sender/addressee, date, category, tags, keywords, and summary
- Copies documents into a date- and category-based folder structure
- Provides a browser UI for searching, reviewing, editing, and reprocessing documents

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
    "PdfToPpmPath": "pdftoppm",
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
- `PdfToPpmPath` and `TesseractPath`: paths to the render and OCR engines
- `RenderDpi`: OCR rendering resolution

## Run

```powershell
dotnet run
```

Then open the local URL shown in the terminal, typically on `http://localhost:5000`.

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
