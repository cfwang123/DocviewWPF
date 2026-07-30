# DocviewWPF

Lightweight multi-tab document viewer for Windows (.NET Framework 4.8 WPF). Inspired by SumatraPDF: fast start, simple UI, open and read. **Supported formats: PDF (`.pdf`), Word (`.docx`), Excel (`.xlsx`).**

**Language:** [中文说明 (README.zh.md)](README.zh.md)

## About

| | |
|--|--|
| **Product** | DocviewWPF |
| **Platform** | Windows x64 · .NET Framework 4.8 · WPF |
| **Supported file formats** | **`.pdf`** · **`.docx`** · **`.xlsx`** |
| **Not supported** | e.g. `.doc`, `.xls`, `.ppt`/`.pptx`, images-as-documents, plain text (unless added later) |

Open via menu, toolbar, drag-and-drop, or command-line path argument. One tab per file; reopening focuses the existing tab.

## Features

### Formats (detail)

| Extension | Format | Highlights |
|-----------|--------|------------|
| **`.pdf`** | PDF | Lazy page render (pdfium), continuous scroll, zoom / fit width / fit page, 90° rotate (`[` `]`), text select & copy, image context menu, **pro edit window** (object-level edit, vector save when possible) |
| **`.docx`** | Word Open XML | Read-only flow layout; TOC (incl. SDT), numbering / bullets, margins |
| **`.xlsx`** | Excel Open XML | Virtualized grid (`OnRender`); freeze panes, filters, column/row resize, wrap, simple edit & save |

### UI & navigation

- Multi-tab: one tab per file; reopening focuses existing tab
- Tab drag: reorder, tear off to a new window, drop back to merge
- Outline sidebar: PDF bookmarks / DOCX headings; filter & highlight
- Sumatra-style chrome: custom title/toolbar, drag title bar, maximize without covering the taskbar, `F` fullscreen

### Session

- Optional restore of last open tabs
- Per-file reading progress (scroll, zoom, page/sheet)
- Recent files menu
- Single-instance: second launch activates existing window and forwards open paths
- **UI language**: Chinese / English / Japanese / Korean (Help → Language, or Settings)

## Requirements

- Windows x64
- .NET Framework 4.8
- Visual Studio / MSBuild (or `dotnet` SDK with net48 targeting pack)

## Build & run

Preferred (local helper scripts, if available):

```bat
slx DocviewWPF
slr DocviewWPF
```

Or:

```bat
dotnet restore DocviewWPF\DocviewWPF.csproj
dotnet build DocviewWPF\DocviewWPF.csproj -c Release
```

Output:

```text
DocviewWPF\bin\Release\net48\DocviewWPF.exe
```

Solution file: `DocviewWPF.slnx`.

## Configuration

User settings and session data live under the current user’s application data folder (not in the repo):

```text
%LocalAppData%\DocviewWPF\
  settings.json
  session.json
  reading_progress.json
  recent.json
```

Runtime logs (when running from build output):

```text
DocviewWPF\bin\Release\net48\logs\docviewwpf_YYYYMMDD.log
```

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
  App.xaml(.cs)
  MainWindow.xaml(.cs)
  Core/           # settings, session, theme, log, single-instance
  Viewers/        # PDF / DOCX / XLSX viewers & PDF pro editor
  Assets/         # icon
README.md
README.zh.md
CHANGELOG.md
```

## Dependencies (NuGet)

- PDFtoImage (pdfium + SkiaSharp)
- DocumentFormat.OpenXml
- System.Memory / System.Runtime.CompilerServices.Unsafe

## License / notes

Internal / personal project tooling unless stated otherwise. Sample documents and local machine paths must not be committed (see `.gitignore`).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
