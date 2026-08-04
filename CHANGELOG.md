# Changelog

All notable changes to **DocviewWPF** are documented here.  
Format inspired by [Keep a Changelog](https://keepachangelog.com/).  
Versions follow [Semantic Versioning](https://semver.org/) when tagged.

---

## [Unreleased]

### Added

- **外部文件自动刷新**：磁盘上的文档被其它程序修改后，已打开标签自动重新加载并刷新显示；若本地有未保存修改，则弹出确认（是否放弃本地修改并加载外部文件）。自身保存不会误触发重载。
- **MD 预览代码块折叠**：围栏代码超过 15 行时默认只显示约 15 行，顶栏提供「显示全部 / 收起」切换（Mermaid 不折叠）。

### Changed

- **MD 进入编辑默认纯代码**（不再默认 Typora）；阅读进度仍可恢复上次布局。
- **MD 纯代码 Ctrl+Z**：就地按行差分修补（不换整篇 Document、不做整篇重绘），去掉约 0.3s 卡顿；复杂块才走分片上色回退。
- **MD 纯代码大块剪切/粘贴**：拦截 RTB 默认剪贴板，按字符串批量写回 + 分片语法上色，避免整篇同步高亮卡死。
- **MD 纯代码大选区删除**：Delete/Backspace 拦截 ≥12 行选区，字符串批量删 + 整换 Document；跳过全文 sync 与 RTB 逐段拆除（此前删 1800 行可卡 2–3s）。
- **关闭时未保存提示**：关窗/关全部/关标签时，对每个有修改的文件逐个询问是否保存（是/否/取消）。
- **MD 纯代码/Typora 代码块语法高亮**：围栏结构变化时整篇重建；编辑代码块时整块重绘；增强关键字/类型/字符串/注释着色（C#/JS/Python/SQL/Bash/Go/Rust 等）。
- **MD 纯代码 / 侧预编辑**：改为 vim 风格简易编辑——仅保留文字颜色、粗斜体、超链接；去掉代码底、标题放大、引用边框、高亮底、删除线装饰、内嵌图预览等块级样式。Typora 模式不变。
- **Markdown 预览改为 WebView2 渲染**（默认纯预览 + 编辑「侧预」右侧）；纯代码 / Typora 模式不变。`MdHtmlBuilder` 生成带 `data-line` 的 HTML；本地图经虚拟主机 `md.assets` 映射。

### Changed

- **MD 预览右键菜单**：去掉浏览器默认项（返回/刷新/另存为/打印等），仅保留「导出 PDF」「导出 HTML」。

### Fixed

- **MD 本地文件链接 / 图片**：同时支持 URL 编码（`05%20远程更新.png`）与未编码（`05 远程更新.png`）路径，自动检测；优先选用磁盘上存在的文件。图片预览、侧预 WebView、点击打开均走同一解析。
- **未处理异常窗**：改为 SerialTool 风格（类型/消息/堆栈、可复制、完整栈开关）；连续报错同窗追加滚动，不再 MessageBox 互斥丢弃。
- **Typora 点击折叠表**：进出表的 Document 重建推迟到 MouseDown 结束后，避免 `TextSelection.TextView` NullReferenceException。
- **Typora conceal 切行/防抖高亮**：切换标记或 `LINE_HL` 重绘后用逻辑偏移恢复光标，避免跳到行首。
- **Typora 拖选文字**：选区非空或按住左键时推迟 conceal / 禁止强制复位光标，避免选区被掐掉。
- **Typora 编辑链接 href**：`](href)` 不再整段塞进 marker Tag；导出时已编辑的标记 Run 以 Text 为准，避免改 `-` 等字符后 `](…)` 重复。
- **锚点/目录跳转**：源码与预览滚到目标时留出约 20px 顶边距，避免标题贴顶被裁切。
- **Typora 分隔线 / 引用**：`---` conceal 时显示全宽横线（不再整行消失）；引用行加左侧竖线、边距与浅底。
- **预览本地图**：`md.assets` 映射扩到文档与图片的公共父目录，支持 `../images/...` 等目录外相对路径。
- **侧预编辑大表格**：滚动后被拽回光标处——去掉滚动时按光标块硬对齐；WebView 异步滚动加抑制窗，避免预览回传反推源码；重建预览时跟源码滚动比例。
- **WebView 表格列宽**：接回 `MdTableLayout.AllocateColumnsDip`（mdview：短列钉死、长列分剩余）；去掉错误的 `display:block` 表布局。
- **WebView 表格横向滚动**：铺满预览的表不再因 `col` 的 `min-width:px` 略超视口而出现横向滚动条（改纯百分比 + `max-width:100%`）。
- **MD 预览硬换行**：段落/引用/列表续行保留源码 `\n` → `softbr`/`<br/>`（对齐 mdview，不再用空格拼接）。
- **MD Mermaid**：` ```mermaid ` 围栏在预览中渲染图表（本地 Assets/mermaid.min.js，失败则回退 CDN）。
- **MD 代码块语法高亮**：预览围栏按语言用 highlight.js 着色（本地 Assets，失败则回退 CDN）；主题 github。
- **MD 阅读位置**：关闭/切换标签时记住预览滚动比例，再次打开恢复；进出编辑/切换 Typora·侧预·纯代码时保持相对位置。
- **MD 目录滚动同步**：预览/源码滚动时左侧目录自动高亮当前标题（对齐 PDF/DOCX，防抖）。
- **Typora 列表**：`-*+` conceal 时显示为 ●；源码里的 `●•○◦` 不 conceal。
- **MD 列表缩进**：按 mdview `indent_cols` 计算 Level；识别 `●•○◦` 等 Unicode 列表符；预览 `padding-left` 每级 25px。GFM 任务列表 `[ ]`/`[x]` 用 CSS 方框渲染。设置项 **Markdown Tab 宽度**（默认 3）；打开时将围栏外 Tab 展成空格；编辑 Tab 键插空格。

### Added

- **HTML `<img>`**：支持块级标签，解析 `width`/`height` 属性与 `style="width:..px;height:..px;"`，预览按尺寸渲染。
- **HTML `<details>`/`<summary>`**：对齐 mdview（嵌套、`open` 默认展开、正文再解析 Markdown）；预览为可折叠 details。
- **Typora conceal 漏切**：折叠表后不用 TextRange 推光标行；切换行时扫一遍强制只保留光标行显示标记。
- **Typora conceal**：隐藏标记用零宽字符代替（不占字宽，光标仍对齐）；原文在 Tag，保存不丢。
- **MD 粘贴图片**：编辑模式下粘贴截图或图片文件，保存到文档旁 `images/`（有文件名则保留，否则时间戳），并插入 `![](images/…)`。
- **编辑按钮按下态**：TXT/MD/XLSX「编辑」改为 Toggle；编辑中浅蓝底+蓝描边，铅笔图标用强调色。
- **MD / 目录侧栏记忆**：`reading_progress.json` 按文件记录 MD 模式（预览 / 纯代码 / Typora / 侧预）与目录开闭；会话恢复或再次打开时还原（无记录时目录仍跟全局默认）。
- **TXT / Markdown 预览与编辑**
  - 打开 `.txt` / `.md`（及 `.log` / `.markdown`）默认**预览**；工具栏铅笔进入编辑，`Ctrl+S` 保存
  - **Markdown 工程模式**：源码（语法高亮 + vim 式 conceal：光标行显示标记、其它行隐藏 `**`/`#`/链接括号等）/ 侧栏同步预览 / 实时边编辑边渲染（Typora 风）
  - 预览支持标题/列表/引用/围栏代码（highlight.js）/GFM 表/链接/Mermaid；目录侧栏；编码自动探测 UTF-8/BOM/GB18030
  - 自检：`DocviewWPF.exe --selftest-md`
  - Typora 本体闭源，说明见 `ref/README.md`；行为参考本地 `mdview` 插件
- **PDF 标注**：手型仅 pan；框选多选/成组解组；橡皮（点擦笔迹 / 整笔删除）；文本自动变宽；JSON 旁路 + 另存烧入 PDF；选色仿 Word + HSV
- **工具栏「目录」按钮**：切换目录侧栏显示（与菜单/F4 同步；按下态表示已显示）
- **PDF 书内链接**：`Ctrl+点击` 跳转页内 GoTo 目标（支持 XYZ 置顶）；URI 链接用系统默认浏览器打开；按住 Ctrl 悬停链接显示手型光标
- **PDF 跳转历史**：目录 / 书内链接 / 页码跳转记入历史；`Alt+←` 后退、`Alt+→` 前进
- **XLSX 双击自适应列宽**：双击列标题右边线，按中/英文字符宽简单估算列内容最大宽度并设列宽
- **文件菜单**：用系统应用打开当前文件（默认关联程序）

### Fixed

- 仅 1 个 Tab 时禁止拖出为独立窗口（原先会拆窗留下空窗 / 状态异常）
- XLSX 拖末列列宽卡顿数秒：拖动改为增量更新 colX；松手只重算依赖该列的换行行；拖动中跳过 ScrollChanged 二次全量重绘
- XLSX 打开后整表空白：`applytablesize` 在 `outer.Width/Height` 为 NaN 时未写入尺寸（Extent/surface 未撑开）；布局完成后再钉视口
- XLSX 最大化卡顿约 1s：长表不再随视口撑大超高 `outer`；尺寸变化只钉视口；Extent 变化不再递归 `applytablesize`
- XLSX 列宽取消 800 上限；支持点选/拖选/Shift 连续选整行、整列（列头/行号）；左上角全选
- DOCX 段落对齐继承样式 `jc`（如 Title 居中）；原先只读段上 jc，标题样式居中丢失
- DOCX 样式默认字号/加粗（style `rPr`，如 Title `sz=32`→16pt）；原先 run 无 sz 时落成 10.5pt 显得偏小

- PDF 在高 DPI / 换机后略糊：页边框不再挤占内容区；全清图 1:1 用最近邻贴图；声明 PerMonitorV2；DPI 变化时清缓存重渲

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
