# DocviewWPF

Windows 轻量多标签文档阅读器（.NET Framework 4.8 WPF）。风格接近 SumatraPDF：启动快、界面简、打开即读。**支持格式：PDF（`.pdf`）、Word（`.docx`）、Excel（`.xlsx`）。**

**English:** [README.md](README.md)

## 关于（About）

| | |
|--|--|
| **产品** | DocviewWPF |
| **运行环境** | Windows x64 · .NET Framework 4.8 · WPF |
| **支持的文件格式** | **`.pdf`** · **`.docx`** · **`.xlsx`** |
| **当前不支持** | 例如 `.doc`、`.xls`、`.ppt`/`.pptx`、纯图片文档、纯文本等（后续可扩展） |

可通过菜单、工具栏、拖放或命令行路径打开。一文件一标签；重复打开会切换到已有标签。

## 功能

### 格式说明

| 扩展名 | 格式 | 说明 |
|--------|------|------|
| **`.pdf`** | PDF | pdfium 按页懒渲染、连续滚动、缩放 / 适宽 / 适页、`[` `]` 旋转 90°、文字选中复制、图片右键复制/另存；**专业编辑窗口**（页对象级编辑，尽量矢量保存） |
| **`.docx`** | Word Open XML | 只读流式排版；目录（含 SDT）、编号/项目符号、页边距 |
| **`.xlsx`** | Excel Open XML | 虚拟化网格（OnRender）；冻结窗格、筛选、列宽/行高、换行、简单编辑与保存 |

### 界面与导航

- 多 Tab：一文件一标签；重复打开则切换到已有标签
- Tab 拖动：排序、拖出独立窗口、拖回合并
- 目录侧栏：PDF 书签 / DOCX 大纲；筛选高亮
- Sumatra 风格：自绘标题栏与工具栏，拖动标题栏 / 双击最大化，最大化不挡任务栏，`F` 全屏

### 会话

- 可选恢复上次打开的标签
- 按文件记忆阅读进度（滚动、缩放、页/表）
- 最近文件菜单
- 单实例：二次启动激活已有窗口并转发打开路径
- **界面语言**：中文 / English / 日本語 / 한국어（帮助 → 语言，或系统参数）

## 环境要求

- Windows x64
- .NET Framework 4.8
- Visual Studio / MSBuild（或带 net48 目标包的 `dotnet` SDK）

## 编译与运行

本地推荐脚本（若已配置）：

```bat
slx DocviewWPF
slr DocviewWPF
```

或：

```bat
dotnet restore DocviewWPF\DocviewWPF.csproj
dotnet build DocviewWPF\DocviewWPF.csproj -c Release
```

输出：

```text
DocviewWPF\bin\Release\net48\DocviewWPF.exe
```

解决方案：`DocviewWPF.slnx`。

## 配置位置

设置与会话写在当前用户的应用数据目录（不在仓库内）：

```text
%LocalAppData%\DocviewWPF\
  settings.json
  session.json
  reading_progress.json
  recent.json
```

运行日志（从构建输出目录启动时）：

```text
DocviewWPF\bin\Release\net48\logs\docviewwpf_YYYYMMDD.log
```

请勿提交密钥、密码、API Key 或本机绝对路径。

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
| Ctrl+S | 保存（编辑模式） |

## 目录结构

```text
DocviewWPF.slnx
DocviewWPF/
  DocviewWPF.csproj
  App.xaml(.cs)
  MainWindow.xaml(.cs)
  Core/           # 设置、会话、主题、日志、单实例
  Viewers/        # PDF / DOCX / XLSX 与 PDF 专业编辑
  Assets/         # 图标
README.md
README.zh.md
CHANGELOG.md
```

## 依赖（NuGet）

- PDFtoImage（pdfium + SkiaSharp）
- DocumentFormat.OpenXml
- System.Memory / System.Runtime.CompilerServices.Unsafe

## PDF 专业编辑（摘要）

- 入口：工具菜单 / 工具栏「PDF 专业编辑」
- 对象级选择、框选多选、拖动（幽灵预览跟手）
- 文字：字符级 Unicode 显示；改字为安全替换（系统字体嵌入，避免 CID 原地 SetText 崩溃）
- 尽量 `GenerateContent` + `ImportPages` + `SaveAsCopy` 矢量保存

## 变更记录

见 [CHANGELOG.md](CHANGELOG.md)。
