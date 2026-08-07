using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using EmojiWpf = Emoji.Wpf;

namespace DocviewWPF;

/// <summary>Markdown 编辑模式（三种）。</summary>
enum MdEditLayout {
	/// <summary>纯代码：vim 风（颜色/粗斜体/链接），无 conceal，无预览。</summary>
	Code = 0,
	/// <summary>Typora：单栏源码 + conceal（无侧栏预览）。</summary>
	Typora = 1,
	/// <summary>纯代码 + 侧边预览：同纯代码样式，右侧同步预览。</summary>
	Side = 2,
}

/// <summary>Markdown 预览渲染引擎。</summary>
enum MdPreviewEngine {
	/// <summary>WebView2 HTML（Mermaid / highlight.js，默认）。</summary>
	WebView = 0,
	/// <summary>纯 WPF FlowDocument（MdFlowBuilder，无浏览器内核）。</summary>
	Wpf = 1,
}

/// <summary>
/// Markdown 查看/编辑：默认预览；工具栏进入编辑后三选一：
/// 1) 纯代码；2) Typora（单栏 conceal）；3) 纯代码+侧边预览。
/// 预览可切换 WebView2 / 纯 WPF。
/// </summary>
sealed class MdViewer : IDocViewer {
	const double MIN_ZOOM = 0.6;
	const double MAX_ZOOM = 2.5;
	const double BASE_FONT = 14;
	const int PREVIEW_DEBOUNCE_MS = 180;
	/// <summary>视口宽度变化后重建预览（表格列宽按 MdTableLayout 重算）。</summary>
	const int PREVIEW_RESIZE_MS = 120;
	/// <summary>编辑后仅重绘当前行的防抖（偏长，避免键入时每字 Clear Inlines）。</summary>
	const int LINE_HL_MS = 280;
	/// <summary>连续键入合并为一个撤销单元的时间窗（过长会像「Ctrl+Z 很慢」）。</summary>
	const int UNDO_MERGE_MS = 280;
	const int MAX_UNDO = 100;
	/// <summary>超过此字符变化视为大块编辑：不整篇同步高亮。</summary>
	const int BULK_EDIT_CHARS = 200;
	/// <summary>超过此行数时，结构变化用分片上色而非 applysourcehighlight。</summary>
	const int BULK_HL_LINES = 60;
	/// <summary>标记符 Run：Tag=mdm:原文。conceal 时 Text 置空（不占位），导出从 Tag 还原。</summary>
	const string MARKER_TAG_PREFIX = "mdm:";
	/// <summary>无序列表 ASCII 标记（-*+）：Tag=mdm-ul:X，conceal 时 Text 显示为 ●。</summary>
	const string LIST_UL_TAG_PREFIX = "mdm-ul:";
	/// <summary>Typora conceal 图片：InlineUIContainer.Tag = mdimg:完整 ![alt](href)。</summary>
	const string IMG_TAG_PREFIX = "mdimg:";
	/// <summary>光标行下方的预览图（仅视觉，不参与源码导出）。</summary>
	const string IMG_PREVIEW_TAG = "mdimg-ui";
	/// <summary>Typora 折叠表：Table.Tag = mdtbl:原始多行源码（含 sep）。</summary>
	const string TBL_TAG_PREFIX = "mdtbl:";

	struct TableRange {
		public int A;
		public int B;
	}

	readonly Grid root;
	readonly ColumnDefinition colside;
	readonly Border pside;
	readonly TreeView tree;
	readonly TextBlock lboutline;
	readonly TextBox eoutline;
	readonly Grid mainHost;

	// 预览：WebView2 HTML 与纯 WPF FlowDocument 二选一（槽位 previewSurface）
	readonly Grid previewSurface;
	readonly WebView2 previewWeb;
	readonly RichTextBox previewRtb;
	MdPreviewEngine previewEngine = MdPreviewEngine.WebView;
	bool previewReady;
	string pendingHtml;
	/// <summary>待映射的 md.assets 根目录（可能宽于文档目录，以覆盖 ../images）。</summary>
	string pendingAssetRoot;
	double pendingScrollY;
	bool restoreScrollAfterNav;
	/// <summary>重建预览后按源码滚动比例对齐（侧预编辑时优先，避免拽回旧预览位置）。</summary>
	double pendingScrollRatio;
	bool restoreScrollRatioAfterNav;
	string mappedAssetDir;
	/// <summary>预览区上次滚动 Y（像素）。</summary>
	double previewScrollY;
	/// <summary>内容区滚动比例 0..1（预览/源码共用，供进度与模式切换）。</summary>
	double contentScrollRatio;
	/// <summary>侧预：预览滚动比例（来自 JS / WPF ScrollViewer），用于反向同步源码。</summary>
	double previewScrollRatio;
	/// <summary>WPF 预览查找黄底高亮。</summary>
	TextRange wpfFindHl;
	// 源码（RichTextBox：高亮 + conceal）
	readonly RichTextBox sourceBox;

	// 布局容器
	readonly Grid editGrid;
	readonly GridSplitter splitV;
	readonly GridSplitter splitH;
	readonly ColumnDefinition colSrc;
	readonly ColumnDefinition colPrev;
	readonly RowDefinition rowSrc;
	readonly RowDefinition rowPrev;

	double zoom = 1.0;
	bool editMode;
	bool dirty;
	bool suppressText;
	bool syncingScroll;
	/// <summary>
	/// 程序化滚动预览后，忽略预览→源码回传直至该 TickCount（WebView 滚动异步，syncingScroll 挡不住）。
	/// </summary>
	int ignorePreviewToSourceUntil;
	const int PREVIEW_SYNC_SUPPRESS_MS = 650;
	bool sideVisible = true;
	bool hasOutline;
	/// <summary>进入编辑默认纯代码；用户可改，阅读进度可恢复上次布局。</summary>
	MdEditLayout layout = MdEditLayout.Code;
	Encoding fileEnc = new UTF8Encoding(false);

	/// <summary>当前文件编码（状态栏 / 切换）。</summary>
	public Encoding FileEncoding => fileEnc;
	public string EncodingName => TextFileIo.DisplayName(fileEnc);
	string rawText = "";
	MdDoc mdDoc;
	string outlineQuery = "";
	/// <summary>当前已应用到 rawText 行首缩进的 Tab 宽（改设置时用于 Retarget）。</summary>
	int appliedTabSize = 3;
	sealed class TocEntry {
		public string Title;
		public int Level;
		public int SourceLine0;
		public TreeViewItem Item;
	}
	readonly List<TocEntry> toc = new();
	/// <summary>目录点击/程序化选中，避免 SelectedItemChanged↔synctoc 重入。</summary>
	bool syncTree;
	/// <summary>目录跳转后短暂忽略滚动回写高亮。</summary>
	int ignoreOutlineSyncUntil;
	const int OUTLINE_DEBOUNCE_MS = 140;
	DispatcherTimer outlineDebounce;
	int lastTocLine = -1;
	int pendingOutlineLine = -1;
	/// <summary>预览视口顶行（data-line），预览↔编辑切换对齐。</summary>
	int lastPreviewTopLine = -1;
	/// <summary>切换模式后待恢复的源行；&lt;0 表示无。</summary>
	int pendingModeSwitchLine = -1;
	bool enteringEdit;

	// debounce
	int nextPreviewAt;
	/// <summary>视口宽度变化后重建预览的截止 TickCount；0=无。</summary>
	int nextPreviewResizeAt;
	int nextHlAt;
	readonly DispatcherTimer tick;
	double lastPreviewPageW;

	// find（不切换预览/编辑/Typora 布局）
	string findQuery;
	bool findIgnoreCase = true;
	int findIndex = -1;
	readonly List<int> findHits = new();

	// Typora conceal：非光标行隐藏标记；光标行通过标记 Run 属性切换恢复（不换 Document）
	int caretLine = -1;
	/// <summary>进出折叠表需换 Document：推迟到 MouseDown 结束后，避免 TextView NRE。</summary>
	bool pendingTableStructHl;
	int pendingTableStructLine = -1;
	int pendingTableStructOff;
	/// <summary>点击折叠图：MouseDown 后展开该行源码（避免同步改 Document）。</summary>
	int pendingImgExpandLine = -1;
	/// <summary>取消「单击延迟展开」，避免双击预览时先展开源码。</summary>
	int imgExpandGen;
	/// <summary>拖拽/选区中延后 conceal，避免掐掉选区。</summary>
	bool pendingConcealSync;
	/// <summary>章节跳转后跳过一次高亮重建的滚动恢复，避免把标题滚出视口。</summary>
	bool skipScrollRestoreOnce;
	/// <summary>rawText 对应的行缓存，避免每次 SplitLines。</summary>
	List<string> cachedLines;
	string cachedLinesSrc;
	/// <summary>0=普通 1=围栏正文 2=围栏标记行（与 cachedLines 同步）。</summary>
	byte[] lineFenceKind;
	/// <summary>普通行是否含需 conceal 的 markdown 标记。</summary>
	bool[] lineHasMarkers;
	/// <summary>围栏正文语言（仅 kind==1 有意义）。</summary>
	string[] lineFenceLang;
	/// <summary>GFM 表行范围（与 cachedLines 同步）。</summary>
	List<TableRange> cachedTables;
	/// <summary>逻辑行 → Paragraph（折叠表行为 null）；Document 结构变时失效。</summary>
	Paragraph[] lineParaCache;
	/// <summary>点击/重绘路径计时：≥ 此毫秒写日志。</summary>
	const int CLICK_LOG_MS = 50;
	/// <summary>大围栏重绘：超过此行数只涂视口附近（虚拟化）。</summary>
	const int FENCE_FULL_RECOLOR_MAX = 80;
	/// <summary>分片高亮视口上下各多涂的行数。</summary>
	const int VIEWPORT_HL_PAD = 24;

	// 自定义撤销（替换 Document 会清掉 RTB 原生 Undo）
	readonly List<(string Text, int Caret)> undoStack = new();
	readonly List<(string Text, int Caret)> redoStack = new();
	int lastUndoAt;
	bool suppressUndo;

	public FrameworkElement View => root;
	public string FilePath { get; private set; }
	public string Title { get; private set; }
	public DocKind Kind => DocKind.Md;
	public double Zoom => zoom;
	public string StatusText {
		get {
			var mode = !editMode ? "预览" : layout switch {
				MdEditLayout.Code => "代码",
				MdEditLayout.Typora => "Typora",
				MdEditLayout.Side => "侧预",
				_ => "编辑",
			};
			var eng = usewpfpreview ? "WPF" : "Web";
			var d = dirty ? " *" : "";
			var lines = countlines(rawText);
			return $"MD  {mode}/{eng}{d}  ·  {lines} 行  ·  {(int)(zoom * 100)}%";
		}
	}
	public int PageCount => 1;
	public int CurrentPage => 1;
	public bool HasOutline => hasOutline;
	public bool SidePanelVisible => false;

	public bool EditMode {
		get => editMode;
		set => seteditmode(value);
	}
	public bool IsDirty => dirty;
	public MdEditLayout EditLayout {
		get => layout;
		set => setlayout(value);
	}
	/// <summary>预览渲染：WebView2 或纯 WPF。</summary>
	public MdPreviewEngine PreviewEngine {
		get => previewEngine;
		set => setpreviewengine(value);
	}

	public event Action StatusChanged;
	public event Action EditModeChanged;
	public event Action DirtyChanged;
	/// <summary>滚动定位章节时：理想标题源行 0-based（主窗章节列表镜像用）。</summary>
	public event Action<int> OutlineHighlightChanged;
	public event Action LayoutChanged;
	public event Action PreviewEngineChanged;
	/// <summary>
	/// 请求在新窗口打开 Markdown：path, editMode, layout, optional anchor。
	/// </summary>
	/// <summary>本地 MD/文档：请在应用内标签打开（path, editMode, layout, anchor）。</summary>
	public event Action<string, bool, MdEditLayout, string> OpenMarkdownNewWindow;
	/// <summary>http(s) 链接：请在应用内浏览器标签打开。</summary>
	public event Action<string> OpenUrlInApp;

	public MdViewer() {
		// —— 目录侧栏 ——
		tree = new TreeView {
			BorderThickness = new Thickness(0),
			Background = Brushes.Transparent,
			Padding = new Thickness(0, 0, 0, 4),
		};
		OutlineUi.ConfigureTree(tree);
		tree.SelectedItemChanged += (_, _) => {
			if (syncTree) return;
			if (tree.SelectedItem is TreeViewItem ti && ti.Tag is int line0) {
				ignoreOutlineSyncUntil = Environment.TickCount + PREVIEW_SYNC_SUPPRESS_MS;
				gotoline(line0);
			}
		};
		lboutline = new TextBlock {
			Text = "无目录",
			Margin = new Thickness(10, 4, 10, 4),
			Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
		};
		eoutline = OutlineUi.MakeFilterBox();
		eoutline.TextChanged += (_, _) => {
			outlineQuery = eoutline.Text?.Trim() ?? "";
			rebuildtocui();
			if (pendingOutlineLine >= 0)
				synctoc(pendingOutlineLine, force: true);
		};
		var btoggle = new Button {
			Content = "«", Width = 28, Height = 22, Padding = new Thickness(0),
			ToolTip = "隐藏目录", Cursor = Cursors.Hand,
			Background = Brushes.Transparent, BorderThickness = new Thickness(0),
		};
		btoggle.Click += (_, _) => SetSidePanelVisible(!sideVisible);
		var head = new DockPanel { Margin = new Thickness(8, 6, 4, 4) };
		DockPanel.SetDock(btoggle, Dock.Right);
		head.Children.Add(btoggle);
		head.Children.Add(new TextBlock {
			Text = "目录", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
			FontSize = AppSettings.Current.UiFontSize,
		});
		var sideBody = new DockPanel();
		DockPanel.SetDock(head, Dock.Top);
		DockPanel.SetDock(eoutline, Dock.Top);
		DockPanel.SetDock(lboutline, Dock.Top);
		sideBody.Children.Add(head);
		sideBody.Children.Add(eoutline);
		sideBody.Children.Add(lboutline);
		sideBody.Children.Add(tree);
		pside = new Border {
			Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
			BorderThickness = new Thickness(0, 0, 1, 0),
			ClipToBounds = true,
			Child = sideBody,
		};

		// —— 预览槽：WebView2 HTML 或纯 WPF FlowDocument ——
		try {
			var eng = AppSettings.Current?.MdPreviewEngine ?? 0;
			previewEngine = eng == 1 ? MdPreviewEngine.Wpf : MdPreviewEngine.WebView;
		} catch { previewEngine = MdPreviewEngine.WebView; }

		previewSurface = new Grid {
			Background = Brushes.White,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
		};
		previewWeb = new WebView2 {
			DefaultBackgroundColor = System.Drawing.Color.White,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
			AllowDrop = true,
		};
		previewWeb.PreviewMouseWheel += onpreviewwheel;
		previewWeb.SizeChanged += onpreviewsizechanged;
		// WebView2 为 HWND：窗口级拖放到不了，单独挂打开文件
		MainWindow.WireFileDropTarget(previewWeb);

		// 纯 WPF 预览：只读 RTB + FlowDocument
		// 页边必须用 RTB.Padding（对齐 HTML body padding:20px 28px 40px）；
		// FlowDocument.PagePadding 在 RichTextBox 内基本不生效，会导致贴边。
		previewRtb = new RichTextBox {
			IsReadOnly = true,
			IsDocumentEnabled = true,
			IsUndoEnabled = false,
			BorderThickness = new Thickness(0),
			Padding = new Thickness(
				MdFlowBuilder.PAGE_PAD_L, MdFlowBuilder.PAGE_PAD_T,
				MdFlowBuilder.PAGE_PAD_R, MdFlowBuilder.PAGE_PAD_B),
			Background = Brushes.White,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Focusable = true,
			CaretBrush = Brushes.Transparent,
			SelectionBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x3B, 0x82, 0xF6)),
			Document = new FlowDocument {
				PagePadding = new Thickness(0),
				Background = Brushes.White,
			},
		};
		previewRtb.PreviewMouseWheel += onpreviewwheel;
		previewRtb.SizeChanged += onpreviewsizechanged;
		previewRtb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(onpreviewwpfscroll), true);
		MainWindow.WireFileDropTarget(previewRtb);
		// 右键导出（与 WebView 自定义菜单一致）
		previewRtb.ContextMenu = buildpreviewctxmenu();

		previewSurface.Children.Add(previewWeb);
		previewSurface.Children.Add(previewRtb);
		applypreviewenginevis();
		// WebView 引擎才立即初始化；WPF 模式延后到切换/导出 PDF
		if (!usewpfpreview)
			_ = ensurepreviewasync();

		// —— 源码编辑：标准 RTB（不用 Emoji.Wpf，避免 InlineUI 拖慢光标/键入）——
		// 彩色 emoji：WPF 预览经 Emoji.Wpf；WebView 由系统字体渲染
		sourceBox = new RichTextBox {
			FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI Emoji, 微软雅黑, monospace"),
			FontSize = BASE_FONT,
			AcceptsTab = true,
			AcceptsReturn = true,
			// 用自定义 undo 栈；替换 Document / 改 Inlines 会破坏 RTB 原生撤销
			IsUndoEnabled = false,
			BorderThickness = new Thickness(0),
			Padding = new Thickness(12, 10, 12, 10),
			Background = Brushes.White, // 纯白
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			Document = new FlowDocument {
				PagePadding = new Thickness(0),
				LineHeight = BASE_FONT * 1.45,
				Background = Brushes.White,
				FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI Emoji, 微软雅黑, monospace"),
			},
		};
		sourceBox.TextChanged += onsourcetext;
		// Typora：光标换行时只切换标记 Run 可见性（不 Clear Inlines、不换 Document）
		sourceBox.SelectionChanged += onselectionchanged;
		// Tab：禁止 RTB 用段落缩进代替 \t；Ctrl+Z/Y 走自定义撤销
		sourceBox.PreviewKeyDown += onsourcepreviewkeydown;
		// 编辑态粘贴图片 / 图片文件 → images/ + Markdown
		DataObject.AddPastingHandler(sourceBox, onsourcepasting);
		KeyboardNavigation.SetTabNavigation(sourceBox, KeyboardNavigationMode.None);
		sourceBox.PreviewMouseWheel += onwheel;
		sourceBox.PreviewMouseLeftButtonDown += onsourceclick;
		sourceBox.PreviewMouseLeftButtonUp += onsourcemouseup;
		sourceBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(onsourcescroll), true);

		// —— 编辑布局网格 ——
		editGrid = new Grid { Background = Brushes.White };
		colSrc = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
		colPrev = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
		rowSrc = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
		rowPrev = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
		editGrid.ColumnDefinitions.Add(colSrc);
		editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
		editGrid.ColumnDefinitions.Add(colPrev);
		editGrid.RowDefinitions.Add(rowSrc);
		editGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
		editGrid.RowDefinitions.Add(rowPrev);
		// 分隔条用浅灰细线，主体仍为白底
		var splitBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
		splitV = new GridSplitter {
			Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
			Background = splitBrush,
			ResizeBehavior = GridResizeBehavior.PreviousAndNext,
		};
		splitH = new GridSplitter {
			Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
			Background = splitBrush,
			ResizeBehavior = GridResizeBehavior.PreviousAndNext,
		};
		// editGrid 仅在 applylayout 时按需挂载 source/preview（避免双父级）
		mainHost = new Grid();
		mainHost.Children.Add(previewSurface); // 默认纯预览

		root = new Grid();
		colside = new ColumnDefinition { Width = new GridLength(220) };
		root.ColumnDefinitions.Add(colside);
		root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
		root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var sp = new GridSplitter {
			Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
			Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
			ResizeBehavior = GridResizeBehavior.PreviousAndNext,
		};
		// 正文区纯白（目录侧栏仍用浅灰区分）
		mainHost.Background = Brushes.White;
		root.Background = Brushes.White;
		Grid.SetColumn(pside, 0);
		Grid.SetColumn(sp, 1);
		Grid.SetColumn(mainHost, 2);
		root.Children.Add(pside);
		root.Children.Add(sp);
		root.Children.Add(mainHost);
		setside(false);

		tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
		tick.Tick += (_, _) => ontick();
		tick.Start();
		MainWindow.WireFileDropTarget(root);
		MainWindow.WireFileDropTarget(sourceBox);
	}

	public void Load(string path) {
		var r = TextFileIo.Load(path);
		FilePath = System.IO.Path.GetFullPath(path);
		Title = System.IO.Path.GetFileName(path);
		fileEnc = r.Encoding ?? new UTF8Encoding(false);
		// WPF RTB 无法改 \\t 显示宽：按 MdTabSize 展成空格后再进编辑器
		var tab = AppSettings.Current?.MdTabSize ?? 3;
		if (tab < 1) tab = 1;
		appliedTabSize = tab;
		rawText = MdParser.ExpandTabs(r.Text ?? "", tab);
		clearundostacks();
		suppressText = true;
		try {
			setsourceplain(rawText);
		} finally { suppressText = false; }
		setdirty(false);
		rebuildpreview(force: true);
		applysourcehighlight(force: true);
		buildtoc();
		seteditmode(false);
		DocLog.Info($"Md Load lines={countlines(rawText)} toc={toc.Count} path={FilePath}");
		StatusChanged?.Invoke();
	}

	public void Save() {
		// rawText 为权威源码；编辑态仅从 RTB 同步（RTB 必须保留全部 MD 标记，conceal 只做视觉）
		syncrawfromeditor();
		TextFileIo.Save(FilePath, rawText, fileEnc);
		setdirty(false);
		rebuildpreview(force: true);
		buildtoc();
		DocLog.Info($"Md Save path={FilePath}");
		StatusChanged?.Invoke();
	}

	/// <summary>按编码重载磁盘（丢弃未保存修改）。</summary>
	public void ReloadWithEncoding(Encoding enc) {
		if (enc == null || string.IsNullOrEmpty(FilePath)) return;
		var r = TextFileIo.LoadWithEncoding(FilePath, enc);
		fileEnc = r.Encoding ?? enc;
		var tab = AppSettings.Current?.MdTabSize ?? 3;
		if (tab < 1) tab = 1;
		appliedTabSize = tab;
		rawText = MdParser.ExpandTabs(r.Text ?? "", tab);
		invalidatelinecache();
		clearundostacks();
		suppressText = true;
		try {
			if (editMode)
				applysourcehighlight(force: true);
			else
				setsourceplain(rawText);
		} finally { suppressText = false; }
		setdirty(false);
		rebuildpreview(force: true);
		buildtoc();
		DocLog.Info($"Md reload enc={TextFileIo.DisplayName(fileEnc)} path={FilePath}");
		StatusChanged?.Invoke();
	}

	/// <summary>导出渲染后的 HTML（相对资源保留路径说明）。</summary>
	public bool ExportHtml(string path) {
		try {
			syncrawfromeditor();
			var doc = MdParser.Parse(rawText ?? "");
			var w = previewWeb?.ActualWidth ?? 720;
			if (w < 100) w = 720;
			var tab = AppSettings.Current?.MdTabSize ?? 3;
			var html = MdHtmlBuilder.Build(doc, FilePath, 1.0, w, tab, out _);
			// 导出独立文件：把虚拟主机换成本地 file 提示（静态资源用 CDN 回退已在 builder 内）
			path = Path.GetFullPath(path);
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);
			File.WriteAllText(path, html, new UTF8Encoding(false));
			DocLog.Info($"Md ExportHtml path={path}");
			return true;
		} catch (Exception ex) {
			DocLog.Warn($"Md ExportHtml: {ex.Message}");
			return false;
		}
	}

	/// <summary>经 WebView2 打印为 PDF（始终走 HTML 路径，与当前预览引擎无关）。</summary>
	public async System.Threading.Tasks.Task<bool> ExportPdfAsync(string path) {
		try {
			syncrawfromeditor();
			await ensurepreviewasync().ConfigureAwait(true);
			for (var i = 0; i < 40 && !previewReady; i++)
				await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(true);
			if (previewWeb?.CoreWebView2 == null) return false;
			// 强制 HTML 导航（WPF 模式下预览区可能未加载 Web 内容）
			mdDoc = MdParser.Parse(rawText ?? "");
			var w = previewsurfacewidth();
			if (w < 100) w = 720;
			var tab = AppSettings.Current?.MdTabSize ?? 3;
			var html = MdHtmlBuilder.Build(mdDoc, FilePath, 1.0, w, tab, out var assetRoot);
			// 临时显示 WebView 以便打印（WPF 模式导出后恢复）
			var wasWpf = usewpfpreview;
			if (wasWpf) {
				previewWeb.Visibility = Visibility.Visible;
				previewRtb.Visibility = Visibility.Collapsed;
			}
			navigatetohtml(html, assetRoot);
			await System.Threading.Tasks.Task.Delay(400).ConfigureAwait(true);
			path = Path.GetFullPath(path);
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);
			await previewWeb.CoreWebView2.PrintToPdfAsync(path).ConfigureAwait(true);
			if (wasWpf) applypreviewenginevis();
			DocLog.Info($"Md ExportPdf path={path}");
			return File.Exists(path);
		} catch (Exception ex) {
			DocLog.Warn($"Md ExportPdf: {ex.Message}");
			try { applypreviewenginevis(); } catch { /* ignore */ }
			return false;
		}
	}

	/// <summary>预览右键：弹出另存为并导出 HTML。</summary>
	void exporthtmlui() {
		try {
			var owner = Window.GetWindow(root);
			var dlg = new SaveFileDialog {
				Filter = "HTML|*.html;*.htm|所有文件|*.*",
				FileName = Path.GetFileNameWithoutExtension(FilePath ?? "export") + ".html",
				InitialDirectory = Path.GetDirectoryName(FilePath ?? "") ?? "",
			};
			if (dlg.ShowDialog(owner) != true) return;
			if (!ExportHtml(dlg.FileName))
				MessageBox.Show(owner, "导出 HTML 失败。", "DocviewWPF",
					MessageBoxButton.OK, MessageBoxImage.Warning);
		} catch (Exception ex) {
			DocLog.Warn($"Md exporthtmlui: {ex.Message}");
			MessageBox.Show(Window.GetWindow(root), "导出 HTML 失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>预览右键：弹出另存为并导出 PDF。</summary>
	async void exportpdfui() {
		Window owner = null;
		try {
			owner = Window.GetWindow(root);
			var dlg = new SaveFileDialog {
				Filter = "PDF|*.pdf|所有文件|*.*",
				FileName = Path.GetFileNameWithoutExtension(FilePath ?? "export") + ".pdf",
				InitialDirectory = Path.GetDirectoryName(FilePath ?? "") ?? "",
			};
			if (dlg.ShowDialog(owner) != true) return;
			var ok = await ExportPdfAsync(dlg.FileName).ConfigureAwait(true);
			if (!ok)
				MessageBox.Show(owner, "导出 PDF 失败（请确认 WebView2 可用）。", "DocviewWPF",
					MessageBoxButton.OK, MessageBoxImage.Warning);
		} catch (Exception ex) {
			DocLog.Warn($"Md exportpdfui: {ex.Message}");
			MessageBox.Show(owner ?? Window.GetWindow(root), "导出 PDF 失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>把编辑器纯文本写回 rawText（要求 RTB 内字符与源码一一对应，含被 conceal 的标记）。</summary>
	void syncrawfromeditor() {
		if (!editMode) return;
		try {
			var t = getsourceplain();
			if (t != null) rawText = t;
		} catch { /* keep rawText */ }
	}

	void seteditmode(bool on) {
		if (editMode == on) return;
		if (on) {
			if (enteringEdit) return;
			_ = entereditkeeplineasync();
			return;
		}
		var line = capturesourcetopline();
		var ratio = capturecontentratio();
		syncrawfromeditor();
		editMode = false;
		detachpreview();
		mainHost.Children.Clear();
		mainHost.Children.Add(previewSurface);
		contentScrollRatio = ratio;
		previewScrollRatio = ratio;
		pendingModeSwitchLine = line;
		pendingScrollRatio = ratio;
		restoreScrollRatioAfterNav = false;
		restoreScrollAfterNav = false;
		rebuildpreview(force: true);
		// 退出编辑后按行对齐预览（WPF 立刻；WebView 在 NavigationCompleted）
		if (usewpfpreview && line >= 0) {
			root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				try {
					scrollpreviewtoline(line);
					synctoc(line, force: true);
				} catch { /* ignore */ }
			}));
			pendingModeSwitchLine = -1;
		}
		try { EditModeChanged?.Invoke(); } catch { /* ignore */ }
		StatusChanged?.Invoke();
	}

	/// <summary>预览→编辑：按预览顶行对齐源码，避免比例映射偏差。</summary>
	async System.Threading.Tasks.Task entereditkeeplineasync() {
		enteringEdit = true;
		try {
			var line = lastPreviewTopLine;
			try {
				line = await querypreviewtoplineasync().ConfigureAwait(true);
			} catch { /* keep */ }
			if (line < 0) line = pendingOutlineLine;
			if (line < 0) line = 0;

			suppressText = true;
			try { setsourceplain(rawText); }
			finally { suppressText = false; }
			editMode = true;
			applylayout();
			applysourcehighlight(force: true);
			try { sourceBox.Focus(); } catch { /* ignore */ }
			ignoreOutlineSyncUntil = Environment.TickCount + PREVIEW_SYNC_SUPPRESS_MS;
			gotoline(line);
			try { EditModeChanged?.Invoke(); } catch { /* ignore */ }
			StatusChanged?.Invoke();
		} finally {
			enteringEdit = false;
		}
	}

	void setlayout(MdEditLayout l) {
		if (layout == l && editMode) {
			applylayout();
			return;
		}
		double saveV = 0, saveH = 0;
		var ratio = capturecontentratio();
		if (editMode) {
			try {
				var sv = findscroll(sourceBox);
				if (sv != null) {
					saveV = sv.VerticalOffset;
					saveH = sv.HorizontalOffset;
				}
			} catch { /* ignore */ }
		}
		layout = l;
		if (editMode) {
			if (l == MdEditLayout.Side) {
				pendingScrollRatio = ratio;
				restoreScrollRatioAfterNav = true;
				restoreScrollAfterNav = false;
			}
			applylayout();
			// 同为编辑布局：尽量保持源码滚动；侧预比例在 NavigationCompleted 恢复
			try {
				root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
					try {
						var sv = findscroll(sourceBox);
						if (sv != null) {
							sv.ScrollToHorizontalOffset(saveH);
							sv.ScrollToVerticalOffset(saveV);
						}
					} catch { /* ignore */ }
				}));
			} catch { /* ignore */ }
		}
		try { LayoutChanged?.Invoke(); } catch { /* ignore */ }
		StatusChanged?.Invoke();
	}

	void applylayout() {
		if (!editMode) return;
		detachpreview();
		// 从 mainHost / editGrid 卸下
		mainHost.Children.Clear();
		editGrid.Children.Clear();

		// 复位行列
		colSrc.Width = new GridLength(1, GridUnitType.Star);
		colPrev.Width = new GridLength(1, GridUnitType.Star);
		rowSrc.Height = new GridLength(1, GridUnitType.Star);
		rowPrev.Height = new GridLength(1, GridUnitType.Star);

		switch (layout) {
			case MdEditLayout.Code:
				// 1) 纯代码：vim 风（颜色/粗斜体/链接），无预览、无 conceal
				mainHost.Children.Add(sourceBox);
				applysourcehighlight(force: true);
				break;
			case MdEditLayout.Typora:
				// 2) Typora：只要左栏（全宽源码 + conceal），无右侧预览
				mainHost.Children.Add(sourceBox);
				applysourcehighlight(force: true);
				break;
			case MdEditLayout.Side:
			default:
				// 3) 侧预：左同纯代码样式 + 右同步预览
				layoutsidebyyside();
				applysourcehighlight(force: true);
				rebuildpreview(force: true);
				break;
		}
		applyzoom();
	}

	/// <summary>左右分栏：源码 | 预览。</summary>
	void layoutsidebyyside() {
		editGrid.Children.Add(sourceBox);
		editGrid.Children.Add(splitV);
		editGrid.Children.Add(previewSurface);
		Grid.SetColumn(sourceBox, 0);
		Grid.SetRow(sourceBox, 0);
		Grid.SetRowSpan(sourceBox, 3);
		Grid.SetColumnSpan(sourceBox, 1);
		Grid.SetColumn(splitV, 1);
		Grid.SetRow(splitV, 0);
		Grid.SetRowSpan(splitV, 3);
		Grid.SetColumnSpan(splitV, 1);
		Grid.SetColumn(previewSurface, 2);
		Grid.SetRow(previewSurface, 0);
		Grid.SetRowSpan(previewSurface, 3);
		Grid.SetColumnSpan(previewSurface, 1);
		splitV.Visibility = Visibility.Visible;
		splitH.Visibility = Visibility.Collapsed;
		colSrc.Width = new GridLength(1, GridUnitType.Star);
		colPrev.Width = new GridLength(1, GridUnitType.Star);
		editGrid.ColumnDefinitions[1].Width = new GridLength(4);
		rowSrc.Height = new GridLength(1, GridUnitType.Star);
		rowPrev.Height = new GridLength(0);
		editGrid.RowDefinitions[1].Height = new GridLength(0);
		mainHost.Children.Add(editGrid);
	}

	/// <summary>仅「侧预」有右侧预览栏。</summary>
	bool hassidepreview =>
		editMode && layout == MdEditLayout.Side;

	/// <summary>仅 Typora 使用 conceal。</summary>
	bool useconceal =>
		editMode && layout == MdEditLayout.Typora;

	/// <summary>
	/// 纯代码 / 侧预：简易编辑（仅文字颜色、粗斜体、超链接），无块级装饰与背景。
	/// </summary>
	bool usesimpleeditor => !useconceal;

	/// <summary>当前预览是否走纯 WPF。</summary>
	bool usewpfpreview => previewEngine == MdPreviewEngine.Wpf;

	void detachpreview() {
		// 避免同一元素多父级：卸下预览槽与源码
		if (previewSurface.Parent is Panel p)
			p.Children.Remove(previewSurface);
		if (sourceBox.Parent is Panel p2)
			p2.Children.Remove(sourceBox);
	}

	void applypreviewenginevis() {
		if (previewWeb != null)
			previewWeb.Visibility = usewpfpreview ? Visibility.Collapsed : Visibility.Visible;
		if (previewRtb != null)
			previewRtb.Visibility = usewpfpreview ? Visibility.Visible : Visibility.Collapsed;
	}

	void setpreviewengine(MdPreviewEngine eng) {
		if (previewEngine == eng) {
			applypreviewenginevis();
			return;
		}
		var ratio = capturecontentratio();
		var line = lastPreviewTopLine >= 0 ? lastPreviewTopLine
			: (editMode ? capturesourcetopline() : -1);
		previewEngine = eng;
		applypreviewenginevis();
		// 持久化
		try {
			AppSettings.Current.MdPreviewEngine = eng == MdPreviewEngine.Wpf ? 1 : 0;
			AppSettings.Current.Save();
		} catch { /* ignore */ }
		if (!usewpfpreview)
			_ = ensurepreviewasync();
		pendingScrollRatio = ratio;
		restoreScrollRatioAfterNav = true;
		restoreScrollAfterNav = false;
		if (line >= 0) {
			pendingModeSwitchLine = line;
			restoreScrollRatioAfterNav = false;
		}
		if (previewvisible)
			rebuildpreview(force: true);
		// WPF 无 NavigationCompleted：立刻恢复滚动
		if (usewpfpreview) {
			root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				try {
					if (pendingModeSwitchLine >= 0) {
						var ln = pendingModeSwitchLine;
						pendingModeSwitchLine = -1;
						scrollpreviewtoline(ln);
						synctoc(ln, force: true);
					} else {
						restorewpfscrollratio(pendingScrollRatio);
					}
				} catch { /* ignore */ }
			}));
		}
		try { PreviewEngineChanged?.Invoke(); } catch { /* ignore */ }
		StatusChanged?.Invoke();
		DocLog.Info($"Md PreviewEngine={previewEngine}");
	}

	ContextMenu buildpreviewctxmenu() {
		var cm = new ContextMenu();
		var miPdf = new MenuItem { Header = "导出 PDF..." };
		miPdf.Click += (_, _) => exportpdfui();
		var miHtml = new MenuItem { Header = "导出 HTML..." };
		miHtml.Click += (_, _) => exporthtmlui();
		cm.Items.Add(miPdf);
		cm.Items.Add(miHtml);
		return cm;
	}

	void setdirty(bool d) {
		if (dirty == d) return;
		dirty = d;
		try { DirtyChanged?.Invoke(); } catch { /* ignore */ }
	}

	/// <summary>键入后围栏开/闭或范围变化时需整篇重建，否则代码块高亮会丢。</summary>
	bool fenceStructureDirty;
	/// <summary>程序化大块替换中，忽略 TextChanged。</summary>
	bool bulkApplying;

	void onsourcetext(object sender, TextChangedEventArgs e) {
		if (suppressText || bulkApplying) return;
		// 用户键入：RTB 含完整标记（conceal 不删字符）→ 同步权威 rawText
		var prev = rawText ?? "";
		var prevCaret = safegetcaretoffset();
		var prevFence = lineFenceKind;
		syncrawfromeditor();
		var newText = rawText ?? "";
		var delta = Math.Abs(newText.Length - prev.Length);
		invalidatelinecache();
		// 立刻重建围栏索引；仅当围栏内容真正变化时置位（行数变了不等于要整篇重绘）
		try { getlinescached(); } catch { /* ignore */ }
		if (fencecontentchanged(prevFence, lineFenceKind))
			fenceStructureDirty = true;
		if (!string.Equals(prev, newText, StringComparison.Ordinal))
			pushundo(prev, prevCaret);
		setdirty(true);

		// 仅「真·大块」字符变化走兜底；禁止因行数+1 就 setsourceplain 整篇（高亮会闪几秒）
		if (usesimpleeditor && delta >= BULK_EDIT_CHARS) {
			nextHlAt = 0;
			fenceStructureDirty = false;
			var caret = safegetcaretoffset();
			// 就地规范化，勿整页抹成 plain
			try {
				var oldSnap = MdParser.SplitLines(prev);
				var lines = getlinescached();
				suppressText = true;
				try {
					if (!trypatchdocumentlines(lines, prevFence, oldSnap)) {
						// 仅结构无法修补时才整换
						setsourceplain(newText);
						setcaretoffset(caret);
						schedulechunkhighlight(++undoHlGen, caret);
					} else {
						++undoHlGen; // 取消进行中的全文件上色
						setcaretoffset(caret);
					}
				} finally { suppressText = false; }
			} catch {
				schedulelinehl();
			}
			if (hassidepreview) schedulepreview();
			else schedulebulktoc();
			StatusChanged?.Invoke();
			return;
		}

		// 侧预才重建预览；Typora 仅延后 TOC（勿每次键入 MdParser.Parse 全文）
		if (hassidepreview)
			schedulepreview();
		else
			schedulebulktoc();
		// 小改动：只重绘当前行/当前代码围栏块
		schedulelinehl();
		StatusChanged?.Invoke();
	}

	/// <summary>围栏「占用」是否变化（忽略纯行数增减导致的数组长度不同）。</summary>
	static bool fencecontentchanged(byte[] a, byte[] b) {
		if (ReferenceEquals(a, b)) return false;
		if (a == null && b == null) return false;
		if (a == null || b == null) {
			// 一侧无索引：仅当另一侧存在非 0 围栏时算变化
			var x = a ?? b;
			for (var i = 0; i < x.Length; i++)
				if (x[i] != 0) return true;
			return false;
		}
		var n = Math.Min(a.Length, b.Length);
		for (var i = 0; i < n; i++)
			if (a[i] != b[i]) return true;
		// 多出来的行若在围栏内
		for (var i = n; i < a.Length; i++)
			if (a[i] != 0) return true;
		for (var i = n; i < b.Length; i++)
			if (b[i] != 0) return true;
		return false;
	}

	void onselectionchanged(object sender, RoutedEventArgs e) {
		if (suppressText || !editMode) return;
		try {
			var ln = getcaretlinefast();
			if (ln != caretLine) {
				// 拖拽选区或按住左键时：绝不能改 Run / 强制 Caret，否则选区被掐掉
				if (isuserselecting()) {
					caretLine = ln;
					pendingConcealSync = true;
				} else {
					applyconcealforcaretchange(caretLine, ln);
				}
			}
			if (!previewvisible)
				synctoc(caretLine);
		} catch (Exception ex) {
			DocLog.Warn($"Md selection conceal: {ex.Message}");
		}
	}

	/// <summary>选区非空或左键按住（正在点选/拖选）。</summary>
	bool isuserselecting() {
		try {
			if (!sourceBox.Selection.IsEmpty) return true;
			if (Mouse.LeftButton == MouseButtonState.Pressed) return true;
		} catch { /* ignore */ }
		return false;
	}

	/// <summary>光标换行后同步 conceal（调用方已确认非拖选）。</summary>
	void applyconcealforcaretchange(int oldLine, int newLine) {
		var off = safegetcaretoffset();
		caretLine = newLine;
		if (!useconceal) return;
		var ot = tableidat(oldLine);
		var nt = tableidat(newLine);
		// 进出表格需改 Document 结构（折叠表 ⇄ 源码行）
		// 不可在 MouseDown/SelectionChanged 同步换 Document，否则 TextSelection.TextView NRE
		if (ot != nt) {
			scheduletablestructhl(newLine, off);
			return;
		}
		var t0 = Environment.TickCount;
		// 只切 old/new（O(1)）；勿 concealallbut 扫全文
		if (oldLine >= 0) setlineshowraw(oldLine, false);
		setlineshowraw(newLine, true);
		restorecaretoffset(off);
		var dt = Environment.TickCount - t0;
		if (dt >= CLICK_LOG_MS)
			DocLog.Info($"Md conceal toggle old={oldLine} new={newLine} ms={dt}");
	}

	void onsourcemouseup(object sender, MouseButtonEventArgs e) {
		if (pendingImgExpandLine >= 0) {
			var ln = pendingImgExpandLine;
			pendingImgExpandLine = -1;
			// 延迟展开：若紧接着双击预览则 gen 作废，避免先展开源码
			var gen = ++imgExpandGen;
			try {
				var t = new DispatcherTimer(DispatcherPriority.Input) {
					Interval = TimeSpan.FromMilliseconds(280),
				};
				t.Tick += (_, _) => {
					try { t.Stop(); } catch { /* ignore */ }
					if (gen != imgExpandGen) return;
					expandimageline(ln);
				};
				t.Start();
			} catch { /* ignore */ }
			return;
		}
		if (!pendingConcealSync) return;
		try {
			sourceBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(flushconcealsync));
		} catch { /* ignore */ }
	}

	/// <summary>Typora：展开指定行的折叠图（显示 ![alt](href) 源码）。</summary>
	void expandimageline(int line0) {
		if (!editMode || !useconceal || line0 < 0) return;
		try {
			// 点图是明确意图：忽略拖选残留，直接展开
			pendingConcealSync = false;
			var old = caretLine;
			var off = offsetofline(rawText ?? "", line0);
			// 落光标时抑制 SelectionChanged，由下方统一 toggle
			suppressText = true;
			try { setcaretoffset(off); }
			finally { suppressText = false; }
			if (old != line0)
				applyconcealforcaretchange(old, line0);
			else
				setlineshowraw(line0, true);
			sourceBox.Focus();
		} catch (Exception ex) {
			DocLog.Warn($"Md expand image line={line0}: {ex.Message}");
		}
	}

	/// <summary>从命中元素向上找 mdimg 折叠图容器，返回逻辑行；失败 -1。</summary>
	int findconcealedimageline(DependencyObject hit) {
		if (hit == null || sourceBox?.Document == null) return -1;
		try {
			DependencyObject d = hit;
			InlineUIContainer ui = null;
			for (var i = 0; i < 12 && d != null; i++) {
				if (d is InlineUIContainer c) {
					var tag = c.Tag as string;
					if (tag != null && tag.StartsWith(IMG_TAG_PREFIX, StringComparison.Ordinal)) {
						ui = c;
						break;
					}
					// 光标行预览图：无需再展开
					if (tag == IMG_PREVIEW_TAG) return -1;
				}
				var next = LogicalTreeHelper.GetParent(d);
				if (next == null && d is Visual)
					next = VisualTreeHelper.GetParent(d);
				d = next;
			}
			if (ui == null) return -1;
			var para = ui.Parent as Paragraph;
			if (para == null) return -1;
			var line = 0;
			foreach (var b in sourceBox.Document.Blocks) {
				if (b is Paragraph p) {
					if (ReferenceEquals(p, para)) return line;
					line++;
				} else if (b is Table tbl) {
					line += tablelinecountfromtag(tbl.Tag as string);
				}
			}
		} catch { /* ignore */ }
		return -1;
	}

	/// <summary>鼠标松开且无选区后补做 conceal。</summary>
	void flushconcealsync() {
		if (!pendingConcealSync) return;
		if (!editMode) {
			pendingConcealSync = false;
			return;
		}
		if (isuserselecting()) return; // 仍在选，等下次
		pendingConcealSync = false;
		if (!useconceal) return;
		try {
			var ln = getcaretlinefast();
			var old = caretLine;
			// old 在拖选中可能已提前改成 ln，仍强制按当前行同步一遍
			applyconcealforcaretchange(old == ln ? -1 : old, ln);
		} catch (Exception ex) {
			DocLog.Warn($"Md flush conceal: {ex.Message}");
		}
	}

	/// <summary>进出折叠表：Background 优先级重建，避开 TextEditorMouse.OnMouseDown。</summary>
	void scheduletablestructhl(int preferLine, int? preferOffset = null) {
		pendingTableStructLine = preferLine;
		pendingTableStructOff = preferOffset ?? safegetcaretoffset();
		if (pendingTableStructHl) return;
		pendingTableStructHl = true;
		try {
			sourceBox.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
				pendingTableStructHl = false;
				var ln = pendingTableStructLine;
				var off = pendingTableStructOff;
				pendingTableStructLine = -1;
				if (!editMode || !useconceal) return;
				if (isuserselecting()) {
					pendingConcealSync = true;
					return;
				}
				try {
					applysourcehighlight(force: true, preferOffset: off);
				} catch (Exception ex) {
					DocLog.Warn($"Md table struct HL: {ex.Message}");
				}
			}));
		} catch (Exception ex) {
			pendingTableStructHl = false;
			DocLog.Warn($"Md schedule table HL: {ex.Message}");
		}
	}

	/// <summary>在 suppressText 下恢复逻辑光标，避免 SelectionChanged 重入。</summary>
	void restorecaretoffset(int offset) {
		if (isuserselecting()) return;
		var was = suppressText;
		suppressText = true;
		try { setcaretoffset(offset); }
		catch { /* ignore */ }
		finally { suppressText = was; }
	}

	void invalidatelinecache() {
		cachedLines = null;
		cachedLinesSrc = null;
		lineFenceKind = null;
		lineHasMarkers = null;
		lineFenceLang = null;
		cachedTables = null;
	}

	/// <summary>
	/// Tab/Shift+Tab 写入字面量 \\t；Ctrl+Z/Y 自定义撤销；纯代码大块剪贴板。
	/// </summary>
	void onsourcepreviewkeydown(object sender, KeyEventArgs e) {
		var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
			&& !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
		var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		if (ctrl) {
			if (e.Key == Key.Z) {
				e.Handled = true;
				if (shift) RedoEdit();
				else UndoEdit();
				return;
			}
			if (e.Key == Key.Y) {
				e.Handled = true;
				RedoEdit();
				return;
			}
			// Ctrl+V：图片优先；纯代码纯文本走批量路径（避免 RTB 大块粘贴卡死）
			if (e.Key == Key.V && editMode) {
				try {
					if (trypasteimages(Clipboard.GetDataObject())) {
						e.Handled = true;
						return;
					}
				} catch (Exception ex) {
					DocLog.Warn($"Md Ctrl+V image: {ex.Message}");
				}
				if (usesimpleeditor) {
					e.Handled = true;
					pasteplaintextfromclipboard();
					return;
				}
			}
			// Ctrl+X：纯代码批量剪切
			if (e.Key == Key.X && editMode && usesimpleeditor) {
				e.Handled = true;
				cutselectionplaintext();
				return;
			}
		}
		// Shift+Insert 粘贴 / Shift+Delete 剪切
		if (editMode && usesimpleeditor && !ctrl) {
			if (e.Key == Key.Insert && shift) {
				e.Handled = true;
				pasteplaintextfromclipboard();
				return;
			}
			if (e.Key == Key.Delete && shift) {
				e.Handled = true;
				cutselectionplaintext();
				return;
			}
			// Delete / Backspace 大选区：禁止 RTB 逐段删（1800 行可卡 2–3s）
			if (e.Key == Key.Delete || e.Key == Key.Back) {
				if (trybulkdeleteselection()) {
					e.Handled = true;
					return;
				}
			}
		}
		if (e.Key != Key.Tab) return;
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
			|| Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
			return;
		e.Handled = true;
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
			removetabatcaret();
		else
			insertplaintext(softtabinsert());
	}

	void onsourcepasting(object sender, DataObjectPastingEventArgs e) {
		if (!editMode) return;
		try {
			if (trypasteimages(e.DataObject)) {
				e.CancelCommand();
				return;
			}
			// 纯代码：取消 RTB 默认粘贴，走字符串批量替换（大块不卡）
			if (usesimpleeditor) {
				var t = clipboardtextfromdata(e.DataObject);
				if (t != null) {
					e.CancelCommand();
					replaceselectionwith(t);
				}
			}
		} catch (Exception ex) {
			DocLog.Warn($"Md paste: {ex.Message}");
		}
	}

	static string clipboardtextfromdata(IDataObject data) {
		if (data == null) return null;
		try {
			if (data.GetDataPresent(DataFormats.UnicodeText)) {
				var t = data.GetData(DataFormats.UnicodeText) as string;
				if (t != null) return t;
			}
			if (data.GetDataPresent(DataFormats.Text))
				return data.GetData(DataFormats.Text) as string;
		} catch { /* ignore */ }
		return null;
	}

	void pasteplaintextfromclipboard() {
		try {
			if (trypasteimages(Clipboard.GetDataObject())) return;
			var t = Clipboard.ContainsText() ? Clipboard.GetText() : null;
			if (t == null) return;
			replaceselectionwith(t);
		} catch (Exception ex) {
			DocLog.Warn($"Md paste text: {ex.Message}");
		}
	}

	void cutselectionplaintext() {
		try {
			if (!editMode) return;
			// 大选区不 sync 全文；小选区仍 sync 保证一致
			var large = selectionlookslarge();
			if (!trygetbulksel(out var text, out var a, out var b, forceSync: !large)) return;
			var selected = text.Substring(a, b - a);
			try { Clipboard.SetText(selected.Replace("\n", "\r\n")); } catch { /* ignore */ }
			pushundo(text, a);
			applybulktext(text.Substring(0, a) + text.Substring(b), a);
		} catch (Exception ex) {
			DocLog.Warn($"Md cut: {ex.Message}");
		}
	}

	/// <summary>
	/// 大选区 Delete/Backspace：用字符串批量删，避免 RTB 逐段拆除着色 Run。
	/// 无选区或选区很小时返回 false，交给 RTB。
	/// </summary>
	bool trybulkdeleteselection() {
		try {
			if (!editMode || !usesimpleeditor) return false;
			// 先用行号粗判；大选区禁止 getsourceplain 全文扫描（那也要 1s+）
			if (!selectionlookslarge()) return false;
			// 信任 rawText（日常编辑已同步），勿再 sync 整篇
			if (!trygetbulksel(out var text, out var a, out var b, forceSync: false))
				return false;
			if (b - a < BULK_EDIT_CHARS) {
				var la = lineof(text, a);
				var lb = lineof(text, b);
				if (lb - la < 12) return false;
			}
			pushundo(text, a);
			applybulktext(text.Substring(0, a) + text.Substring(b), a);
			return true;
		} catch (Exception ex) {
			DocLog.Warn($"Md bulk delete: {ex.Message}");
			return false;
		}
	}

	/// <summary>选区是否跨多段/多行（不读 Selection.Text，避免大选区卡顿）。</summary>
	bool selectionlookslarge() {
		try {
			var sel = sourceBox.Selection;
			if (sel == null || sel.IsEmpty) return false;
			var a = lineofpointer(sel.Start);
			var b = lineofpointer(sel.End);
			if (b < a) { var t = a; a = b; b = t; }
			if (b - a >= 12) return true;
			return false;
		} catch { return false; }
	}

	/// <summary>返回有效选区 [a,b)。大选区请 forceSync=false，避免全文导出。</summary>
	bool trygetbulksel(out string text, out int a, out int b, bool forceSync = true) {
		text = "";
		a = b = 0;
		if (forceSync && !suppressText && !bulkApplying)
			syncrawfromeditor();
		text = rawText ?? "";
		getselectionoffsetsfast(out a, out b);
		if (a < 0) a = 0;
		if (b > text.Length) b = text.Length;
		if (a > text.Length) a = text.Length;
		if (b < a) { var t = a; a = b; b = t; }
		return a < b;
	}

	/// <summary>TextPointer 所在逻辑行（0-based），仅扫块不取 Text。</summary>
	int lineofpointer(TextPointer tp) {
		if (tp == null) return 0;
		try {
			var line = 0;
			foreach (var b in sourceBox.Document.Blocks) {
				if (b is Paragraph p) {
					if (tp.CompareTo(p.ContentEnd) <= 0) {
						if (tp.CompareTo(p.ContentStart) < 0)
							return line > 0 ? line - 1 : 0;
						return line;
					}
					line++;
				} else if (b is Table tbl) {
					var n = tablelinecountfromtag(tbl.Tag as string);
					if (tp.CompareTo(tbl.ContentEnd) <= 0 && tp.CompareTo(tbl.ContentStart) >= 0)
						return line;
					line += n;
				}
			}
			return line > 0 ? line - 1 : 0;
		} catch { return 0; }
	}

	/// <summary>
	/// 用纯文本替换当前选区（或在光标插入）。
	/// 小改动就地补丁（其它行高亮不闪）；大块才整换 Document。
	/// </summary>
	void replaceselectionwith(string insert) {
		if (!editMode) return;
		insert = (insert ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		try {
			// 同步权威源码
			if (!suppressText && !bulkApplying)
				syncrawfromeditor();
			var text = rawText ?? "";
			getselectionoffsetsfast(out var a, out var b);
			if (a < 0) a = 0;
			if (b < a) b = a;
			if (a > text.Length) a = text.Length;
			if (b > text.Length) b = text.Length;
			var next = text.Substring(0, a) + insert + text.Substring(b);
			if (string.Equals(next, text, StringComparison.Ordinal)) return;
			var caret = a + insert.Length;
			pushundo(text, a);

			// 是否大块：按删除/插入量与换行数判断（单行粘贴绝不能整篇重解析）
			var removed = b - a;
			var added = insert.Length;
			var newLines = 0;
			for (var i = 0; i < insert.Length; i++)
				if (insert[i] == '\n') newLines++;
			var selLines = 0;
			for (var i = a; i < b && i < text.Length; i++)
				if (text[i] == '\n') selLines++;
			var isBulk = removed >= BULK_EDIT_CHARS || added >= BULK_EDIT_CHARS
				|| newLines >= 12 || selLines >= 12;

			if (isBulk)
				applybulktext(next, caret);
			else
				applysmalltext(next, caret);
		} catch (Exception ex) {
			DocLog.Warn($"Md replaceselection: {ex.Message}");
		}
	}

	/// <summary>小改动：就地差分补丁，保留未改行的语法高亮。</summary>
	void applysmalltext(string text, int caret) {
		bulkApplying = true;
		suppressText = true;
		suppressUndo = true;
		try {
			var prevFence = lineFenceKind;
			// 改 rawText 前取出旧行缓存，差分时用字符串比，避免逐段读 Run
			List<string> oldLines = null;
			try { oldLines = getlinescached(); } catch { /* ignore */ }
			rawText = text ?? "";
			invalidatelinecache();
			var len = (rawText ?? "").Length;
			if (caret < 0) caret = 0;
			if (caret > len) caret = len;
			nextHlAt = 0;
			// 取消进行中的整篇分片上色，避免把刚贴的内容再整页刷掉
			++undoHlGen;
			var lines = getlinescached();
			if (!trypatchdocumentlines(lines, prevFence, oldLines)) {
				// 含 Table 等：退回整换，但小改动很少走到
				setsourceplain(rawText);
				setcaretoffset(caret);
				var ln = lineof(rawText, caret);
				if (ln >= 0 && ln < lines.Count)
					recoloroneline(ln, showRaw: true, preferOffset: caret);
			} else {
				setcaretoffset(caret);
			}
			fenceStructureDirty = false;
			setdirty(true);
			if (hassidepreview)
				schedulepreview();
			StatusChanged?.Invoke();
		} finally {
			suppressUndo = false;
			suppressText = false;
			bulkApplying = false;
		}
	}

	/// <summary>大块写回：整换 plain Document + 分片上色（大删改绝不逐段 Remove）。</summary>
	void applybulktext(string text, int caret) {
		bulkApplying = true;
		suppressText = true;
		suppressUndo = true; // pushundo 已在调用方完成
		var t0 = Environment.TickCount;
		try {
			rawText = text ?? "";
			invalidatelinecache();
			var len = (rawText ?? "").Length;
			if (caret < 0) caret = 0;
			if (caret > len) caret = len;
			nextHlAt = 0;
			fenceStructureDirty = false;
			// 一次替换 Document，比 Remove 上千个 Paragraph 快一个数量级
			setsourceplain(rawText);
			setcaretoffset(caret);
			setdirty(true);
			var buildMs = Environment.TickCount - t0;
			if (buildMs >= 30)
				DocLog.Info($"Md applybulktext chars={len} ms={buildMs}");
			// 分片上色，避免一次 Clear 上万行
			schedulechunkhighlight(++undoHlGen, caret);
			if (hassidepreview)
				schedulepreview();
			else {
				// 目录解析大文档也贵：延后到 idle
				schedulebulktoc();
			}
			StatusChanged?.Invoke();
		} finally {
			suppressUndo = false;
			suppressText = false;
			bulkApplying = false;
		}
	}

	int nextBulkTocAt;

	void schedulebulktoc() {
		nextBulkTocAt = Environment.TickCount + 200;
	}

	static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
		".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff",
	};

	/// <summary>
	/// 剪贴板位图或图片文件 → md 同级 images/，插入 ![alt](images/…)。
	/// 有原名则保留；无名称（截图位图）用时间戳。
	/// </summary>
	bool trypasteimages(IDataObject data) {
		if (data == null || !editMode) return false;
		if (string.IsNullOrWhiteSpace(FilePath)) {
			DocLog.Warn("Md paste image: 文档尚未保存到路径，无法写入 images/");
			return false;
		}
		var inserts = new List<string>();

		// 1) 资源管理器复制的图片文件
		if (data.GetDataPresent(DataFormats.FileDrop)) {
			var files = data.GetData(DataFormats.FileDrop) as string[];
			if (files != null) {
				foreach (var f in files) {
					if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) continue;
					var ext = Path.GetExtension(f);
					if (!ImageExts.Contains(ext)) continue;
					var rel = saveimagefile(f, Path.GetFileName(f));
					if (!string.IsNullOrEmpty(rel))
						inserts.Add(mdimageline(rel, Path.GetFileNameWithoutExtension(rel)));
				}
			}
		}

		// 2) 截图 / 复制的位图（无文件名 → 时间戳.png）
		if (inserts.Count == 0) {
			BitmapSource bmp = null;
			try {
				if (data.GetDataPresent(DataFormats.Bitmap))
					bmp = data.GetData(DataFormats.Bitmap) as BitmapSource;
			} catch { /* ignore */ }
			if (bmp == null) {
				try {
					if (Clipboard.ContainsImage())
						bmp = Clipboard.GetImage();
				} catch { /* ignore */ }
			}
			if (bmp != null) {
				var rel = saveimagebitmap(bmp, null);
				if (!string.IsNullOrEmpty(rel))
					inserts.Add(mdimageline(rel, Path.GetFileNameWithoutExtension(rel)));
			}
		}

		if (inserts.Count == 0) return false;
		var block = string.Join("\n", inserts);
		insertplaintext(block);
		DocLog.Info($"Md paste image n={inserts.Count}");
		return true;
	}

	static string mdimageline(string relPath, string alt) {
		alt ??= "";
		relPath = (relPath ?? "").Replace('\\', '/');
		return $"![{alt}]({relPath})";
	}

	string imagesdir() {
		var baseDir = Path.GetDirectoryName(FilePath);
		if (string.IsNullOrEmpty(baseDir))
			baseDir = Environment.CurrentDirectory;
		var dir = Path.Combine(baseDir, "images");
		Directory.CreateDirectory(dir);
		return dir;
	}

	/// <summary>复制已有图片文件到 images/；preferredName 空则时间戳。</summary>
	string saveimagefile(string srcPath, string preferredName) {
		var ext = Path.GetExtension(srcPath);
		if (string.IsNullOrEmpty(ext)) ext = ".png";
		var name = string.IsNullOrWhiteSpace(preferredName)
			? timestampimgname(ext)
			: sanitizefilename(preferredName);
		if (string.IsNullOrEmpty(Path.GetExtension(name)))
			name += ext;
		var dest = uniqueimagepath(imagesdir(), name);
		File.Copy(srcPath, dest, overwrite: false);
		return "images/" + Path.GetFileName(dest);
	}

	/// <summary>位图存 PNG 到 images/；preferredName 空则时间戳。</summary>
	string saveimagebitmap(BitmapSource bmp, string preferredName) {
		if (bmp == null) return null;
		if (bmp.CanFreeze && !bmp.IsFrozen) {
			try { bmp.Freeze(); } catch { /* ignore */ }
		}
		var name = string.IsNullOrWhiteSpace(preferredName)
			? timestampimgname(".png")
			: sanitizefilename(preferredName);
		if (string.IsNullOrEmpty(Path.GetExtension(name)))
			name += ".png";
		else if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
			name = Path.GetFileNameWithoutExtension(name) + ".png";
		var dest = uniqueimagepath(imagesdir(), name);
		var enc = new PngBitmapEncoder();
		enc.Frames.Add(BitmapFrame.Create(bmp));
		using (var fs = File.Create(dest))
			enc.Save(fs);
		return "images/" + Path.GetFileName(dest);
	}

	static string timestampimgname(string ext) {
		if (string.IsNullOrEmpty(ext)) ext = ".png";
		if (ext[0] != '.') ext = "." + ext;
		return DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ext;
	}

	static string sanitizefilename(string name) {
		if (string.IsNullOrWhiteSpace(name)) return timestampimgname(".png");
		name = Path.GetFileName(name.Trim());
		foreach (var c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');
		name = name.Trim(' ', '.');
		if (string.IsNullOrEmpty(name)) return timestampimgname(".png");
		return name;
	}

	static string uniqueimagepath(string dir, string fileName) {
		fileName = sanitizefilename(fileName);
		var dest = Path.Combine(dir, fileName);
		if (!File.Exists(dest)) return dest;
		var stem = Path.GetFileNameWithoutExtension(fileName);
		var ext = Path.GetExtension(fileName);
		for (var i = 2; i < 1000; i++) {
			dest = Path.Combine(dir, $"{stem}_{i}{ext}");
			if (!File.Exists(dest)) return dest;
		}
		return Path.Combine(dir, timestampimgname(ext));
	}

	/// <summary>按 MdTabSize 插入空格至下一制表位（软 Tab，源码区显示宽度与设置一致）。</summary>
	string softtabinsert() {
		var tabSize = AppSettings.Current?.MdTabSize ?? 3;
		if (tabSize < 1) tabSize = 1;
		try {
			if (!suppressText) {
				var cur = getsourceplain();
				if (cur != null) rawText = cur;
			}
		} catch { /* keep */ }
		var text = rawText ?? "";
		getselectionoffsets(out var a, out var _);
		if (a < 0) a = 0;
		if (a > text.Length) a = text.Length;
		var lineStart = 0;
		for (var i = a - 1; i >= 0; i--) {
			if (text[i] == '\n') { lineStart = i + 1; break; }
		}
		var prefix = text.Substring(lineStart, a - lineStart);
		var col = 0;
		foreach (var ch in prefix) {
			if (ch == '\t') col += tabSize - (col % tabSize);
			else col++;
		}
		var n = tabSize - (col % tabSize);
		if (n <= 0) n = tabSize;
		return new string(' ', n);
	}

	void insertplaintext(string insert) {
		if (string.IsNullOrEmpty(insert) || !editMode) return;
		// 纯代码：与粘贴同一路径（小插入仍很快；大插入不整篇 HL）
		if (usesimpleeditor) {
			replaceselectionwith(insert);
			return;
		}
		// Typora：先把当前 RTB 同步进 rawText
		try {
			if (!suppressText) {
				var cur = getsourceplain();
				if (cur != null) rawText = cur;
			}
		} catch { /* keep */ }
		var text = rawText ?? "";
		getselectionoffsets(out var a, out var b);
		if (a < 0) a = 0;
		if (b < a) b = a;
		if (a > text.Length) a = text.Length;
		if (b > text.Length) b = text.Length;
		pushundo(text, a);
		rawText = text.Substring(0, a) + insert + text.Substring(b);
		invalidatelinecache();
		setdirty(true);
		rebuildeditorfromraw(a + insert.Length);
		schedulepreview();
		StatusChanged?.Invoke();
	}

	/// <summary>删除光标前一个 \\t，或当前选区；若在行首连续空白则去掉一个 Tab。</summary>
	void removetabatcaret() {
		try {
			if (!suppressText) {
				var cur = getsourceplain();
				if (cur != null) rawText = cur;
			}
		} catch { /* keep */ }
		var text = rawText ?? "";
		getselectionoffsets(out var a, out var b);
		if (a < 0) a = 0;
		if (b > text.Length) b = text.Length;
		if (a > text.Length) a = text.Length;
		if (b < a) b = a;
		if (a != b) {
			pushundo(text, a);
			rawText = text.Substring(0, a) + text.Substring(b);
			invalidatelinecache();
			setdirty(true);
			rebuildeditorfromraw(a);
			schedulepreview();
			StatusChanged?.Invoke();
			return;
		}
		// 光标前是 \t 则删掉；否则按 MdTabSize 删前导空格
		if (a > 0 && text[a - 1] == '\t') {
			pushundo(text, a);
			rawText = text.Substring(0, a - 1) + text.Substring(a);
			invalidatelinecache();
			setdirty(true);
			rebuildeditorfromraw(a - 1);
			schedulepreview();
			StatusChanged?.Invoke();
			return;
		}
		var tabSize = AppSettings.Current?.MdTabSize ?? 3;
		if (tabSize < 1) tabSize = 1;
		var del = 0;
		while (del < tabSize && a - 1 - del >= 0 && text[a - 1 - del] == ' ')
			del++;
		if (del <= 0) return;
		pushundo(text, a);
		rawText = text.Substring(0, a - del) + text.Substring(a);
		invalidatelinecache();
		setdirty(true);
		rebuildeditorfromraw(a - del);
		schedulepreview();
		StatusChanged?.Invoke();
	}

	void clearundostacks() {
		undoStack.Clear();
		redoStack.Clear();
		lastUndoAt = 0;
	}

	void pushundo(string prevText, int caret) {
		if (suppressUndo) return;
		prevText ??= "";
		var now = Environment.TickCount;
		// 合并连续输入为一次撤销单元
		if (undoStack.Count > 0 && lastUndoAt != 0 && now - lastUndoAt < UNDO_MERGE_MS) {
			lastUndoAt = now;
			return;
		}
		if (undoStack.Count > 0 && undoStack[undoStack.Count - 1].Text == prevText)
			return;
		undoStack.Add((prevText, caret < 0 ? 0 : caret));
		if (undoStack.Count > MAX_UNDO)
			undoStack.RemoveAt(0);
		redoStack.Clear();
		lastUndoAt = now;
	}

	/// <summary>撤销到上一编辑单元。成功返回 true。</summary>
	public bool UndoEdit() {
		if (undoStack.Count == 0) return false;
		var cur = rawText ?? "";
		var caret = safegetcaretoffset();
		var snap = undoStack[undoStack.Count - 1];
		undoStack.RemoveAt(undoStack.Count - 1);
		redoStack.Add((cur, caret));
		if (redoStack.Count > MAX_UNDO)
			redoStack.RemoveAt(0);
		applyundorsnap(snap.Text, snap.Caret);
		return true;
	}

	/// <summary>重做。成功返回 true。</summary>
	public bool RedoEdit() {
		if (redoStack.Count == 0) return false;
		var cur = rawText ?? "";
		var caret = safegetcaretoffset();
		var snap = redoStack[redoStack.Count - 1];
		redoStack.RemoveAt(redoStack.Count - 1);
		undoStack.Add((cur, caret));
		if (undoStack.Count > MAX_UNDO)
			undoStack.RemoveAt(0);
		applyundorsnap(snap.Text, snap.Caret);
		return true;
	}

	/// <summary>撤销异步高亮代数：丢弃过期的整篇重绘。</summary>
	int undoHlGen;

	void applyundorsnap(string text, int caret) {
		suppressUndo = true;
		try {
			var prevFence = lineFenceKind;
			rawText = text ?? "";
			invalidatelinecache();
			setdirty(true);
			nextHlAt = 0;
			// 纯代码/侧预：就地按行修补（不换 Document、不整篇重绘）
			if (usesimpleeditor)
				rebuildeditorfast(caret, prevFence);
			else
				rebuildeditorfromraw(caret);
			// 纯代码无侧预：不必为撤销触发预览防抖；目录仍可轻量刷新
			if (hassidepreview)
				schedulepreview();
			else
				try { buildtoc(); } catch { /* ignore */ }
			StatusChanged?.Invoke();
		} finally {
			suppressUndo = false;
			lastUndoAt = 0; // 下一键重新开撤销单元
		}
	}

	/// <summary>
	/// 撤销/重做快路径：优先就地改行（O(变更行)）；仅失败时整篇替换。
	/// 不再在 Background 整篇 applysourcehighlight（那是 ~0.3s 卡顿主因）。
	/// </summary>
	void rebuildeditorfast(int caret, byte[] prevFenceKinds) {
		var gen = ++undoHlGen;
		try {
			var len = (rawText ?? "").Length;
			if (caret < 0) caret = 0;
			if (caret > len) caret = len;
			var lines = getlinescached();
			var paraN = countparagraphs();
			var lineN = lines?.Count ?? 0;
			// 行数剧变（大块删除/粘贴的撤销）：整换 Document，勿逐段增删
			var lineDelta = Math.Abs(lineN - paraN);
			suppressText = true;
			try {
				if (lineDelta >= 40 || lineN >= BULK_HL_LINES && lineDelta >= 12
					|| !trypatchdocumentlines(lines, prevFenceKinds, null)) {
					setsourceplain(rawText);
					setcaretoffset(caret);
					schedulechunkhighlight(gen, caret);
				} else {
					setcaretoffset(caret);
				}
			} finally {
				suppressText = false;
			}
			fenceStructureDirty = false;
		} catch (Exception ex) {
			DocLog.Warn($"rebuildeditorfast: {ex.Message}");
			rebuildeditorfromraw(caret);
		}
	}

	/// <summary>
	/// 就地同步 Document 与 lines：用公共前缀/后缀差分，只改中间变化段。
	/// 中间插入一行时，后面未改行的 Paragraph 原样保留（高亮不闪）。
	/// </summary>
	/// <param name="oldLinesSnap">变更前的行缓存；有则优先用其做相等判断，避免读 Run。</param>
	bool trypatchdocumentlines(List<string> lines, byte[] prevFenceKinds, List<string> oldLinesSnap) {
		if (sourceBox?.Document == null || lines == null) return false;
		var blocks = sourceBox.Document.Blocks;
		var paras = new List<Paragraph>();
		foreach (var b in blocks) {
			if (b is Paragraph p) paras.Add(p);
			else return false;
		}

		var lh = sourceBox.FontSize * 1.45;
		var oldN = paras.Count;
		var newN = lines.Count;

		bool lineeq(int oi, int ni) {
			var want = lines[ni] ?? "";
			bool sameText;
			if (oldLinesSnap != null && oi >= 0 && oi < oldLinesSnap.Count)
				sameText = string.Equals(oldLinesSnap[oi] ?? "", want, StringComparison.Ordinal);
			else
				sameText = string.Equals(paragraphplaintext(paras[oi]), want, StringComparison.Ordinal);
			if (!sameText) return false;
			// 255=未知，与 0（正文）视为相同，避免误伤前缀导致整段重绘
			var ka = fencekindat(prevFenceKinds, oi);
			var kb = fencekindat(lineFenceKind, ni);
			if (ka == 255) ka = 0;
			if (kb == 255) kb = 0;
			return ka == kb;
		}

		// 公共前缀
		var pref = 0;
		while (pref < oldN && pref < newN && lineeq(pref, pref))
			pref++;
		// 公共后缀（不与前缀重叠）
		var suf = 0;
		while (suf < oldN - pref && suf < newN - pref
			&& lineeq(oldN - 1 - suf, newN - 1 - suf))
			suf++;

		var oldMidEnd = oldN - suf; // exclusive
		var newMidEnd = newN - suf;

		// 删除旧中间段（从后往前）
		for (var i = oldMidEnd - 1; i >= pref; i--) {
			blocks.Remove(paras[i]);
			paras.RemoveAt(i);
		}

		// 插入新中间段（仅这些行重绘高亮）
		for (var i = pref; i < newMidEnd; i++) {
			var p = new Paragraph {
				Margin = new Thickness(0),
				Padding = new Thickness(0),
				LineHeight = lh,
			};
			fillsourceline(p, lines, i, showRaw: true);
			var insertIdx = i;
			if (insertIdx >= paras.Count) {
				blocks.Add(p);
				paras.Add(p);
			} else {
				blocks.InsertBefore(paras[insertIdx], p);
				paras.Insert(insertIdx, p);
			}
		}

		if (newN == 0 && paras.Count == 0) {
			blocks.Add(new Paragraph(new Run("")) {
				Margin = new Thickness(0),
				LineHeight = lh,
			});
		}
		invalidateparacache();
		return true;
	}

	static byte fencekindat(byte[] arr, int i) {
		if (arr == null || i < 0 || i >= arr.Length) return 255;
		return arr[i];
	}

	/// <summary>段落可见纯文本（简单编辑无 conceal Tag 时即源码）。</summary>
	static string paragraphplaintext(Paragraph p) {
		if (p == null) return "";
		try {
			var sb = new StringBuilder();
			appendsourceinlines(sb, p.Inlines);
			return sb.ToString();
		} catch {
			try {
				return new TextRange(p.ContentStart, p.ContentEnd).Text?
					.Replace("\r\n", "\n").Replace("\r", "").Replace("\n", "") ?? "";
			} catch { return ""; }
		}
	}

	/// <summary>整篇替换后的分片高亮：视口优先，再 ApplicationIdle 涂离屏行。</summary>
	void schedulechunkhighlight(int gen, int caret) {
		var lineCount = 0;
		try { lineCount = getlinescached()?.Count ?? 0; } catch { /* ignore */ }
		var chunk = lineCount > 2000 ? 80 : lineCount > 500 ? 60 : 40;
		getviewportlinerange(out var vTop, out var vBot);
		var caretLn = 0;
		try { caretLn = lineof(rawText ?? "", caret); } catch { /* ignore */ }
		var priA = Math.Max(0, Math.Min(vTop, caretLn) - VIEWPORT_HL_PAD);
		var priB = Math.Min(Math.Max(0, lineCount - 1), Math.Max(vBot, caretLn) + VIEWPORT_HL_PAD);
		// phase0=视口；phase1=视口前；phase2=视口后
		var phase = 0;
		var next = priA;

		void step() {
			try {
				if (gen != undoHlGen || !editMode || !usesimpleeditor) return;
				var lines = getlinescached();
				if (lines == null || lines.Count == 0) return;
				int a, b;
				if (phase == 0) {
					a = next;
					b = Math.Min(priB + 1, next + chunk);
				} else if (phase == 1) {
					a = next;
					b = Math.Min(priA, next + chunk);
				} else {
					a = next;
					b = Math.Min(lines.Count, next + chunk);
				}
				suppressText = true;
				try {
					for (var i = a; i < b; i++)
						recoloroneline(i, showRaw: true, preferOffset: null);
				} finally {
					suppressText = false;
				}
				next = b;
				var more = false;
				if (phase == 0) {
					if (next <= priB)
						more = true;
					else if (priA > 0) {
						phase = 1;
						next = 0;
						more = true;
					} else if (priB + 1 < lines.Count) {
						phase = 2;
						next = priB + 1;
						more = true;
					}
				} else if (phase == 1) {
					if (next < priA)
						more = true;
					else if (priB + 1 < lines.Count) {
						phase = 2;
						next = priB + 1;
						more = true;
					}
				} else {
					more = next < lines.Count;
				}
				if (more) {
					sourceBox.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(step));
				} else {
					suppressText = true;
					try { setcaretoffset(caret); }
					finally { suppressText = false; }
				}
			} catch (Exception ex) {
				DocLog.Warn($"chunkHL: {ex.Message}");
			}
		}
		try {
			sourceBox.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(step));
		} catch { /* ignore */ }
	}

	int safegetcaretoffset() {
		try { return getcaretoffset(); }
		catch { return 0; }
	}

	void getselectionoffsets(out int start, out int end) =>
		getselectionoffsetsfast(out start, out end);

	/// <summary>按段落推算选区在 rawText 中的偏移（避免全文 TextRange O(n)）。</summary>
	void getselectionoffsetsfast(out int start, out int end) {
		start = 0;
		end = 0;
		try {
			start = pointertorawoffset(sourceBox.Selection.Start);
			end = pointertorawoffset(sourceBox.Selection.End);
			if (end < start) {
				var t = start;
				start = end;
				end = t;
			}
		} catch {
			start = safegetcaretoffset();
			end = start;
		}
	}

	/// <summary>TextPointer → rawText 字符偏移（按行块累加，不用 ContentStart 全文 Range）。</summary>
	int pointertorawoffset(TextPointer tp) {
		if (tp == null) return 0;
		try {
			var text = rawText ?? "";
			var line = 0;
			foreach (var b in sourceBox.Document.Blocks) {
				if (b is Paragraph p) {
					// 落在本段
					if (tp.CompareTo(p.ContentEnd) <= 0) {
						if (tp.CompareTo(p.ContentStart) <= 0)
							return offsetofline(text, line);
						var baseOff = offsetofline(text, line);
						var lines = getlinescached();
						var maxLocal = (lines != null && line < lines.Count)
							? (lines[line] ?? "").Length
							: int.MaxValue;
						if (tp.CompareTo(p.ContentEnd) >= 0)
							return baseOff + (maxLocal == int.MaxValue ? 0 : maxLocal);
						var tr = new TextRange(p.ContentStart, tp);
						var local = (tr.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Length;
						if (local > maxLocal) local = maxLocal;
						return baseOff + local;
					}
					line++;
				} else if (b is Table tbl) {
					var n = tablelinecountfromtag(tbl.Tag as string);
					if (tp.CompareTo(tbl.ContentEnd) <= 0 && tp.CompareTo(tbl.ContentStart) >= 0)
						return offsetofline(text, line);
					line += n;
				}
			}
			return text.Length;
		} catch { return 0; }
	}

	int textoffset(TextPointer tp) => pointertorawoffset(tp);

	void rebuildeditorfromraw(int caret) {
		// applysourcehighlight 内部已 suppressText + 保滚动
		try {
			var len = (rawText ?? "").Length;
			if (caret < 0) caret = 0;
			if (caret > len) caret = len;
			applysourcehighlight(force: true, preferOffset: caret);
			try {
				var p = paragraphat(caretLine);
				p?.BringIntoView();
			} catch { /* ignore */ }
		} catch (Exception ex) {
			DocLog.Warn($"rebuildeditorfromraw: {ex.Message}");
		}
	}

	void schedulepreview() {
		nextPreviewAt = Environment.TickCount + PREVIEW_DEBOUNCE_MS;
	}
	void schedulelinehl() {
		var n = 0;
		try { n = cachedLines?.Count ?? 0; } catch { /* ignore */ }
		// 长文档加长防抖，减少键入时 Clear Inlines
		var ms = n > 4000 ? 520 : n > 1500 ? 400 : LINE_HL_MS;
		nextHlAt = Environment.TickCount + ms;
	}

	void ontick() {
		var now = Environment.TickCount;
		if (nextPreviewAt != 0 && now - nextPreviewAt >= 0) {
			nextPreviewAt = 0;
			if (hassidepreview) {
				rebuildpreview(force: false);
				try { buildtoc(); } catch { /* ignore */ }
			}
		}
		if (nextBulkTocAt != 0 && now - nextBulkTocAt >= 0) {
			nextBulkTocAt = 0;
			try { buildtoc(); } catch { /* ignore */ }
		}
		if (nextPreviewResizeAt != 0 && now - nextPreviewResizeAt >= 0) {
			nextPreviewResizeAt = 0;
			if (previewvisible)
				rebuildpreview(force: true);
		}
		if (nextHlAt != 0 && now - nextHlAt >= 0) {
			nextHlAt = 0;
			if (editMode)
				applylinehighlight();
		}
		if (pendingConcealSync)
			flushconcealsync();
	}

	/// <summary>预览区可见：纯预览 或 侧预。</summary>
	bool previewvisible => !editMode || hassidepreview;

	void onpreviewsizechanged(object sender, SizeChangedEventArgs e) {
		if (!e.WidthChanged) return;
		if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1) return;
		if (!previewvisible) return;
		var w = previewsurfacewidth();
		if (Math.Abs(w - lastPreviewPageW) < 8) return;
		nextPreviewResizeAt = Environment.TickCount + PREVIEW_RESIZE_MS;
	}

	double previewsurfacewidth() {
		var rawW = usewpfpreview
			? (previewRtb?.ActualWidth ?? 0)
			: (previewWeb?.ActualWidth ?? 0);
		var w = rawW;
		if (w < 40) w = previewSurface?.ActualWidth ?? 0;
		// WPF：PageWidth 用内容区宽（扣 RTB 左右 padding + 垂直滚动条，与实际排版区一致）
		if (usewpfpreview && w > 40)
			w = Math.Max(80, w - MdFlowBuilder.PAGE_PAD_L - MdFlowBuilder.PAGE_PAD_R
				- SystemParameters.VerticalScrollBarWidth);
		try { DocLog.Info($"previewsurfacewidth: raw={rawW} w={w} wpf={usewpfpreview} vscroll={SystemParameters.VerticalScrollBarWidth}"); } catch { }
		return w;
	}

	/// <summary>
	/// 编辑后轻量高亮：只动当前行或当前代码围栏块；禁止小改动整篇 setsourceplain。
	/// </summary>
	void applylinehighlight() {
		if (!editMode) return;
		// 选区中重绘会 Clear Inlines / 复位光标，直接掐选区
		if (isuserselecting()) {
			schedulelinehl();
			return;
		}
		var t0 = Environment.TickCount;
		try {
			var lines = getlinescached();
			if (lines == null) return;
			var pc = countparagraphs();
			var off = safegetcaretoffset();
			var structDirty = fenceStructureDirty;
			fenceStructureDirty = false;
			var lineDelta = Math.Abs(pc - lines.Count);

			// 段落数与逻辑行不一致：就地差分修补（保留未改块高亮）
			if (lineDelta != 0) {
				if (usesimpleeditor) {
					suppressText = true;
					try {
						// 大删/大贴导致行数剧变才整换；+1 行粘贴必须走 patch
						if (lineDelta >= 40) {
							setsourceplain(rawText ?? "");
							setcaretoffset(off);
							schedulechunkhighlight(++undoHlGen, off);
						} else {
							++undoHlGen; // 取消全文件分片上色
							if (!trypatchdocumentlines(lines, null, null)) {
								// 无法 patch 时只重绘光标附近，绝不整页 plain
								var ln0 = lineof(rawText ?? "", off);
								normalizeparasnear(lines, ln0, lineDelta);
								setcaretoffset(off);
								recolornear(ln0, off);
							} else {
								setcaretoffset(off);
							}
						}
					} finally { suppressText = false; }
					return;
				}
				applysourcehighlight(force: true, preferOffset: off);
				return;
			}

			var ln = getcaretlinefast();
			if (useconceal) {
				if (caretLine >= 0 && caretLine != ln && caretLine < lines.Count)
					setlineshowraw(caretLine, false);
			}
			caretLine = ln;

			// 围栏结构有变或光标在代码块内：只重绘该围栏块（过大则视口虚拟化）
			if (structDirty || tryfenceblockrange(ln, out var fa, out var fb)) {
				if (!tryfenceblockrange(ln, out fa, out fb)) {
					recoloroneline(ln, showRaw: true, preferOffset: off);
				} else if (fb - fa > FENCE_FULL_RECOLOR_MAX) {
					getviewportlinerange(out var vt, out var vb);
					var a = Math.Max(fa, Math.Min(vt, ln) - VIEWPORT_HL_PAD);
					var b = Math.Min(fb, Math.Max(vb, ln) + VIEWPORT_HL_PAD);
					// 围栏开/闭行也刷新，避免语言标记/闭合符丢色
					if (fa < a) {
						var show = !useconceal || fa == ln;
						recoloroneline(fa, showRaw: show, preferOffset: fa == ln ? off : (int?)null);
					}
					if (fb > b) {
						var show = !useconceal || fb == ln;
						recoloroneline(fb, showRaw: show, preferOffset: fb == ln ? off : (int?)null);
					}
					for (var i = a; i <= b; i++) {
						var show = !useconceal || i == ln;
						recoloroneline(i, showRaw: show, preferOffset: i == ln ? off : (int?)null);
					}
				} else {
					for (var i = fa; i <= fb; i++) {
						var show = !useconceal || i == ln;
						recoloroneline(i, showRaw: show, preferOffset: i == ln ? off : (int?)null);
					}
				}
			} else {
				recoloroneline(ln, showRaw: true, preferOffset: off);
			}
			restorecaretoffset(off);
		} catch (Exception ex) {
			DocLog.Warn($"Md lineHL: {ex.Message}");
		} finally {
			var dt = Environment.TickCount - t0;
			if (dt >= CLICK_LOG_MS)
				DocLog.Info($"Md lineHL ms={dt}");
		}
	}

	/// <summary>无法完整 patch 时，按行数差在光标附近增删段落并上色。</summary>
	void normalizeparasnear(List<string> lines, int focusLine, int lineDelta) {
		if (lines == null || sourceBox?.Document == null) return;
		var blocks = sourceBox.Document.Blocks;
		var paras = new List<Paragraph>();
		foreach (var b in blocks) {
			if (b is Paragraph p) paras.Add(p);
			else return;
		}
		var lh = sourceBox.FontSize * 1.45;
		if (focusLine < 0) focusLine = 0;
		if (focusLine > lines.Count) focusLine = lines.Count;
		// 删多余
		while (paras.Count > lines.Count && paras.Count > 0) {
			var idx = Math.Min(focusLine, paras.Count - 1);
			blocks.Remove(paras[idx]);
			paras.RemoveAt(idx);
			if (focusLine > 0 && focusLine >= paras.Count) focusLine = paras.Count - 1;
		}
		// 增不足
		while (paras.Count < lines.Count) {
			var i = Math.Min(focusLine, paras.Count);
			var p = new Paragraph {
				Margin = new Thickness(0),
				Padding = new Thickness(0),
				LineHeight = lh,
			};
			var li = Math.Min(i, lines.Count - 1);
			if (li < 0) li = 0;
			fillsourceline(p, lines, li, showRaw: true);
			if (i >= paras.Count) {
				blocks.Add(p);
				paras.Add(p);
			} else {
				blocks.InsertBefore(paras[i], p);
				paras.Insert(i, p);
			}
		}
		// 同步焦点行文本
		if (focusLine >= 0 && focusLine < lines.Count && focusLine < paras.Count) {
			paras[focusLine].Inlines.Clear();
			fillsourceline(paras[focusLine], lines, focusLine, showRaw: true);
		}
		invalidateparacache();
	}

	void recolornear(int ln, int caretOff) {
		if (ln < 0) ln = 0;
		var lines = getlinescached();
		if (lines == null || lines.Count == 0) return;
		var a = Math.Max(0, ln - 1);
		var b = Math.Min(lines.Count - 1, ln + 1);
		for (var i = a; i <= b; i++)
			recoloroneline(i, showRaw: true, preferOffset: i == ln ? caretOff : (int?)null);
	}

	/// <summary>若 line0 落在 ``` / ~~~ 围栏内（含开闭行），返回 [a,b] 行号。</summary>
	bool tryfenceblockrange(int line0, out int a, out int b) {
		a = b = -1;
		try {
			var lines = getlinescached();
			ensurelineindex(lines);
			if (lineFenceKind == null || line0 < 0 || line0 >= lineFenceKind.Length)
				return false;
			var kind = lineFenceKind[line0];
			if (kind == 0) return false;
			// 定位开围栏行（kind=2 且前一行非 body）
			if (kind == 1) {
				a = line0;
				while (a > 0 && lineFenceKind[a] == 1) a--;
				if (lineFenceKind[a] != 2) return false;
			} else {
				// kind==2：可能是开或闭
				if (line0 > 0 && lineFenceKind[line0 - 1] == 1) {
					// 闭围栏：回退到 body 再找 open
					a = line0 - 1;
					while (a > 0 && lineFenceKind[a] == 1) a--;
					if (lineFenceKind[a] != 2) return false;
				} else {
					a = line0; // 开围栏
				}
			}
			// 从 open 找到 close（下一个 kind=2）或块末
			b = a;
			for (var i = a + 1; i < lineFenceKind.Length; i++) {
				if (lineFenceKind[i] == 0) break;
				b = i;
				if (lineFenceKind[i] == 2) break;
			}
			return b >= a;
		} catch {
			return false;
		}
	}

	int countparagraphs() {
		try {
			ensureparacache();
			if (lineParaCache != null) {
				var n = 0;
				for (var i = 0; i < lineParaCache.Length; i++)
					if (lineParaCache[i] != null) n++;
				return n;
			}
		} catch { /* fall through */ }
		var c = 0;
		try {
			foreach (var b in sourceBox.Document.Blocks)
				if (b is Paragraph) c++;
		} catch { /* ignore */ }
		return c;
	}

	async System.Threading.Tasks.Task ensurepreviewasync() {
		if (previewReady) return;
		try {
			// 与 BrowserViewer 共用同一 Environment，避免进程内多环境冲突
			var env = await WebView2Env.GetAsync().ConfigureAwait(true);
			if (previewWeb.CoreWebView2 == null)
				await previewWeb.EnsureCoreWebView2Async(env).ConfigureAwait(true);
			var core = previewWeb.CoreWebView2;
			if (core == null) return;
			// 仍启用以触发 ContextMenuRequested，再清空默认项只留导出
			core.Settings.AreDefaultContextMenusEnabled = true;
			core.Settings.IsStatusBarEnabled = false;
			core.Settings.AreDevToolsEnabled = false;
			core.Settings.IsZoomControlEnabled = false; // 用宿主 ZoomFactor，与工具栏一致
			core.Settings.AreDefaultScriptDialogsEnabled = false;
			core.ContextMenuRequested += onpreviewctxmenu;
			core.NavigationStarting += onpreviewnavstarting;
			core.WebMessageReceived += onpreviewwebmsg;
			core.NavigationCompleted += onpreviewnavcompleted;
			previewReady = true;
			applypreviewzoom();
			mapstaticfolder();
			mapassetfolder();
			if (!string.IsNullOrEmpty(pendingHtml))
				navigatetohtml(pendingHtml);
			DocLog.Info("Md WebView2 ready");
		} catch (Exception ex) {
			DocLog.Warn($"Md WebView2 init fail: {ex.Message}");
		}
	}

	void mapstaticfolder() {
		if (!previewReady || previewWeb.CoreWebView2 == null) return;
		try {
			var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
			if (!Directory.Exists(dir)) return;
			previewWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
				MdHtmlBuilder.StaticHost, Path.GetFullPath(dir),
				CoreWebView2HostResourceAccessKind.Allow);
		} catch (Exception ex) {
			DocLog.Warn($"Md static map: {ex.Message}");
		}
	}

	void mapassetfolder(string assetRoot = null) {
		if (!previewReady || previewWeb.CoreWebView2 == null) return;
		try {
			var dir = assetRoot;
			if (string.IsNullOrEmpty(dir))
				dir = string.IsNullOrEmpty(FilePath) ? null : Path.GetDirectoryName(FilePath);
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
			var full = Path.GetFullPath(dir);
			if (string.Equals(mappedAssetDir, full, StringComparison.OrdinalIgnoreCase))
				return;
			if (!string.IsNullOrEmpty(mappedAssetDir)) {
				try {
					previewWeb.CoreWebView2.ClearVirtualHostNameToFolderMapping(MdHtmlBuilder.AssetHost);
				} catch { /* ignore */ }
			}
			previewWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
				MdHtmlBuilder.AssetHost, full, CoreWebView2HostResourceAccessKind.Allow);
			mappedAssetDir = full;
		} catch (Exception ex) {
			DocLog.Warn($"Md asset map: {ex.Message}");
		}
	}

	/// <summary>MD 预览右键：图片由 JS 弹宿主菜单；其它区域为导出。</summary>
	void onpreviewctxmenu(object sender, CoreWebView2ContextMenuRequestedEventArgs e) {
		try {
			var core = previewWeb?.CoreWebView2;
			if (core?.Environment == null) {
				e.Handled = true;
				return;
			}
			var target = e.ContextMenuTarget;
			// 图片：JS contextmenu 已 preventDefault 并 post imgctx，此处吞掉 WebView 菜单
			if (target != null && target.Kind == CoreWebView2ContextMenuTargetKind.Image) {
				e.Handled = true;
				return;
			}

			var items = e.MenuItems;
			items.Clear();
			var env = core.Environment;
			var miPdf = env.CreateContextMenuItem("导出 PDF", null, CoreWebView2ContextMenuItemKind.Command);
			miPdf.CustomItemSelected += (_, __) => {
				try {
					previewWeb.Dispatcher.BeginInvoke(new Action(exportpdfui));
				} catch (Exception ex) {
					DocLog.Warn($"Md ctx export pdf: {ex.Message}");
				}
			};
			var miHtml = env.CreateContextMenuItem("导出 HTML", null, CoreWebView2ContextMenuItemKind.Command);
			miHtml.CustomItemSelected += (_, __) => {
				try {
					previewWeb.Dispatcher.BeginInvoke(new Action(exporthtmlui));
				} catch (Exception ex) {
					DocLog.Warn($"Md ctx export html: {ex.Message}");
				}
			};
			items.Add(miPdf);
			items.Add(miHtml);
		} catch (Exception ex) {
			DocLog.Warn($"Md ContextMenuRequested: {ex.Message}");
			e.Handled = true;
		}
	}

	/// <summary>WebView 图片 src（md.assets / file / http）→ 本地路径（若有）。</summary>
	string resolvepreviewimgpath(string srcUri) {
		if (string.IsNullOrWhiteSpace(srcUri)) return null;
		srcUri = srcUri.Trim();
		try {
			if (srcUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) {
				var local = new Uri(srcUri).LocalPath;
				if (File.Exists(local)) return local;
			}
		} catch { /* ignore */ }
		try {
			if (File.Exists(srcUri)) return Path.GetFullPath(srcUri);
		} catch { /* ignore */ }
		try {
			var prefix = "https://" + MdHtmlBuilder.AssetHost + "/";
			if (srcUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrEmpty(mappedAssetDir)) {
				var rel = srcUri.Substring(prefix.Length);
				try { rel = Uri.UnescapeDataString(rel); } catch { /* keep */ }
				rel = rel.Replace('/', Path.DirectorySeparatorChar);
				var full = Path.GetFullPath(Path.Combine(mappedAssetDir, rel));
				if (File.Exists(full)) return full;
			}
		} catch { /* ignore */ }
		return null;
	}

	/// <param name="dataPath">img data-path 本地绝对路径（优先）。</param>
	/// <param name="srcUri">img src（md.assets / http）。</param>
	bool tryloadpreviewimage(string dataPath, string srcUri, out BitmapSource bmp, out string path) {
		bmp = null;
		path = null;
		try {
			if (!string.IsNullOrWhiteSpace(dataPath) && File.Exists(dataPath)) {
				path = Path.GetFullPath(dataPath);
				bmp = ImageOverlay.LoadFile(path);
				if (bmp != null) return true;
			}
			path = resolvepreviewimgpath(srcUri);
			if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
				bmp = ImageOverlay.LoadFile(path);
				if (bmp != null) return true;
			}
			// md.assets 不能走 WPF Http 加载；仅真实 http(s) 外链
			if (!string.IsNullOrWhiteSpace(srcUri)
				&& (srcUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
					|| srcUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
				&& srcUri.IndexOf(MdHtmlBuilder.AssetHost, StringComparison.OrdinalIgnoreCase) < 0
				&& srcUri.IndexOf(MdHtmlBuilder.StaticHost, StringComparison.OrdinalIgnoreCase) < 0) {
				bmp = ImageOverlay.LoadUri(srcUri);
				return bmp != null;
			}
		} catch (Exception ex) {
			DocLog.Warn($"Md load preview img: {ex.Message}");
		}
		return false;
	}

	void showpreviewimgctx(string dataPath, string srcUri) {
		if (!tryloadpreviewimage(dataPath, srcUri, out var bmp, out var path)) {
			DocLog.Warn($"Md imgctx load fail path={dataPath} src={srcUri} map={mappedAssetDir}");
			MessageBox.Show(
				"无法加载该图片（本地路径无效或资源未映射）。\n" + (dataPath ?? srcUri ?? ""),
				"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		var cm = new ContextMenu();
		var miCopy = new MenuItem { Header = "复制图片" };
		miCopy.Click += (_, _) => ImageOverlay.CopyImage(bmp);
		var miFile = new MenuItem { Header = "复制为文件" };
		miFile.Click += (_, _) => ImageOverlay.CopyAsFile(path, bmp);
		var miSave = new MenuItem { Header = "保存图片..." };
		miSave.Click += (_, _) => {
			Window owner = null;
			try { owner = Window.GetWindow(root); } catch { /* ignore */ }
			ImageOverlay.SaveImageAs(owner, bmp, path);
		};
		cm.Items.Add(miCopy);
		cm.Items.Add(miFile);
		cm.Items.Add(miSave);
		try {
			cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
			cm.PlacementTarget = previewWeb;
		} catch { /* ignore */ }
		cm.IsOpen = true;
		DocLog.Info($"Md imgctx menu path={path} bmp={(bmp != null ? bmp.PixelWidth + "x" + bmp.PixelHeight : "null")}");
	}

	void onpreviewnavstarting(object sender, CoreWebView2NavigationStartingEventArgs e) {
		// NavigateToString / about:blank / 虚拟主机资源放行；其它外链交给 handlenav
		try {
			var uri = e.Uri ?? "";
			if (string.IsNullOrEmpty(uri)
				|| uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
				|| uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
				|| uri.IndexOf(MdHtmlBuilder.AssetHost, StringComparison.OrdinalIgnoreCase) >= 0
				|| uri.IndexOf(MdHtmlBuilder.StaticHost, StringComparison.OrdinalIgnoreCase) >= 0
				|| uri.IndexOf("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase) >= 0
				|| uri.IndexOf("cdnjs.cloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
				return;
			e.Cancel = true;
			handlenav(uri);
		} catch { /* ignore */ }
	}

	void onpreviewwebmsg(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
		try {
			var json = e.TryGetWebMessageAsString();
			if (string.IsNullOrEmpty(json)) return;
			// 轻量解析，避免引入 JSON 库
			if (json.IndexOf("\"t\":\"nav\"", StringComparison.Ordinal) >= 0
				|| json.IndexOf("\"t\": \"nav\"", StringComparison.Ordinal) >= 0) {
				var href = extractjsonstr(json, "href");
				if (!string.IsNullOrEmpty(href))
					handlenav(href);
				return;
			}
			// 预览区双击图片 → 全窗 overlay
			if (json.IndexOf("\"t\":\"img\"", StringComparison.Ordinal) >= 0
				|| json.IndexOf("\"t\": \"img\"", StringComparison.Ordinal) >= 0) {
				var path = extractjsonstr(json, "path");
				var src = extractjsonstr(json, "src");
				var alt = extractjsonstr(json, "alt");
				if (!string.IsNullOrEmpty(path) && File.Exists(path))
					ImageOverlay.ShowFile(path, string.IsNullOrEmpty(alt) ? null : alt);
				else if (!string.IsNullOrEmpty(src))
					ImageOverlay.ShowUriOrPath(src, string.IsNullOrEmpty(alt) ? null : alt);
				return;
			}
			// 预览区右键图片 → WPF 菜单（复制/复制为文件/保存）
			if (json.IndexOf("\"t\":\"imgctx\"", StringComparison.Ordinal) >= 0
				|| json.IndexOf("\"t\": \"imgctx\"", StringComparison.Ordinal) >= 0) {
				var path = extractjsonstr(json, "path");
				var src = extractjsonstr(json, "src");
				showpreviewimgctx(path, src);
				return;
			}
			if (json.IndexOf("\"t\":\"scroll\"", StringComparison.Ordinal) >= 0
				|| json.IndexOf("\"t\": \"scroll\"", StringComparison.Ordinal) >= 0) {
				var y = extractjsondouble(json, "y");
				var max = extractjsondouble(json, "max");
				previewScrollY = y;
				previewScrollRatio = max > 1 ? y / max : 0;
				contentScrollRatio = previewScrollRatio;
				var top = (int)extractjsondouble(json, "top");
				if (top >= 0) lastPreviewTopLine = top;
				var outline = (int)extractjsondouble(json, "outline");
				if (outline >= 0)
					synctoc(outline);
				// 进度落盘（主窗 StatusChanged → scheduleprogresssave）
				try { StatusChanged?.Invoke(); } catch { /* ignore */ }
				// 侧预：预览滚动比例回写源码
				if (syncingScroll || !hassidepreview) return;
				// 源码驱动的预览滚动尚未落定：忽略回传，避免「一滚就弹回光标处」
				if (ignorePreviewToSourceUntil != 0
					&& Environment.TickCount - ignorePreviewToSourceUntil < 0)
					return;
				try {
					syncingScroll = true;
					var srcSv = findscroll(sourceBox);
					if (srcSv != null)
						srcSv.ScrollToVerticalOffset(previewScrollRatio * srcSv.ScrollableHeight);
				} finally { syncingScroll = false; }
			}
		} catch (Exception ex) {
			DocLog.Warn($"Md webmsg: {ex.Message}");
		}
	}

	void onpreviewnavcompleted(object sender, CoreWebView2NavigationCompletedEventArgs e) {
		if (pendingModeSwitchLine >= 0) {
			var line = pendingModeSwitchLine;
			pendingModeSwitchLine = -1;
			restoreScrollRatioAfterNav = false;
			restoreScrollAfterNav = false;
			suppresspreviewtosource();
			scrollpreviewtoline(line);
			synctoc(line, force: true);
			return;
		}
		if (restoreScrollRatioAfterNav) {
			restoreScrollRatioAfterNav = false;
			restoreScrollAfterNav = false;
			suppresspreviewtosource();
			var r = pendingScrollRatio.ToString(System.Globalization.CultureInfo.InvariantCulture);
			_ = runpreviewjs($"window.mdScrollRatio&&mdScrollRatio({r});");
			_ = synctocfrompreviewasync(force: true);
			return;
		}
		if (!restoreScrollAfterNav) {
			_ = synctocfrompreviewasync(force: true);
			return;
		}
		restoreScrollAfterNav = false;
		suppresspreviewtosource();
		var y = pendingScrollY;
		_ = runpreviewjs($"window.scrollTo(0, {y.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
		_ = synctocfrompreviewasync(force: true);
	}

	void suppresspreviewtosource() {
		ignorePreviewToSourceUntil = Environment.TickCount + PREVIEW_SYNC_SUPPRESS_MS;
	}

	static string extractjsonstr(string json, string key) {
		var needle = "\"" + key + "\"";
		var i = json.IndexOf(needle, StringComparison.Ordinal);
		if (i < 0) return null;
		i = json.IndexOf(':', i + needle.Length);
		if (i < 0) return null;
		i = json.IndexOf('"', i + 1);
		if (i < 0) return null;
		var j = i + 1;
		var sb = new StringBuilder();
		while (j < json.Length) {
			var c = json[j];
			if (c == '\\' && j + 1 < json.Length) {
				sb.Append(json[j + 1]);
				j += 2;
				continue;
			}
			if (c == '"') break;
			sb.Append(c);
			j++;
		}
		return sb.ToString();
	}

	static double extractjsondouble(string json, string key) {
		var needle = "\"" + key + "\"";
		var i = json.IndexOf(needle, StringComparison.Ordinal);
		if (i < 0) return 0;
		i = json.IndexOf(':', i + needle.Length);
		if (i < 0) return 0;
		i++;
		while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
		var j = i;
		while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-' || json[j] == 'e' || json[j] == 'E' || json[j] == '+'))
			j++;
		if (j <= i) return 0;
		if (double.TryParse(json.Substring(i, j - i), System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var v))
			return v;
		return 0;
	}

	void navigatetohtml(string html, string assetRoot = null) {
		pendingHtml = html;
		if (!string.IsNullOrEmpty(assetRoot))
			pendingAssetRoot = assetRoot;
		if (!previewReady || previewWeb.CoreWebView2 == null) return;
		try {
			mapstaticfolder();
			mapassetfolder(pendingAssetRoot);
			previewWeb.NavigateToString(html ?? "");
			pendingHtml = null;
		} catch (Exception ex) {
			DocLog.Warn($"Md NavigateToString: {ex.Message}");
		}
	}

	async System.Threading.Tasks.Task runpreviewjs(string script) {
		if (!previewReady || previewWeb.CoreWebView2 == null || string.IsNullOrEmpty(script))
			return;
		try {
			await previewWeb.ExecuteScriptAsync(script).ConfigureAwait(true);
		} catch (Exception ex) {
			DocLog.Warn($"Md js: {ex.Message}");
		}
	}

	void rebuildpreview(bool force) {
		try {
			if (!previewvisible && !force) return;
			var text = rawText ?? "";
			mdDoc = MdParser.Parse(text);
			var w = previewsurfacewidth();
			if (w < 100) w = 720;
			lastPreviewPageW = w;
			if (usewpfpreview) {
				rebuildpreviewwpf(w);
				return;
			}
			// 保滚动：重建后 NavigationCompleted 再滚回
			if (previewReady && previewWeb.CoreWebView2 != null) {
				_ = capturepreviewscrollthenrebuild(w);
				return;
			}
			var tab = AppSettings.Current?.MdTabSize ?? 3;
			var html = MdHtmlBuilder.Build(mdDoc, FilePath, 1.0, w, tab, out var assetRoot);
			navigatetohtml(html, assetRoot);
		} catch (Exception ex) {
			DocLog.Warn($"Md preview fail: {ex.Message}");
		}
	}

	/// <summary>纯 WPF FlowDocument 预览（MdFlowBuilder）。</summary>
	void rebuildpreviewwpf(double pageW) {
		try {
			// 保滚动比例
			double keepRatio = -1;
			if (hassidepreview) {
				var srcSv = findscroll(sourceBox);
				if (srcSv != null && srcSv.ScrollableHeight > 1)
					keepRatio = srcSv.VerticalOffset / srcSv.ScrollableHeight;
				else
					keepRatio = contentScrollRatio;
			} else {
				var sv = findscroll(previewRtb);
				if (sv != null && sv.ScrollableHeight > 1)
					keepRatio = sv.VerticalOffset / sv.ScrollableHeight;
				else if (previewScrollRatio > 0 || contentScrollRatio > 0)
					keepRatio = previewScrollRatio > 0 ? previewScrollRatio : contentScrollRatio;
			}
			if (pendingModeSwitchLine < 0 && restoreScrollRatioAfterNav)
				keepRatio = pendingScrollRatio;

			var w = pageW > 100 ? pageW : 720;
			// 保证 RTB 页边与 HTML body 一致（重建时不丢）
			previewRtb.Padding = new Thickness(
				MdFlowBuilder.PAGE_PAD_L, MdFlowBuilder.PAGE_PAD_T,
				MdFlowBuilder.PAGE_PAD_R, MdFlowBuilder.PAGE_PAD_B);
			// w 已是内容区宽；缩放用 LayoutTransform
			var fd = MdFlowBuilder.Build(mdDoc, w, handlenav, FilePath, embedImages: true);
			previewRtb.Document = fd;
			try {
				if (w > 40 && Math.Abs(fd.PageWidth - w) > 1)
					fd.PageWidth = w;
			} catch { /* ignore */ }
			applypreviewzoom();

			// 恢复滚动 / 按行
			if (pendingModeSwitchLine >= 0) {
				var line = pendingModeSwitchLine;
				pendingModeSwitchLine = -1;
				restoreScrollRatioAfterNav = false;
				restoreScrollAfterNav = false;
				suppresspreviewtosource();
				root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
					try {
						scrollpreviewtoline(line);
						synctoc(line, force: true);
					} catch { /* ignore */ }
				}));
			} else if (keepRatio >= 0) {
				restoreScrollRatioAfterNav = false;
				var r = keepRatio;
				root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
					try {
						restorewpfscrollratio(r);
						var top = capturewpfpreviewtopline();
						if (top >= 0) {
							lastPreviewTopLine = top;
							synctoc(top, force: true);
						}
					} catch { /* ignore */ }
				}));
			} else {
				_ = synctocfrompreviewasync(force: true);
			}
		} catch (Exception ex) {
			DocLog.Warn($"Md WPF preview: {ex.Message}");
		}
	}

	void restorewpfscrollratio(double ratio) {
		try {
			var sv = findscroll(previewRtb);
			if (sv == null) return;
			var r = Math.Max(0, Math.Min(1, ratio));
			previewScrollRatio = r;
			contentScrollRatio = r;
			sv.ScrollToVerticalOffset(r * sv.ScrollableHeight);
		} catch { /* ignore */ }
	}

	async System.Threading.Tasks.Task capturepreviewscrollthenrebuild(double pageW) {
		try {
			// 侧预编辑：跟源码滚动比例，避免重建后回到旧预览 Y 再反推源码
			if (hassidepreview) {
				var srcSv = findscroll(sourceBox);
				if (srcSv != null && srcSv.ScrollableHeight > 1) {
					pendingScrollRatio = srcSv.VerticalOffset / srcSv.ScrollableHeight;
					restoreScrollRatioAfterNav = true;
					restoreScrollAfterNav = false;
				} else {
					pendingScrollRatio = 0;
					restoreScrollRatioAfterNav = true;
					restoreScrollAfterNav = false;
				}
			} else {
				var raw = await previewWeb.ExecuteScriptAsync("window.mdGetScroll?mdGetScroll():'{\"y\":0,\"max\":0}'")
					.ConfigureAwait(true);
				if (!string.IsNullOrEmpty(raw) && raw.Length >= 2 && raw[0] == '"') {
					raw = System.Text.RegularExpressions.Regex.Unescape(raw.Substring(1, raw.Length - 2));
				}
				pendingScrollY = extractjsondouble(raw ?? "", "y");
				restoreScrollAfterNav = pendingScrollY > 0;
				restoreScrollRatioAfterNav = false;
			}
			suppresspreviewtosource();
			var tab = AppSettings.Current?.MdTabSize ?? 3;
			var html = MdHtmlBuilder.Build(mdDoc, FilePath, 1.0, pageW, tab, out var assetRoot);
			navigatetohtml(html, assetRoot);
		} catch {
			try {
				var tab = AppSettings.Current?.MdTabSize ?? 3;
				var html = MdHtmlBuilder.Build(mdDoc, FilePath, 1.0, pageW, tab, out var assetRoot);
				navigatetohtml(html, assetRoot);
			} catch (Exception ex) {
				DocLog.Warn($"Md preview rebuild: {ex.Message}");
			}
		}
	}

	/// <summary>WPF 预览滚动：更新比例、目录；侧预时反推源码。</summary>
	void onpreviewwpfscroll(object sender, ScrollChangedEventArgs e) {
		if (!usewpfpreview || !previewvisible) return;
		if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
		try {
			var sv = findscroll(previewRtb);
			if (sv != null && sv.ScrollableHeight > 1) {
				previewScrollRatio = sv.VerticalOffset / sv.ScrollableHeight;
				previewScrollY = sv.VerticalOffset;
				contentScrollRatio = previewScrollRatio;
			}
			var top = capturewpfpreviewtopline();
			if (top >= 0) lastPreviewTopLine = top;
			if (top >= 0) synctoc(top);
			try { StatusChanged?.Invoke(); } catch { /* ignore */ }

			if (syncingScroll || !hassidepreview) return;
			if (ignorePreviewToSourceUntil != 0
				&& Environment.TickCount - ignorePreviewToSourceUntil < 0)
				return;
			try {
				syncingScroll = true;
				var srcSv = findscroll(sourceBox);
				if (srcSv != null)
					srcSv.ScrollToVerticalOffset(previewScrollRatio * srcSv.ScrollableHeight);
			} finally { syncingScroll = false; }
		} catch { /* ignore */ }
	}

	/// <summary>WPF 预览视口顶附近块的源行。</summary>
	int capturewpfpreviewtopline() {
		try {
			var fd = previewRtb?.Document;
			if (fd == null) return lastPreviewTopLine;
			previewRtb.UpdateLayout();
			const double margin = 40;
			var bestLine = -1;
			var bestY = double.MinValue;
			var fallbackLine = lastPreviewTopLine;
			var fb = double.MaxValue;
			foreach (var b in fd.Blocks)
				scanblocktopline(b, margin, ref bestLine, ref bestY, ref fallbackLine, ref fb);
			return bestLine >= 0 ? bestLine : fallbackLine;
		} catch {
			return lastPreviewTopLine;
		}
	}

	void scanblocktopline(Block b, double margin, ref int bestLine, ref double bestY,
		ref int fallbackLine, ref double fb) {
		if (b == null) return;
		if (b.Tag is int ln) {
			try {
				var tp = b is Paragraph p ? p.ContentStart
					: (b is BlockUIContainer ? b.ContentStart : b.ContentStart);
				var rect = tp.GetCharacterRect(LogicalDirection.Forward);
				if (rect != Rect.Empty && !double.IsNaN(rect.Top)) {
					var d = Math.Abs(rect.Top);
					if (d < fb) { fb = d; fallbackLine = ln; }
					if (rect.Top <= margin && rect.Top >= bestY) {
						bestY = rect.Top;
						bestLine = ln;
					}
				}
			} catch { /* ignore */ }
		}
		if (b is List list) {
			foreach (var li in list.ListItems)
				foreach (var c in li.Blocks)
					scanblocktopline(c, margin, ref bestLine, ref bestY, ref fallbackLine, ref fb);
		} else if (b is Section sec) {
			foreach (var c in sec.Blocks)
				scanblocktopline(c, margin, ref bestLine, ref bestY, ref fallbackLine, ref fb);
		}
	}

	// ---------- 源码纯文本 ⇄ RichTextBox ----------
	string getsourceplain() {
		try {
			var doc = sourceBox.Document;
			var sb = new StringBuilder(Math.Max(256, (rawText?.Length ?? 0) + 64));
			var first = true;
			var simple = usesimpleeditor;
			foreach (var block in doc.Blocks) {
				if (block is Paragraph p) {
					if (!first) sb.Append('\n');
					first = false;
					if (simple)
						appendsimpleplain(sb, p.Inlines);
					else
						appendsourceinlines(sb, p.Inlines);
				} else if (block is Table tbl) {
					var tag = tbl.Tag as string;
					if (tag != null && tag.StartsWith(TBL_TAG_PREFIX, StringComparison.Ordinal)) {
						if (!first) sb.Append('\n');
						first = false;
						sb.Append(tag, TBL_TAG_PREFIX.Length, tag.Length - TBL_TAG_PREFIX.Length);
					}
				}
			}
			return sb.ToString();
		} catch {
			return rawText ?? "";
		}
	}

	/// <summary>纯代码：只拼 Run 文本，不做 conceal Tag 还原（更快）。</summary>
	static void appendsimpleplain(StringBuilder sb, InlineCollection inlines) {
		if (sb == null || inlines == null) return;
		foreach (var inl in inlines) {
			if (inl is Run r)
				sb.Append(r.Text);
			else if (inl is Span sp)
				appendsimpleplain(sb, sp.Inlines);
		}
	}

	/// <summary>导出 Inlines：mdm: / mdm-ul 还原标记；mdimg 还原 ![…](…)。</summary>
	static void appendsourceinlines(StringBuilder sb, InlineCollection inlines) {
		if (sb == null || inlines == null) return;
		foreach (var inl in inlines) {
			if (inl is Run r) {
				var tag = r.Tag as string;
				if (tag != null && tag.StartsWith(LIST_UL_TAG_PREFIX, StringComparison.Ordinal)) {
					// 列表符被编辑拆 Run 后以 Text 为准（非 ●）
					var raw = tag.Substring(LIST_UL_TAG_PREFIX.Length);
					var tx = r.Text ?? "";
					if (tx.Length > 0 && tx != "●" && tx != raw)
						sb.Append(tx);
					else
						sb.Append(raw);
					continue;
				}
				if (tag != null && tag.StartsWith(MARKER_TAG_PREFIX, StringComparison.Ordinal)) {
					var raw = tag.Substring(MARKER_TAG_PREFIX.Length);
					var tx = r.Text ?? "";
					// 用户在标记 Run 内编辑时 WPF 会拆 Run 且两边仍带完整 Tag；
					// 若仍按 Tag 导出会重复整段（如 ](href) 出现多次）。已编辑则以 Text 为准。
					if (!ismarkerconcealtext(tx, raw))
						sb.Append(tx);
					else
						sb.Append(raw);
					continue;
				}
				sb.Append(r.Text);
			} else if (inl is InlineUIContainer ui) {
				var tag = ui.Tag as string;
				if (tag != null && tag.StartsWith(IMG_TAG_PREFIX, StringComparison.Ordinal))
					sb.Append(tag, IMG_TAG_PREFIX.Length, tag.Length - IMG_TAG_PREFIX.Length);
			} else if (inl is Span sp) {
				appendsourceinlines(sb, sp.Inlines);
			}
		}
	}

	/// <summary>conceal 态：空或等长零宽字符（与 Tag 原文对应）。</summary>
	static bool ismarkerconcealtext(string text, string raw) {
		raw ??= "";
		if (string.IsNullOrEmpty(text)) return true;
		if (text.Length != raw.Length) return false;
		for (var i = 0; i < text.Length; i++)
			if (text[i] != '\u200B') return false;
		return true;
	}

	void setsourceplain(string text) {
		text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		var lh = sourceBox.FontSize * 1.45;
		var fd = new FlowDocument {
			FontFamily = sourceBox.FontFamily,
			FontSize = sourceBox.FontSize,
			PagePadding = new Thickness(0),
			LineHeight = lh,
			Background = Brushes.White,
		};
		var lines = MdParser.SplitLines(text);
		if (lines.Count == 0) {
			fd.Blocks.Add(new Paragraph(new Run("")) {
				Margin = new Thickness(0),
				Padding = new Thickness(0),
				LineHeight = lh,
			});
		} else {
			for (var i = 0; i < lines.Count; i++) {
				fd.Blocks.Add(new Paragraph(new Run(lines[i] ?? "")) {
					Margin = new Thickness(0),
					Padding = new Thickness(0),
					LineHeight = lh,
				});
			}
		}
		// 整文档替换，旧着色树一次性丢弃（比逐段 Delete 快得多）
		sourceBox.Document = fd;
		invalidateparacache();
	}

	// ---------- 语法高亮 + conceal（仅视觉；标记不占位）----------
	/// <summary>
	/// 标记符 Run：Tag=mdm:原文。conceal 时 Text 置空（不占布局），导出从 Tag 还原。
	/// </summary>
	Run markerrun(string text, bool show) {
		text ??= "";
		var r = new Run("") { Tag = MARKER_TAG_PREFIX + text };
		applymarkervisibility(r, show);
		return r;
	}

	/// <summary>ASCII 列表符 -*+：光标行显示原文，其它行显示 ●（Tag 保留原字符供导出）。</summary>
	Run listasciimarkrun(string mark, bool showRaw) {
		if (string.IsNullOrEmpty(mark)) mark = "-";
		var r = new Run(showRaw ? mark : "●") { Tag = LIST_UL_TAG_PREFIX + mark };
		applylistbulletvisibility(r, showRaw);
		return r;
	}

	void applymarkervisibility(Run r, bool show) {
		if (r == null) return;
		var tag = r.Tag as string;
		var raw = "";
		if (tag != null && tag.StartsWith(MARKER_TAG_PREFIX, StringComparison.Ordinal))
			raw = tag.Substring(MARKER_TAG_PREFIX.Length);
		// conceal：零宽字符保长度（光标偏移仍与 rawText 对齐），视觉不占宽
		r.Text = show ? raw : (raw.Length == 0 ? "" : new string('\u200B', raw.Length));
		r.Foreground = show ? brush(0x9C, 0xA3, 0xAF) : Brushes.Transparent;
	}

	void applylistbulletvisibility(Run r, bool showRaw) {
		if (r == null) return;
		var tag = r.Tag as string;
		if (tag == null || !tag.StartsWith(LIST_UL_TAG_PREFIX, StringComparison.Ordinal)) return;
		var raw = tag.Substring(LIST_UL_TAG_PREFIX.Length);
		if (string.IsNullOrEmpty(raw)) raw = "-";
		r.Text = showRaw ? raw : "●";
		r.Foreground = showRaw ? brush(0x9C, 0xA3, 0xAF) : Brushes.Black;
	}

	/// <summary>切换某行标记可见性：含图片/表格行需整行或整块重绘。</summary>
	void setlineshowraw(int line0, bool showRaw) {
		if (line0 < 0) return;
		// 改 Run.Text / Clear Inlines 常把光标打回行首，进出前后用逻辑偏移顶住
		var off = safegetcaretoffset();
		try {
			if (lineintable(line0)) {
				// 表内展开态：按行重绘；若结构仍是折叠表则推迟全量重建（避免 MouseDown 中换 Document）
				var p = paragraphat(line0);
				if (p == null) {
					scheduletablestructhl(line0, off);
					return;
				}
				recoloroneline(line0, showRaw, off);
				return;
			}
			var p0 = paragraphat(line0);
			if (p0 == null) return;
			if (linehasimage(line0)) {
				recoloroneline(line0, showRaw, off);
				return;
			}
			// HR/引用有块级边框，必须整行重绘
			try {
				var lines = getlinescached();
				if (lines != null && line0 < lines.Count) {
					var ln = lines[line0] ?? "";
					if (ishrline(ln) || ln.StartsWith(">", StringComparison.Ordinal)) {
						recoloroneline(line0, showRaw, off);
						return;
					}
				}
			} catch { /* fallthrough */ }
			if (lineHasMarkers != null && line0 < lineHasMarkers.Length
				&& !lineHasMarkers[line0]
				&& (lineFenceKind == null || line0 >= lineFenceKind.Length || lineFenceKind[line0] == 0))
				return;
			foreach (var inl in p0.Inlines) {
				if (inl is not Run r) continue;
				var tag = r.Tag as string;
				if (tag == null) continue;
				if (tag.StartsWith(LIST_UL_TAG_PREFIX, StringComparison.Ordinal))
					applylistbulletvisibility(r, showRaw);
				else if (tag.StartsWith(MARKER_TAG_PREFIX, StringComparison.Ordinal))
					applymarkervisibility(r, showRaw);
			}
		} finally {
			if (!isuserselecting())
				restorecaretoffset(off);
		}
	}

	bool linehasimage(int line0) {
		try {
			var lines = getlinescached();
			if (lines == null || line0 < 0 || line0 >= lines.Count) return false;
			var line = lines[line0];
			return line != null && line.IndexOf("![", StringComparison.Ordinal) >= 0;
		} catch {
			return false;
		}
	}

	List<TableRange> gettablescached(List<string> lines) {
		if (cachedTables != null) return cachedTables;
		cachedTables = scantables(lines);
		return cachedTables;
	}

	static List<TableRange> scantables(List<string> lines) {
		var list = new List<TableRange>();
		if (lines == null) return list;
		for (var i = 0; i < lines.Count; ) {
			if (i + 1 < lines.Count
				&& (lines[i] ?? "").IndexOf('|') >= 0
				&& MdParser.IsTableSeparator(lines[i + 1])) {
				var a = i;
				i += 2;
				while (i < lines.Count
					&& (lines[i] ?? "").IndexOf('|') >= 0
					&& !string.IsNullOrWhiteSpace(lines[i])) {
					if (MdParser.IsTableSeparator(lines[i])) { i++; continue; }
					i++;
				}
				list.Add(new TableRange { A = a, B = i - 1 });
				continue;
			}
			i++;
		}
		return list;
	}

	static bool trytableat(List<TableRange> tables, int line0, out TableRange tr) {
		tr = default;
		if (tables == null) return false;
		foreach (var t in tables) {
			if (t.A == line0) { tr = t; return true; }
		}
		return false;
	}

	bool lineintable(int line0) => tableidat(line0) >= 0;

	int tableidat(int line0) {
		if (line0 < 0) return -1;
		try {
			var lines = getlinescached();
			var tables = gettablescached(lines);
			for (var i = 0; i < tables.Count; i++) {
				if (line0 >= tables[i].A && line0 <= tables[i].B)
					return i;
			}
		} catch { /* ignore */ }
		return -1;
	}

	static int tablelinecountfromtag(string tag) {
		if (string.IsNullOrEmpty(tag) || !tag.StartsWith(TBL_TAG_PREFIX, StringComparison.Ordinal))
			return 1;
		var body = tag.Substring(TBL_TAG_PREFIX.Length);
		if (body.Length == 0) return 1;
		var n = 1;
		foreach (var ch in body)
			if (ch == '\n') n++;
		return n;
	}

	Table buildsourcetable(List<string> lines, TableRange tr) {
		try {
			if (lines == null || tr.A < 0 || tr.B >= lines.Count || tr.B < tr.A) return null;
			var rows = new List<string[]>();
			List<string> align = null;
			for (var i = tr.A; i <= tr.B; i++) {
				var line = lines[i] ?? "";
				if (MdParser.IsTableSeparator(line)) {
					if (align == null) align = MdParser.ParseTableAlignments(line);
					continue;
				}
				rows.Add(MdParser.SplitTableCells(line));
			}
			if (rows.Count == 0) return null;
			var block = new MdBlock {
				Kind = MdBlockKind.Table,
				SourceLine0 = tr.A,
				SourceLine1 = tr.B,
				TableRows = rows,
				TableAlign = align ?? new List<string>(),
			};
			var pageW = sourceBox?.ActualWidth ?? 720;
			if (pageW < 200) pageW = 720;
			var table = MdFlowBuilder.BuildEditorTable(block, FilePath, pageW);
			var sb = new StringBuilder();
			for (var i = tr.A; i <= tr.B; i++) {
				if (i > tr.A) sb.Append('\n');
				sb.Append(lines[i] ?? "");
			}
			table.Tag = TBL_TAG_PREFIX + sb.ToString();
			return table;
		} catch (Exception ex) {
			DocLog.Warn($"Md buildsourcetable: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// 原地重绘单行（编辑后语法/结构变化）。不替换 Document、不跑全文 emoji。
	/// </summary>
	/// <param name="preferOffset">若给定则重绘后恢复该逻辑偏移（优先于段落内测算）。</param>
	void recoloroneline(int line0, bool showRaw, int? preferOffset = null) {
		if (line0 < 0) return;
		var lines = getlinescached();
		if (line0 >= lines.Count) return;
		var p = paragraphat(line0);
		if (p == null) return;
		var t0 = Environment.TickCount;
		suppressText = true;
		try {
			var restoreOff = preferOffset;
			if (restoreOff == null) {
				try {
					var cp = sourceBox.CaretPosition;
					if (cp != null
						&& cp.CompareTo(p.ContentStart) >= 0
						&& cp.CompareTo(p.ContentEnd) <= 0)
						restoreOff = safegetcaretoffset();
				} catch { /* keep null */ }
			}
			p.Inlines.Clear();
			fillsourceline(p, lines, line0, showRaw);
			if (restoreOff != null)
				setcaretoffset(restoreOff.Value);
		} catch (Exception ex) {
			DocLog.Warn($"recoloroneline {line0}: {ex.Message}");
		} finally {
			suppressText = false;
		}
		var dt = Environment.TickCount - t0;
		if (dt >= CLICK_LOG_MS)
			DocLog.Info($"Md recoloroneline line={line0} showRaw={showRaw} ms={dt} len={(lines[line0] ?? "").Length}");
	}

	void setcaretinparagraph(Paragraph p, int localOffset) {
		if (p == null) return;
		try {
			if (localOffset <= 0) {
				sourceBox.CaretPosition = p.ContentStart;
				return;
			}
			var nav = p.ContentStart;
			var seen = 0;
			while (nav != null && nav.CompareTo(p.ContentEnd) < 0) {
				if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text) {
					var run = nav.GetTextInRun(LogicalDirection.Forward) ?? "";
					if (seen + run.Length >= localOffset) {
						sourceBox.CaretPosition = nav.GetPositionAtOffset(localOffset - seen, LogicalDirection.Forward);
						return;
					}
					seen += run.Length;
					nav = nav.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
				} else {
					nav = nav.GetNextContextPosition(LogicalDirection.Forward);
				}
			}
			sourceBox.CaretPosition = p.ContentEnd;
		} catch { /* ignore */ }
	}

	/// <summary>
	/// 全量重建源码高亮（Load / 进编辑 / 结构变化）。点击与普通键入不得走此路径。
	/// Typora：光标行 showRaw，其它行 conceal。
	/// </summary>
	/// <param name="preferLine">优先逻辑行（折叠表时勿用 TextRange 偏移推行号）。</param>
	/// <param name="preferOffset">优先 rawText 字符偏移（插入/撤销后恢复）。</param>
	void applysourcehighlight(bool force, int? preferLine = null, int? preferOffset = null) {
		if (!editMode && !force) return;
		try {
			suppressText = true;
			var text = rawText ?? "";
			var lines = MdParser.SplitLines(text);
			cachedLines = lines;
			cachedLinesSrc = text;
			buildlineindex(lines);

			int caretLn;
			if (preferOffset != null) {
				var off = preferOffset.Value;
				if (off < 0) off = 0;
				if (off > text.Length) off = text.Length;
				caretLn = lineof(text, off);
			} else if (preferLine != null) {
				caretLn = preferLine.Value;
			} else if (caretLine >= 0 && caretLine < lines.Count) {
				// 进出表触发的重建：沿用 onselectionchanged 已定的逻辑行
				caretLn = caretLine;
			} else {
				// 折叠表时 TextRange 偏移会把单元格字算进去，不能用来推行号
				caretLn = getcaretlinefast();
			}
			if (caretLn < 0) caretLn = 0;
			if (lines.Count > 0 && caretLn >= lines.Count) caretLn = lines.Count - 1;
			caretLine = caretLn;

			var sv = findscroll(sourceBox);
			double saveV = 0, saveH = 0;
			if (sv != null) {
				saveV = sv.VerticalOffset;
				saveH = sv.HorizontalOffset;
			}
			var fd = new FlowDocument {
				FontFamily = sourceBox.FontFamily,
				FontSize = sourceBox.FontSize,
				PagePadding = new Thickness(0),
				LineHeight = sourceBox.FontSize * 1.45,
				Background = Brushes.White,
			};
			var conceal = useconceal;
			var tables = gettablescached(lines);
			var tBuild = Environment.TickCount;
			for (var li = 0; li < lines.Count; ) {
				TableRange tr;
				if (conceal && trytableat(tables, li, out tr)
					&& (caretLn < tr.A || caretLn > tr.B)) {
					// Typora：光标不在表内 → 折叠为可视化 Table
					var tbl = buildsourcetable(lines, tr);
					if (tbl != null) {
						fd.Blocks.Add(tbl);
						li = tr.B + 1;
						continue;
					}
				}
				var p = new Paragraph {
					Margin = new Thickness(0),
					Padding = new Thickness(0),
					LineHeight = sourceBox.FontSize * 1.45,
				};
				var showRaw = !conceal || li == caretLn;
				fillsourceline(p, lines, li, showRaw);
				fd.Blocks.Add(p);
				li++;
			}
			var buildMs = Environment.TickCount - tBuild;
			if (lines.Count == 0)
				fd.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });
			sourceBox.Document = fd;
			invalidateparacache();
			// 按逻辑偏移落光标（preferOffset）；否则仅落行首
			if (preferOffset != null)
				setcaretoffset(preferOffset.Value);
			else
				setcarettoline(caretLn);
			if (sv != null && !skipScrollRestoreOnce) {
				try {
					sv.ScrollToVerticalOffset(saveV);
					sv.ScrollToHorizontalOffset(saveH);
				} catch { /* ignore */ }
			}
			skipScrollRestoreOnce = false;
			if (buildMs >= 15)
				DocLog.Info($"Md fullHL lines={lines.Count} buildMs={buildMs}");
		} catch (Exception ex) {
			DocLog.Warn($"Md highlight fail: {ex.Message}");
		} finally {
			suppressText = false;
		}
	}

	List<string> getlinescached() {
		var t = rawText ?? "";
		if (cachedLines != null && cachedLinesSrc == t && lineFenceKind != null)
			return cachedLines;
		cachedLinesSrc = t;
		cachedLines = MdParser.SplitLines(t);
		buildlineindex(cachedLines);
		return cachedLines;
	}

	void ensurelineindex(List<string> lines) {
		if (lineFenceKind != null && lineFenceKind.Length == lines.Count) return;
		buildlineindex(lines);
	}

	/// <summary>预计算围栏状态与是否含 conceal 标记，点击时 O(1) 判断是否重绘。</summary>
	void buildlineindex(List<string> lines) {
		var n = lines?.Count ?? 0;
		lineFenceKind = new byte[n];
		lineHasMarkers = new bool[n];
		lineFenceLang = new string[n];
		var inFence = false;
		var fenceLang = "";
		var fenceCh = '\0';
		for (var i = 0; i < n; i++) {
			var line = lines[i] ?? "";
			if (tryfence(line, out var fch, out var flang, out var closeOnly)) {
				if (!inFence) {
					lineFenceKind[i] = 2; // open
					inFence = true;
					fenceCh = fch;
					fenceLang = flang ?? "";
					lineFenceLang[i] = fenceLang;
				} else if (fch == fenceCh && closeOnly) {
					lineFenceKind[i] = 2; // close
					inFence = false;
					fenceLang = "";
					fenceCh = '\0';
				} else {
					lineFenceKind[i] = 1; // body
					lineFenceLang[i] = fenceLang;
				}
			} else if (inFence) {
				lineFenceKind[i] = 1;
				lineFenceLang[i] = fenceLang;
			} else {
				lineFenceKind[i] = 0;
				lineHasMarkers[i] = linehasmdmarkers(line);
			}
		}
		cachedTables = scantables(lines);
	}

	static bool linehasmdmarkers(string line) {
		if (string.IsNullOrEmpty(line)) return false;
		// 需 conceal 的常见标记；无则点击不重绘
		return line.IndexOf("**", StringComparison.Ordinal) >= 0
			|| line.IndexOf("__", StringComparison.Ordinal) >= 0
			|| line.IndexOf("~~", StringComparison.Ordinal) >= 0
			|| line.IndexOf("==", StringComparison.Ordinal) >= 0
			|| line.IndexOf('`') >= 0
			|| line.IndexOf('[') >= 0
			|| line.IndexOf(']') >= 0
			|| line.IndexOf('|') >= 0
			|| line.TrimStart().StartsWith("#", StringComparison.Ordinal)
			|| line.TrimStart().StartsWith(">", StringComparison.Ordinal)
			|| ishrline(line)
			|| System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*([*+-]|\d+[.)])\s+");
	}

	/// <summary>GFM 分隔线：--- / *** / ___（可夹空格）。</summary>
	static bool ishrline(string line) =>
		!string.IsNullOrEmpty(line)
		&& System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$");

	/// <summary>围栏开/闭行：``` / ~~~ 灰色，语言标识紫色。</summary>
	void fillfencemarkerline(Paragraph p, string line, Brush bg) {
		if (p == null) return;
		line ??= "";
		if (!tryfence(line, out var ch, out var lang, out var closeOnly)) {
			var r = new Run(line) { Foreground = brush(0x6B, 0x72, 0x80) };
			if (bg != null) r.Background = bg;
			p.Inlines.Add(r);
			return;
		}
		// 保留行首缩进
		var lead = 0;
		while (lead < line.Length && (line[lead] == ' ' || line[lead] == '\t')) lead++;
		if (lead > 0)
			p.Inlines.Add(new Run(line.Substring(0, lead)));
		var rest = line.Substring(lead);
		var n = 0;
		while (n < rest.Length && rest[n] == ch) n++;
		var ticks = rest.Substring(0, n);
		var after = n < rest.Length ? rest.Substring(n) : "";
		var rTicks = new Run(ticks) { Foreground = brush(0x6B, 0x72, 0x80) };
		if (bg != null) rTicks.Background = bg;
		p.Inlines.Add(rTicks);
		if (!closeOnly && after.Length > 0) {
			// 语言 + 可选其余
			var rLang = new Run(after) {
				Foreground = brush(0x7C, 0x3A, 0xED),
				FontWeight = FontWeights.SemiBold,
			};
			if (bg != null) rLang.Background = bg;
			p.Inlines.Add(rLang);
		} else if (after.Length > 0) {
			var rRest = new Run(after) { Foreground = brush(0x9C, 0xA3, 0xAF) };
			if (bg != null) rRest.Background = bg;
			p.Inlines.Add(rRest);
		}
	}

	/// <summary>填充一行源码样式（用预计算围栏索引，避免每次 O(n) 扫描）。</summary>
	void fillsourceline(Paragraph p, List<string> lines, int line0, bool showRaw) {
		if (p == null || lines == null || line0 < 0 || line0 >= lines.Count) return;
		var line = lines[line0] ?? "";
		var simple = usesimpleeditor;
		var codeBg = brush(0xF3, 0xF4, 0xF6);
		// 先清掉 HR/引用块级装饰，避免换行后残留
		p.BorderThickness = new Thickness(0);
		p.BorderBrush = null;
		p.Background = Brushes.Transparent;
		p.Padding = new Thickness(0);
		p.Margin = new Thickness(0);
		ensurelineindex(lines);
		var kind = lineFenceKind != null && line0 < lineFenceKind.Length ? lineFenceKind[line0] : (byte)0;
		if (kind == 2) {
			// 围栏开/闭：```lang 语言名单独着色
			if (!simple) p.Background = codeBg;
			fillfencemarkerline(p, line, simple ? null : codeBg);
			return;
		}
		if (kind == 1) {
			var lang = lineFenceLang != null && line0 < lineFenceLang.Length ? lineFenceLang[line0] : "";
			// 代码块正文：语法高亮（纯代码无底色，Typora 保留浅底）
			if (!simple) p.Background = codeBg;
			MdFlowBuilder.AppendCode(p.Inlines, line, lang);
			if (p.Inlines.Count == 0) p.Inlines.Add(new Run(""));
			return;
		}
		// 分隔线
		if (ishrline(line)) {
			if (simple) {
				p.Inlines.Add(new Run(line) { Foreground = brush(0x9C, 0xA3, 0xAF) });
				return;
			}
			fillhrline(p, line, showRaw);
			return;
		}
		// 引用
		if (line.StartsWith(">", StringComparison.Ordinal)) {
			if (simple) {
				// 仅灰色 > 与正文色，无左边框/底色
				p.Inlines.Add(new Run(">") { Foreground = brush(0x9C, 0xA3, 0xAF) });
				if (line.Length > 1 && line[1] == ' ') {
					p.Inlines.Add(new Run(" "));
					colorinlines(p.Inlines, line.Substring(2), true, null, 0, brush(0x4B, 0x55, 0x63));
				} else if (line.Length > 1) {
					colorinlines(p.Inlines, line.Substring(1), true, null, 0, brush(0x4B, 0x55, 0x63));
				}
				return;
			}
			fillquoteline(p, line, showRaw, line0);
			return;
		}
		colorline(p.Inlines, line, showRaw, line0);
		// Typora：标题放大时抬高行高；纯代码等宽行高
		if (!simple) {
			var hi = 0;
			while (hi < line.Length && line[hi] == '#') hi++;
			if (hi > 0 && hi <= 6 && hi < line.Length && line[hi] == ' ') {
				var sizes = new[] { 1.35, 1.22, 1.12, 1.06, 1.02, 1.0 };
				var fs = sourceBox.FontSize * sizes[hi - 1];
				p.LineHeight = Math.Max(p.LineHeight, fs * 1.45);
			}
		}
	}

	void fillhrline(Paragraph p, string line, bool showRaw) {
		var rule = brush(0xD1, 0xD5, 0xDB);
		if (useconceal && !showRaw) {
			// 视觉：底边全宽横线；标记用零宽保长度
			p.BorderBrush = rule;
			p.BorderThickness = new Thickness(0, 0, 0, 1);
			p.Margin = new Thickness(0, 12, 0, 12);
			p.Padding = new Thickness(0);
			p.Inlines.Add(markerrun(line, false));
			return;
		}
		// 光标行 / 纯代码：显示 --- 原文
		p.Margin = new Thickness(0, 4, 0, 4);
		if (useconceal)
			p.Inlines.Add(markerrun(line, true));
		else
			p.Inlines.Add(new Run(line) { Foreground = brush(0x9C, 0xA3, 0xAF) });
	}

	void fillquoteline(Paragraph p, string line, bool showRaw, int srcLine = -1) {
		if (useconceal) {
			p.BorderBrush = brush(0x9C, 0xA3, 0xAF);
			p.BorderThickness = new Thickness(3, 0, 0, 0);
			p.Margin = new Thickness(4, 4, 0, 4);
			p.Padding = new Thickness(12, 4, 8, 4);
			p.Background = brush(0xF3, 0xF4, 0xF6);
		}
		p.Inlines.Add(markerrun(">", showRaw));
		if (line.Length > 1 && line[1] == ' ') {
			p.Inlines.Add(markerrun(" ", showRaw));
			colorinlines(p.Inlines, line.Substring(2), showRaw, null, 0, brush(0x4B, 0x55, 0x63), srcLine);
		} else if (line.Length > 1) {
			colorinlines(p.Inlines, line.Substring(1), showRaw, null, 0, brush(0x4B, 0x55, 0x63), srcLine);
		}
	}

	/// <summary>
	/// 是否围栏行。isCloseOnly=true 表示仅围栏符（可作闭合）；开围栏可带语言。
	/// </summary>
	static bool tryfence(string line, out char fenceCh, out string lang, out bool isCloseOnly) {
		fenceCh = '\0';
		lang = "";
		isCloseOnly = false;
		if (line == null) return false;
		var t = line.TrimStart();
		char ch;
		if (t.StartsWith("```", StringComparison.Ordinal)) ch = '`';
		else if (t.StartsWith("~~~", StringComparison.Ordinal)) ch = '~';
		else return false;
		var i = 0;
		while (i < t.Length && t[i] == ch) i++;
		if (i < 3) return false;
		var rest = t.Substring(i).Trim();
		// 闭合：后面无内容或仅空白
		if (rest.Length == 0) {
			fenceCh = ch;
			isCloseOnly = true;
			return true;
		}
		// 开围栏：语言标识（无空格的单词）
		fenceCh = ch;
		lang = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
		isCloseOnly = false;
		return true;
	}

	void colorline(InlineCollection inlines, string line, bool showMarkers, int srcLine = -1) {
		if (line == null) line = "";
		var simple = usesimpleeditor;
		// 围栏标记行（未在 inFence 状态机中处理时的回退）
		if (tryfence(line, out _, out _, out _)) {
			if (simple) {
				inlines.Add(new Run(line) { Foreground = brush(0x6B, 0x72, 0x80) });
			} else {
				inlines.Add(new Run(line) {
					Foreground = brush(0x6B, 0x72, 0x80),
					Background = brush(0xF3, 0xF4, 0xF6),
				});
			}
			return;
		}
		// 标题：# 标记始终写入；正文按 h1–h6 不同颜色
		var hi = 0;
		while (hi < line.Length && line[hi] == '#') hi++;
		if (hi > 0 && hi <= 6 && hi < line.Length && line[hi] == ' ') {
			var mark = line.Substring(0, hi + 1);
			var body = line.Substring(hi + 1);
			var headFg = headingfg(hi);
			if (simple) {
				// vim 风：同字号，# 与标题同色系略淡，正文 SemiBold + 层级色
				inlines.Add(new Run(mark) { Foreground = headingmarkfg(hi) });
				colorinlines(inlines, body, showMarkers, FontWeights.SemiBold, 0, headFg, srcLine);
			} else {
				inlines.Add(markerrun(mark, showMarkers));
				var sizes = new[] { 1.35, 1.22, 1.12, 1.06, 1.02, 1.0 };
				colorinlines(inlines, body, showMarkers, FontWeights.SemiBold,
					sourceBox.FontSize * sizes[hi - 1], headFg, srcLine);
			}
			return;
		}
		// 列表：ASCII -*+ 在 Typora conceal 时显示为 ●；Unicode ●•○◦ 永不 conceal
		var umAscii = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)([*+-])(\s+)(.*)$");
		if (umAscii.Success) {
			var indent = umAscii.Groups[1].Value;
			var mark = umAscii.Groups[2].Value;
			var sp = umAscii.Groups[3].Value;
			var body = umAscii.Groups[4].Value;
			if (indent.Length > 0) inlines.Add(new Run(indent));
			if (useconceal)
				inlines.Add(listasciimarkrun(mark, showMarkers));
			else
				inlines.Add(new Run(mark) { Foreground = brush(0x9C, 0xA3, 0xAF) });
			if (simple)
				inlines.Add(new Run(sp));
			else
				inlines.Add(markerrun(sp, showMarkers));
			colorinlines(inlines, body, showMarkers, srcLine: srcLine);
			return;
		}
		var umUni = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)([●•○◦])(\s+)(.*)$");
		if (umUni.Success) {
			// ● 等：原文显示，不做 conceal
			var pref = umUni.Groups[1].Value + umUni.Groups[2].Value + umUni.Groups[3].Value;
			var body = umUni.Groups[4].Value;
			if (simple)
				inlines.Add(new Run(pref) { Foreground = brush(0x9C, 0xA3, 0xAF) });
			else
				inlines.Add(new Run(pref));
			colorinlines(inlines, body, showMarkers, srcLine: srcLine);
			return;
		}
		var om = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)(\d{1,9}[.)]\s+)(.*)$");
		if (om.Success) {
			var pref = om.Groups[1].Value + om.Groups[2].Value;
			var body = om.Groups[3].Value;
			if (simple)
				inlines.Add(new Run(pref) { Foreground = brush(0x9C, 0xA3, 0xAF) });
			else
				inlines.Add(markerrun(pref, showMarkers));
			colorinlines(inlines, body, showMarkers, srcLine: srcLine);
			return;
		}
		colorinlines(inlines, line, showMarkers, srcLine: srcLine);
	}

	void colorinlines(InlineCollection inlines, string text, bool showMarkers,
		FontWeight? weight = null, double fontSize = 0, Brush forceFg = null, int srcLine = -1) {
		if (string.IsNullOrEmpty(text)) {
			inlines.Add(new Run(""));
			return;
		}
		var simple = usesimpleeditor;
		var spans = tokenizeinline(text);
		foreach (var sp in spans) {
			Run r;
			switch (sp.Kind) {
				case "bold":
					// 始终输出 **body** 字符
					if (simple)
						inlines.Add(new Run("**") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					else
						inlines.Add(markerrun("**", showMarkers));
					r = new Run(sp.Text ?? "") { FontWeight = FontWeights.Bold };
					applybase(r, weight, fontSize, forceFg);
					inlines.Add(r);
					if (simple)
						inlines.Add(new Run("**") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					else
						inlines.Add(markerrun("**", showMarkers));
					break;
				case "italic":
					if (simple)
						inlines.Add(new Run("*") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					else
						inlines.Add(markerrun("*", showMarkers));
					r = new Run(sp.Text ?? "") { FontStyle = FontStyles.Italic };
					applybase(r, weight, fontSize, forceFg);
					inlines.Add(r);
					if (simple)
						inlines.Add(new Run("*") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					else
						inlines.Add(markerrun("*", showMarkers));
					break;
				case "code":
					if (simple) {
						// 仅着色，无底色
						inlines.Add(new Run("`") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(new Run(sp.Text ?? "") { Foreground = brush(0xB9, 0x1C, 0x1C) });
						inlines.Add(new Run("`") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					} else {
						inlines.Add(markerrun("`", showMarkers));
						r = new Run(sp.Text ?? "") {
							Background = brush(0xF3, 0xF4, 0xF6),
							Foreground = brush(0xB9, 0x1C, 0x1C),
						};
						inlines.Add(r);
						inlines.Add(markerrun("`", showMarkers));
					}
					break;
				case "mark":
					if (simple) {
						inlines.Add(new Run("==") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(new Run(sp.Text ?? "") { Foreground = brush(0xB4, 0x53, 0x09) });
						inlines.Add(new Run("==") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					} else {
						inlines.Add(markerrun("==", showMarkers));
						r = new Run(sp.Text ?? "") { Background = brush(0xFE, 0xF0, 0x8A) };
						inlines.Add(r);
						inlines.Add(markerrun("==", showMarkers));
					}
					break;
				case "strike":
					if (simple) {
						// 不画删除线，仅灰色正文
						inlines.Add(new Run("~~") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(new Run(sp.Text ?? "") { Foreground = brush(0x6B, 0x72, 0x80) });
						inlines.Add(new Run("~~") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					} else {
						inlines.Add(markerrun("~~", showMarkers));
						r = new Run(sp.Text ?? "") { TextDecorations = TextDecorations.Strikethrough };
						inlines.Add(r);
						inlines.Add(markerrun("~~", showMarkers));
					}
					break;
				case "link": {
					// 光标行：href 用普通 Run（可编辑）；整段 ](href) 放进 marker 时，
					// 编辑会拆 Run 且两边保留完整 Tag → 导出重复。
					if (simple)
						inlines.Add(new Run("[") { Foreground = brush(0x9C, 0xA3, 0xAF) });
					else
						inlines.Add(markerrun("[", showMarkers));
					r = new Run(sp.Text ?? "") {
						Foreground = brush(0x25, 0x63, 0xEB),
						TextDecorations = TextDecorations.Underline,
						ToolTip = (sp.Href ?? "") + "\nCtrl+点击打开",
						Cursor = Cursors.Hand,
					};
					inlines.Add(r);
					var href = sp.Href ?? "";
					if (simple || showMarkers) {
						if (simple) {
							inlines.Add(new Run("](") { Foreground = brush(0x9C, 0xA3, 0xAF) });
							inlines.Add(new Run(href) { Foreground = brush(0x9C, 0xA3, 0xAF) });
							inlines.Add(new Run(")") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						} else {
							inlines.Add(markerrun("](", true));
							inlines.Add(new Run(href) { Foreground = brush(0x9C, 0xA3, 0xAF) });
							inlines.Add(markerrun(")", true));
						}
					} else {
						inlines.Add(markerrun("](" + href + ")", false));
					}
					break;
				}
				case "image": {
					var alt = sp.Text ?? "";
					var href = sp.Href ?? "";
					var full = "![" + alt + "](" + href + ")";
					// Typora 非光标行：只显示图（语法在 Tag，导出不丢）
					if (useconceal && !showMarkers) {
						var img = MdFlowBuilder.TryLoadImage(href, FilePath, sourceimgmaxw());
						if (img != null) {
							// 点击图 → 展开该行源码（行号在构建时绑定，避免反查失败）
							img.Cursor = Cursors.Hand;
							img.ToolTip = full + "\n单击显示图片代码 · 双击预览";
							img.Focusable = false;
							var imgLine = srcLine;
							var imgBmp = (img as Image)?.Source as BitmapSource;
							img.PreviewMouseLeftButtonDown += (s, e) => {
								if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
								// 双击：取消单击展开，全窗预览（ImageOverlay.Wire 也会响应）
								if (e.ClickCount >= 2) {
									pendingImgExpandLine = -1;
									imgExpandGen++;
									e.Handled = true;
									var bs = imgBmp ?? (s as Image)?.Source as BitmapSource;
									if (bs != null) ImageOverlay.Show(bs, alt);
									return;
								}
								var ln = imgLine >= 0 ? imgLine : findconcealedimageline(s as DependencyObject);
								if (ln >= 0) pendingImgExpandLine = ln;
								// 不 Handled：让 RTB 仍能拿焦点；展开在 MouseUp 延迟做
							};
							inlines.Add(new InlineUIContainer(img) {
								Tag = IMG_TAG_PREFIX + full,
								BaselineAlignment = BaselineAlignment.Center,
							});
							break;
						}
						// 加载失败则退回源码
					}
					// 光标行 / 纯代码：源码着色；纯代码无内嵌预览图
					if (simple) {
						inlines.Add(new Run("![") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(new Run(alt) {
							Foreground = brush(0x05, 0x96, 0x69),
							ToolTip = href,
						});
						inlines.Add(new Run("](") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(new Run(href) { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(new Run(")") { Foreground = brush(0x9C, 0xA3, 0xAF) });
						break;
					}
					inlines.Add(markerrun("![", showMarkers));
					inlines.Add(new Run(alt) {
						Foreground = brush(0x05, 0x96, 0x69),
						ToolTip = href,
					});
					if (showMarkers) {
						inlines.Add(markerrun("](", true));
						inlines.Add(new Run(href) { Foreground = brush(0x9C, 0xA3, 0xAF) });
						inlines.Add(markerrun(")", true));
					} else {
						inlines.Add(markerrun("](" + href + ")", false));
					}
					if (useconceal && showMarkers) {
						var preview = MdFlowBuilder.TryLoadImage(href, FilePath, sourceimgmaxw());
						if (preview != null) {
							inlines.Add(new LineBreak());
							inlines.Add(new InlineUIContainer(preview) {
								Tag = IMG_PREVIEW_TAG,
								BaselineAlignment = BaselineAlignment.Center,
							});
						}
					}
					break;
				}
				default:
					r = new Run(sp.Text ?? "");
					applybase(r, weight, fontSize, forceFg);
					inlines.Add(r);
					break;
			}
		}
	}

	static void applybase(Run r, FontWeight? weight, double fontSize, Brush forceFg) {
		if (weight != null) r.FontWeight = weight.Value;
		if (fontSize > 0.1) r.FontSize = fontSize;
		if (forceFg != null) r.Foreground = forceFg;
	}

	double sourceimgmaxw() {
		try {
			var w = sourceBox?.ActualWidth ?? 0;
			if (w > 120) return Math.Min(560, w - 36);
		} catch { /* ignore */ }
		return 480;
	}

	/// <summary>行内分词，保留标记文本于 Kind 语义中。</summary>
	static List<MdSpan> tokenizeinline(string text) {
		return MdParser.ParseInlines(text);
	}

	// ---------- 链接：预览点击 / 源码 Ctrl+点击；Typora 点图展开 ----------
	void onsourceclick(object sender, MouseButtonEventArgs e) {
		// Typora：点在折叠图上（含子元素命中）→ 标记待展开行
		if (useconceal && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
			try {
				var hit = sourceBox.InputHitTest(e.GetPosition(sourceBox)) as DependencyObject;
				var imgLn = findconcealedimageline(hit);
				if (imgLn >= 0) {
					pendingImgExpandLine = imgLn;
					return;
				}
			} catch { /* ignore */ }
		}
		if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
		try {
			var pos = sourceBox.GetPositionFromPoint(e.GetPosition(sourceBox), true);
			if (pos == null) return;
			// 用点击位置所在行/列，不用 caret（否则滚到别处 Ctrl+点会解析错行）
			var abs = textoffset(pos);
			var text = rawText ?? "";
			var line0 = lineof(text, abs);
			var lines = MdParser.SplitLines(text);
			if (line0 < 0 || line0 >= lines.Count) return;
			var lineText = lines[line0] ?? "";
			// 行内字符偏移
			var lineStartAbs = 0;
			for (var i = 0; i < line0; i++)
				lineStartAbs += (lines[i]?.Length ?? 0) + 1; // + \n
			var col = abs - lineStartAbs;
			if (col < 0) col = 0;
			if (col > lineText.Length) col = lineText.Length;
			var spans = MdParser.ParseInlines(lineText);
			// 按 span 在行内的字符范围命中（含链接标记）
			var at = 0;
			foreach (var sp in spans) {
				var piece = spanrawlength(sp);
				var a = at;
				var b = at + piece;
				at = b;
				if (sp.Kind != "link" && sp.Kind != "image") continue;
				if (string.IsNullOrWhiteSpace(sp.Href)) continue;
				// 点在链接可见区或整段 markdown 链上均可
				if (col >= a && col <= b) {
					handlenav(sp.Href);
					e.Handled = true;
					return;
				}
			}
			// 回退：行内任一链接（兼容旧行为）
			foreach (var sp in spans) {
				if (sp.Kind != "link" && sp.Kind != "image") continue;
				if (string.IsNullOrWhiteSpace(sp.Href)) continue;
				handlenav(sp.Href);
				e.Handled = true;
				return;
			}
		} catch { /* ignore */ }
	}

	/// <summary>span 在源码中的近似字符长度（含 markdown 标记）。</summary>
	static int spanrawlength(MdSpan sp) {
		if (sp == null) return 0;
		var t = sp.Text ?? "";
		switch (sp.Kind) {
			case "bold": return 4 + t.Length; // ** **
			case "italic": return 2 + t.Length;
			case "code": return 2 + t.Length;
			case "mark": return 4 + t.Length;
			case "strike": return 4 + t.Length;
			case "link": return 4 + t.Length + (sp.Href?.Length ?? 0); // [t](h)
			case "image": return 5 + t.Length + (sp.Href?.Length ?? 0); // ![t](h)
			default: return t.Length;
		}
	}

	/// <summary>预览 Hyperlink / 源码 Ctrl+点击 统一入口。</summary>
	void handlenav(string href) {
		if (string.IsNullOrWhiteSpace(href)) return;
		href = href.Trim();
		// 去掉伪 scheme
		if (href.StartsWith("mdlink://anchor/", StringComparison.OrdinalIgnoreCase)) {
			try {
				href = "#" + Uri.UnescapeDataString(href.Substring("mdlink://anchor/".Length));
			} catch { href = "#" + href.Substring("mdlink://anchor/".Length); }
		} else if (href.StartsWith("mdlink://path/", StringComparison.OrdinalIgnoreCase)) {
			try {
				href = Uri.UnescapeDataString(href.Substring("mdlink://path/".Length));
			} catch { href = href.Substring("mdlink://path/".Length); }
		}

		// 拆 file#anchor
		string filePart = null;
		string anchor = null;
		var hash = href.IndexOf('#');
		if (hash == 0) {
			JumpToAnchor(href.Substring(1));
			return;
		}
		if (hash > 0) {
			filePart = href.Substring(0, hash).Trim();
			anchor = href.Substring(hash + 1);
		} else {
			filePart = href;
		}

		// 仅锚点已处理；空 file 当成本文
		if (string.IsNullOrEmpty(filePart)) {
			if (!string.IsNullOrEmpty(anchor)) JumpToAnchor(anchor);
			return;
		}

		// http(s) → 应用内浏览器标签；mailto 仍交系统
		if (filePart.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| filePart.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
			try {
				if (OpenUrlInApp != null)
					OpenUrlInApp(filePart);
				else
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
						FileName = filePart,
						UseShellExecute = true,
					});
			} catch (Exception ex) {
				DocLog.Warn($"Md nav url fail: {ex.Message}");
			}
			return;
		}
		if (filePart.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) {
			try {
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
					FileName = filePart,
					UseShellExecute = true,
				});
			} catch (Exception ex) {
				DocLog.Warn($"Md nav mailto fail: {ex.Message}");
			}
			return;
		}

		// 解析本地路径（相对当前文件目录）
		var full = ResolveHrefPath(FilePath, filePart);
		if (string.IsNullOrEmpty(full)) {
			DocLog.Warn($"Md nav resolve fail href={filePart}");
			return;
		}

		// Markdown / 其它支持文档 → 应用内标签（保持当前预览/编辑模式）
		if (MdFlowBuilder.IsMdHref(filePart) || DocKindUtil.FromPath(full) == DocKind.Md) {
			try {
				OpenMarkdownNewWindow?.Invoke(full, editMode, layout, anchor);
			} catch (Exception ex) {
				DocLog.Warn($"Md OpenMarkdownNewWindow: {ex.Message}");
			}
			return;
		}

		var kind = DocKindUtil.FromPath(full);
		if (kind != DocKind.Unknown) {
			try {
				OpenMarkdownNewWindow?.Invoke(full, false, MdEditLayout.Typora, null);
			} catch { /* ignore */ }
			return;
		}

		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
				FileName = full,
				UseShellExecute = true,
			});
		} catch (Exception ex) {
			DocLog.Warn($"Md nav open fail: {ex.Message}");
		}
	}

	/// <summary>
	/// 相对当前文档解析链接路径（公开便于自检）。
	/// 同时支持 URL 编码（%20、%E4…）与未编码路径：自动检测，优先返回磁盘上存在的路径。
	/// </summary>
	public static string ResolveHrefPath(string baseFile, string href) {
		if (string.IsNullOrWhiteSpace(href)) return null;
		href = href.Trim().Trim('"');
		// file:///（Uri.LocalPath 已解码 %20 等）
		if (href.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) {
			try {
				var u = new Uri(href);
				return u.LocalPath;
			} catch { /* fallthrough */ }
		}

		// 候选：原文 + URL 解码（若含 %xx 且解码后不同）
		// 文件名字面含 %20 时优先原文；常见 MD 导出为编码路径时走解码。
		string decoded = null;
		if (href.IndexOf('%') >= 0) {
			try {
				var d = Uri.UnescapeDataString(href);
				if (!string.Equals(d, href, StringComparison.Ordinal))
					decoded = d;
			} catch { /* 非法 % 序列，仅用原文 */ }
		}

		string first = null;
		// 先原文后解码：两者都存在时保留链接字面名
		foreach (var cand in new[] { href, decoded }) {
			if (cand == null) continue;
			var full = resolvelocalpath(baseFile, cand);
			if (full == null) continue;
			if (first == null) first = full;
			try {
				if (File.Exists(full) || Directory.Exists(full))
					return full;
			} catch { /* ignore IO */ }
		}
		// 均不存在：若有解码候选优先返回解码路径（更接近真实文件名）
		if (decoded != null) {
			var dfull = resolvelocalpath(baseFile, decoded);
			if (dfull != null) return dfull;
		}
		return first;
	}

	/// <summary>将 href 规范为本地绝对路径（不探测磁盘、不 URL 解码）。</summary>
	static string resolvelocalpath(string baseFile, string href) {
		if (string.IsNullOrEmpty(href)) return null;
		href = href.Replace('/', System.IO.Path.DirectorySeparatorChar);
		try {
			if (System.IO.Path.IsPathRooted(href))
				return System.IO.Path.GetFullPath(href);
			var dir = string.IsNullOrEmpty(baseFile)
				? Environment.CurrentDirectory
				: (System.IO.Path.GetDirectoryName(baseFile) ?? Environment.CurrentDirectory);
			return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, href));
		} catch {
			return null;
		}
	}

	/// <summary>跳到章节标题（slug / 原文匹配）。</summary>
	public void JumpToAnchor(string name) {
		jumptoanchor(name);
	}

	void jumptoanchor(string name) {
		if (string.IsNullOrWhiteSpace(name)) return;
		name = name.Trim().TrimStart('#');
		// URL decode（%20 等）；+ 当空格
		try { name = Uri.UnescapeDataString(name); } catch { /* keep */ }
		name = name.Replace('+', ' ');
		// 保证 toc 最新
		if (toc.Count == 0) buildtoc();

		// 1) 标题精确（链接空格原样，不先转成 -）
		foreach (var t in toc) {
			if (string.Equals((t.Title ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)) {
				gotoline(t.SourceLine0);
				return;
			}
		}
		// 2) 标题包含链接原文（含空格）
		foreach (var t in toc) {
			var title = (t.Title ?? "").Trim();
			if (title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) {
				gotoline(t.SourceLine0);
				return;
			}
		}
		// 3) 紧凑匹配：忽略空格与 -/_ 差异（链接不强制空格→-）
		var wantCompact = compactanchor(name);
		foreach (var t in toc) {
			var title = (t.Title ?? "").Trim();
			if (string.Equals(compactanchor(title), wantCompact, StringComparison.OrdinalIgnoreCase)) {
				gotoline(t.SourceLine0);
				return;
			}
			var stripped = System.Text.RegularExpressions.Regex.Replace(title, @"^\d+[\.、]\s*", "");
			if (string.Equals(compactanchor(stripped), wantCompact, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(stripped, name, StringComparison.OrdinalIgnoreCase)
				|| stripped.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) {
				gotoline(t.SourceLine0);
				return;
			}
		}
		// 4) GitHub 风格：标题侧空格→-，兼容 #hello-world；链接侧仍可用空格
		var wantSlug = slugify(name, spaceToDash: true);
		var wantSlugKeepSp = slugify(name, spaceToDash: false);
		foreach (var t in toc) {
			var ts = slugify(t.Title, spaceToDash: true);
			var ts2 = slugify(t.Title, spaceToDash: false);
			if (string.Equals(ts, wantSlug, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ts2, wantSlugKeepSp, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ts, wantSlugKeepSp, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ts2, wantSlug, StringComparison.OrdinalIgnoreCase)) {
				gotoline(t.SourceLine0);
				return;
			}
		}
		DocLog.Info($"Md anchor not found: {name}");
	}

	/// <summary>去掉空白与 -/_，便于 #foo bar 与「foo bar」/「foo-bar」互认。</summary>
	public static string compactanchor(string s) {
		if (string.IsNullOrEmpty(s)) return "";
		var sb = new StringBuilder(s.Length);
		foreach (var ch in s.Trim().ToLowerInvariant()) {
			if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_') continue;
			if (char.IsLetterOrDigit(ch) || ch >= 0x4e00)
				sb.Append(ch);
		}
		return sb.ToString();
	}

	/// <summary>
	/// 标题 slug。spaceToDash=true：空格→-（GitHub）；false：空格直接去掉（链接侧不转 -）。
	/// </summary>
	public static string slugify(string title, bool spaceToDash = true) {
		if (string.IsNullOrEmpty(title)) return "";
		title = title.Trim().ToLowerInvariant();
		var sb = new StringBuilder(title.Length);
		foreach (var ch in title) {
			if (char.IsLetterOrDigit(ch) || ch >= 0x4e00) // CJK
				sb.Append(ch);
			else if (char.IsWhiteSpace(ch)) {
				// 链接锚点：不把空格转成 -，直接跳过
				if (spaceToDash) sb.Append('-');
			} else if (ch == '-' || ch == '_')
				sb.Append(ch);
			// drop other punct
		}
		var s = sb.ToString();
		while (s.IndexOf("--", StringComparison.Ordinal) >= 0)
			s = s.Replace("--", "-");
		return s.Trim('-');
	}

	// ---------- 滚动同步 ----------
	void onsourcescroll(object sender, ScrollChangedEventArgs e) {
		if (syncingScroll || !editMode) return;
		if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
		contentScrollRatio = capturecontentratio();
		// 纯代码 / Typora：源码滚动 → 目录
		if (!previewvisible || !hassidepreview) {
			var ln = caretLine >= 0 ? caretLine : getcaretlinefast();
			synctoc(ln);
			try { StatusChanged?.Invoke(); } catch { /* ignore */ }
		}
		if (!hassidepreview) return;
		try {
			syncingScroll = true;
			var srcSv = findscroll(sourceBox);
			if (srcSv == null) return;
			var ratio = srcSv.ScrollableHeight > 1
				? srcSv.VerticalOffset / srcSv.ScrollableHeight
				: 0;
			contentScrollRatio = ratio;
			previewScrollRatio = ratio;
			// 抑制预览→源码回传，避免来回拽
			suppresspreviewtosource();
			// 普通段落：按视口顶行对齐；表内仍用比例（表块共用 data-line）
			var topLine = capturesourcetopline();
			if (usewpfpreview) {
				if (topLine >= 0 && !lineintable(topLine))
					scrollpreviewtoline(topLine);
				else
					restorewpfscrollratio(ratio);
			} else if (topLine >= 0 && !lineintable(topLine)) {
				_ = runpreviewjs($"window.mdScrollToLine&&mdScrollToLine({topLine});");
			} else {
				var r = ratio.ToString(System.Globalization.CultureInfo.InvariantCulture);
				_ = runpreviewjs($"window.mdScrollRatio&&mdScrollRatio({r});");
			}
			try { StatusChanged?.Invoke(); } catch { /* ignore */ }
		} catch { /* ignore */ }
		finally { syncingScroll = false; }
	}

	/// <summary>当前内容区垂直滚动比例 0..1。</summary>
	double capturecontentratio() {
		try {
			if (editMode) {
				var sv = findscroll(sourceBox);
				if (sv != null && sv.ScrollableHeight > 1)
					return sv.VerticalOffset / sv.ScrollableHeight;
				return contentScrollRatio;
			}
			return previewScrollRatio > 0 || previewScrollY > 0
				? previewScrollRatio
				: contentScrollRatio;
		} catch {
			return contentScrollRatio;
		}
	}

	/// <summary>源码视口顶部附近的行号（0-based）；优先 GetPositionFromPoint，避免扫全文。</summary>
	int capturesourcetopline() {
		try {
			var lines = getlinescached();
			if (lines == null || lines.Count == 0)
				return caretLine >= 0 ? caretLine : 0;
			ensureparacache(lines.Count);
			try {
				var tp = sourceBox.GetPositionFromPoint(new Point(8, 8), true);
				if (tp != null) {
					var p = tp.Paragraph;
					if (p != null && lineParaCache != null) {
						for (var i = 0; i < lineParaCache.Length; i++) {
							if (ReferenceEquals(lineParaCache[i], p))
								return i;
						}
					}
				}
			} catch { /* fall through */ }
			// 回退：用滚动偏移估行，再在窗口内用 GetCharacterRect 精修
			var sv = findscroll(sourceBox);
			if (sv == null)
				return caretLine >= 0 ? caretLine : 0;
			var lh = Math.Max(8.0, sourceBox.FontSize * 1.45);
			var est = (int)(sv.VerticalOffset / lh);
			if (est < 0) est = 0;
			if (est >= lines.Count) est = lines.Count - 1;
			const double margin = 36;
			var best = est;
			var any = false;
			var a = Math.Max(0, est - 40);
			var b = Math.Min(lines.Count - 1, est + 80);
			for (var i = a; i <= b; i++) {
				var p = paragraphat(i);
				if (p == null) continue;
				Rect rect;
				try { rect = p.ContentStart.GetCharacterRect(LogicalDirection.Forward); }
				catch { continue; }
				if (rect == Rect.Empty || double.IsNaN(rect.Top)) continue;
				if (rect.Top <= margin) {
					best = i;
					any = true;
				}
			}
			return any ? best : est;
		} catch {
			return caretLine >= 0 ? caretLine : 0;
		}
	}

	/// <summary>可见行范围（含少量边距）；用于分片/围栏视口高亮。</summary>
	void getviewportlinerange(out int top, out int bottom) {
		top = 0;
		bottom = 0;
		try {
			var lines = getlinescached();
			var n = lines?.Count ?? 0;
			if (n <= 0) return;
			top = capturesourcetopline();
			if (top < 0) top = 0;
			if (top >= n) top = n - 1;
			var sv = findscroll(sourceBox);
			var lh = Math.Max(8.0, sourceBox.FontSize * 1.45);
			var visible = sv != null
				? (int)Math.Ceiling(sv.ViewportHeight / lh) + 2
				: 40;
			if (visible < 20) visible = 20;
			bottom = Math.Min(n - 1, top + visible);
		} catch {
			top = Math.Max(0, caretLine);
			bottom = top;
		}
	}

	async System.Threading.Tasks.Task<int> querypreviewtoplineasync() {
		try {
			if (usewpfpreview) {
				var top = capturewpfpreviewtopline();
				if (top >= 0) lastPreviewTopLine = top;
				return lastPreviewTopLine;
			}
			if (!previewReady || previewWeb?.CoreWebView2 == null)
				return lastPreviewTopLine;
			var raw = await previewWeb.ExecuteScriptAsync(
				"window.mdGetTopLine?String(mdGetTopLine()):'-1'").ConfigureAwait(true);
			if (string.IsNullOrEmpty(raw)) return lastPreviewTopLine;
			if (raw.Length >= 2 && raw[0] == '"')
				raw = System.Text.RegularExpressions.Regex.Unescape(raw.Substring(1, raw.Length - 2));
			if (int.TryParse(raw, out var line) && line >= 0) {
				lastPreviewTopLine = line;
				return line;
			}
		} catch { /* ignore */ }
		return lastPreviewTopLine;
	}

	static ScrollViewer findscroll(DependencyObject root) {
		if (root == null) return null;
		if (root is ScrollViewer sv) return sv;
		// Hyperlink/Run 等 ContentElement 不是 Visual，不能 GetChildrenCount
		if (!(root is Visual) && !(root is System.Windows.Media.Media3D.Visual3D))
			return null;
		try {
			var n = VisualTreeHelper.GetChildrenCount(root);
			for (var i = 0; i < n; i++) {
				var c = findscroll(VisualTreeHelper.GetChild(root, i));
				if (c != null) return c;
			}
		} catch { /* ignore non-visual */ }
		return null;
	}

	// ---------- TOC ----------
	void buildtoc() {
		toc.Clear();
		var text = rawText ?? "";
		var doc = MdParser.Parse(text);
		mdDoc = doc;
		var headAutoNum = true;
		try { headAutoNum = AppSettings.Current?.MdHeadingAutoNumber ?? true; } catch { /* keep true */ }
		var headNum = headAutoNum ? new MdHeadingNumber() : null;
		foreach (var b in doc.Blocks) {
			if (b.Kind != MdBlockKind.Heading) continue;
			var title = b.Text ?? "";
			if (headNum != null && !string.IsNullOrWhiteSpace(title))
				title = headNum.PrefixTitle(b.Level, title);
			toc.Add(new TocEntry {
				Title = title,
				Level = b.Level,
				SourceLine0 = b.SourceLine0,
			});
		}
		hasOutline = toc.Count > 0;
		rebuildtocui();
		// 内嵌「目录」已并入主窗左侧「章节列表」，永久关闭
		setside(false);
		lastTocLine = -1;
		if (hasOutline)
			_ = synctocfrompreviewasync(force: true);
	}

	void rebuildtocui() {
		tree.Items.Clear();
		foreach (var te in toc)
			te.Item = null;
		if (toc.Count == 0) {
			lboutline.Text = "无目录";
			lboutline.Visibility = Visibility.Visible;
			return;
		}
		lboutline.Visibility = Visibility.Collapsed;
		var q = outlineQuery ?? "";
		var stack = new List<TreeViewItem>();
		foreach (var t in toc) {
			if (!string.IsNullOrEmpty(q) && (t.Title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
				continue;
			var item = new TreeViewItem {
				Header = OutlineUi.MakeHeader(t.Title, "", q),
				Tag = t.SourceLine0,
				IsExpanded = t.Level <= 1,
				Padding = new Thickness(Math.Max(0, (t.Level - 1) * 12), 2, 4, 2),
			};
			t.Item = item;
			while (stack.Count > 0 && stack.Count >= t.Level)
				stack.RemoveAt(stack.Count - 1);
			if (stack.Count == 0)
				tree.Items.Add(item);
			else
				stack[stack.Count - 1].Items.Add(item);
			stack.Add(item);
		}
	}

	/// <summary>按源行同步目录高亮（滚动防抖；force 立即）。</summary>
	void synctoc(int line0, bool force = false) {
		if (syncTree || !hasOutline || toc.Count == 0) return;
		if (ignoreOutlineSyncUntil != 0
			&& Environment.TickCount - ignoreOutlineSyncUntil < 0)
			return;
		pendingOutlineLine = line0;
		if (!force) {
			if (line0 == lastTocLine) {
				TocEntry bestPeek = null;
				foreach (var te in toc) {
					if (te.Item == null || te.SourceLine0 > line0) continue;
					bestPeek = te;
				}
				var want = bestPeek != null ? OutlineUi.FindVisibleOnPath(bestPeek.Item) : null;
				if (want != null && ReferenceEquals(tree.SelectedItem, want))
					return;
			}
			scheduleoutlinedebounce();
			return;
		}
		stopoutlinedebounce();
		applytocsync(force: true, center: true);
	}

	void scheduleoutlinedebounce() {
		if (outlineDebounce == null) {
			outlineDebounce = new DispatcherTimer(DispatcherPriority.Background) {
				Interval = TimeSpan.FromMilliseconds(OUTLINE_DEBOUNCE_MS),
			};
			outlineDebounce.Tick += (_, _) => {
				try { outlineDebounce.Stop(); } catch { /* ignore */ }
				applytocsync(force: false, center: false);
			};
		}
		outlineDebounce.Stop();
		outlineDebounce.Start();
	}

	void stopoutlinedebounce() {
		try { outlineDebounce?.Stop(); } catch { /* ignore */ }
	}

	void applytocsync(bool force, bool center) {
		if (syncTree || !hasOutline || toc.Count == 0) return;
		// 目录跳转抑制窗：不向主窗推中间高亮（连点时防跳）
		if (ignoreOutlineSyncUntil != 0
			&& unchecked(Environment.TickCount - ignoreOutlineSyncUntil) < 0)
			return;
		var line = pendingOutlineLine;
		// 旧逻辑：文档序中最后一个 SourceLine0≤line 的标题
		TocEntry best = null;
		foreach (var te in toc) {
			if (te == null || te.SourceLine0 > line) continue;
			best = te;
		}
		if (best == null) return;
		// 主窗「章节列表」镜像（不另算一套定位）
		try { OutlineHighlightChanged?.Invoke(best.SourceLine0); } catch { /* ignore */ }
		// 内嵌树已隐藏，无 Item 则只更新主窗
		if (best.Item == null) {
			lastTocLine = line;
			return;
		}
		var sel = OutlineUi.FindVisibleOnPath(best.Item);
		if (sel == null) return;
		if (ReferenceEquals(tree.SelectedItem, sel)) {
			lastTocLine = line;
			return;
		}
		lastTocLine = line;
		syncTree = true;
		try {
			if (tree.SelectedItem is TreeViewItem old && !ReferenceEquals(old, sel))
				old.IsSelected = false;
			sel.IsSelected = true;
			OutlineUi.ScrollItemIntoView(sel, center);
		} catch { /* ignore */ }
		finally { syncTree = false; }
	}

	async System.Threading.Tasks.Task synctocfrompreviewasync(bool force) {
		try {
			if (usewpfpreview) {
				var top = capturewpfpreviewtopline();
				if (top >= 0) synctoc(top, force);
				return;
			}
			if (!previewReady || previewWeb?.CoreWebView2 == null) return;
			var raw = await previewWeb.ExecuteScriptAsync(
				"window.mdGetOutlineLine?String(mdGetOutlineLine()):'-1'").ConfigureAwait(true);
			if (string.IsNullOrEmpty(raw)) return;
			if (raw.Length >= 2 && raw[0] == '"')
				raw = System.Text.RegularExpressions.Regex.Unescape(raw.Substring(1, raw.Length - 2));
			if (int.TryParse(raw, out var line) && line >= 0)
				synctoc(line, force);
		} catch { /* ignore */ }
	}

	void gotoline(int line0) {
		if (editMode) {
			try {
				var lines = MdParser.SplitLines(rawText ?? "");
				if (line0 < 0) line0 = 0;
				if (line0 >= lines.Count) line0 = Math.Max(0, lines.Count - 1);
				skipScrollRestoreOnce = true;
				var p = paragraphat(line0);
				if (p != null) {
					sourceBox.Focus();
					sourceBox.CaretPosition = p.ContentStart;
					// 标题顶对齐视口（BringIntoView 只保证可见，常偏上/偏下）
					scrollpointertotop(sourceBox, p.ContentStart);
				}
				if (hassidepreview)
					scrollpreviewtoline(line0);
			} catch { /* ignore */ }
		} else {
			scrollpreviewtoline(line0);
		}
	}

	/// <summary>
	/// 将 TextPointer 所在行滚到 ScrollViewer 视口顶部，并留出顶边距，避免标题贴边被裁切。
	/// </summary>
	void scrollpointertotop(FrameworkElement host, TextPointer tp) {
		if (host == null || tp == null) return;
		const double TOP_INSET = 20;
		try {
			host.UpdateLayout();
			var sv = findscroll(host);
			if (sv == null) {
				tp.Paragraph?.BringIntoView();
				return;
			}
			// 布局后再量一次，避免首次 rect 不准
			host.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				try {
					host.UpdateLayout();
					var rect = tp.GetCharacterRect(LogicalDirection.Forward);
					if (rect == Rect.Empty || double.IsNaN(rect.Top)) return;
					var target = sv.VerticalOffset + rect.Top - TOP_INSET;
					if (target < 0) target = 0;
					sv.ScrollToVerticalOffset(target);
					// 再校正一帧（字体/行高稳定后）
					host.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
						try {
							var r2 = tp.GetCharacterRect(LogicalDirection.Forward);
							if (r2 == Rect.Empty || double.IsNaN(r2.Top)) return;
							var adj = r2.Top - TOP_INSET;
							if (Math.Abs(adj) > 0.5)
								sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + adj));
						} catch { /* ignore */ }
					}));
				} catch {
					try { tp.Paragraph?.BringIntoView(); } catch { /* ignore */ }
				}
			}));
		} catch { /* ignore */ }
	}

	Paragraph paragraphat(int line0) {
		if (line0 < 0) return null;
		ensureparacache();
		if (lineParaCache == null || line0 >= lineParaCache.Length) return null;
		return lineParaCache[line0];
	}

	void invalidateparacache() {
		lineParaCache = null;
	}

	void ensureparacache(int expectLines = -1) {
		if (lineParaCache != null) {
			if (expectLines < 0 || lineParaCache.Length == expectLines) return;
		}
		try {
			var lines = getlinescached();
			var n = lines?.Count ?? 0;
			var cache = new Paragraph[n];
			var line = 0;
			foreach (var b in sourceBox.Document.Blocks) {
				if (b is Paragraph p) {
					if (line < n) cache[line] = p;
					line++;
				} else if (b is Table tbl) {
					var tn = tablelinecountfromtag(tbl.Tag as string);
					line += tn;
				}
			}
			lineParaCache = cache;
		} catch {
			lineParaCache = null;
		}
	}

	// ---------- caret offset ----------
	/// <summary>
	/// 逻辑源码偏移：按行定位，避免折叠表单元格文字进入 TextRange。
	/// </summary>
	int getcaretoffset() {
		try {
			var text = rawText ?? "";
			var ln = getcaretlinefast();
			var baseOff = offsetofline(text, ln);
			var p = paragraphat(ln);
			if (p == null) return baseOff;
			var tp = sourceBox.CaretPosition;
			if (tp == null || tp.CompareTo(p.ContentStart) <= 0) return baseOff;
			var lines = getlinescached();
			var maxLocal = (lines != null && ln < lines.Count) ? (lines[ln] ?? "").Length : int.MaxValue;
			if (tp.CompareTo(p.ContentEnd) >= 0) return baseOff + maxLocal;
			var tr = new TextRange(p.ContentStart, tp);
			var local = (tr.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Length;
			// 光标行图片预览的 LineBreak 不计入源码
			if (local > maxLocal) local = maxLocal;
			return baseOff + local;
		} catch { return 0; }
	}

	void setcaretoffset(int offset) {
		try {
			var text = rawText ?? "";
			if (offset < 0) offset = 0;
			if (offset > text.Length) offset = text.Length;
			var ln = lineof(text, offset);
			var local = offset - offsetofline(text, ln);
			setcarettoline(ln);
			var p = paragraphat(ln);
			if (p != null && local > 0)
				setcaretinparagraph(p, local);
		} catch { /* ignore */ }
	}

	/// <summary>慢路径：全文 TextRange（仅全量高亮等少用处）。</summary>
	int getcaretline() {
		try {
			return lineof(rawText ?? "", getcaretoffset());
		} catch { return 0; }
	}

	/// <summary>
	/// 快路径：按 Document 块推算逻辑行（折叠表占多行）。
	/// </summary>
	int getcaretlinefast() {
		try {
			var tp = sourceBox.CaretPosition ?? sourceBox.Document.ContentStart;
			var line = 0;
			foreach (var b in sourceBox.Document.Blocks) {
				if (b is Paragraph p) {
					if (tp.CompareTo(p.ContentEnd) <= 0) {
						if (tp.CompareTo(p.ContentStart) < 0)
							return line > 0 ? line - 1 : 0;
						return line;
					}
					line++;
				} else if (b is Table tbl) {
					var n = tablelinecountfromtag(tbl.Tag as string);
					if (tp.CompareTo(tbl.ContentEnd) <= 0 && tp.CompareTo(tbl.ContentStart) >= 0)
						return line; // 点在折叠表上 → 表首行
					line += n;
				}
			}
			return line > 0 ? line - 1 : 0;
		} catch {
			return caretLine >= 0 ? caretLine : 0;
		}
	}

	/// <summary>rawText 中第 line0 行的起始偏移。</summary>
	static int offsetofline(string text, int line0) {
		if (string.IsNullOrEmpty(text) || line0 <= 0) return 0;
		var line = 0;
		for (var i = 0; i < text.Length; i++) {
			if (line == line0) return i;
			if (text[i] == '\n') line++;
		}
		return text.Length;
	}

	/// <summary>将光标落到逻辑行（折叠表则落在 Table 块上）。</summary>
	void setcarettoline(int line0) {
		try {
			if (line0 < 0) line0 = 0;
			var line = 0;
			foreach (var b in sourceBox.Document.Blocks) {
				if (b is Paragraph p) {
					if (line == line0) {
						sourceBox.CaretPosition = p.ContentStart;
						return;
					}
					line++;
				} else if (b is Table tbl) {
					var n = tablelinecountfromtag(tbl.Tag as string);
					if (line0 >= line && line0 < line + n) {
						sourceBox.CaretPosition = tbl.ContentStart;
						return;
					}
					line += n;
				}
			}
			sourceBox.CaretPosition = sourceBox.Document.ContentEnd;
		} catch { /* ignore */ }
	}

	/// <summary>确保仅 keep 行显示标记；其余行强制 conceal（修复漏切）。</summary>
	void concealallbut(int keep) {
		if (!useconceal) return;
		try {
			var lines = getlinescached();
			if (lines == null) return;
			for (var i = 0; i < lines.Count; i++) {
				if (i == keep) continue;
				var p = paragraphat(i);
				if (p == null) continue; // 折叠表块
				if (lineintable(i) || linehasimage(i)) {
					setlineshowraw(i, false);
					continue;
				}
				foreach (var inl in p.Inlines) {
					if (inl is not Run r) continue;
					var tag = r.Tag as string;
					if (tag == null) continue;
					if (tag.StartsWith(LIST_UL_TAG_PREFIX, StringComparison.Ordinal))
						applylistbulletvisibility(r, false);
					else if (tag.StartsWith(MARKER_TAG_PREFIX, StringComparison.Ordinal))
						applymarkervisibility(r, false);
				}
			}
			if (keep >= 0 && keep < lines.Count && paragraphat(keep) != null)
				setlineshowraw(keep, true);
		} catch (Exception ex) {
			DocLog.Warn($"Md concealallbut: {ex.Message}");
		}
	}

	/// <summary>
	/// 压测：Typora 连续点击换行（含 conceal 标记可见性切换）。
	/// 计时含 CaretPosition + toggle + 布局；返回最大毫秒数。
	/// </summary>
	public int PerfTyporaClickStorm(int clicks, out double avgMs) {
		avgMs = 0;
		if (!editMode) {
			EditMode = true;
			EditLayout = MdEditLayout.Typora;
		} else if (layout != MdEditLayout.Typora) {
			EditLayout = MdEditLayout.Typora;
		}
		try { sourceBox.Focus(); } catch { /* ignore */ }
		sourceBox.UpdateLayout();
		var paras = new List<Paragraph>();
		foreach (var b in sourceBox.Document.Blocks) {
			if (b is Paragraph p) paras.Add(p);
		}
		if (paras.Count == 0) {
			avgMs = 0;
			return 0;
		}
		long sum = 0;
		var max = 0;
		// 预热
		try {
			sourceBox.CaretPosition = paras[0].ContentStart;
			caretLine = 0;
			setlineshowraw(0, true);
			sourceBox.UpdateLayout();
		} catch { /* ignore */ }

		for (var i = 0; i < clicks; i++) {
			var idx = (i * 7 + 3) % paras.Count;
			var t0 = Environment.TickCount;
			try {
				sourceBox.CaretPosition = paras[idx].ContentStart;
				var old = caretLine;
				var ln = idx; // 与 CaretPosition 目标一致，避免再扫全文
				if (ln != old) {
					if (old >= 0) setlineshowraw(old, false);
					setlineshowraw(ln, true);
					caretLine = ln;
				}
			} catch (Exception ex) {
				DocLog.Warn($"PerfTyporaClickStorm i={i}: {ex.Message}");
			}
			var dt = Environment.TickCount - t0;
			if (dt < 0) dt = 0;
			sum += dt;
			if (dt > max) max = dt;
			if (dt >= CLICK_LOG_MS)
				DocLog.Info($"Md clickStorm i={i} line={idx} ms={dt}");
		}
		avgMs = clicks > 0 ? (double)sum / clicks : 0;
		DocLog.Info($"Md clickStorm done n={clicks} paras={paras.Count} maxMs={max} avgMs={avgMs:F1}");
		return max;
	}

	/// <summary>压测：连续键入（单行重绘路径）。返回最大单次 TextChanged→lineHL 毫秒。</summary>
	public int PerfTyporaEditStorm(int keystrokes, out double avgMs) {
		avgMs = 0;
		if (!editMode) {
			EditMode = true;
			EditLayout = MdEditLayout.Typora;
		} else if (layout != MdEditLayout.Typora) {
			EditLayout = MdEditLayout.Typora;
		}
		try { sourceBox.Focus(); } catch { /* ignore */ }
		// 移到文末避免在标题行拆结构
		try {
			sourceBox.CaretPosition = sourceBox.Document.ContentEnd;
			caretLine = getcaretlinefast();
		} catch { /* ignore */ }
		long sum = 0;
		var max = 0;
		for (var i = 0; i < keystrokes; i++) {
			var t0 = Environment.TickCount;
			try {
				// 模拟键入一个字符（走 TextChanged → 自定义 undo + lineHL）
				var pos = sourceBox.CaretPosition ?? sourceBox.Document.ContentEnd;
				sourceBox.Selection.Select(pos, pos);
				sourceBox.Selection.Text = "x";
				// 立即跑行高亮（不等 debounce）
				nextHlAt = 0;
				applylinehighlight();
			} catch (Exception ex) {
				DocLog.Warn($"PerfTyporaEditStorm i={i}: {ex.Message}");
			}
			var dt = Environment.TickCount - t0;
			if (dt < 0) dt = 0;
			sum += dt;
			if (dt > max) max = dt;
			if (dt >= CLICK_LOG_MS)
				DocLog.Info($"Md editStorm i={i} ms={dt}");
		}
		avgMs = keystrokes > 0 ? (double)sum / keystrokes : 0;
		DocLog.Info($"Md editStorm done n={keystrokes} maxMs={max} avgMs={avgMs:F1}");
		return max;
	}

	/// <summary>测试用：当前光标行是否有可见标记（非 Transparent）。</summary>
	public bool CaretLineShowsMarkers() {
		try {
			var ln = getcaretlinefast();
			var p = paragraphat(ln);
			if (p == null) return false;
			var hasMarker = false;
			var anyVisible = false;
			foreach (var inl in p.Inlines) {
				if (inl is not Run r) continue;
				var tag = r.Tag as string;
				if (tag == null) continue;
				if (tag.StartsWith(LIST_UL_TAG_PREFIX, StringComparison.Ordinal)) {
					hasMarker = true;
					// 光标行应为原文 -*+（非 ●）
					if (r.Text != "●") anyVisible = true;
					continue;
				}
				if (!tag.StartsWith(MARKER_TAG_PREFIX, StringComparison.Ordinal)) continue;
				hasMarker = true;
				var raw = tag.Substring(MARKER_TAG_PREFIX.Length);
				if (!string.IsNullOrEmpty(raw) && r.Text == raw)
					anyVisible = true;
			}
			return hasMarker && anyVisible;
		} catch { return false; }
	}

	/// <summary>跳转到指定源码行（0-based）；预览态滚预览，编辑态移光标。</summary>
	public void MoveCaretToLine(int line0) {
		// 抑制滚动同步高亮，避免跳转途中主窗 TOC 被中间章节抢选
		ignoreOutlineSyncUntil = unchecked(Environment.TickCount + PREVIEW_SYNC_SUPPRESS_MS);
		stopoutlinedebounce();
		pendingOutlineLine = line0;
		lastTocLine = line0;
		if (!editMode) {
			// 预览：滚到对应标题/行
			gotoline(line0);
			return;
		}
		if (useconceal && lineintable(line0) && paragraphat(line0) == null) {
			caretLine = line0;
			applysourcehighlight(force: true);
		}
		var p = paragraphat(line0);
		if (p == null) return;
		sourceBox.Focus();
		sourceBox.CaretPosition = p.ContentStart;
		var old = caretLine;
		caretLine = line0;
		if (useconceal) {
			if (old >= 0 && old != line0 && tableidat(old) == tableidat(line0))
				setlineshowraw(old, false);
			setlineshowraw(line0, true);
		}
	}

	/// <summary>测试用：当前权威源码。</summary>
	public string GetRawText() => rawText ?? "";

	/// <summary>
	/// 主窗章节列表高亮：当前视口/光标对应的标题源行（0-based）；无则 -1。
	/// </summary>
	public int GetActiveOutlineLine() {
		if (!hasOutline || toc.Count == 0) return -1;
		var line = pendingOutlineLine;
		if (line < 0) line = caretLine;
		if (line < 0) line = lastTocLine;
		if (line < 0) return -1;
		TocEntry best = null;
		foreach (var te in toc) {
			if (te == null || te.SourceLine0 > line) continue;
			best = te;
		}
		return best != null ? best.SourceLine0 : -1;
	}

	/// <summary>测试用：在光标处插入文本（走 rawText，避开 conceal 下 Selection.Text 错位）。</summary>
	public void InsertTextAtCaret(string text) {
		if (string.IsNullOrEmpty(text) || !editMode) return;
		insertplaintext(text);
	}

	/// <summary>测试用：复制图片到 images/，返回相对路径（images/…）。</summary>
	public string ImportImageFileForTest(string srcPath) {
		if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath)) return null;
		return saveimagefile(srcPath, Path.GetFileName(srcPath));
	}

	static int lineof(string text, int offset) {
		if (string.IsNullOrEmpty(text) || offset <= 0) return 0;
		if (offset > text.Length) offset = text.Length;
		var n = 0;
		for (var i = 0; i < offset && i < text.Length; i++)
			if (text[i] == '\n') n++;
		return n;
	}

	// ---------- IDocViewer ----------
	public void SetZoom(double z) {
		zoom = clamp(z, MIN_ZOOM, MAX_ZOOM);
		applyzoom();
		StatusChanged?.Invoke();
	}
	public void ZoomBy(double factor) => SetZoom(zoom * factor);
	public void ZoomIn() => SetZoom(zoom * 1.15);
	public void ZoomOut() => SetZoom(zoom / 1.15);
	public void ZoomFitWidth() => SetZoom(1.0);
	public void ZoomFitPage() => SetZoom(1.0);
	/// <summary>按当前 MdTabSize 重算行首缩进、展开剩余 Tab，并重建源码与预览。</summary>
	public void RefreshPreview() {
		// 系统参数可能改了预览引擎
		try {
			var eng = (AppSettings.Current?.MdPreviewEngine ?? 0) == 1
				? MdPreviewEngine.Wpf : MdPreviewEngine.WebView;
			if (eng != previewEngine) {
				previewEngine = eng;
				applypreviewenginevis();
				if (!usewpfpreview)
					_ = ensurepreviewasync();
			}
		} catch { /* ignore */ }

		var tab = AppSettings.Current?.MdTabSize ?? 3;
		if (tab < 1) tab = 1;
		var next = rawText ?? "";
		if (tab != appliedTabSize && appliedTabSize >= 1)
			next = MdParser.RetargetLeadingIndent(next, appliedTabSize, tab);
		next = MdParser.ExpandTabs(next, tab);
		appliedTabSize = tab;
		if (!string.Equals(next, rawText, StringComparison.Ordinal)) {
			rawText = next;
			invalidatelinecache();
			setdirty(true);
			if (editMode)
				applysourcehighlight(force: true);
			else {
				suppressText = true;
				try { setsourceplain(rawText); }
				finally { suppressText = false; }
			}
		} else if (editMode) {
			// 文本未变也刷新高亮（设置项其它影响）
			applysourcehighlight(force: true);
		}
		rebuildpreview(force: true);
		// 标题自动编号等设置影响目录文案
		try { buildtoc(); } catch { /* ignore */ }
		try { PreviewEngineChanged?.Invoke(); } catch { /* ignore */ }
	}
	public void RotateBy(int deltaQuarterTurns) { }
	public void GoPrevPage() { }
	public void GoNextPage() { }
	public void GoToPage(int page1Based) { }

	void applyzoom() {
		var fs = BASE_FONT * zoom;
		sourceBox.FontSize = fs;
		if (sourceBox.Document != null)
			sourceBox.Document.FontSize = fs;
		applypreviewzoom();
	}

	void applypreviewzoom() {
		try {
			if (usewpfpreview) {
				// 纯 WPF：LayoutTransform 缩放（不重建文档）
				if (Math.Abs(zoom - 1.0) < 0.02)
					previewRtb.LayoutTransform = Transform.Identity;
				else
					previewRtb.LayoutTransform = new ScaleTransform(zoom, zoom);
			} else if (previewReady) {
				previewWeb.ZoomFactor = zoom;
			}
		} catch { /* ignore */ }
	}

	public void SetSidePanelVisible(bool show) => setside(false);

	/// <summary>文档内嵌目录侧栏已废弃（改用主窗「章节列表」），始终隐藏。</summary>
	void setside(bool show) {
		sideVisible = false;
		colside.Width = new GridLength(0);
		pside.Visibility = Visibility.Collapsed;
		StatusChanged?.Invoke();
	}

	public void CaptureViewState(out double h, out double v, out double z, out int sheetOrPage) {
		h = 0;
		z = zoom;
		sheetOrPage = editMode ? (int)layout + 10 : 1;
		// V：始终存 0..1 滚动比例（跨预览/编辑、窗宽变化更稳）
		v = capturecontentratio();
		contentScrollRatio = v;
		if (editMode) {
			try {
				var sv = findscroll(sourceBox);
				if (sv != null) h = sv.HorizontalOffset;
			} catch { /* ignore */ }
		}
	}

	public void RestoreViewState(double h, double v, double z, int sheetOrPage) {
		if (z > 0.05) SetZoom(z);
		// 模式由 MainWindow.restoremdmode 恢复；此处仅兼容旧 Sheet 编码写回 layout 偏好
		if (sheetOrPage >= 10) {
			var lay = sheetOrPage - 10;
			if (lay >= 0 && lay <= 2 && !editMode)
				layout = (MdEditLayout)lay;
		}
		try {
			root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				try {
					if (editMode) {
						var sv = findscroll(sourceBox);
						if (sv != null) {
							if (h > 0) sv.ScrollToHorizontalOffset(h);
							// 新格式 V∈[0,1] 为比例；旧数据 V>1 为绝对偏移
							if (v > 1.0001)
								sv.ScrollToVerticalOffset(v);
							else
								sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(1, v)) * sv.ScrollableHeight);
						}
					} else if (usewpfpreview) {
						var r = v > 1.0001
							? 0 // 旧绝对像素在 WPF 下无法精确还原，落到顶
							: Math.Max(0, Math.Min(1, v));
						if (v > 1.0001) {
							// 有 ScrollableHeight 后再尽量按像素
							pendingScrollY = v;
							previewScrollY = v;
							root.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
								try {
									var sv = findscroll(previewRtb);
									if (sv != null) sv.ScrollToVerticalOffset(v);
								} catch { /* ignore */ }
							}));
						} else {
							contentScrollRatio = r;
							previewScrollRatio = r;
							restorewpfscrollratio(r);
						}
					} else {
						if (v > 1.0001) {
							// 旧：绝对像素 Y
							pendingScrollY = v;
							previewScrollY = v;
							restoreScrollAfterNav = true;
							restoreScrollRatioAfterNav = false;
							var y = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
							_ = runpreviewjs($"window.scrollTo(0, {y});");
						} else {
							var r = Math.Max(0, Math.Min(1, v));
							contentScrollRatio = r;
							previewScrollRatio = r;
							pendingScrollRatio = r;
							restoreScrollRatioAfterNav = true;
							restoreScrollAfterNav = false;
							var rs = r.ToString(System.Globalization.CultureInfo.InvariantCulture);
							_ = runpreviewjs($"window.mdScrollRatio&&mdScrollRatio({rs});");
						}
					}
				} catch { /* ignore */ }
			}));
		} catch { /* ignore */ }
	}

	public bool TryCopySelection() {
		try {
			if (editMode && !sourceBox.Selection.IsEmpty) {
				Clipboard.SetText(sourceBox.Selection.Text);
				return true;
			}
			// 纯 WPF 预览可选中复制
			if (!editMode && usewpfpreview && previewRtb != null && !previewRtb.Selection.IsEmpty) {
				Clipboard.SetText(previewRtb.Selection.Text);
				return true;
			}
			return false;
		} catch { return false; }
	}

	public FindResult Find(string text, bool forward, bool ignoreCase, bool restart = false, bool fromView = false) {
		if (string.IsNullOrEmpty(text)) {
			ClearFind();
			return FindResult.Miss();
		}
		try {
			// 编辑中以 RTB 为准同步 rawText
			if (editMode) {
				try {
					var t = getsourceplain();
					if (t != null) rawText = t;
				} catch { /* keep */ }
			}
			if (restart || findQuery != text || findIgnoreCase != ignoreCase || findHits.Count == 0)
				rebuildfind(text, ignoreCase);
			if (findHits.Count == 0) {
				clearfindhighlight();
				return FindResult.Miss();
			}
			if (findIndex < 0)
				findIndex = forward ? 0 : findHits.Count - 1;
			else
				findIndex = forward
					? (findIndex + 1) % findHits.Count
					: (findIndex - 1 + findHits.Count) % findHits.Count;
			// 禁止切换预览/编辑/布局，只在当前模式内跳转高亮
			jumptofind(findIndex);
			return FindResult.Hit(findIndex + 1, findHits.Count);
		} catch (Exception ex) {
			DocLog.Warn($"Md Find: {ex.Message}");
			return FindResult.Miss(findHits.Count);
		}
	}

	public void ClearFind() {
		clearfindhighlight();
		findQuery = null;
		findHits.Clear();
		findIndex = -1;
	}

	void rebuildfind(string text, bool ignoreCase) {
		findQuery = text;
		findIgnoreCase = ignoreCase;
		findHits.Clear();
		findIndex = -1;
		// 始终在权威源码中查找（含 MD 标记），与是否编辑无关
		var src = rawText ?? "";
		if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(text)) return;
		var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var i = 0;
		while (i < src.Length) {
			var j = src.IndexOf(text, i, cmp);
			if (j < 0) break;
			findHits.Add(j);
			i = j + Math.Max(1, text.Length);
		}
		DocLog.Info($"Md find rebuild q={text} hits={findHits.Count} edit={editMode}");
	}

	/// <summary>当前模式内跳到第 idx 个命中：编辑→源码选区；预览→滚动+黄底；侧预两边都尽量对齐。</summary>
	void jumptofind(int idx) {
		if (idx < 0 || idx >= findHits.Count || string.IsNullOrEmpty(findQuery)) return;
		var off = findHits[idx];
		var len = findQuery.Length;
		var line0 = lineof(rawText ?? "", off);
		clearfindhighlight();

		if (editMode) {
			// 源码框：选中命中（不改 layout）
			try {
				setcaretoffset(off);
				var start = sourceBox.CaretPosition;
				if (start != null) {
					var end = pointerfromoffset(sourceBox.Document, off + len) ?? start;
					// 用精确偏移还原选区起止
					start = pointerfromoffset(sourceBox.Document, off) ?? start;
					sourceBox.Selection.Select(start, end);
					scrollpointertotop(sourceBox, start);
				}
				// 侧预：同步滚到对应块
				if (hassidepreview)
					scrollpreviewtoline(line0);
			} catch (Exception ex) {
				DocLog.Warn($"Md find jump edit: {ex.Message}");
			}
			return;
		}

		// 纯预览：滚到块 + WebView 内黄底高亮
		try {
			scrollpreviewtoline(line0);
			highlightinpreview(findQuery, idx);
		} catch (Exception ex) {
			DocLog.Warn($"Md find jump preview: {ex.Message}");
		}
	}

	void scrollpreviewtoline(int line0) {
		try {
			if (mdDoc == null)
				mdDoc = MdParser.Parse(rawText ?? "");
			var target = line0;
			if (mdDoc != null) {
				var bi = MdParser.BlockIndexForLine(mdDoc, line0);
				if (bi >= 0 && bi < mdDoc.Blocks.Count)
					target = mdDoc.Blocks[bi].SourceLine0;
			}
			suppresspreviewtosource();
			if (usewpfpreview) {
				scrollwpfpreviewtoline(target);
				lastPreviewTopLine = target;
				return;
			}
			_ = runpreviewjs($"window.mdScrollToLine&&mdScrollToLine({target});");
		} catch { /* ignore */ }
	}

	void scrollwpfpreviewtoline(int line0) {
		try {
			var fd = previewRtb?.Document;
			if (fd == null) return;
			var block = MdFlowBuilder.FindBlockBySourceLine(fd, line0);
			if (block == null) return;
			TextPointer tp = null;
			try { tp = block.ContentStart; } catch { /* ignore */ }
			if (tp != null)
				scrollpointertotop(previewRtb, tp);
			else
				block.BringIntoView();
		} catch { /* ignore */ }
	}

	/// <summary>在预览中高亮第 hitIndex 次可见文本命中。</summary>
	void highlightinpreview(string query, int hitIndex) {
		if (string.IsNullOrEmpty(query)) return;
		if (usewpfpreview) {
			highlightinwpfpreview(query, hitIndex);
			return;
		}
		var q = jsencode(query);
		_ = runpreviewjs($"window.mdHighlightFind&&mdHighlightFind(\"{q}\",{hitIndex});");
	}

	void highlightinwpfpreview(string query, int hitIndex) {
		try {
			clearwpffindhl();
			var fd = previewRtb?.Document;
			if (fd == null || string.IsNullOrEmpty(query)) return;
			var full = new TextRange(fd.ContentStart, fd.ContentEnd).Text ?? "";
			var cmp = findIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			var i = 0;
			var n = 0;
			var found = -1;
			while (i < full.Length) {
				var j = full.IndexOf(query, i, cmp);
				if (j < 0) break;
				if (n == hitIndex) { found = j; break; }
				n++;
				i = j + Math.Max(1, query.Length);
			}
			if (found < 0) return;
			var start = pointerfromcharindex(fd, found);
			var end = pointerfromcharindex(fd, found + query.Length);
			if (start == null || end == null) return;
			wpfFindHl = new TextRange(start, end);
			wpfFindHl.ApplyPropertyValue(TextElement.BackgroundProperty,
				new SolidColorBrush(Color.FromRgb(0xFE, 0xF0, 0x8A)));
			previewRtb.Selection.Select(start, end);
			scrollpointertotop(previewRtb, start);
		} catch (Exception ex) {
			DocLog.Warn($"Md WPF find hl: {ex.Message}");
		}
	}

	void clearwpffindhl() {
		try {
			if (wpfFindHl != null) {
				wpfFindHl.ApplyPropertyValue(TextElement.BackgroundProperty, null);
				wpfFindHl = null;
			}
			if (previewRtb != null && !previewRtb.Selection.IsEmpty)
				previewRtb.Selection.Select(previewRtb.Document.ContentStart, previewRtb.Document.ContentStart);
		} catch { /* ignore */ }
	}

	void clearfindhighlight() {
		if (usewpfpreview)
			clearwpffindhl();
		else
			_ = runpreviewjs("window.mdClearFind&&mdClearFind();");
	}

	static string jsencode(string s) {
		if (string.IsNullOrEmpty(s)) return "";
		var sb = new StringBuilder(s.Length + 8);
		foreach (var ch in s) {
			switch (ch) {
				case '\\': sb.Append("\\\\"); break;
				case '"': sb.Append("\\\""); break;
				case '\r': sb.Append("\\r"); break;
				case '\n': sb.Append("\\n"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (ch < 32) sb.AppendFormat("\\u{0:x4}", (int)ch);
					else sb.Append(ch);
					break;
			}
		}
		return sb.ToString();
	}

	/// <summary>按 FlowDocument 纯文本字符下标（\n 计 1）取 TextPointer。</summary>
	static TextPointer pointerfromcharindex(FlowDocument doc, int charIndex) {
		if (doc == null) return null;
		if (charIndex <= 0) return doc.ContentStart;
		try {
			var nav = doc.ContentStart;
			var seen = 0;
			while (nav != null && nav.CompareTo(doc.ContentEnd) < 0) {
				if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text) {
					var run = nav.GetTextInRun(LogicalDirection.Forward) ?? "";
					// GetTextInRun 不含换行；段落边界另计
					if (seen + run.Length >= charIndex)
						return nav.GetPositionAtOffset(charIndex - seen, LogicalDirection.Forward);
					seen += run.Length;
					nav = nav.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
				} else {
					var next = nav.GetNextContextPosition(LogicalDirection.Forward);
					if (next == null) break;
					// ElementEnd of Paragraph ≈ \n in TextRange.Text
					if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd
						&& nav.Parent is Paragraph) {
						if (seen + 1 >= charIndex) return next;
						seen++;
					}
					nav = next;
				}
			}
			return doc.ContentEnd;
		} catch { return doc.ContentStart; }
	}

	/// <summary>按源码偏移（与 getcaretoffset 一致）定位 TextPointer。</summary>
	static TextPointer pointerfromoffset(FlowDocument doc, int offset) {
		return pointerfromcharindex(doc, offset);
	}

	void onwheel(object sender, MouseWheelEventArgs e) {
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
			if (e.Delta > 0) ZoomIn();
			else ZoomOut();
			e.Handled = true;
		}
	}

	/// <summary>
	/// 预览区 Ctrl+滚轮缩放；
	/// 纯 WPF 普通滚轮按视口比例加速（系统默认行滚过慢）；WebView 交给内核。
	/// </summary>
	void onpreviewwheel(object sender, MouseWheelEventArgs e) {
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
			if (e.Delta > 0) ZoomIn();
			else ZoomOut();
			e.Handled = true;
			return;
		}
		// 仅 WPF 预览：加大步进（约 1/4 视口，多档 Delta 叠加）
		if (!usewpfpreview || !previewvisible) return;
		try {
			var sv = findscroll(previewRtb);
			if (sv == null) return;
			// 与 Docx 一致：视口 22%，且按 notch 数叠加（高速触控板/高分辨率滚轮）
			var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
			var step = Math.Max(96, sv.ViewportHeight * 0.25) * notches;
			// LayoutTransform 缩放时偏移在未缩放坐标系，按 zoom 放大步进以保持观感
			if (zoom > 0.05) step /= zoom;
			e.Handled = true;
			if (e.Delta > 0)
				sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - step));
			else
				sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + step));
		} catch { /* ignore */ }
	}

	public void Dispose() {
		try { tick.Stop(); } catch { /* ignore */ }
		ClearFind();
		try { previewWeb?.Dispose(); } catch { /* ignore */ }
		rawText = null;
		mdDoc = null;
	}

	static int countlines(string s) {
		if (string.IsNullOrEmpty(s)) return 0;
		var n = 1;
		foreach (var c in s)
			if (c == '\n') n++;
		return n;
	}

	static double clamp(double v, double a, double b) {
		if (v < a) return a;
		if (v > b) return b;
		return v;
	}

	/// <summary>h1–h6 标题正文色（纯代码 / Typora 共用）。</summary>
	static Brush headingfg(int level) {
		switch (level) {
			case 1: return brush(0x1D, 0x4E, 0xD8); // 蓝
			case 2: return brush(0x6D, 0x28, 0xD9); // 紫
			case 3: return brush(0x0F, 0x76, 0x6E); // 青
			case 4: return brush(0xC2, 0x41, 0x0C); // 橙
			case 5: return brush(0xBE, 0x18, 0x5D); // 玫红
			default: return brush(0x47, 0x55, 0x69); // 灰蓝 h6
		}
	}

	/// <summary># 标记色：同层级略淡，便于与正文区分。</summary>
	static Brush headingmarkfg(int level) {
		switch (level) {
			case 1: return brush(0x60, 0xA5, 0xFA);
			case 2: return brush(0xA7, 0x8B, 0xFA);
			case 3: return brush(0x5E, 0xEA, 0xD4);
			case 4: return brush(0xFD, 0xBA, 0x74);
			case 5: return brush(0xF9, 0xA8, 0xD4);
			default: return brush(0x94, 0xA3, 0xB8);
		}
	}

	static SolidColorBrush brush(byte r, byte g, byte b) {
		var br = new SolidColorBrush(Color.FromRgb(r, g, b));
		if (br.CanFreeze) br.Freeze();
		return br;
	}
}
