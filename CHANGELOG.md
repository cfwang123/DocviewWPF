# Changelog

All notable changes to **DocviewWPF** are documented here.  
Format inspired by [Keep a Changelog](https://keepachangelog.com/).  
Versions follow [Semantic Versioning](https://semver.org/) when tagged.

---

## [Unreleased]

## [1.0.2] - 2026-08-07

### Added

- **MD 预览双引擎**：可切换 **WebView2 HTML**（默认，Mermaid/语法高亮）与 **纯 WPF FlowDocument**（`MdFlowBuilder`，无浏览器内核）；工具栏地球/文档图标切换，系统参数可设默认；侧预/目录/滚动同步/查找均支持两引擎。
- **标题栏系统参数按钮**：标签栏「+」右侧齿轮图标，打开系统参数（同 Ctrl+,）。
- **发布打包脚本**：`node scripts/pack-release.js [--build]` → `release/DocviewWPF_x.x.x.7z`。
- **图片预览**：`.png` / `.jpg` / `.gif` / `.bmp` / `.webp` 等；光标中心缩放、拖拽平移、适应、90° 旋转。
- **CSV / TSV 预览**：`CsvViewer` + 虚拟网格；RFC4180 引号/逗号；首行冻结表头；只读。
- **浏览器标签**：Edge WebView2 内核 + 地址栏；前进/后退/刷新；新窗口请求可开新标签。
- **命令行标签**：ConPTY 真伪终端 + VT 渲染；cmd / PowerShell；支持 TUI；工作目录与缩放。
- **代码/文本扩展**：常见 `.py` / `.cs` / `.js` / `.ts` / `.json` / `.xml` 等走 `TextViewer`（与 TXT 共用）。
- **打印**：Ctrl+P / 菜单 / 工具栏；`PrintVisual` 按可打印区域缩放，避免整页裁切。
- **MD 导出**：文件 → 导出 → Markdown 导出 HTML / PDF；图片另存为（png/jpg/bmp）。
- **状态栏编码**：右下角显示 UTF-8 / GB18030 等；点击切换并从磁盘按新编码重载（脏文件确认）。
- **行号**：代码/TXT **预览**每行前缀行号，随正文滚动、缩放同步。
- **左右分屏**：查看 → 左右分屏（Ctrl+\）；右侧 ComboBox 选已开文件；独立 Viewer 实例。
- **最近关闭标签**：`ClosedTabsStore`（最多 20）；Ctrl+Shift+T / 文件 → 重新打开；写入 session。
- **文件夹工作区**：左侧多 Tab（文件夹浏览 + 目录 TOC）；打开文件夹 / 双击打开 / 懒加载；工作区路径随 session 恢复；标题栏悬停新建文件/文件夹、刷新、全部折叠。
- **书签栏**：Chrome 风文件/文件夹/分组；Ctrl+D 添加/编辑；Ctrl+Shift+B 显隐；拖入文件/文件夹；`bookmarks.json`。
- **自动更新**：从 GitHub Releases 检查/下载；`--apply-update` 无 UI 替换安装目录；进度窗。
- **外部文件自动刷新**：磁盘上文档被其它程序修改后已打开标签自动重载；本地有未保存修改则确认；自身保存不误触发。
- **MD 预览代码块折叠**：围栏超过约 15 行默认折叠，顶栏「显示全部 / 收起」（Mermaid 不折叠）。
- **HTML `<img>`**：块级标签，解析 `width`/`height` 与 style 尺寸。
- **HTML `<details>`/`<summary>`**：嵌套、`open` 默认展开、正文再解析 Markdown；预览可折叠。
- **MD 粘贴图片**：编辑模式粘贴截图/图片文件到文档旁 `images/`，插入 `![](images/…)`。
- **编辑按钮按下态**：TXT/MD/XLSX「编辑」Toggle；编辑中浅蓝底+蓝描边。

### Changed

- **双击图片预览**：对齐**文档区**（`pcontent` 屏幕矩形）的无边框弹层——半透明背景、fit 居中；**顶层 Window** 避免 WebView2 HWND 盖住 WPF 层；复制/另存（Ctrl+C/S）；Esc 关闭。

### Fixed

- **MD 预览图片右键菜单**：复制图片、复制为文件（资源管理器粘贴）、保存图片（WebView2 / WPF 双引擎）；剪贴板写入加固。
- **MD 表格短表不撑满**：列 need 总和小于页宽时表宽收缩为内容宽（HTML `width:px` / WPF Pixel 列）；超出才铺满并让长列换行。
- **MD HTML 表格短列误换行**：`overflow-wrap` 改为 `normal`；项目号等无空格短列加 `nowrap`；列宽改用 FormattedText 实测并钉死短列，避免 `E2026108` 被拆成两行。
- **MD WPF 预览表格裁切**：列宽改为 **Star 比例列**（短列钉死、长列按 need 分剩余，总宽 100% 页宽），单元格内自动换行，不再撑出屏幕或走横向滚动。
- **MD 长文档编辑卡顿**：视口优先分片高亮；段落行缓存（`paragraphat` O(1)）；大围栏只重绘可见区；Typora 键入不再每次全文 TOC；conceal 换行只切光标行；行高亮防抖随行数自适应。
- **命令行标签输入无反应 / 易卡死 / 中文显示错乱**：
  - ConPTY 对齐官方 MiniTerm；短按键 `WriteSync` 直写管道
  - **中文输入法**：`ImmGetOpenStatus` 打开时字母/空格/回车交 IME；`ImmSetCompositionWindow` 候选窗跟光标；组字串光标处预览；确认后 UTF-8 写入 PTY
  - 绘制按单元格网格；CJK 宽字符裁剪
  - 自检：`DocviewWPF.exe --selftest-console`

---

## [1.0.1] - 2026-07-31

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
- Lightweight toolbar icons; caption drag / double-click maximize; maximize within work area; `F` fullscreen
- PDF page rotate 90° with `[` `]`; high-DPI rendering
- Shared-read / temp-copy open for locked files; async loading UI
- Native DLLs only under `x64\` / `x86\` (root copies stripped; `NativeBootstrap` loads by architecture)

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
- Tab session aggregates all live main windows; setting “restore last tabs”
- User data directory name aligned with product (`DocviewWPF`)
- Version **1.0.1** (Release)

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
- Offline preview assets (`Assets/mermaid.min.js`, `highlight.min.js`, `highlight-github.min.css`) should ship with the build output.
- Update check uses GitHub Releases API (`cfwang123/DocviewWPF`); network may require proxy in restricted environments.
