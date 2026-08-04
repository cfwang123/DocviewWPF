# DocviewWPF

Windows 多标签文档应用（.NET Framework 4.8 WPF），支持 **PDF / DOCX / XLSX / TXT / Markdown**。核心能力：快速打开与 PDF 连续阅读、**PDF 专业编辑**（对象级文字/图片）、大表 **XLSX** 虚拟网格与简单编辑、DOCX 结构阅读、**TXT/MD 预览与编辑**（MD 工程模式：源码 conceal / 侧栏同步 / 实时预览）、Chrome 式标签、单实例、会话恢复、四语界面（中/英/日/韩）。

**English:** [README.md](README.md)

## Features（功能特性）

相对「只读 PDF」类工具或「只跑 Office」的程序，DocviewWPF 更突出的能力：

- **多格式同窗** — 同一应用内多标签打开 **PDF / DOCX / XLSX / TXT / MD**，不是只能看 PDF。
- **PDF 专业编辑** — 独立窗口做页对象级编辑：点选 / 框选多选、拖动幽灵预览跟手、中文安全改字（系统字体嵌入，避免 CID 崩溃）、插图 / 遮盖 / 矩形、撤销重做；在 pdfium 支持时尽量矢量保存。
- **大表 XLSX** — 自绘虚拟化网格（`OnRender`）、冻结窗格、筛选、列宽行高、换行，以及接近 Excel 的简单编辑（合并、对齐、字体颜色、方向键/Tab、块选）。
- **DOCX 结构阅读** — 流式排版 + 目录（含 SDT）、编号/项目符、页边距，无需启动 Word。
- **TXT / Markdown** — 默认 **WebView2 HTML 预览**（**Mermaid**、围栏 **语法高亮**）；工具栏进入编辑并保存；编辑态可**粘贴图片**到 `images/`。MD 工程模式：源码高亮与 conceal、侧栏同步预览（WebView）、Typora 风单栏编辑；标题目录、链接 Ctrl+点击。演示见仓库根目录 `sample.md`。
- **Chrome 式标签** — 拖动排序（带动画）、拖出即成独立窗口、拖回合并。
- **真单实例** — 二次启动只激活已有进程并转发打开路径，不堆多个主窗。
- **会话与阅读进度** — 可选恢复上次标签；PDF / DOCX / XLSX 记滚动与缩放；**MD 记预览/编辑模式**；有目录的文档**按文件记目录开闭**。
- **查找全屏高亮** — Ctrl+F；视口内全部匹配高亮（当前与其它区分），各 Tab 独立搜索框。
- **四语界面** — 中文 / English / 日本語 / 한국어（帮助 → 语言，或系统参数）。
- **轻量自绘壳** — 自绘标题栏与图标工具栏，最大化不挡任务栏，`F` 全屏。

另有：目录侧栏、锁定文件共享打开、最近文件、主题与界面字号等。

## 环境要求

- Windows x64
- .NET Framework 4.8
- **Microsoft Edge WebView2 Runtime**（MD 预览用；一般已随 Edge 安装）
- Visual Studio / MSBuild（或带 net48 目标包的 `dotnet` SDK）

## 编译与运行

```bat
dotnet restore DocviewWPF\DocviewWPF.csproj
dotnet build DocviewWPF\DocviewWPF.csproj -c Release
```

输出：`DocviewWPF\bin\Release\net48\DocviewWPF.exe`  
解决方案：`DocviewWPF.slnx`

## 配置位置

```text
%LocalAppData%\DocviewWPF\
  settings.json
  session.json
  reading_progress.json
  recent.json
```

运行日志（从构建输出启动时）：`DocviewWPF\bin\Release\net48\logs\docviewwpf_YYYYMMDD.log`

## 快捷键

| 快捷键 | 作用 |
|--------|------|
| Ctrl+O | 打开 |
| Ctrl+W | 关闭当前 Tab |
| Ctrl+Tab / Ctrl+Shift+Tab | 下一 / 上一标签 |
| Ctrl++ / Ctrl+- / Ctrl+0 | 放大 / 缩小 / 实际大小 |
| Ctrl+滚轮 | 缩放 |
| F | 全屏 |
| Esc | 退出全屏 / 取消选择 |
| Ctrl+F | 查找 |
| Ctrl+C | 复制选中 |
| Ctrl+点击 | PDF 书内链接跳转（URI 则打开浏览器） |
| Alt+← / Alt+→ | PDF 跳转历史：后退 / 前进 |
| Ctrl+S | 保存（编辑模式） |

## 目录结构

```text
DocviewWPF.slnx
DocviewWPF/
  DocviewWPF.csproj
  App.xaml(.cs) · MainWindow.xaml(.cs)
  Core/           # 设置、会话、主题、日志、i18n、单实例
  Viewers/        # PDF / DOCX / XLSX / TXT / MD 与 PDF 专业编辑
  Assets/
README.md · README.zh.md · CHANGELOG.md · ref/
```

## TXT / Markdown 用法简述

1. 打开 `.txt` / `.md`（或拖放）→ 默认**预览**
2. 工具栏铅笔图标 → **编辑**；再点一次回到预览
3. Markdown 编辑时第二行工具栏切换布局：
   - **源码**：语法高亮；非光标行 conceal 标记（`**`、`#`、链接括号等）；链接 `Ctrl+点击` 打开
   - **侧栏**：左源码右预览，滚动/块同步
   - **实时**：上编辑下预览，边写边渲染（Typora 风）
4. `Ctrl+S` 保存（沿用原文件编码）

自检：`DocviewWPF.exe --selftest-md`

## 依赖（NuGet）

- PDFtoImage（pdfium + SkiaSharp）
- DocumentFormat.OpenXml
- System.Memory / System.Runtime.CompilerServices.Unsafe

## 变更记录

见 [CHANGELOG.md](CHANGELOG.md)。
