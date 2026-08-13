# Document Tagger

A private, localhost-only C# web application for image-only scanned PDFs. It renders each PDF page, runs OCR, asks local Ollama to extract metadata, then copies and renames the PDF into a tagged folder structure. Metadata is held in SQLite for search and written beside each processed PDF as a readable `.metadata.json` sidecar file.

## Prerequisites

- .NET 8 SDK (already available on this computer)
- [Poppler for Windows](https://github.com/oschwartz10612/poppler-windows/releases) — provides `pdftoppm` (already available in this environment)
- [Tesseract OCR for Windows](https://github.com/UB-Mannheim/tesseract/wiki) — install it and make `tesseract.exe` available on PATH, or set `TesseractPath` to its full path in `appsettings.json`.
- Ollama and the requested model:

  ```powershell
  ollama pull llama3.1
  ```

## Run

```powershell
dotnet run
```

Open the local address shown in the terminal (normally `http://localhost:5000`). Select **Process scans**. The originals in `C:\Users\pharl\Scans` are never moved or changed.

Processed PDFs are copied to `C:\Users\pharl\Scans\Processed\<category>\<year>\`, using broad categories such as `Bills`, `Legal`, `Medical`, and `Appointments`. Each filename begins with its date, for example `2026-08-12 - TV Licence - Permissions and Limitations.pdf`; same-day duplicates receive an incrementing suffix. The application database is `App_Data\documents.db` and is intentionally excluded from Git.

## Settings

`appsettings.json` controls input/output folders, local Ollama model and URLs, OCR executable locations, and render quality. For another model already installed in Ollama, change `OllamaModel` temporarily.

The web interface searches title, sender/addressee, tag, document date, keywords and the complete OCR text. Use **Reprocess** after changing OCR or model settings.
