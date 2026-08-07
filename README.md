# DocviewWPF

Windows multi-tab document app (.NET Framework 4.8 WPF) for **PDF / DOCX / XLSX / CSV / TXT / Markdown / images**, plus **browser** and **console** tabs. Core features: continuous PDF reading, **PDF pro edit** & **annotations**, large-sheet **XLSX** virtual grid, DOCX structure view, **TXT/MD** preview & edit (Mermaid, syntax highlight, engineer modes), Chrome-style tabs, folder workspace, bookmarks, single-instance, session restore, auto-update, four UI languages (zh/en/ja/ko).

**Language:** [中文说明 (README.zh.md)](README.zh.md)

## Features

Highlights that set DocviewWPF apart from a plain PDF-only reader or office-only apps:

- **Multi-format tabs** — open **PDF / DOCX / XLSX / CSV·TSV / TXT·code / MD / images** side by side; also **browser** (WebView2) and **console** (ConPTY + VT, cmd/PowerShell) tabs.
- **PDF Pro Editor** — dedicated window for page-object edit: select / marquee multi-select, drag with live ghost preview, text replace with embedded system fonts (CJK-safe), images, whiteout, shapes; undo/redo; vector-oriented save when pdfium allows.
- **PDF annotations** — pen / highlight / text / shapes; marquee multi-select & group; eraser; color picker (Word-style + HSV); sidecar JSON + burn-in save-as PDF.
- **XLSX built for large sheets** — virtualized `OnRender` grid, freeze panes, filters, column/row resize, wrap, Excel-like simple edit (merge, align, font, colors, arrow/Tab, block select).
- **CSV / TSV** — virtual grid preview (RFC4180 quotes; frozen header row; read-only).
- **DOCX reading that keeps structure** — flow layout with outline (incl. SDT fields), numbering/bullets, margins — without launching Word.
- **TXT / Markdown** — WebView2 HTML preview (**Mermaid**, fenced **syntax highlight**), switchable to **pure WPF FlowDocument** preview (no browser runtime); toolbar edit + save; paste images into `images/`. MD engineer mode: source highlight + conceal / side-by-side sync / Typora-style live preview; outline; Ctrl+click links; export HTML/PDF. See `ref/sample.md` when present.
- **Images** — pan / zoom (cursor-centered), fit, rotate 90° steps.
- **Chrome-style tabs** — reorder with animation, tear off into a new window, drop back to merge; **reopen closed tab** (Ctrl+Shift+T).
- **Split view** — left/right dual pane (Ctrl+\); right side picks an already-open file.
- **Folder workspace** — VS Code–style left explorer + outline TOC tabs; open folder, lazy expand, session restore of workspace path.
- **Bookmarks bar** — Chrome-style file/folder/group bookmarks (Ctrl+D, Ctrl+Shift+B); drag files onto the bar.
- **True single-instance** — second launch activates the existing process and forwards open paths.
- **Session & reading progress** — optional restore of last tabs + closed-tab stack + workspace; per-file scroll/zoom/page/sheet; MD mode & outline open/close memory.
- **External file watch** — reload when the file changes on disk (prompt if dirty).
- **Print** — Ctrl+P / menu / toolbar (`PrintVisual`, scaled to printable area).
- **Encoding status** — status-bar encoding for text/MD; click to switch and reload.
- **Find with on-screen highlight** — Ctrl+F; all matches in view highlighted (current vs others), per-tab search text.
- **Auto-update** — check GitHub Releases and apply in-place (Help → Check for updates).
- **UI in four languages** — 中文 / English / 日本語 / 한국어 (Help → Language, or Settings).
- **Lightweight chrome** — custom title bar & icon toolbar, maximize within work area (no taskbar cover), `F` / F11 fullscreen.

Also: shared-read open for locked files, recent files, themes, UI font size, Markdown tab width & heading auto-number settings.

## Requirements

- Windows x64
- .NET Framework 4.8
- **Microsoft Edge WebView2 Runtime** (MD preview, browser tabs, MD export PDF; usually already installed with Edge)
- Visual Studio / MSBuild (or `dotnet` SDK with net48 targeting pack)

## Build & run

```bat
dotnet restore DocviewWPF\DocviewWPF.csproj
dotnet build DocviewWPF\DocviewWPF.csproj -c Release
```

Output: `DocviewWPF\bin\Release\net48\DocviewWPF.exe`  
Solution: `DocviewWPF.slnx`

### Package (7z)

```bat
node scripts/pack-release.js --build
```

Creates `release/DocviewWPF_x.x.x.7z` (version from csproj `<Version>`; `release/` is gitignored).

Self-tests (exit 0 = pass):

```bat
DocviewWPF.exe --selftest-md
DocviewWPF.exe --selftest-typora-click
```

## Configuration

```text
%LocalAppData%\DocviewWPF\
  settings.json
  session.json
  reading_progress.json
  recent.json
  bookmarks.json
```

Runtime logs (from build output): `DocviewWPF\bin\Release\net48\logs\docviewwpf_YYYYMMDD.log`

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+O | Open |
| Ctrl+W | Close tab |
| Ctrl+Shift+T | Reopen last closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+\ | Toggle left/right split view |
| Ctrl+D | Add / edit bookmark |
| Ctrl+Shift+B | Toggle bookmarks bar |
| Ctrl++ / Ctrl+- / Ctrl+0 | Zoom in / out / actual size |
| Ctrl+wheel | Zoom |
| Ctrl+P | Print |
| F / F11 | Fullscreen |
| F4 | Toggle outline / side panel |
| Esc | Exit fullscreen / clear selection |
| Ctrl+F | Find |
| Ctrl+C | Copy selection |
| Ctrl+Click | PDF internal link jump (URI opens browser) |
| Alt+← / Alt+→ | PDF nav history: back / forward |
| Ctrl+S | Save (edit modes) |

## Project layout

```text
DocviewWPF.slnx
DocviewWPF/
  DocviewWPF.csproj
  App.xaml(.cs) · MainWindow.xaml(.cs)
  Core/           # settings, session, bookmarks, updater, theme, log, i18n, single-instance
  Viewers/        # PDF / DOCX / XLSX / CSV / TXT / MD / image / browser / console + PDF edit & annot
  Viewers/Terminal/  # ConPTY + VT terminal
  Assets/         # app icon; offline mermaid.min.js / highlight.js
README.md · README.zh.md · CHANGELOG.md
```

## Dependencies (NuGet)

- PDFtoImage (pdfium + SkiaSharp)
- DocumentFormat.OpenXml
- Microsoft.Web.WebView2
- Emoji.Wpf
- System.Memory / System.Runtime.CompilerServices.Unsafe

Offline preview assets under `DocviewWPF/Assets/`: `mermaid.min.js`, `highlight.min.js`, `highlight-github.min.css` (CDN fallback if missing).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
