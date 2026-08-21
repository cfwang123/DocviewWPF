# DocviewWPF

Windows 多标签文档应用（.NET Framework 4.8 WPF），支持 **PDF / DOC·DOCX / XLS·XLSX / CSV / TXT / Markdown / 图片**，并提供 **浏览器** 与 **命令行** 标签。核心能力：快速打开与 PDF 连续阅读、**PDF 专业编辑** 与 **标注**、大表 **XLSX** 虚拟网格与简单编辑、DOC/DOCX 结构阅读、**TXT/MD 预览与编辑**（Mermaid、语法高亮、工程模式）、Chrome 式标签、文件夹工作区、书签栏、单实例、会话恢复、自动更新、四语界面（中/英/日/韩）。

**English:** [README.md](README.md)

## Features（功能特性）

相对「只读 PDF」类工具或「只跑 Office」的程序，DocviewWPF 更突出的能力：

- **多格式同窗** — 同一应用内多标签打开 **PDF / DOC·DOCX / XLS·XLSX / CSV·TSV / TXT·代码 / MD / 图片**；另支持 **浏览器**（Edge WebView2）与 **命令行**（ConPTY + VT，cmd/PowerShell，可跑 TUI）。
- **旧版 Office 只读** — **`.doc` / `.xls`**（NPOI）走现有 DOCX / XLSX 查看器，无需启动 Word/Excel。
- **PDF 专业编辑** — 独立窗口做页对象级编辑：点选 / 框选多选、拖动幽灵预览跟手、中文安全改字（系统字体嵌入，避免 CID 崩溃）、插图 / 遮盖 / 矩形、撤销重做；在 pdfium 支持时尽量矢量保存。
- **PDF 标注** — 手写/高亮/文本/形状；框选多选与成组；橡皮；选色仿 Word + HSV；旁路 JSON + 另存烧入 PDF。
- **大表 XLS / XLSX** — 自绘虚拟化网格（`OnRender`）、冻结窗格、筛选、列宽行高、换行，以及接近 Excel 的简单编辑（合并、对齐、字体颜色、方向键/Tab、块选）；**`.xls`** 只读打开。
- **CSV / TSV** — 虚拟网格预览（RFC4180 引号；首行冻结表头；只读）。
- **DOC / DOCX 结构阅读** — DOCX 流式排版 + 目录（含 SDT）、编号/项目符、页边距；旧 **`.doc`** 只读打开，无需启动 Word。
- **TXT / Markdown** — 默认 **WebView2 HTML 预览**（**Mermaid**、围栏 **语法高亮**），可切换 **纯 WPF FlowDocument 预览**（无浏览器内核）；工具栏进入编辑并保存；编辑态可**粘贴图片**到 `images/`。MD 工程模式：源码高亮与 conceal、侧栏同步预览、Typora 风单栏编辑；标题目录、链接 Ctrl+点击；导出 HTML/PDF。演示见 `ref/sample.md`（本地参考目录）。
- **图片预览** — 光标为中心缩放、拖拽平移、适应、90° 旋转。
- **Chrome 式标签** — 拖动排序（带动画）、拖出即成独立窗口、拖回合并；**重新打开关闭的标签**（Ctrl+Shift+T）。
- **分屏** — 左右分屏（Ctrl+\）；右侧从已开文件中选择。
- **文件夹工作区** — 左侧 VS Code 风资源管理器 + 目录 TOC 双 Tab；打开文件夹、懒加载展开；工作区路径随会话恢复。
- **书签栏** — Chrome 风文件/文件夹/分组书签（Ctrl+D、Ctrl+Shift+B）；可拖文件到书签栏。
- **真单实例** — 二次启动只激活已有进程并转发打开路径，不堆多个主窗。
- **会话与阅读进度** — 可选恢复上次标签 + 最近关闭栈 + 工作区；PDF / DOCX / XLSX 记滚动与缩放；**MD 记预览/编辑模式**；有目录的文档**按文件记目录开闭**。
- **外部文件自动刷新** — 磁盘被其它程序修改后自动重载（本地有未保存修改则确认）。
- **打印** — Ctrl+P / 菜单 / 工具栏（`PrintVisual`，按可打印区域缩放）。
- **状态栏编码** — 文本/MD 显示编码；点击切换并按新编码重载。
- **查找全屏高亮** — Ctrl+F；视口内全部匹配高亮（当前与其它区分），各 Tab 独立搜索框。
- **自动更新** — 从 GitHub Releases 检查并应用更新（帮助 → 检查更新）。
- **四语界面** — 中文 / English / 日本語 / 한국어（帮助 → 语言，或系统参数）。
- **命令行标签** — ConPTY + xterm 风 VT（配色 / truecolor / BCE，适配 nvim 等 TUI）；实心不闪光标；中文 IME 组字预览跟光标；cmd / PowerShell；子进程 `TERM=xterm-256color`、`COLORTERM=truecolor`。
- **轻量自绘壳** — 自绘标题栏与图标工具栏，最大化不挡任务栏，`F` / F11 全屏。

另有：锁定文件共享打开、最近文件、主题与界面字号、Markdown Tab 宽度与标题自动编号等。

## 环境要求

- Windows x64
- .NET Framework 4.8
- **Microsoft Edge WebView2 Runtime**（MD 预览、浏览器标签、MD 导出 PDF；一般已随 Edge 安装）
- Visual Studio / MSBuild（或带 net48 目标包的 `dotnet` SDK）

## 编译与运行

```bat
dotnet restore DocviewWPF\DocviewWPF.csproj
dotnet build DocviewWPF\DocviewWPF.csproj -c Release
```

输出：`DocviewWPF\bin\Release\net48\DocviewWPF.exe`  
解决方案：`DocviewWPF.slnx`

### 打包发布（7z）

```bat
node scripts/pack-release.js --build
node scripts/pack-release.js --build --publish   # 编译 + 打包 + 发布到 GitHub Releases
node scripts/pack-release.js --publish-only      # 仅发布已有 7z（需已 gh auth login）
```

生成：`release/DocviewWPF_x.x.x.7z`（版本取自 csproj `<Version>`；`release/` 已 gitignore，不上传）。Release 说明自动取自 `CHANGELOG.md` 对应版本节。

自检（退出码 0=通过）：

```bat
DocviewWPF.exe --selftest-md
DocviewWPF.exe --selftest-typora-click
DocviewWPF.exe --selftest-console
```

## 配置位置

```text
%LocalAppData%\DocviewWPF\
  settings.json
  session.json
  reading_progress.json
  recent.json
  bookmarks.json
```

运行日志（从构建输出启动时）：`DocviewWPF\bin\Release\net48\logs\docviewwpf_YYYYMMDD.log`

## 快捷键

| 快捷键 | 作用 |
|--------|------|
| Ctrl+O | 打开 |
| Ctrl+W | 关闭当前 Tab |
| Ctrl+Shift+T | 重新打开关闭的标签 |
| Ctrl+Tab / Ctrl+Shift+Tab | 下一 / 上一标签 |
| Ctrl+\ | 左右分屏开关 |
| Ctrl+D | 添加 / 编辑书签 |
| Ctrl+Shift+B | 显示 / 隐藏书签栏 |
| Ctrl++ / Ctrl+- / Ctrl+0 | 放大 / 缩小 / 实际大小 |
| Ctrl+滚轮 | 缩放 |
| Ctrl+P | 打印 |
| F / F11 | 全屏 |
| F4 | 切换目录 / 侧栏 |
| Esc | 退出全屏 / 取消选择 |
| Ctrl+F | 查找 |
| Ctrl+C | 复制选中 |
| Ctrl+点击 | PDF 书内链接跳转（URI 则打开浏览器） |
| Alt+← / Alt+→ | PDF 跳转历史：后退 / 前进 |
| Ctrl+S | 保存（编辑模式） |
| Alt+F4 | 关闭窗口（命令行标签内同样有效，不会被终端吞掉） |

### 命令行说明

- 点终端区域输入；工具栏（Shell / 工作目录）仍走应用快捷键。
- **中文输入法**：组字串显示在终端光标处；纯 ASCII 组字后回车可当 shell 命令提交（如 `dir`）。
- **TUI**（nvim 等）：尽量给够窗口尺寸；配色依赖 BCE + truecolor（已内置）。
- **Alt+F4** 始终关程序；单独 **F4** 在非命令行场景切换目录侧栏（命令行内功能键进 PTY，仅 Alt+F4 放行关窗）。

## 目录结构

```text
DocviewWPF.slnx
DocviewWPF/
  DocviewWPF.csproj
  App.xaml(.cs) · MainWindow.xaml(.cs)
  Core/           # 设置、会话、书签、更新、主题、日志、i18n、单实例
  Viewers/        # PDF / DOC·DOCX / XLS·XLSX / CSV / TXT / MD / 图片 / 浏览器 / 命令行 与 PDF 编辑·标注
  Viewers/Terminal/  # ConPTY + VT 终端
  Viewers/LegacyOfficeLoader.cs  # 旧 .doc / .xls
  Assets/         # 图标；离线 mermaid.min.js / highlight.js
README.md · README.zh.md · CHANGELOG.md
```

## TXT / Markdown 用法简述

1. 打开 `.txt` / `.md`（或拖放）→ 默认**预览**
2. 工具栏铅笔图标 → **编辑**；再点一次回到预览
3. Markdown 编辑时第二行工具栏切换布局：
   - **源码**：语法高亮；非光标行 conceal 标记（`**`、`#`、链接括号等）；链接 `Ctrl+点击` 打开
   - **侧栏**：左源码右预览，滚动/块同步
   - **实时**：上编辑下预览，边写边渲染（Typora 风）
4. `Ctrl+S` 保存（沿用原文件编码）
5. 文件 → 导出 → Markdown 导出 HTML / PDF；编辑态可粘贴截图到 `images/`

自检：`DocviewWPF.exe --selftest-md`

## 依赖（NuGet）

- PDFtoImage（pdfium + SkiaSharp）
- DocumentFormat.OpenXml
- NPOI / NPOI.HWPF（旧版 `.xls` / `.doc`）
- Microsoft.Web.WebView2
- Emoji.Wpf
- System.Memory / System.Runtime.CompilerServices.Unsafe

预览离线资源（`DocviewWPF/Assets/`）：`mermaid.min.js`、`highlight.min.js`、`highlight-github.min.css`（缺失时回退 CDN）。

## 变更记录

见 [CHANGELOG.md](CHANGELOG.md)。
