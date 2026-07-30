# Changelog

All notable changes to **DocviewWPF** are documented here.  
Format inspired by [Keep a Changelog](https://keepachangelog.com/).  
Versions follow [Semantic Versioning](https://semver.org/) when tagged.

---

## [Unreleased]

### Added

- **UI language switch**: 中文 / English / 日本語 / 한국어  
  - Menu: Help → Language  
  - Settings: language combo (preview on change, save on OK)  
  - Persisted as `language` in settings.json (`zh` / `en` / `ja` / `ko`)
- **PDF pro editor window** (object-level editing)
  - Select / marquee multi-select / group move with ghost preview while dragging
  - Edit text via safe replace (delete old object + embed system font; avoids CID `SetText` / `GetFontData` crashes)
  - Insert text / image / whiteout / filled rectangle
  - Scale, rotate, delete, duplicate; page insert / delete / rotate
  - Undo / redo (document snapshot stack)
  - Vector save path: `GenerateContent` → `ImportPages` → `SaveAsCopy` (when supported)
  - Font size from glyph box / char width (avoids 1pt invisible text when matrix scale ≈ 1)
  - Prefer **STZhongsong (华文中宋)** and related system fonts for CJK titles
  - Font combobox shows only displayable names (no mojibake BaseFont)
- **XLSX simple edit mode**: merge/align/font/colors/wrap, save; icon toolbar; Excel-like arrow/Tab navigation; caret Left/Right, Shift+Enter newline, Shift+arrows block select
- **PDF viewer polish**: soft multi-tile / soft single-tile zoom; layout-only zoom pin (cleared on user scroll)
- Find highlight: Ctrl+F focuses search; all on-screen matches highlighted (PDF / DOCX / XLSX)
- Find start: first match from current viewport downward; per-tab search text
- XLSX filter UI like Excel (header filter + sort, cancel sort)
- Single-instance: second process activates existing window and forwards open paths
- Tab drag (Chrome-style): reorder with animation, tear-off window, drop to merge
- PDF outline jump uses XYZ when available (title to top of viewport)
- Reading progress for PDF / DOCX / XLSX
- XLSX freeze panes, column filters (check / contains / date)
- DOCX SDT TOC, numbering / bullets, margins and hanging indent
- Sumatra-style toolbar icons; caption drag / double-click maximize; maximize within work area; `F` fullscreen
- PDF page rotate 90° with `[` `]`; high-DPI rendering
- Shared-read / temp-copy open for locked files; async loading UI

### Fixed

- Session restore wiped by empty session write on multi-window exit
- Outline indent too wide
- Maximize covering taskbar
- UI freeze while loading large documents (less UI pump, background prepare)
- PDF pro editor: crash on enter (`GetFontSize` AV) — use matrix / bounds size estimate
- PDF pro editor: crash on apply text (`GetFontData` AV on some embedded fonts) — disable embed font dump
- PDF pro editor: garbled property text — char-level Unicode assignment to objects
- PDF pro editor: font combobox mojibake — map to system font display names only
- PDF pro editor: drag only moved blue chrome — ghost preview follows mouse; commit on mouse up

### Changed

- **Project rename**: `DocView` → **DocviewWPF**
  - Output: `DocviewWPF.exe`
  - Namespace / product: `DocviewWPF`
  - Settings folder: `%LocalAppData%\DocviewWPF\`
  - Build: `slx DocviewWPF` / `slr DocviewWPF`
- Tab session aggregates all live main windows; setting “restore last tabs”
- User data directory name aligned with product (`DocviewWPF`)

---

## [0.1.0] - Initial

### Added

- Multi-tab reader for PDF / DOCX / XLSX
- PDF lazy render, outline sidebar, find, selection copy
- Drag-drop and command-line open; recent files
- System settings (theme, UI font size, restore tabs, etc.)

---

## Notes for packagers / contributors

- Do not commit secrets, API keys, passwords, or absolute machine paths (see `.gitignore`).
- Do not commit `bin/`, `obj/`, `tmp/`, personal sample PDFs/DOCX/XLSX, or `*.lnk` with local targets.
- Prefer Release configuration for shipping builds.
