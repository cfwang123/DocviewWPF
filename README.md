# DocviewWPF

Lightweight multi-tab document viewer for Windows (.NET Framework 4.8 WPF): fast start, simple UI, open and read. **Supported formats: PDF (`.pdf`), Word (`.docx`), Excel (`.xlsx`).**

**Language:** [中文说明 (README.zh.md)](README.zh.md)

## Features

Highlights that set DocviewWPF apart from a plain PDF-only reader or office-only apps:

- **Three formats in one window** — open **PDF / DOCX / XLSX** side by side in tabs (not PDF-only).
- **PDF Pro Editor** — dedicated window for page-object edit: select / marquee multi-select, drag with live ghost preview, text replace with embedded system fonts (CJK-safe), images, whiteout, shapes; undo/redo; vector-oriented save when pdfium allows.
- **XLSX built for large sheets** — virtualized `OnRender` grid, freeze panes, filters, column/row resize, wrap, and a simple Excel-like edit mode (merge, align, font, colors, arrow/Tab navigation, block select).
- **DOCX reading that keeps structure** — flow layout with outline (incl. SDT fields), numbering/bullets, margins — without launching Word.
- **Chrome-style tabs** — reorder with animation, tear off into a new window while dragging, drop back to merge.
- **True single-instance** — second launch activates the existing process and forwards open paths (no duplicate main windows).
- **Session & reading progress** — optional restore of last tabs; per-file scroll/zoom/page/sheet memory for PDF, DOCX, and XLSX.
- **Find with on-screen highlight** — Ctrl+F; all matches in view highlighted (current vs others), per-tab search text.
- **UI in four languages** — 中文 / English / 日本語 / 한국어 (Help → Language, or Settings).
- **Lightweight chrome** — custom title bar & icon toolbar, maximize within work area (no taskbar cover), `F` fullscreen.

Also: outline sidebar, shared-read open for locked files, recent files, themes and UI font size.

## Requirements

- Windows x64
- .NET Framework 4.8
- Visual Studio / MSBuild (or `dotnet` SDK with net48 targeting pack)

## Build & run

```bat
dotnet restore DocviewWPF\DocviewWPF.csproj
dotnet build DocviewWPF\DocviewWPF.csproj -c Release
```

Output: `DocviewWPF\bin\Release\net48\DocviewWPF.exe`  
Solution: `DocviewWPF.slnx`

## Configuration

```text
%LocalAppData%\DocviewWPF\
  settings.json
  session.json
  reading_progress.json
  recent.json
```

Runtime logs (from build output): `DocviewWPF\bin\Release\net48\logs\docviewwpf_YYYYMMDD.log`

Do not commit secrets, API keys, passwords, or machine-specific paths.

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+O | Open |
| Ctrl+W | Close tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl++ / Ctrl+- / Ctrl+0 | Zoom in / out / actual size |
| Ctrl+wheel | Zoom |
| F | Fullscreen |
| Esc | Exit fullscreen / clear selection |
| Ctrl+F | Find |
| Ctrl+C | Copy selection |
| Ctrl+S | Save (edit modes) |

## Project layout

```text
DocviewWPF.slnx
DocviewWPF/
  DocviewWPF.csproj
  App.xaml(.cs) · MainWindow.xaml(.cs)
  Core/           # settings, session, theme, log, i18n, single-instance
  Viewers/        # PDF / DOCX / XLSX & PDF pro editor
  Assets/
README.md · README.zh.md · CHANGELOG.md
```

## Dependencies (NuGet)

- PDFtoImage (pdfium + SkiaSharp)
- DocumentFormat.OpenXml
- System.Memory / System.Runtime.CompilerServices.Unsafe

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
