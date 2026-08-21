using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DocviewWPF;

public partial class MainWindow : Window {
	/// <summary>文档区域（图片预览层挂在此 Grid 上，覆盖标签内容，不含侧栏/工具栏）。</summary>
	public Panel DocOverlayHost => pcontent;
	const int WM_GETMINMAXINFO = 0x0024;
	const uint MONITOR_DEFAULTTONEAREST = 2;
	const double TAB_DRAG_THRESHOLD = 6;

	/// <summary>所有存活主窗（多窗口 Tab 拖拽/合并）。</summary>
	static readonly List<MainWindow> liveWindows = new();

	readonly List<DocTab> opentabs = new();
	int titleClickTick;
	bool pageBoxSilent;
	bool restoring;
	bool suppressTabLoad;
	/// <summary>
	/// 启动时最终要钉住的 Tab（会话恢复 / 命令行打开）。
	/// 命令行打开会覆盖会话选中，避免 Loaded 延迟回调把焦点拉回旧 Tab。
	/// </summary>
	DocTab pendingPinTab;
	/// <summary>次要窗口：不恢复会话、不解析命令行。</summary>
	readonly bool isSecondary;
	/// <summary>滚动进度防抖保存。</summary>
	DispatcherTimer progressTimer;
	IDocViewer progressViewer;

	/// <summary>外部文件变更：防抖处理（TickCount）。</summary>
	DispatcherTimer fileWatchTimer;
	const int FILE_WATCH_DEBOUNCE_MS = 450;
	const int FILE_WATCH_SELF_SUPPRESS_MS = 1000;

	// —— Tab 拖拽状态 ——
	DocTab tabDragDoc;
	Point tabDragStart;
	bool tabDragging;
	FrameworkElement tabDragHeader;
	int tabDragInsert = -1;
	MainWindow tabDragOverWin;
	/// <summary>按下时鼠标在 Tab 芯片内的偏移（DIP）。</summary>
	Point tabDragGrabInHeader;
	/// <summary>浮动窗模式下，光标相对窗口左上角的偏移（DIP）。</summary>
	Point tabDragGrabInWin;
	/// <summary>拖拽过程中已应用的插入下标（live reorder）。</summary>
	int tabDragLiveAdj = -1;
	/// <summary>已拖出标签栏，正在跟手浮动窗口。</summary>
	bool tabDragFloated;
	/// <summary>拆窗/并窗时转移捕获，忽略 LostMouseCapture 取消。</summary>
	bool tabDragTransferring;
	const double TAB_SLIDE_MS = 160;

	/// <summary>z 键缩放循环：0=适页 1=适宽 2=100%。</summary>
	int zoomCycle;
	/// <summary>切换/恢复 Tab 查找框时禁止 TextChanged 清高亮。</summary>
	bool findBoxSilent;

	/// <summary>新窗口打开 MD 后待应用的模式/锚点（loadasync 完成后消费）。</summary>
	bool? pendingMdEdit;
	MdEditLayout? pendingMdLayout;
	string pendingMdAnchor;

	public MainWindow() : this(false) { }

	public MainWindow(bool secondary) {
		isSecondary = secondary;
		InitializeComponent();
		liveWindows.Add(this);
		Closed += (_, _) => {
			liveWindows.Remove(this);
			// 无窗口时退出
			if (liveWindows.Count == 0) {
				try { Application.Current?.Shutdown(); } catch { /* ignore */ }
			}
		};
		SourceInitialized += (_, _) => {
			var hwnd = new WindowInteropHelper(this).Handle;
			var src = HwndSource.FromHwnd(hwnd);
			src?.AddHook(wndproc);
			// 供次实例 SetForegroundWindow
			try { SingleInstance.PublishHwnd(hwnd); } catch { /* ignore */ }
		};
		Activated += (_, _) => {
			try {
				var hwnd = new WindowInteropHelper(this).Handle;
				if (hwnd != IntPtr.Zero)
					SingleInstance.PublishHwnd(hwnd);
			} catch { /* ignore */ }
		};
		applyuifont();
		initcaption();
		initmenu();
		inittoolbar();
		initxlsxedit();
		inittxtmdedit();
		initpdfedit();
		initpdfannot();
		initdrop();
		initfilewatch();
		initbookmarks();
		if (!isSecondary)
			restorewindowbounds();
		Loaded += (_, _) => {
			applyuifont();
			if (!isSecondary) {
				// 先恢复关闭栈 / 工作区，再恢复标签
				try {
					var sess = SessionStore.Load();
					ClosedTabsStore.ReplaceAll(sess.ClosedTabs);
					if (!string.IsNullOrWhiteSpace(sess.WorkspaceFolder))
						setworkspace(sess.WorkspaceFolder, rebuild: true);
					leftSideVisible = sess.LeftSideVisible;
					applyleftsideui();
					if (sideTabs != null && sess.LeftSideTab >= 0 && sess.LeftSideTab < sideTabs.Items.Count)
						sideTabs.SelectedIndex = sess.LeftSideTab;
				} catch { /* ignore */ }
				if (AppSettings.Current.RestoreTabs)
					restoresession();
				openargs();
				// 无工作区时：用当前文件目录
				if (string.IsNullOrEmpty(workspaceFolder) && current()?.Path != null)
					trysetworkspacefromfile(current().Path);
			}
			// 书签栏所有窗口都显示（状态全局）
			try { refreshbookmarksbar(); } catch { /* ignore */ }
		};
		Closing += onwindowclosing;
		// 兜底：进度与窗口位置；会话已在各窗 Closing 写入，此处勿再 savesession（opentabs 已清空会写成 0）
		if (!isSecondary) {
			Application.Current.Exit += (_, _) => {
				try { saveallprogress(); } catch { /* ignore */ }
				try { savewindowbounds(); } catch { /* ignore */ }
			};
		}
	}

	void initcaption() {
		bmin.Click += (_, _) => WindowState = WindowState.Minimized;
		bmax.Click += (_, _) => togglemax();
		bclosewin.Click += (_, _) => Close();
		if (bnewtab != null) {
			// + 弹出菜单：浏览器 / 命令行
			bnewtab.Click += (_, e) => {
				if (bnewtab.ContextMenu != null) {
					bnewtab.ContextMenu.PlacementTarget = bnewtab;
					bnewtab.ContextMenu.IsOpen = true;
					e.Handled = true;
				}
			};
			if (mnnewbrowser != null) mnnewbrowser.Click += (_, _) => openbrowsertab();
			if (mnnewconsole != null) mnnewconsole.Click += (_, _) => openconsoletab();
		}
		if (bsettings != null)
			bsettings.Click += (_, _) => opensettings();
		syncmaxbtn();
	}

	void ontitledrag(object sender, MouseButtonEventArgs e) {
		// 空白标题区主要靠 WindowChrome CaptionHeight 系统拖动/双击；
		// 此处兜底：点在未标 IsHitTestVisibleInChrome 的客户区时仍可拖/双击。
		if (e.ChangedButton != MouseButton.Left) return;
		if (istitleinteractive(e.OriginalSource as DependencyObject)) return;

		if (e.ClickCount == 2) {
			togglemax();
			titleClickTick = 0;
			e.Handled = true;
			return;
		}

		titleClickTick = Environment.TickCount;
		try {
			if (Mouse.LeftButton != MouseButtonState.Pressed) return;
			if (WindowState == WindowState.Maximized) {
				// 最大化下拖出：先还原再 DragMove
				var mouse = PointToScreen(e.GetPosition(this));
				var ratio = SystemParameters.PrimaryScreenWidth > 1
					? mouse.X / SystemParameters.PrimaryScreenWidth
					: 0.5;
				WindowState = WindowState.Normal;
				Left = mouse.X - ActualWidth * ratio;
				Top = Math.Max(0, mouse.Y - 16);
			}
			DragMove();
		} catch { /* ignore */ }
	}

	/// <summary>标题栏上菜单 / 按钮 / Tab 芯片等不应启动拖窗。</summary>
	static bool istitleinteractive(DependencyObject d) {
		while (d != null) {
			if (d is Menu || d is MenuItem || d is Button) return true;
			if (d is Border br && br.Tag is TabItem) return true;
			if (d is FrameworkElement fe && fe.Parent is Panel p && p.Name == "ptabs")
				return true;
			d = safevisualparent(d);
		}
		return false;
	}

	void onstatechanged(object sender, EventArgs e) {
		syncmaxbtn();
		syncmaxchrome();
	}

	void togglemax() {
		WindowState = WindowState == WindowState.Maximized
			? WindowState.Normal
			: WindowState.Maximized;
	}

	void syncmaxbtn() {
		if (bmax == null) return;
		bmax.Content = WindowState == WindowState.Maximized ? "❐" : "□";
		bmax.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
	}

	/// <summary>最大化时去掉可调边与外框，避免多出一圈或盖住任务栏观感异常。</summary>
	void syncmaxchrome() {
		var maxed = WindowState == WindowState.Maximized;
		try {
			var chrome = WindowChrome.GetWindowChrome(this);
			if (chrome != null)
				chrome.ResizeBorderThickness = maxed || fullscreen
					? new Thickness(0)
					: new Thickness(8, 6, 8, 6);
		} catch { /* ignore */ }
		try {
			if (proot != null)
				proot.BorderThickness = maxed || fullscreen ? new Thickness(0) : new Thickness(1);
		} catch { /* ignore */ }
	}

	/// <summary>
	/// 普通最大化：限制到工作区（不压任务栏）。
	/// F 全屏：铺满整块显示器（含任务栏区域）。
	/// </summary>
	IntPtr wndproc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
		if (msg == WM_GETMINMAXINFO) {
			wmgetminmaxinfo(hwnd, lParam, fullMonitor: fullscreen);
			handled = true;
		}
		return IntPtr.Zero;
	}

	static void wmgetminmaxinfo(IntPtr hwnd, IntPtr lParam, bool fullMonitor) {
		var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
		var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
		if (mon == IntPtr.Zero) return;
		var mi = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
		if (!GetMonitorInfo(mon, ref mi)) return;
		var monitor = mi.rcMonitor;
		var target = fullMonitor ? mi.rcMonitor : mi.rcWork;
		mmi.ptMaxPosition.X = target.Left - monitor.Left;
		mmi.ptMaxPosition.Y = target.Top - monitor.Top;
		mmi.ptMaxSize.X = target.Right - target.Left;
		mmi.ptMaxSize.Y = target.Bottom - target.Top;
		mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
		mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
		Marshal.StructureToPtr(mmi, lParam, false);
	}

	[DllImport("user32.dll")]
	static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

	[StructLayout(LayoutKind.Sequential)]
	struct PointI {
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct MinMaxInfo {
		public PointI ptReserved;
		public PointI ptMaxSize;
		public PointI ptMaxPosition;
		public PointI ptMinTrackSize;
		public PointI ptMaxTrackSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct RectI {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	struct MonitorInfo {
		public int cbSize;
		public RectI rcMonitor;
		public RectI rcWork;
		public int dwFlags;
	}

	/// <summary>工作区根目录（资源管理器）。</summary>
	string workspaceFolder;
	/// <summary>浏览器新标签序号（browser:new-N）。</summary>
	int browserSeq;
	/// <summary>命令行新标签序号（console:new-N）。</summary>
	int consoleSeq;
	/// <summary>主窗左侧栏是否展开。</summary>
	bool leftSideVisible = true;
	bool syncOutlineTree;
	/// <summary>主窗章节列表：由 Viewer 原 applytocsync 结果镜像，不另算定位。</summary>
	bool mainOutlineHlSyncing;
	int lastMainOutlineTag = int.MinValue;
	/// <summary>用户点击章节后，忽略滚动镜像高亮的截止 TickCount（防连点跳闪）。</summary>
	int ignoreMainOutlineHlUntil;
	const int MAIN_OUTLINE_CLICK_SUPPRESS_MS = 700;
	/// <summary>筛选后点击结果：清空搜索后展开并定位到该 Tag。</summary>
	bool pendingOutlineReveal;
	int pendingOutlineRevealTag;

	void initmenu() {
		mnopen.Click += (_, _) => openfiles();
		if (mnopenfolder != null) mnopenfolder.Click += (_, _) => openfolder();
		mnrecent.SubmenuOpened += (_, _) => buildrecentmenu();
		if (mnprint != null) mnprint.Click += (_, _) => printcurrent();
		if (mnexporthtml != null) mnexporthtml.Click += (_, _) => exportmdhtml();
		if (mnexportpdf != null) mnexportpdf.Click += (_, _) => exportmdpdf();
		if (mnsaveimage != null) mnsaveimage.Click += (_, _) => saveimageas();
		if (mncopypath != null) mncopypath.Click += (_, _) => copyfilepath();
		if (mnshowinexplorer != null) mnshowinexplorer.Click += (_, _) => showinexplorer();
		if (mnopenwithsystem != null) mnopenwithsystem.Click += (_, _) => openwithsystem();
		mnclose.Click += (_, _) => closecurrent();
		mncloseall.Click += (_, _) => closeall();
		if (mnreopen != null) mnreopen.Click += (_, _) => reopenclosedtab();
		mnexit.Click += (_, _) => Close();
		mnzoomin.Click += (_, _) => zoomin();
		mnzoomout.Click += (_, _) => zoomout();
		mnzoom100.Click += (_, _) => setzoom(1.0);
		mnfitwidth.Click += (_, _) => fitwidth();
		mnfitpage.Click += (_, _) => fitpage();
		mnprev.Click += (_, _) => navpage(false);
		mnnext.Click += (_, _) => navpage(true);
		mngotopage.Click += (_, _) => focuspagebox();
		mnside.Click += (_, _) => toggleside();
		if (mnbookmarks != null) mnbookmarks.Click += (_, _) => togglebookmarksbar();
		if (mnaddbookmark != null) mnaddbookmark.Click += (_, _) => addoreditbookmarkdialog();
		if (mnsplit != null) mnsplit.Click += (_, _) => togglesplit();
		if (mnsplitclose != null) mnsplitclose.Click += (_, _) => closesplit();
		if (bsplitclose != null) bsplitclose.Click += (_, _) => closesplit();
		if (csplitfile != null) csplitfile.SelectionChanged += onsplitfileselected_handler;
		if (lbenc != null) {
			lbenc.MouseLeftButtonUp += (_, _) => pickencoding();
		}
		// 左侧文件夹：标题栏悬停 4 按钮（仿 VS Code Explorer）
		if (pexplorerhdr != null && pexploreracts != null) {
			pexplorerhdr.MouseEnter += (_, _) => setexploreracts(true);
			pexplorerhdr.MouseLeave += (_, _) => setexploreracts(false);
		}
		if (lbworkspace != null)
			lbworkspace.MouseLeftButtonUp += (_, _) => openfolder();
		if (bnewfile != null) bnewfile.Click += (_, _) => newworkspacefile();
		if (bnewfolder != null) bnewfolder.Click += (_, _) => newworkspacefolder();
		if (bfolderrefresh != null) bfolderrefresh.Click += (_, _) => refreshfoldertree();
		if (bfoldercollapse != null) bfoldercollapse.Click += (_, _) => collapsefoldertree();
		if (treeFiles != null) {
			FolderTree.ConfigureTree(treeFiles);
			treeFiles.MouseDoubleClick += onfiletreedoubleclick;
			treeFiles.KeyDown += onfiletreekeydown;
		}
		if (treeOutline != null) {
			OutlineUi.ConfigureTree(treeOutline);
			treeOutline.SelectedItemChanged += onoutlinetreeselected;
		}
		if (eoutlinefilter != null)
			eoutlinefilter.TextChanged += (_, _) => rebuildmainoutline();
		if (sideTabs != null)
			sideTabs.SelectionChanged += (_, _) => { /* 选中目录 Tab 时刷新 TOC */ if (sideTabs.SelectedIndex == 1) rebuildmainoutline(); };
		if (mnpdfeditor != null) mnpdfeditor.Click += (_, _) => openpdfeditor();
		mnsettings.Click += (_, _) => opensettings();
		if (mnlang != null) buildlangmenu();
		if (mncheckupdate != null)
			mncheckupdate.Click += async (_, _) => {
				try {
					await AppUpdater.RunCheckAndUpdateAsync(this);
				} catch (Exception ex) {
					DocLog.Error("checkupdate", ex);
					MessageBox.Show(this, "检查更新失败: " + ex.Message, "DocviewWPF",
						MessageBoxButton.OK, MessageBoxImage.Warning);
				}
			};
		mabout.Click += (_, _) => {
			var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
			var verText = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.2";
			MessageBox.Show(
				Loc.Tf("about_body", verText, Loc.T("about_features")),
				Loc.T("about"),
				MessageBoxButton.OK,
				MessageBoxImage.Information);
		};
		buildrecentmenu();
		Loc.LanguageChanged += () => {
			try {
				Dispatcher.BeginInvoke(new Action(() => {
					applylang();
					buildlangmenu();
					buildrecentmenu();
				}));
			} catch { /* ignore */ }
		};
		applylang();
	}

	void buildlangmenu() {
		if (mnlang == null) return;
		mnlang.Header = Loc.T("language");
		mnlang.Items.Clear();
		foreach (var (code, name) in Loc.Languages) {
			var c = code;
			var mi = new MenuItem {
				Header = name,
				IsCheckable = true,
				IsChecked = string.Equals(Loc.Lang, c, StringComparison.OrdinalIgnoreCase),
			};
			mi.Click += (_, _) => setuilang(c);
			mnlang.Items.Add(mi);
		}
	}

	void setuilang(string code) {
		if (string.IsNullOrWhiteSpace(code)) return;
		Loc.SetLanguage(code);
		AppSettings.Current.Language = Loc.Lang;
		AppSettings.Current.Save();
		applylang();
		buildlangmenu();
	}

	/// <summary>按当前语言刷新主窗菜单/工具栏/状态等静态文案。</summary>
	void applylang() {
		try {
			if (mnhamburger != null) mnhamburger.ToolTip = Loc.T("menu");
			if (mnfile != null) mnfile.Header = Loc.T("file");
			if (mnopen != null) mnopen.Header = Loc.T("open");
			if (mnopenfolder != null) mnopenfolder.Header = "打开文件夹(_F)...";
			if (mnrecent != null) mnrecent.Header = Loc.T("recent");
			if (mnprint != null) mnprint.Header = Loc.T("print");
			if (mnexport != null) mnexport.Header = "导出";
			if (mnexporthtml != null) mnexporthtml.Header = "Markdown 导出 HTML...";
			if (mnexportpdf != null) mnexportpdf.Header = "Markdown 导出 PDF...";
			if (mnsaveimage != null) mnsaveimage.Header = "图片另存为...";
			if (mncopypath != null) mncopypath.Header = Loc.T("copy_path");
			if (mnshowinexplorer != null) mnshowinexplorer.Header = Loc.T("show_in_explorer");
			if (mnopenwithsystem != null) mnopenwithsystem.Header = Loc.T("open_with_system");
			if (mnclose != null) mnclose.Header = Loc.T("close");
			if (mncloseall != null) mncloseall.Header = Loc.T("close_all");
			if (mnreopen != null) mnreopen.Header = "重新打开关闭的标签";
			if (mnexit != null) mnexit.Header = Loc.T("exit");
			if (mnview != null) mnview.Header = Loc.T("view");
			if (mnzoomin != null) mnzoomin.Header = Loc.T("zoom_in");
			if (mnzoomout != null) mnzoomout.Header = Loc.T("zoom_out");
			if (mnzoom100 != null) mnzoom100.Header = Loc.T("zoom_100");
			if (mnfitpage != null) mnfitpage.Header = Loc.T("fit_page");
			if (mnfitwidth != null) mnfitwidth.Header = Loc.T("fit_width");
			if (mnprev != null) mnprev.Header = Loc.T("prev_page");
			if (mnnext != null) mnnext.Header = Loc.T("next_page");
			if (mngotopage != null) mngotopage.Header = Loc.T("goto_page");
			if (mnside != null) mnside.Header = Loc.T("side_panel");
			if (mnsplit != null) mnsplit.Header = "左右分屏";
			if (mnsplitclose != null) mnsplitclose.Header = "关闭分屏";
			if (mntools != null) mntools.Header = Loc.T("tools");
			if (mnpdfeditor != null) mnpdfeditor.Header = Loc.T("pdf_pro_edit");
			if (mnsettings != null) mnsettings.Header = Loc.T("settings");
			if (mnhelp != null) mnhelp.Header = Loc.T("help");
			if (mnlang != null) mnlang.Header = Loc.T("language");
			if (mncheckupdate != null) mncheckupdate.Header = "检查更新...";
			if (mabout != null) mabout.Header = Loc.T("about");

			if (bmin != null) bmin.ToolTip = Loc.T("minimize");
			if (bmax != null)
				bmax.ToolTip = WindowState == WindowState.Maximized ? Loc.T("restore") : Loc.T("maximize");
			if (bclosewin != null) bclosewin.ToolTip = Loc.T("close_window");
			if (bsettings != null) bsettings.ToolTip = Loc.T("tip_settings");

			if (bopen != null) bopen.ToolTip = Loc.T("tip_open");
			if (bprint != null) bprint.ToolTip = Loc.T("tip_print");
			if (bside != null) bside.ToolTip = Loc.T("tip_side");
			if (lbpagelabel != null) lbpagelabel.Text = Loc.T("page_label");
			if (epage != null) epage.ToolTip = Loc.T("tip_page");
			if (bprev != null) bprev.ToolTip = Loc.T("tip_prev");
			if (bnext != null) bnext.ToolTip = Loc.T("tip_next");
			if (bfitpage != null) bfitpage.ToolTip = Loc.T("tip_fit_page");
			if (bfitwidth != null) bfitwidth.ToolTip = Loc.T("tip_fit_width");
			if (brotatel != null) brotatel.ToolTip = Loc.T("tip_rotate_left");
			if (brotater != null) brotater.ToolTip = Loc.T("tip_rotate_right");
			if (bzoomout != null) bzoomout.ToolTip = Loc.T("tip_zoom_out");
			if (bzoomin != null) bzoomin.ToolTip = Loc.T("tip_zoom_in");
			if (lbfindlabel != null) lbfindlabel.Text = Loc.T("find_label");
			if (efind != null) efind.ToolTip = Loc.T("tip_find");
			if (bfindprev != null) bfindprev.ToolTip = Loc.T("tip_find_prev");
			if (bfindnext != null) bfindnext.ToolTip = Loc.T("tip_find_next");
			if (bcase != null) bcase.ToolTip = Loc.T("tip_case");
			if (bxlsxedit != null && bxlsxedit.Visibility == Visibility.Visible)
				bxlsxedit.ToolTip = Loc.T("tip_xlsx_edit");
			if (bxlsxsave != null) bxlsxsave.ToolTip = Loc.T("tip_save");
			if (bpannot != null) bpannot.ToolTip = Loc.T("tip_annot");
			if (bpfdedit != null) bpfdedit.ToolTip = Loc.T("tip_pdf_edit");
			if (bpdfsave != null) bpdfsave.ToolTip = Loc.T("tip_pdf_save");
			if (bannothand != null) bannothand.ToolTip = Loc.T("tip_annot_hand");
			if (bannotsel != null) bannotsel.ToolTip = Loc.T("tip_annot_sel");
			if (bannotpen != null) bannotpen.ToolTip = Loc.T("tip_annot_pen");
			if (bannothl != null) bannothl.ToolTip = Loc.T("tip_annot_hl");
			if (bannoteraser != null) bannoteraser.ToolTip = Loc.T("tip_annot_eraser");
			if (cannoterasermode != null) cannoterasermode.ToolTip = Loc.T("tip_annot_eraser_mode");
			if (bannottext != null) bannottext.ToolTip = Loc.T("tip_annot_text");
			if (bannotnote != null) bannotnote.ToolTip = Loc.T("tip_annot_note");
			if (bannotrect != null) bannotrect.ToolTip = Loc.T("tip_annot_rect");
			if (bannotell != null) bannotell.ToolTip = Loc.T("tip_annot_ell");
			if (bannotline != null) bannotline.ToolTip = Loc.T("tip_annot_line");
			if (bannotarrow != null) bannotarrow.ToolTip = Loc.T("tip_annot_arrow");
			if (bannotcolor != null) bannotcolor.ToolTip = Loc.T("tip_annot_color");
			if (cannotfont != null) cannotfont.ToolTip = Loc.T("tip_font");
			if (cannotfontsize != null) cannotfontsize.ToolTip = Loc.T("tip_font_size");
			if (bannotgroup != null) bannotgroup.ToolTip = Loc.T("tip_annot_group");
			if (bannotungroup != null) bannotungroup.ToolTip = Loc.T("tip_annot_ungroup");
			if (bannotcopy != null) bannotcopy.ToolTip = Loc.T("tip_annot_copy");
			if (bannotdel != null) bannotdel.ToolTip = Loc.T("tip_annot_del");
			if (bannotsave != null) bannotsave.ToolTip = Loc.T("tip_annot_save");
			if (bannotsavepdf != null) bannotsavepdf.ToolTip = Loc.T("tip_annot_save_pdf");
			if (lbannottip != null) lbannottip.Text = Loc.T("annot_tip");

			if (bpdfsel != null) bpdfsel.ToolTip = Loc.T("tip_pdf_sel");
			if (bpdftext != null) bpdftext.ToolTip = Loc.T("tip_pdf_text");
			if (bpdfimg != null) bpdfimg.ToolTip = Loc.T("tip_pdf_img");
			if (bpdfedittext != null) bpdfedittext.ToolTip = Loc.T("tip_pdf_edit_sel");
			if (bpdfdel != null) bpdfdel.ToolTip = Loc.T("tip_pdf_del");
			if (cpdffont != null) cpdffont.ToolTip = Loc.T("tip_font");
			if (cpdffontsize != null) cpdffontsize.ToolTip = Loc.T("tip_font_size");
			if (bpdfbold != null) bpdfbold.ToolTip = Loc.T("tip_bold");
			if (bpdfitalic != null) bpdfitalic.ToolTip = Loc.T("tip_italic");
			if (bpdffore != null) bpdffore.ToolTip = Loc.T("tip_fore");
			if (bpdfback != null) bpdfback.ToolTip = Loc.T("tip_back");

			if (bxmerge != null) bxmerge.ToolTip = Loc.T("tip_merge");
			if (bxunmerge != null) bxunmerge.ToolTip = Loc.T("tip_unmerge");
			if (bxalignl != null) bxalignl.ToolTip = Loc.T("tip_align_l");
			if (bxalignc != null) bxalignc.ToolTip = Loc.T("tip_align_c");
			if (bxalignr != null) bxalignr.ToolTip = Loc.T("tip_align_r");
			if (bxvalignt != null) bxvalignt.ToolTip = Loc.T("tip_valign_t");
			if (bxvalignm != null) bxvalignm.ToolTip = Loc.T("tip_valign_m");
			if (bxvalignb != null) bxvalignb.ToolTip = Loc.T("tip_valign_b");
			if (cxfont != null) cxfont.ToolTip = Loc.T("tip_font");
			if (cxfontsize != null) cxfontsize.ToolTip = Loc.T("tip_font_size");
			if (bxbold != null) bxbold.ToolTip = Loc.T("tip_bold");
			if (bxitalic != null) bxitalic.ToolTip = Loc.T("tip_italic");
			if (bxfore != null) bxfore.ToolTip = Loc.T("tip_fore");
			if (bxback != null) bxback.ToolTip = Loc.T("tip_cell_back");
			if (bxwrap != null) bxwrap.ToolTip = Loc.T("tip_wrap");

			// 侧栏标题若存在
			try {
				var lb = FindName("lbside") as TextBlock;
				if (lb != null) lb.Text = Loc.T("outline");
				var ef = FindName("esidefilter") as TextBox;
				if (ef != null) ef.ToolTip = Loc.T("filter_outline");
			} catch { /* ignore */ }

			if (lbstatus != null && (string.IsNullOrEmpty(lbstatus.Text) || lbstatus.Text == "就绪" || lbstatus.Text == "Ready"
				|| lbstatus.Text == "準備完了" || lbstatus.Text == "준비됨"))
				lbstatus.Text = Loc.T("ready");
		} catch (Exception ex) {
			DocLog.Warn("applylang: " + ex.Message);
		}
	}

	/// <summary>重建「最近文件」子菜单：最多 20 条 + 清除全部。</summary>
	void buildrecentmenu() {
		if (mnrecent == null) return;
		mnrecent.Items.Clear();
		var files = RecentFilesStore.Load();
		if (files.Count == 0) {
			mnrecent.Items.Add(new MenuItem { Header = "(—)", IsEnabled = false });
		} else {
			for (var i = 0; i < files.Count; i++) {
				var path = files[i];
				var name = Path.GetFileName(path);
				var header = i < 9 ? $"_{i + 1} {name}" : $"{i + 1} {name}";
				var mi = new MenuItem {
					Header = header,
					ToolTip = path,
					Tag = path,
				};
				mi.Click += (_, _) => openrecent(path);
				mnrecent.Items.Add(mi);
			}
		}
		mnrecent.Items.Add(new Separator());
		var mclear = new MenuItem {
			Header = Loc.T("clear_recent"),
			IsEnabled = files.Count > 0,
		};
		mclear.Click += (_, _) => {
			RecentFilesStore.Clear();
			buildrecentmenu();
			lbstatus.Text = Loc.T("recent_cleared");
		};
		mnrecent.Items.Add(mclear);
	}

	void openrecent(string path) {
		path = pathnorm(path);
		if (path == null) return;
		if (!File.Exists(path)) {
			RecentFilesStore.Remove(path);
			buildrecentmenu();
			MessageBox.Show($"文件不存在，已从最近列表移除:\n{path}",
				"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		openpath(path, loadNow: true);
	}

	void opensettings() {
		try {
			var dlg = new SettingsWindow(this);
			if (dlg.ShowDialog() == true) {
				// 语言可能已在设置窗内切换
				if (!string.IsNullOrEmpty(AppSettings.Current.Language)
					&& !string.Equals(Loc.Lang, AppSettings.Current.Language, StringComparison.OrdinalIgnoreCase))
					Loc.SetLanguage(AppSettings.Current.Language);
				applylang();
				buildlangmenu();
				applyuifont();
				refreshmdpreviews();
			}
		} catch (Exception ex) {
			DocLog.Error("opensettings", ex);
			App.ShowError(ex, "系统参数");
		}
	}

	/// <summary>系统参数变更后重建已打开 MD 的预览（Tab 宽度 / 列表缩进）。</summary>
	void refreshmdpreviews() {
		try {
			foreach (var d in opentabs) {
				if (d?.Viewer is MdViewer mv) {
					try { mv.RefreshPreview(); }
					catch { /* ignore */ }
				}
			}
		} catch { /* ignore */ }
	}

	/// <summary>
	/// 界面字号统一应用到主窗、工具栏高度与控件；目录字号在各 Viewer 的 OutlineUi 中读取同一设置。
	/// </summary>
	void applyuifont() {
		var fs = 12.0;
		try { fs = AppSettings.Current.UiFontSize; } catch { /* ignore */ }
		if (fs < 10) fs = 10;
		if (fs > 18) fs = 18;
		FontSize = fs;

		// 工具栏高度随字号：图标按钮约 26 + 内边距
		var barH = Math.Max(28, fs + 16);
		if (ptoolbar != null) {
			ptoolbar.Height = barH;
			ptoolbar.Padding = new Thickness(6, 2, 6, 2);
		}
		if (ptitle != null)
			ptitle.Height = Math.Max(26, fs + 14);

		// 工具栏内输入框高度
		var boxH = Math.Max(20, fs + 8);
		if (epage != null) { epage.Height = boxH; epage.FontSize = fs; }
		if (efind != null) { efind.Height = boxH; efind.FontSize = fs; }
		if (bcase != null) { bcase.Height = boxH; bcase.FontSize = Math.Max(10, fs - 1); }
		if (lbpagelabel != null) lbpagelabel.FontSize = fs;
		if (lbpagetotal != null) lbpagetotal.FontSize = fs;
		if (lbfindlabel != null) lbfindlabel.FontSize = fs;
		if (lbfind != null) lbfind.FontSize = Math.Max(10, fs - 1);
		if (lbstatus != null) lbstatus.FontSize = Math.Max(9, fs - 1);
		if (lbpath != null) lbpath.FontSize = Math.Max(9, fs - 1);
		if (pstatus != null) pstatus.Height = Math.Max(18, fs + 6);

		// 动态改 ToolBtn 字号（资源 Style）
		try {
			if (TryFindResource("ToolBtn") is Style st) {
				// Style 只读 setters 不易改；直接遍历工具栏按钮
			}
		} catch { /* ignore */ }
		scalechildrenfont(ptoolbar, fs);
	}

	static void scalechildrenfont(DependencyObject root, double fs) {
		if (root == null) return;
		if (!(root is Visual) && !(root is System.Windows.Media.Media3D.Visual3D))
			return;
		try {
			var n = VisualTreeHelper.GetChildrenCount(root);
			for (var i = 0; i < n; i++) {
				var c = VisualTreeHelper.GetChild(root, i);
				if (c is Control ctrl && !(c is TextBox) && !(c is ToggleButton))
					ctrl.FontSize = fs;
				else if (c is TextBlock tb)
					tb.FontSize = fs;
				else if (c is TextBox te)
					te.FontSize = fs;
				scalechildrenfont(c, fs);
			}
		} catch { /* ignore non-visual */ }
	}

	void restorewindowbounds() {
		var s = AppSettings.Current;
		if (!s.RememberWindow) return;
		try {
			if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop)
				&& s.WinWidth >= MinWidth && s.WinHeight >= MinHeight) {
				// 确保在可见屏幕内
				var left = s.WinLeft;
				var top = s.WinTop;
				var w = s.WinWidth;
				var h = s.WinHeight;
				var va = SystemParameters.VirtualScreenLeft;
				var vt = SystemParameters.VirtualScreenTop;
				var vw = SystemParameters.VirtualScreenWidth;
				var vh = SystemParameters.VirtualScreenHeight;
				if (left + w < va + 40) left = va;
				if (top + 40 < vt) top = vt;
				if (left > va + vw - 40) left = va + vw - w;
				if (top > vt + vh - 40) top = vt + vh - h;
				Left = left;
				Top = top;
				Width = w;
				Height = h;
			}
			if (s.WinMaximized)
				WindowState = WindowState.Maximized;
		} catch { /* ignore */ }
	}

	void savewindowbounds() {
		var s = AppSettings.Current;
		if (!s.RememberWindow) {
			s.Save();
			return;
		}
		try {
			s.WinMaximized = WindowState == WindowState.Maximized;
			if (WindowState == WindowState.Normal) {
				s.WinLeft = Left;
				s.WinTop = Top;
				s.WinWidth = Width;
				s.WinHeight = Height;
			} else {
				// 最大化时用 RestoreBounds
				var rb = RestoreBounds;
				if (rb.Width > 0 && rb.Height > 0) {
					s.WinLeft = rb.Left;
					s.WinTop = rb.Top;
					s.WinWidth = rb.Width;
					s.WinHeight = rb.Height;
				}
			}
			s.Save();
		} catch { /* ignore */ }
	}

	void inittoolbar() {
		bopen.Click += (_, _) => openfiles();
		if (bprint != null) bprint.Click += (_, _) => printcurrent();
		if (bside != null) bside.Click += (_, _) => toggleside();
		bzoomin.Click += (_, _) => zoomin();
		bzoomout.Click += (_, _) => zoomout();
		bfitwidth.Click += (_, _) => fitwidth();
		bfitpage.Click += (_, _) => fitpage();
		if (brotatel != null) brotatel.Click += (_, _) => rotateview(-1);
		if (brotater != null) brotater.Click += (_, _) => rotateview(1);
		bprev.Click += (_, _) => navpage(false);
		bnext.Click += (_, _) => navpage(true);
		bfindnext.Click += (_, _) => dofind(true, restart: false);
		bfindprev.Click += (_, _) => dofind(false, restart: false);
		if (bcase != null) {
			bcase.Checked += (_, _) => {
				bcase.Content = "Aa";
				bcase.ToolTip = "区分大小写（已开）";
				var cur = current();
				if (cur != null) cur.FindCase = true;
				if (!findBoxSilent) clearfindui();
			};
			bcase.Unchecked += (_, _) => {
				bcase.Content = "Aa";
				bcase.ToolTip = "忽略大小写（点击开启区分）";
				var cur = current();
				if (cur != null) cur.FindCase = false;
				if (!findBoxSilent) clearfindui();
			};
			// 默认忽略大小写（与 Sumatra 一致）
			bcase.IsChecked = false;
		}
		efind.TextChanged += (_, _) => {
			if (findBoxSilent) return;
			var cur = current();
			if (cur != null) cur.FindText = efind.Text ?? "";
			// 改字只清当前 Tab 的计数与高亮，不丢各 Tab 自己的 FindText
			clearfindui();
			if (cur != null) cur.FindResultText = "";
		};
		efind.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) {
				// 始终从当前视口起算：首击第1个，连续下一个；滚远后从新视口重起
				dofind(true, restart: false, fromView: true);
				keepfindfocus();
				e.Handled = true;
			}
		};
		epage.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) {
				jumppage();
				e.Handled = true;
			}
		};
		epage.LostKeyboardFocus += (_, _) => jumppage();
	}

	/// <summary>TXT / MD 编辑入口与 MD 工程模式布局切换。</summary>
	void inittxtmdedit() {
		if (btxtedit != null)
			btxtedit.Click += (_, _) => toggletxtmdedit();
		if (btxtsave != null)
			btxtsave.Click += (_, _) => savecurrenttxtmd();
		if (bmdsource != null)
			bmdsource.Click += (_, _) => setmdlayout(MdEditLayout.Code);
		if (bmdlive != null)
			bmdlive.Click += (_, _) => setmdlayout(MdEditLayout.Typora);
		if (bmdside != null)
			bmdside.Click += (_, _) => setmdlayout(MdEditLayout.Side);
		if (bmdweb != null)
			bmdweb.Click += (_, _) => setmdpreviewengine(MdPreviewEngine.WebView);
		if (bmdwpf != null)
			bmdwpf.Click += (_, _) => setmdpreviewengine(MdPreviewEngine.Wpf);
	}

	/// <summary>XLSX 编辑模式工具栏：仅编辑模式显示格式按钮。</summary>
	void initxlsxedit() {
		if (bxlsxedit != null)
			bxlsxedit.Click += (_, _) => togglexlsxedit();
		if (bxlsxsave != null)
			bxlsxsave.Click += (_, _) => savecurrentxlsx();
		if (bxmerge != null) bxmerge.Click += (_, _) => withxlsx(x => x.MergeCells());
		if (bxunmerge != null) bxunmerge.Click += (_, _) => withxlsx(x => x.UnmergeCells());
		if (bxalignl != null) bxalignl.Click += (_, _) => withxlsx(x => x.SetAlign(TextAlignment.Left));
		if (bxalignc != null) bxalignc.Click += (_, _) => withxlsx(x => x.SetAlign(TextAlignment.Center));
		if (bxalignr != null) bxalignr.Click += (_, _) => withxlsx(x => x.SetAlign(TextAlignment.Right));
		if (bxvalignt != null) bxvalignt.Click += (_, _) => withxlsx(x => x.SetVAlign(0));
		if (bxvalignm != null) bxvalignm.Click += (_, _) => withxlsx(x => x.SetVAlign(1));
		if (bxvalignb != null) bxvalignb.Click += (_, _) => withxlsx(x => x.SetVAlign(2));
		if (bxbold != null) bxbold.Click += (_, _) => withxlsx(x => {
			x.ToggleBold();
			syncxlsxstyleui(x);
		});
		if (bxitalic != null) bxitalic.Click += (_, _) => withxlsx(x => {
			x.ToggleItalic();
			syncxlsxstyleui(x);
		});
		if (bxfore != null) bxfore.Click += (_, _) => pickxlsxcolor(fore: true);
		if (bxback != null) bxback.Click += (_, _) => pickxlsxcolor(fore: false);
		if (bxwrap != null) bxwrap.Click += (_, _) => withxlsx(x => {
			x.ToggleWrap();
			syncxlsxstyleui(x);
		});
		if (bxwraprow != null) bxwraprow.Click += (_, _) => withxlsx(x => x.SetWrapRows(true));
		if (bxwrapcol != null) bxwrapcol.Click += (_, _) => withxlsx(x => x.SetWrapCols(true));

		if (cxfont != null) {
			foreach (var f in new[] {
				"Calibri", "微软雅黑", "宋体", "黑体", "楷体", "Arial", "Times New Roman", "Consolas", "Segoe UI",
			})
				cxfont.Items.Add(f);
			cxfont.SelectionChanged += (_, _) => {
				if (cxfont.SelectedItem is string name)
					withxlsx(x => x.SetFontName(name));
			};
			cxfont.LostKeyboardFocus += (_, _) => {
				var name = cxfont.Text?.Trim();
				if (!string.IsNullOrEmpty(name))
					withxlsx(x => x.SetFontName(name));
			};
		}
		if (cxfontsize != null) {
			foreach (var s in new[] { "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36" })
				cxfontsize.Items.Add(s);
			cxfontsize.SelectionChanged += (_, _) => {
				if (cxfontsize.SelectedItem is string s && double.TryParse(s, out var pt))
					withxlsx(x => x.SetFontSizePt(pt));
			};
			cxfontsize.LostKeyboardFocus += (_, _) => {
				if (double.TryParse(cxfontsize.Text?.Trim(), out var pt))
					withxlsx(x => x.SetFontSizePt(pt));
			};
		}
	}

	/// <summary>PDF 标注模式工具栏。</summary>
	void initpdfannot() {
		if (bpannot != null) {
			bpannot.Click += (_, _) => {
				var p = currentviewer() as PdfViewer;
				if (p == null) {
					if (bpannot != null) bpannot.IsChecked = false;
					return;
				}
				p.AnnotMode = bpannot.IsChecked == true;
				syncpdfannotui();
			};
		}
		void settool(PdfAnnotSurface.Tool t) {
			withpdf(p => p.AnnotSetTool(t));
			syncannottools(t);
		}
		if (bannothand != null) bannothand.Click += (_, _) => settool(PdfAnnotSurface.Tool.Hand);
		if (bannotsel != null) bannotsel.Click += (_, _) => settool(PdfAnnotSurface.Tool.Select);
		if (bannotpen != null) bannotpen.Click += (_, _) => settool(PdfAnnotSurface.Tool.Pen);
		if (bannothl != null) bannothl.Click += (_, _) => settool(PdfAnnotSurface.Tool.Highlighter);
		if (bannoteraser != null) bannoteraser.Click += (_, _) => settool(PdfAnnotSurface.Tool.Eraser);
		if (cannoterasermode != null) {
			cannoterasermode.SelectionChanged += (_, _) => {
				if (cannoterasermode.SelectedItem is ComboBoxItem ci) {
					var tag = ci.Tag as string ?? "";
					var mode = string.Equals(tag, "stroke", StringComparison.OrdinalIgnoreCase)
						? PdfAnnotSurface.EraserMode.Stroke
						: PdfAnnotSurface.EraserMode.Point;
					withpdf(p => p.AnnotSetEraserMode(mode));
				}
			};
		}
		if (bannottext != null) bannottext.Click += (_, _) => settool(PdfAnnotSurface.Tool.Text);
		if (bannotnote != null) bannotnote.Click += (_, _) => settool(PdfAnnotSurface.Tool.Note);
		if (bannotrect != null) bannotrect.Click += (_, _) => settool(PdfAnnotSurface.Tool.Rect);
		if (bannotell != null) bannotell.Click += (_, _) => settool(PdfAnnotSurface.Tool.Ellipse);
		if (bannotline != null) bannotline.Click += (_, _) => settool(PdfAnnotSurface.Tool.Line);
		if (bannotarrow != null) bannotarrow.Click += (_, _) => settool(PdfAnnotSurface.Tool.Arrow);
		if (bannotdel != null) bannotdel.Click += (_, _) => withpdf(p => p.AnnotDeleteSelected());
		if (bannotcopy != null) bannotcopy.Click += (_, _) => withpdf(p => p.AnnotDuplicate());
		if (bannotgroup != null) bannotgroup.Click += (_, _) => withpdf(p => p.AnnotGroupSelected());
		if (bannotungroup != null) bannotungroup.Click += (_, _) => withpdf(p => p.AnnotUngroupSelected());
		if (bannotsave != null) bannotsave.Click += (_, _) => {
			withpdf(p => {
				if (p.SaveAnnots())
					lbstatus.Text = "标注已保存: " + (p.AnnotFilePath ?? "");
				else
					lbstatus.Text = "标注保存失败";
			});
		};
		if (bannotsavepdf != null) bannotsavepdf.Click += (_, _) => saveannotsaspdf();
		if (bannotcolor != null) bannotcolor.Click += (_, _) => pickannotcolor();
		if (cannotfont != null) {
			foreach (var f in new[] {
				"Microsoft YaHei", "宋体", "黑体", "楷体", "Arial", "Times New Roman", "Calibri", "Consolas",
			})
				cannotfont.Items.Add(f);
			cannotfont.Text = "Microsoft YaHei";
			cannotfont.SelectionChanged += (_, _) => {
				if (annotStyleSilent) return;
				if (cannotfont.SelectedItem is string name)
					withpdf(p => p.AnnotSetFont(name));
			};
			cannotfont.LostKeyboardFocus += (_, _) => {
				if (annotStyleSilent) return;
				var name = cannotfont.Text?.Trim();
				if (!string.IsNullOrEmpty(name))
					withpdf(p => p.AnnotSetFont(name));
			};
		}
		if (cannotfontsize != null) {
			foreach (var s in new[] { "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36" })
				cannotfontsize.Items.Add(s);
			cannotfontsize.Text = "12";
			cannotfontsize.SelectionChanged += (_, _) => {
				if (annotStyleSilent) return;
				if (cannotfontsize.SelectedItem is string s && double.TryParse(s, out var pt))
					withpdf(p => p.AnnotSetFontSize(pt));
			};
			cannotfontsize.LostKeyboardFocus += (_, _) => {
				if (annotStyleSilent) return;
				if (double.TryParse(cannotfontsize.Text?.Trim(), out var pt))
					withpdf(p => p.AnnotSetFontSize(pt));
			};
		}
	}

	bool annotStyleSilent;

	/// <summary>将标注烧入页面后另存为 PDF（栅格化）。</summary>
	void saveannotsaspdf() {
		var p = currentviewer() as PdfViewer;
		if (p == null) return;
		try {
			// 无标注时提示
			// SaveAnnotsAsPdf 会再检查
			var src = p.FilePath;
			var dir = string.IsNullOrEmpty(src) ? "" : Path.GetDirectoryName(src);
			var baseName = string.IsNullOrEmpty(src)
				? "annotated"
				: Path.GetFileNameWithoutExtension(src);
			var dlg = new SaveFileDialog {
				Title = Loc.T("tip_annot_save_pdf"),
				Filter = "PDF|*.pdf|所有文件|*.*",
				FileName = baseName + "-annotated.pdf",
				InitialDirectory = string.IsNullOrEmpty(dir) || !Directory.Exists(dir)
					? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
					: dir,
				AddExtension = true,
				DefaultExt = ".pdf",
				OverwritePrompt = true,
			};
			if (dlg.ShowDialog(this) != true) return;
			var outPath = dlg.FileName;
			// 避免误覆盖正在打开的源文件（除非用户明确选了同源路径）
			if (!string.IsNullOrEmpty(src)
				&& string.Equals(Path.GetFullPath(src), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase)) {
				var r = MessageBox.Show(this,
					"将覆盖当前打开的 PDF，页面会栅格化且原可选文字会丢失。\n是否继续？",
					"另存为 PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
				if (r != MessageBoxResult.Yes) return;
			}
			lbstatus.Text = "正在导出带标注的 PDF…";
			// 若覆盖当前打开文件，抑制外部变更误触发
			if (!string.IsNullOrEmpty(src)
				&& string.Equals(Path.GetFullPath(src), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
				markselfwrite(current());
			var saved = p.SaveAnnotsAsPdf(outPath);
			if (!string.IsNullOrEmpty(src)
				&& string.Equals(Path.GetFullPath(src), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
				markselfwrite(current());
			lbstatus.Text = "已另存: " + saved;
			MessageBox.Show(this,
				"已将标注烧入页面并保存为：\n" + saved +
				"\n\n说明：导出为图像 PDF，原矢量文字不可再选；旁路 .annot.json 仍保留。",
				"另存为 PDF", MessageBoxButton.OK, MessageBoxImage.Information);
		} catch (Exception ex) {
			DocLog.Error("saveannotsaspdf", ex);
			MessageBox.Show(this, "另存失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
			lbstatus.Text = "另存失败";
		}
	}

	void pickannotcolor() {
		var p = currentviewer() as PdfViewer;
		if (p == null || !p.AnnotMode) return;
		var initial = System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35);
		try {
			var st = p.SelectedAnnot;
			if (st != null) initial = st.Color;
		} catch { /* ignore */ }
		var picked = AnnotColorDialog.Pick(this, initial);
		if (picked == null) return;
		var c = picked.Value;
		p.AnnotSetColor(c);
		if (bannotcolor != null && bannotcolor.Content is Grid g && g.Children.Count > 0
			&& g.Children[0] is System.Windows.Shapes.Ellipse el)
			el.Fill = new SolidColorBrush(c);
	}

	void syncannottools(PdfAnnotSurface.Tool t) {
		void set(ToggleButton b, bool on) {
			if (b != null) b.IsChecked = on;
		}
		set(bannothand, t == PdfAnnotSurface.Tool.Hand);
		set(bannotsel, t == PdfAnnotSurface.Tool.Select);
		set(bannotpen, t == PdfAnnotSurface.Tool.Pen);
		set(bannothl, t == PdfAnnotSurface.Tool.Highlighter);
		set(bannoteraser, t == PdfAnnotSurface.Tool.Eraser);
		set(bannottext, t == PdfAnnotSurface.Tool.Text);
		set(bannotnote, t == PdfAnnotSurface.Tool.Note);
		set(bannotrect, t == PdfAnnotSurface.Tool.Rect);
		set(bannotell, t == PdfAnnotSurface.Tool.Ellipse);
		set(bannotline, t == PdfAnnotSurface.Tool.Line);
		set(bannotarrow, t == PdfAnnotSurface.Tool.Arrow);
	}

	void syncannotstyleui(PdfViewer p) {
		if (p == null || annotStyleSilent) return;
		annotStyleSilent = true;
		try {
			var st = p.SelectedAnnot;
			if (st != null && st.Kind is PdfAnnotKind.Text or PdfAnnotKind.Note) {
				if (cannotfont != null)
					cannotfont.Text = string.IsNullOrWhiteSpace(st.FontName) ? "Microsoft YaHei" : st.FontName;
				if (cannotfontsize != null)
					cannotfontsize.Text = (st.FontSize > 1 ? st.FontSize : 12).ToString("0.##");
			}
		} catch { /* ignore */ }
		finally { annotStyleSilent = false; }
	}

	void syncpdfannotui() {
		var p = currentviewer() as PdfViewer;
		hookpdfevents(p);
		var isPdf = p != null;
		var annot = isPdf && p.AnnotMode;
		if (bpannot != null) {
			bpannot.Visibility = isPdf ? Visibility.Visible : Visibility.Collapsed;
			bpannot.IsChecked = annot;
		}
		if (ppdfannot != null)
			ppdfannot.Visibility = annot ? Visibility.Visible : Visibility.Collapsed;
		if (annot) {
			syncannottools(PdfAnnotSurface.Tool.Hand);
			syncannotstyleui(p);
		}
	}

	/// <summary>PDF 编辑模式工具栏。</summary>
	void initpdfedit() {
		if (bpfdedit != null) bpfdedit.Click += (_, _) => togglepdfedit();
		if (bpdfsave != null) bpdfsave.Click += (_, _) => savecurrentpdf();
		if (bpdfsel != null) bpdfsel.Click += (_, _) => withpdf(p => p.EditSetToolSelect());
		if (bpdftext != null) bpdftext.Click += (_, _) => withpdf(p => p.EditSetToolAddText());
		if (bpdfimg != null) bpdfimg.Click += (_, _) => withpdf(p => p.EditSetToolAddImage());
		if (bpdfedittext != null) bpdfedittext.Click += (_, _) => {
			withpdf(p => {
				if (!p.EditFromTextSelection())
					MessageBox.Show(this, "请先在阅读模式下拖选要修改的文字，再进入编辑并点此按钮。\n也可直接用「添加文字」在页面上点选位置。",
						"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			});
		};
		if (bpdfdel != null) bpdfdel.Click += (_, _) => withpdf(p => p.EditDeleteSelected());
		if (bpdfbold != null) bpdfbold.Click += (_, _) => withpdf(p => {
			p.EditToggleBold();
			syncpdfstyleui(p);
		});
		if (bpdfitalic != null) bpdfitalic.Click += (_, _) => withpdf(p => {
			p.EditToggleItalic();
			syncpdfstyleui(p);
		});
		if (bpdffore != null) bpdffore.Click += (_, _) => pickpdfcolor(fore: true);
		if (bpdfback != null) bpdfback.Click += (_, _) => pickpdfcolor(fore: false);
		if (cpdffont != null) {
			foreach (var f in new[] {
				"Microsoft YaHei", "宋体", "黑体", "楷体", "Arial", "Times New Roman", "Calibri", "Consolas",
			})
				cpdffont.Items.Add(f);
			cpdffont.SelectionChanged += (_, _) => {
				if (cpdffont.SelectedItem is string name)
					withpdf(p => p.EditSetFont(name));
			};
			cpdffont.LostKeyboardFocus += (_, _) => {
				var name = cpdffont.Text?.Trim();
				if (!string.IsNullOrEmpty(name))
					withpdf(p => p.EditSetFont(name));
			};
		}
		if (cpdffontsize != null) {
			foreach (var s in new[] { "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48" })
				cpdffontsize.Items.Add(s);
			cpdffontsize.SelectionChanged += (_, _) => {
				if (cpdffontsize.SelectedItem is string s && double.TryParse(s, out var pt))
					withpdf(p => p.EditSetFontSize(pt));
			};
			cpdffontsize.LostKeyboardFocus += (_, _) => {
				if (double.TryParse(cpdffontsize.Text?.Trim(), out var pt))
					withpdf(p => p.EditSetFontSize(pt));
			};
		}
	}

	void withpdf(Action<PdfViewer> action) {
		var p = currentviewer() as PdfViewer;
		if (p == null) return;
		try {
			action(p);
			updatestatus();
			syncpdfstyleui(p);
		} catch (Exception ex) {
			DocLog.Error("pdf edit", ex);
			MessageBox.Show(this, ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void togglepdfedit() {
		// 工具栏「编辑 PDF」打开专业编辑窗口（更完整）
		openpdfeditor();
	}

	void openpdfeditor() {
		var path = currentfilepath();
		var v = currentviewer() as PdfViewer;
		if (v != null && !string.IsNullOrWhiteSpace(v.FilePath))
			path = v.FilePath;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
			// 无当前 PDF：选文件
			var dlg = new OpenFileDialog {
				Filter = "PDF|*.pdf|所有文件|*.*",
				Title = "打开 PDF 进行专业编辑",
			};
			if (dlg.ShowDialog(this) != true) return;
			path = dlg.FileName;
		} else if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) {
			MessageBox.Show(this, "请先打开一个 PDF 文件，或从菜单选择 PDF。", "PDF 专业编辑",
				MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			PdfEditorWindow.Open(this, path);
		} catch (Exception ex) {
			DocLog.Error("openpdfeditor", ex);
			MessageBox.Show(this, "无法打开专业编辑: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void savecurrentpdf() {
		var p = currentviewer() as PdfViewer;
		if (p == null) return;
		try {
			if (!p.IsDirty && !p.EditMode) {
				lbstatus.Text = "无修改需要保存";
				return;
			}
			var r = MessageBox.Show(this,
				"保存将把当前页与编辑内容合成为图像 PDF（矢量文字不可再选）。\n是否继续？",
				"保存 PDF", MessageBoxButton.YesNo, MessageBoxImage.Question);
			if (r != MessageBoxResult.Yes) return;
			markselfwrite(current());
			p.SaveEdits();
			markselfwrite(current());
			lbstatus.Text = "已保存: " + p.FilePath;
			syncpdfeditui();
			refreshtabtitle(current());
			updatestatus();
		} catch (Exception ex) {
			DocLog.Error("savecurrentpdf", ex);
			MessageBox.Show(this, "保存失败: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void pickpdfcolor(bool fore) {
		var p = currentviewer() as PdfViewer;
		if (p == null || !p.EditMode) return;
		var colors = new[] {
			System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00),
			System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00),
			System.Windows.Media.Color.FromRgb(0x00, 0x80, 0x00),
			System.Windows.Media.Color.FromRgb(0x00, 0x00, 0xFF),
			System.Windows.Media.Color.FromRgb(0xFF, 0xC0, 0x00),
			System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27),
			System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
			System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0x9C),
			System.Windows.Media.Color.FromRgb(0xC6, 0xEF, 0xCE),
			System.Windows.Media.Color.FromRgb(0xBD, 0xD7, 0xEE),
		};
		var dlg = new Window {
			Title = fore ? "文字颜色" : "背景颜色",
			Width = 280, Height = 160,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this, ResizeMode = ResizeMode.NoResize,
			Background = System.Windows.Media.Brushes.White,
		};
		var panel = new WrapPanel { Margin = new Thickness(10) };
		foreach (var c in colors) {
			var b = new Button {
				Width = 28, Height = 28, Margin = new Thickness(3),
				Background = new SolidColorBrush(c),
				BorderBrush = System.Windows.Media.Brushes.Gray,
				BorderThickness = new Thickness(1), Tag = c,
			};
			b.Click += (_, _) => {
				var col = (System.Windows.Media.Color)b.Tag;
				if (fore) p.EditSetForeColor(col);
				else p.EditSetBackColor(col);
				dlg.DialogResult = true;
				dlg.Close();
				updatestatus();
			};
			panel.Children.Add(b);
		}
		if (!fore) {
			var clear = new Button { Content = "清除背景", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
			clear.Click += (_, _) => {
				p.EditSetBackColor(null);
				dlg.DialogResult = true;
				dlg.Close();
			};
			var root = new DockPanel();
			DockPanel.SetDock(clear, Dock.Bottom);
			root.Children.Add(clear);
			root.Children.Add(panel);
			dlg.Content = root;
		} else {
			dlg.Content = panel;
		}
		dlg.ShowDialog();
	}

	PdfViewer hookedPdf;

	void hookpdfevents(PdfViewer p) {
		if (ReferenceEquals(hookedPdf, p)) return;
		if (hookedPdf != null) {
			try { hookedPdf.EditModeChanged -= onpdfeditmode; } catch { /* ignore */ }
			try { hookedPdf.DirtyChanged -= onpdfdirty; } catch { /* ignore */ }
			try { hookedPdf.EditSelectionChanged -= onpdfsel; } catch { /* ignore */ }
			try { hookedPdf.AnnotModeChanged -= onpdfannotmode; } catch { /* ignore */ }
			try { hookedPdf.AnnotChanged -= onpdfannotchanged; } catch { /* ignore */ }
		}
		hookedPdf = p;
		if (p == null) return;
		p.EditModeChanged += onpdfeditmode;
		p.DirtyChanged += onpdfdirty;
		p.EditSelectionChanged += onpdfsel;
		p.AnnotModeChanged += onpdfannotmode;
		p.AnnotChanged += onpdfannotchanged;
	}

	void onpdfeditmode() => syncpdfeditui();
	void onpdfdirty() {
		syncpdfeditui();
		refreshtabtitle(current());
	}
	void onpdfannotmode() => syncpdfannotui();
	void onpdfannotchanged() {
		updatestatus();
		if (hookedPdf != null) {
			syncannotstyleui(hookedPdf);
			// 双击文本等内部切工具后刷新按钮
			try { syncannottools(hookedPdf.AnnotCurrentTool); } catch { /* ignore */ }
		}
	}
	void onpdfsel() {
		if (hookedPdf != null) syncpdfstyleui(hookedPdf);
	}

	void syncpdfeditui() {
		var p = currentviewer() as PdfViewer;
		hookpdfevents(p);
		var isPdf = p != null;
		// 主工具栏：始终提供「专业编辑」入口；内嵌编辑条保留作轻量编辑
		if (bpfdedit != null) {
			bpfdedit.Visibility = isPdf ? Visibility.Visible : Visibility.Collapsed;
			bpfdedit.ToolTip = "打开 PDF 专业编辑窗口";
		}
		var editing = isPdf && p.EditMode;
		if (bpdfsave != null) {
			// 专业窗独立保存；主窗仅在内嵌编辑脏时显示
			bpdfsave.Visibility = isPdf && p.IsDirty ? Visibility.Visible : Visibility.Collapsed;
			bpdfsave.IsEnabled = isPdf && p.IsDirty;
		}
		if (ppdfedit != null)
			ppdfedit.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
		if (editing) syncpdfstyleui(p);
		syncpdfannotui();
	}

	bool pdfStyleSilent;

	void syncpdfstyleui(PdfViewer p) {
		if (p == null || pdfStyleSilent) return;
		pdfStyleSilent = true;
		try {
			var st = p.SelectedEdit;
			if (bpdfbold != null) bpdfbold.IsChecked = st?.Bold == true;
			if (bpdfitalic != null) bpdfitalic.IsChecked = st?.Italic == true;
			if (cpdffont != null && st != null && st.Kind == PdfEditKind.Text)
				cpdffont.Text = string.IsNullOrWhiteSpace(st.FontName) ? "Microsoft YaHei" : st.FontName;
			if (cpdffontsize != null && st != null && st.Kind == PdfEditKind.Text)
				cpdffontsize.Text = (st.FontSizePt > 1 ? st.FontSizePt : 12).ToString("0.##");
		} catch { /* ignore */ }
		finally { pdfStyleSilent = false; }
	}

	void withxlsx(Action<XlsxViewer> action) {
		var x = currentviewer() as XlsxViewer;
		if (x == null || !x.EditMode) return;
		try {
			action(x);
			updatestatus();
			syncxlsxstyleui(x);
		} catch (Exception ex) {
			DocLog.Error("xlsx edit", ex);
			MessageBox.Show(this, ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	XlsxViewer hookedXlsx;

	void hookxlsxevents(XlsxViewer x) {
		if (ReferenceEquals(hookedXlsx, x)) return;
		if (hookedXlsx != null) {
			try { hookedXlsx.SelectionChanged -= onxlsxselection; } catch { /* ignore */ }
			try { hookedXlsx.EditModeChanged -= onxlsxeditmode; } catch { /* ignore */ }
			try { hookedXlsx.DirtyChanged -= onxlsxdirty; } catch { /* ignore */ }
		}
		hookedXlsx = x;
		if (x == null) return;
		x.SelectionChanged += onxlsxselection;
		x.EditModeChanged += onxlsxeditmode;
		x.DirtyChanged += onxlsxdirty;
	}

	void onxlsxselection() {
		if (hookedXlsx != null) syncxlsxstyleui(hookedXlsx);
	}
	void onxlsxeditmode() => syncxlsxeditui();
	void onxlsxdirty() {
		syncxlsxeditui();
		refreshtabtitle(current());
	}

	void togglexlsxedit() {
		var x = currentviewer() as XlsxViewer;
		if (x == null) return;
		x.EditMode = !x.EditMode;
		syncxlsxeditui();
		updatestatus();
	}

	// ---------- TXT / MD 编辑 ----------
	TextViewer hookedTxt;
	MdViewer hookedMd;

	void hooktxtmdevents() {
		var t = currentviewer() as TextViewer;
		var m = currentviewer() as MdViewer;
		if (!ReferenceEquals(hookedTxt, t)) {
			if (hookedTxt != null) {
				try { hookedTxt.EditModeChanged -= ontxtmdeditmode; } catch { /* ignore */ }
				try { hookedTxt.DirtyChanged -= ontxtmddirty; } catch { /* ignore */ }
			}
			hookedTxt = t;
			if (t != null) {
				t.EditModeChanged += ontxtmdeditmode;
				t.DirtyChanged += ontxtmddirty;
			}
		}
		if (!ReferenceEquals(hookedMd, m)) {
			if (hookedMd != null) {
				try { hookedMd.EditModeChanged -= ontxtmdeditmode; } catch { /* ignore */ }
				try { hookedMd.DirtyChanged -= ontxtmddirty; } catch { /* ignore */ }
				try { hookedMd.LayoutChanged -= ontxtmdlayout; } catch { /* ignore */ }
				try { hookedMd.PreviewEngineChanged -= ontxtmdlayout; } catch { /* ignore */ }
			}
			hookedMd = m;
			if (m != null) {
				m.EditModeChanged += ontxtmdeditmode;
				m.DirtyChanged += ontxtmddirty;
				m.LayoutChanged += ontxtmdlayout;
				m.PreviewEngineChanged += ontxtmdlayout;
			}
		}
	}

	void ontxtmdeditmode() => synctxtmdui();
	void ontxtmddirty() {
		synctxtmdui();
		refreshtabtitle(current());
	}
	void ontxtmdlayout() => synctxtmdui();

	void toggletxtmdedit() {
		if (currentviewer() is TextViewer t) {
			t.EditMode = !t.EditMode;
			synctxtmdui();
			updatestatus();
			return;
		}
		if (currentviewer() is MdViewer m) {
			m.EditMode = !m.EditMode;
			// 进入编辑：沿用上次布局（Typora/侧预/纯代码），不强制改
			synctxtmdui();
			updatestatus();
			saveprogress(m);
		}
	}

	void savecurrenttxtmd() {
		try {
			if (currentviewer() is TextViewer t) {
				markselfwrite(current());
				t.Save();
				markselfwrite(current());
				lbstatus.Text = "已保存: " + t.FilePath;
				synctxtmdui();
				refreshtabtitle(current());
				updatestatus();
				return;
			}
			if (currentviewer() is MdViewer m) {
				markselfwrite(current());
				m.Save();
				markselfwrite(current());
				lbstatus.Text = "已保存: " + m.FilePath;
				synctxtmdui();
				refreshtabtitle(current());
				updatestatus();
			}
		} catch (Exception ex) {
			DocLog.Error("savecurrenttxtmd", ex);
			MessageBox.Show(this, "保存失败: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void setmdlayout(MdEditLayout layout) {
		var m = currentviewer() as MdViewer;
		if (m == null) return;
		if (!m.EditMode) m.EditMode = true;
		m.EditLayout = layout;
		synctxtmdui();
		updatestatus();
		saveprogress(m);
	}

	/// <summary>切换当前 MD（及设置）预览引擎：WebView2 / 纯 WPF。</summary>
	void setmdpreviewengine(MdPreviewEngine eng) {
		var m = currentviewer() as MdViewer;
		if (m == null) return;
		m.PreviewEngine = eng;
		// 同步其它已开 MD 标签
		try {
			foreach (var d in opentabs) {
				if (d?.Viewer is MdViewer other && !ReferenceEquals(other, m)) {
					try { other.PreviewEngine = eng; } catch { /* ignore */ }
				}
			}
		} catch { /* ignore */ }
		synctxtmdui();
		updatestatus();
	}

	/// <summary>Toggle 工具图标：选中时用强调色描边，未选中恢复 muted。</summary>
	void settooliconactive(System.Windows.Shapes.Path icon, bool on) {
		if (icon == null) return;
		try {
			var brush = on
				? (TryFindResource("Accent") as Brush)
				: (TryFindResource("TextMuted") as Brush);
			if (brush != null) icon.Stroke = brush;
			icon.StrokeThickness = on ? 1.8 : 1.4;
		} catch { /* ignore */ }
	}

	void synctxtmdui() {
		hooktxtmdevents();
		var t = currentviewer() as TextViewer;
		var m = currentviewer() as MdViewer;
		var isText = t != null || m != null;
		var editing = (t != null && t.EditMode) || (m != null && m.EditMode);
		var dirty = (t != null && t.IsDirty) || (m != null && m.IsDirty);

		if (btxtedit != null) {
			btxtedit.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
			btxtedit.IsChecked = editing;
			settooliconactive(icontxtedit, editing);
			if (t != null)
				btxtedit.ToolTip = editing ? "退出编辑（回到预览）" : "编辑文本";
			else if (m != null)
				btxtedit.ToolTip = editing ? "退出编辑（回到预览）" : "编辑 Markdown";
		}
		if (btxtsave != null) {
			btxtsave.Visibility = isText && (editing || dirty) ? Visibility.Visible : Visibility.Collapsed;
			btxtsave.IsEnabled = isText && dirty;
		}
		if (pmdedit != null)
			pmdedit.Visibility = m != null && m.EditMode ? Visibility.Visible : Visibility.Collapsed;

		// MD 预览引擎切换（预览/编辑都可用）
		if (pmdengine != null)
			pmdengine.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
		if (m != null) {
			var eng = m.PreviewEngine;
			if (bmdweb != null) bmdweb.IsChecked = eng == MdPreviewEngine.WebView;
			if (bmdwpf != null) bmdwpf.IsChecked = eng == MdPreviewEngine.Wpf;
		}

		if (m != null && m.EditMode) {
			var lay = m.EditLayout;
			if (bmdsource != null) bmdsource.IsChecked = lay == MdEditLayout.Code;
			if (bmdlive != null) bmdlive.IsChecked = lay == MdEditLayout.Typora;
			if (bmdside != null) bmdside.IsChecked = lay == MdEditLayout.Side;
			if (lbmdtip != null) {
				var engTip = m.PreviewEngine == MdPreviewEngine.Wpf ? "WPF" : "Web";
				lbmdtip.Text = lay switch {
					MdEditLayout.Code => "纯代码 · 颜色/粗斜体/链接 · 无预览",
					MdEditLayout.Typora => "Typora · 单栏 · conceal · 无侧预",
					MdEditLayout.Side => $"侧预 · 右侧同步预览（{engTip}）",
					_ => "",
				};
			}
		}
	}

	void savecurrentxlsx() {
		var x = currentviewer() as XlsxViewer;
		if (x == null) return;
		try {
			markselfwrite(current());
			x.Save();
			markselfwrite(current());
			lbstatus.Text = "已保存: " + x.FilePath;
			syncxlsxeditui();
			refreshtabtitle(current());
			updatestatus();
		} catch (Exception ex) {
			DocLog.Error("savecurrentxlsx", ex);
			MessageBox.Show(this, "保存失败: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void pickxlsxcolor(bool fore) {
		var x = currentviewer() as XlsxViewer;
		if (x == null || !x.EditMode) return;
		try {
			// 简易色板：常见色
			var colors = new[] {
				System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00),
				System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00),
				System.Windows.Media.Color.FromRgb(0x00, 0x80, 0x00),
				System.Windows.Media.Color.FromRgb(0x00, 0x00, 0xFF),
				System.Windows.Media.Color.FromRgb(0xFF, 0xC0, 0x00),
				System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x00),
				System.Windows.Media.Color.FromRgb(0x00, 0xB0, 0xF0),
				System.Windows.Media.Color.FromRgb(0x70, 0x30, 0xA0),
				System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
				System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2),
				System.Windows.Media.Color.FromRgb(0xFF, 0xC7, 0xCE),
				System.Windows.Media.Color.FromRgb(0xC6, 0xEF, 0xCE),
				System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0x9C),
				System.Windows.Media.Color.FromRgb(0xBD, 0xD7, 0xEE),
			};
			var dlg = new Window {
				Title = fore ? "文字颜色" : "背景颜色",
				Width = 280,
				Height = 180,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Owner = this,
				ResizeMode = ResizeMode.NoResize,
				Background = System.Windows.Media.Brushes.White,
			};
			var panel = new WrapPanel { Margin = new Thickness(10) };
			foreach (var c in colors) {
				var b = new Button {
					Width = 28,
					Height = 28,
					Margin = new Thickness(3),
					Background = new SolidColorBrush(c),
					BorderBrush = System.Windows.Media.Brushes.Gray,
					BorderThickness = new Thickness(1),
					Tag = c,
				};
				b.Click += (_, _) => {
					var col = (System.Windows.Media.Color)b.Tag;
					if (fore) x.SetForeColor(col);
					else x.SetBackColor(col);
					dlg.DialogResult = true;
					dlg.Close();
					updatestatus();
				};
				panel.Children.Add(b);
			}
			var clear = new Button { Content = "清除", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
			clear.Click += (_, _) => {
				if (fore) x.SetForeColor(null);
				else x.SetBackColor(null);
				dlg.DialogResult = true;
				dlg.Close();
				updatestatus();
			};
			var root = new DockPanel();
			DockPanel.SetDock(clear, Dock.Bottom);
			root.Children.Add(clear);
			root.Children.Add(panel);
			dlg.Content = root;
			dlg.ShowDialog();
		} catch (Exception ex) {
			DocLog.Error("pickxlsxcolor", ex);
		}
	}

	void syncxlsxeditui() {
		var x = currentviewer() as XlsxViewer;
		hookxlsxevents(x);
		var isXlsx = x != null;
		var canEdit = x != null && x.CanEdit;
		var editing = isXlsx && canEdit && x.EditMode;
		if (bxlsxedit != null) {
			bxlsxedit.Visibility = isXlsx && canEdit ? Visibility.Visible : Visibility.Collapsed;
			bxlsxedit.IsChecked = editing;
			settooliconactive(iconxlsxedit, editing);
			bxlsxedit.ToolTip = editing ? "退出编辑模式" : "编辑表格";
		}
		if (bxlsxsave != null) {
			bxlsxsave.Visibility = isXlsx && (editing || x.IsDirty) ? Visibility.Visible : Visibility.Collapsed;
			bxlsxsave.IsEnabled = isXlsx && x.IsDirty;
		}
		if (pxlsxedit != null)
			pxlsxedit.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
		if (editing)
			syncxlsxstyleui(x);
	}

	bool xlsxStyleSilent;

	void syncxlsxstyleui(XlsxViewer x) {
		if (x == null || xlsxStyleSilent) return;
		xlsxStyleSilent = true;
		try {
			var st = x.PeekSelectionStyle();
			if (bxbold != null) bxbold.IsChecked = st?.Bold == true;
			if (bxitalic != null) bxitalic.IsChecked = st?.Italic == true;
			if (bxwrap != null) bxwrap.IsChecked = st?.WrapText == true;
			if (cxfont != null && st != null) {
				var fn = string.IsNullOrWhiteSpace(st.FontName) ? "Calibri" : st.FontName;
				cxfont.Text = fn;
			}
			if (cxfontsize != null && st != null) {
				var pt = st.FontSizePt > 1 ? st.FontSizePt : 11;
				cxfontsize.Text = pt.ToString("0.##");
			}
		} catch { /* ignore */ }
		finally { xlsxStyleSilent = false; }
	}

	/// <summary>打印当前文档视图（WPF PrintVisual；尽量适应纸张）。</summary>
	void printcurrent() {
		var viewer = currentviewer();
		var v = viewer?.View;
		if (v == null) {
			MessageBox.Show(this, Loc.T("no_print"), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			var dlg = new PrintDialog();
			if (dlg.ShowDialog() != true) return;
			var cur = current();
			var title = cur?.Viewer?.Title
				?? (cur?.Path != null ? Path.GetFileName(cur.Path) : null)
				?? "DocviewWPF";
			// 按可打印区域缩放 Visual，避免裁切
			v.Measure(new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
			v.Arrange(new Rect(0, 0, v.DesiredSize.Width, v.DesiredSize.Height));
			var scale = Math.Min(
				dlg.PrintableAreaWidth / Math.Max(1, v.ActualWidth),
				dlg.PrintableAreaHeight / Math.Max(1, v.ActualHeight));
			if (scale > 1) scale = 1;
			if (scale < 0.05) scale = 0.05;
			var tg = new System.Windows.Media.TransformGroup();
			tg.Children.Add(new System.Windows.Media.ScaleTransform(scale, scale));
			var old = v.LayoutTransform;
			try {
				v.LayoutTransform = tg;
				v.UpdateLayout();
				dlg.PrintVisual(v, title);
			} finally {
				v.LayoutTransform = old;
				v.UpdateLayout();
			}
			if (lbstatus != null) lbstatus.Text = "已发送打印: " + title;
		} catch (Exception ex) {
			DocLog.Error("printcurrent", ex);
			MessageBox.Show(this, Loc.Tf("print_failed", ex.Message), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void reopenclosedtab() {
		var path = ClosedTabsStore.Pop();
		// 浏览器历史 URL
		if (!string.IsNullOrEmpty(path)
			&& (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) {
			try {
				browserSeq++;
				var doc = addtabshell(path, DocKind.Browser, isPreview: false);
				activatetab(doc, loadNow: true);
			} catch (Exception ex) {
				DocLog.Warn($"reopen browser: {ex.Message}");
			}
			return;
		}
		if (string.IsNullOrEmpty(path)) {
			if (lbstatus != null) lbstatus.Text = "没有最近关闭的标签";
			return;
		}
		if (!File.Exists(path)) {
			MessageBox.Show(this, Loc.Tf("file_missing", path), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
			// 继续弹下一个
			if (ClosedTabsStore.Count > 0) reopenclosedtab();
			return;
		}
		openpath(path, loadNow: true);
		if (lbstatus != null) lbstatus.Text = "已重新打开: " + Path.GetFileName(path);
	}

	void exportmdhtml() {
		var m = currentviewer() as MdViewer;
		if (m == null) {
			MessageBox.Show(this, "请先打开 Markdown 文件。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		var dlg = new SaveFileDialog {
			Filter = "HTML|*.html;*.htm|所有文件|*.*",
			FileName = Path.GetFileNameWithoutExtension(m.FilePath ?? "export") + ".html",
			InitialDirectory = Path.GetDirectoryName(m.FilePath ?? "") ?? "",
		};
		if (dlg.ShowDialog(this) != true) return;
		if (m.ExportHtml(dlg.FileName))
			lbstatus.Text = "已导出 HTML: " + dlg.FileName;
		else
			MessageBox.Show(this, "导出 HTML 失败。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
	}

	async void exportmdpdf() {
		var m = currentviewer() as MdViewer;
		if (m == null) {
			MessageBox.Show(this, "请先打开 Markdown 文件。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		var dlg = new SaveFileDialog {
			Filter = "PDF|*.pdf|所有文件|*.*",
			FileName = Path.GetFileNameWithoutExtension(m.FilePath ?? "export") + ".pdf",
			InitialDirectory = Path.GetDirectoryName(m.FilePath ?? "") ?? "",
		};
		if (dlg.ShowDialog(this) != true) return;
		try {
			lbstatus.Text = "正在导出 PDF…";
			var ok = await m.ExportPdfAsync(dlg.FileName);
			if (ok) lbstatus.Text = "已导出 PDF: " + dlg.FileName;
			else MessageBox.Show(this, "导出 PDF 失败（请确认 WebView2 可用）。", "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		} catch (Exception ex) {
			DocLog.Error("exportmdpdf", ex);
			MessageBox.Show(this, "导出 PDF 失败: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void saveimageas() {
		var img = currentviewer() as ImageViewer;
		if (img == null) {
			MessageBox.Show(this, "请先打开图片文件。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		var dlg = new SaveFileDialog {
			Filter = "PNG|*.png|JPEG|*.jpg;*.jpeg|BMP|*.bmp|所有文件|*.*",
			FileName = Path.GetFileNameWithoutExtension(img.FilePath ?? "image") + ".png",
			InitialDirectory = Path.GetDirectoryName(img.FilePath ?? "") ?? "",
		};
		if (dlg.ShowDialog(this) != true) return;
		if (img.SaveAs(dlg.FileName))
			lbstatus.Text = "图片已另存: " + dlg.FileName;
		else
			MessageBox.Show(this, "另存失败。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
	}

	void pickencoding() {
		Encoding curEnc = null;
		var t = currentviewer() as TextViewer;
		var m = currentviewer() as MdViewer;
		if (t != null) curEnc = t.FileEncoding;
		else if (m != null) curEnc = m.FileEncoding;
		else return;

		var menu = new ContextMenu();
		foreach (var enc in TextFileIo.CommonEncodings()) {
			var e = enc;
			var name = TextFileIo.DisplayName(e);
			var mi = new MenuItem {
				Header = name,
				IsChecked = curEnc != null && curEnc.CodePage == e.CodePage
					&& (curEnc.GetPreamble().Length == e.GetPreamble().Length
						|| !(e is UTF8Encoding)),
			};
			// UTF-8 BOM 精确匹配
			if (e is UTF8Encoding u8a && curEnc is UTF8Encoding u8b)
				mi.IsChecked = u8a.GetPreamble().Length == u8b.GetPreamble().Length;
			mi.Click += (_, _) => applyencoding(e);
			menu.Items.Add(mi);
		}
		menu.PlacementTarget = lbenc;
		menu.IsOpen = true;
	}

	void applyencoding(Encoding enc) {
		if (enc == null) return;
		var t = currentviewer() as TextViewer;
		var m = currentviewer() as MdViewer;
		var dirty = (t != null && t.IsDirty) || (m != null && m.IsDirty);
		if (dirty) {
			var r = MessageBox.Show(this,
				"切换编码将从磁盘重新加载并丢弃未保存修改，是否继续？",
				"切换编码", MessageBoxButton.YesNo, MessageBoxImage.Warning);
			if (r != MessageBoxResult.Yes) return;
		}
		try {
			if (t != null) t.ReloadWithEncoding(enc);
			else if (m != null) m.ReloadWithEncoding(enc);
			updatestatus();
			if (lbstatus != null)
				lbstatus.Text = "已切换编码: " + TextFileIo.DisplayName(enc);
		} catch (Exception ex) {
			MessageBox.Show(this, "切换编码失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	// ---------- 分屏 ----------
	bool splitOn;
	IDocViewer splitViewer;
	string splitPath;

	void togglesplit() {
		if (splitOn) closesplit();
		else opensplit();
	}

	void opensplit() {
		// 需要至少两个已开文件才有意义；仅一个时仍打开空分屏供选择
		splitOn = true;
		if (colsplit != null) colsplit.Width = new GridLength(4);
		if (colsidepane != null) colsidepane.Width = new GridLength(1, GridUnitType.Star);
		if (spsplit != null) spsplit.Visibility = Visibility.Visible;
		if (psplit != null) psplit.Visibility = Visibility.Visible;
		if (mnsplit != null) mnsplit.IsChecked = true;
		refreshsplitlist();
		// 默认选中「另一个」标签
		var cur = current()?.Path;
		string other = null;
		foreach (var d in opentabs) {
			if (d?.Path == null) continue;
			if (cur != null && string.Equals(d.Path, cur, StringComparison.OrdinalIgnoreCase)) continue;
			other = d.Path;
			break;
		}
		if (other == null && opentabs.Count > 0)
			other = opentabs[0].Path;
		if (other != null)
			loadsplit(other);
	}

	void closesplit() {
		splitOn = false;
		disposesplitside();
		if (colsplit != null) colsplit.Width = new GridLength(0);
		if (colsidepane != null) colsidepane.Width = new GridLength(0);
		if (spsplit != null) spsplit.Visibility = Visibility.Collapsed;
		if (psplit != null) psplit.Visibility = Visibility.Collapsed;
		if (mnsplit != null) mnsplit.IsChecked = false;
		if (csplitfile != null) csplitfile.Items.Clear();
	}

	void refreshsplitlist() {
		if (csplitfile == null) return;
		csplitfile.SelectionChanged -= onsplitfileselected_handler;
		csplitfile.Items.Clear();
		foreach (var d in opentabs) {
			if (d?.Path == null) continue;
			csplitfile.Items.Add(new SplitFileItem(d.Path, tabdisplayname(d.Path, d.Kind)));
		}
		// 恢复选中
		if (splitPath != null) {
			foreach (SplitFileItem it in csplitfile.Items) {
				if (string.Equals(it.Path, splitPath, StringComparison.OrdinalIgnoreCase)) {
					csplitfile.SelectedItem = it;
					break;
				}
			}
		}
		csplitfile.SelectionChanged += onsplitfileselected_handler;
	}

	void onsplitfileselected(object sender, SelectionChangedEventArgs e) => onsplitfileselected_handler(sender, e);
	void onsplitfileselected_handler(object sender, SelectionChangedEventArgs e) {
		if (csplitfile?.SelectedItem is SplitFileItem it)
			loadsplit(it.Path);
	}

	void loadsplit(string path) {
		path = pathnorm(path);
		if (path == null || !File.Exists(path)) return;
		if (string.Equals(splitPath, path, StringComparison.OrdinalIgnoreCase) && splitViewer != null)
			return;
		disposesplitside();
		try {
			var kind = DocKindUtil.FromPath(path);
			if (kind == DocKind.Unknown) return;
			var v = ViewerFactory.Create(kind);
			v.Load(path);
			splitViewer = v;
			splitPath = path;
			if (psplithost != null)
				psplithost.Child = v.View;
			refreshsplitlist();
			DocLog.Info($"split load path={path}");
		} catch (Exception ex) {
			DocLog.Warn($"split load: {ex.Message}");
			disposesplitside();
		}
	}

	void disposesplitside() {
		try {
			if (psplithost != null) psplithost.Child = null;
		} catch { /* ignore */ }
		try { splitViewer?.Dispose(); } catch { /* ignore */ }
		splitViewer = null;
		splitPath = null;
	}

	sealed class SplitFileItem {
		public string Path;
		public string Name;
		public SplitFileItem(string path, string name) { Path = path; Name = name; }
		public override string ToString() => Name ?? Path ?? "";
	}

	/// <summary>复制当前标签对应文件的完整路径到剪贴板。</summary>
	void copyfilepath() {
		var path = currentfilepath();
		if (path == null) {
			MessageBox.Show(this, "当前没有打开的文件。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			Clipboard.SetText(path);
			if (lbstatus != null) lbstatus.Text = "已复制路径: " + path;
		} catch (Exception ex) {
			DocLog.Error("copyfilepath", ex);
			MessageBox.Show(this, "复制失败: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>在资源管理器中选中并显示当前文件。</summary>
	void showinexplorer() {
		var path = currentfilepath();
		if (path == null) {
			MessageBox.Show(this, Loc.T("no_file"), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			if (!File.Exists(path)) {
				// 文件已删：尽量打开所在目录
				var dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
					Process.Start("explorer.exe", dir);
				else
					MessageBox.Show(this, string.Format(Loc.T("file_missing"), path), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			// /select, 后需完整路径；带空格时用引号
			Process.Start("explorer.exe", "/select,\"" + path + "\"");
		} catch (Exception ex) {
			DocLog.Error("showinexplorer", ex);
			MessageBox.Show(this, string.Format(Loc.T("explorer_failed"), ex.Message), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>用系统默认关联应用打开当前文件（PDF→默认阅读器等）。</summary>
	void openwithsystem() {
		var path = currentfilepath();
		if (path == null) {
			MessageBox.Show(this, Loc.T("no_file"), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			if (!File.Exists(path)) {
				MessageBox.Show(this, string.Format(Loc.T("file_missing"), path), "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			Process.Start(new ProcessStartInfo {
				FileName = path,
				UseShellExecute = true,
			});
			if (lbstatus != null)
				lbstatus.Text = Loc.T("open_with_system") + ": " + Path.GetFileName(path);
		} catch (Exception ex) {
			DocLog.Error("openwithsystem", ex);
			MessageBox.Show(this, string.Format(Loc.T("open_with_system_failed"), ex.Message),
				"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>当前标签文件路径；无则 null。</summary>
	string currentfilepath() {
		var cur = current();
		if (cur == null || string.IsNullOrWhiteSpace(cur.Path)) return null;
		return cur.Path;
	}

	void initdrop() {
		// 隧道 Preview*：先于子控件处理，避免内容区 RTB/滚动条吞掉拖放
		PreviewDragEnter += onfiledrag;
		PreviewDragOver += onfiledrag;
		PreviewDrop += onfiledrop;
		// 冒泡兜底（空白标题区等）
		DragEnter += onfiledrag;
		DragOver += onfiledrag;
		Drop += onfiledrop;
		// 内容区显式绑定（空页 / Tab 页）
		if (pcontent != null) {
			pcontent.AllowDrop = true;
			pcontent.PreviewDragOver += onfiledrag;
			pcontent.PreviewDrop += onfiledrop;
		}
		if (pempty != null) {
			pempty.AllowDrop = true;
			pempty.PreviewDragOver += onfiledrag;
			pempty.PreviewDrop += onfiledrop;
		}
		if (tabs != null) {
			tabs.AllowDrop = true;
			tabs.PreviewDragOver += onfiledrag;
			tabs.PreviewDrop += onfiledrop;
		}
		// 左侧文件夹/目录栏：拖入文件夹 → 打开工作区
		wirefolderdroptarget(pleft);
		wirefolderdroptarget(treeFiles);
		wirefolderdroptarget(treeOutline);
		wirefolderdroptarget(sideTabs);
	}

	/// <summary>左侧栏接受文件夹拖入（打开工作区）。</summary>
	void wirefolderdroptarget(UIElement el) {
		if (el == null) return;
		try {
			el.AllowDrop = true;
			el.PreviewDragEnter += onfiledrag;
			el.PreviewDragOver += onfiledrag;
			el.PreviewDrop += onfiledrop;
			el.DragEnter += onfiledrag;
			el.DragOver += onfiledrag;
			el.Drop += onfiledrop;
		} catch { /* ignore */ }
	}

	void onfiledrag(object sender, DragEventArgs e) {
		try {
			// 书签内部拖排序：在书签栏上显示栏内插入线（移出分组时尤其重要）
			if (e.Data != null && e.Data.GetDataPresent(BookmarkDragFormat)) {
				if (isoverbookmarkbar(e)) {
					e.Effects = DragDropEffects.Move;
					e.Handled = true;
					updatebookmarkbarinsertdragui(e);
				} else if (isovergrouppopup(e)) {
					e.Effects = DragDropEffects.Move;
					// 弹层内由 ongrouppopupdragover 画线；清掉栏上指示
					hidebookmarkinsertmark();
				} else {
					hidebookmarkinsertmark();
					hidegrouppopupinsertmark();
					setgroupdrophighlight(null, false);
				}
				return;
			}
			if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) {
				e.Effects = DragDropEffects.None;
				e.Handled = true;
				return;
			}
			// 落在书签栏：交给书签逻辑（高亮提示），勿当打开文件
			if (isoverbookmarkbar(e)) {
				e.Effects = DragDropEffects.Copy;
				e.Handled = true;
				setbookmarkdrophighlight(true);
				return;
			}
			clearbookmarkdrophighlight();
			hidebookmarkinsertmark();
			e.Effects = DragDropEffects.Copy;
			e.Handled = true;
		} catch { /* ignore */ }
	}

	void onfiledrop(object sender, DragEventArgs e) {
		try {
			// 书签内部拖放
			if (e.Data != null && e.Data.GetDataPresent(BookmarkDragFormat)) {
				if (isoverbookmarkbar(e)) {
					onbookmarkbardrop(pbookmarks, e);
					e.Handled = true;
				}
				return;
			}
			if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
			var files = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (files == null || files.Length == 0) return;
			// 书签栏优先：添加书签，而不是打开文件
			if (isoverbookmarkbar(e)) {
				e.Handled = true;
				e.Effects = DragDropEffects.Copy;
				addfilesasbookmarks(files);
				return;
			}
			clearbookmarkdrophighlight();
			hidebookmarkinsertmark();
			e.Handled = true;
			e.Effects = DragDropEffects.Copy;
			opendroppedpaths(files, preferFolderWorkspace: isleftdroptarget(sender));
			bringtofront();
		} catch (Exception ex) {
			DocLog.Warn($"file drop: {ex.Message}");
		}
	}

	/// <summary>鼠标是否在可见的书签栏矩形内（窗口级 Preview 拖放判定）。</summary>
	bool isoverbookmarkbar(DragEventArgs e) {
		if (pbookmarks == null || pbookmarks.Visibility != Visibility.Visible) return false;
		if (pbookmarks.ActualWidth < 1 || pbookmarks.ActualHeight < 1) return false;
		try {
			var pos = e.GetPosition(pbookmarks);
			return pos.X >= 0 && pos.Y >= 0
				&& pos.X <= pbookmarks.ActualWidth
				&& pos.Y <= pbookmarks.ActualHeight;
		} catch {
			return false;
		}
	}

	/// <summary>鼠标是否在打开的分组弹层内。</summary>
	bool isovergrouppopup(DragEventArgs e) {
		if (bookmarkGroupPopup == null || !bookmarkGroupPopup.IsOpen) return false;
		if (bookmarkGroupPopup.Child is not FrameworkElement fe) return false;
		if (fe.ActualWidth < 1 || fe.ActualHeight < 1) return false;
		try {
			var pos = e.GetPosition(fe);
			return pos.X >= 0 && pos.Y >= 0
				&& pos.X <= fe.ActualWidth
				&& pos.Y <= fe.ActualHeight;
		} catch {
			return false;
		}
	}

	/// <summary>在书签栏上拖内部书签时：清弹层指示，在栏上画插入线或分组高亮。</summary>
	void updatebookmarkbarinsertdragui(DragEventArgs e) {
		// 移出到栏：不要在分组弹层底部画线
		hidegrouppopupinsertmark();
		// 分组：中心区=移入；两侧=排序插入（与 Chrome 类似）
		var groupHit = hitbookmarkgroupzone(e);
		if (groupHit.btn != null && groupHit.intoGroup) {
			hidebookmarkinsertmark();
			setgroupdrophighlight(groupHit.btn, true);
			return;
		}
		setgroupdrophighlight(null, false);
		showbookmarkinsertmark(hitbookmarkinsertindex(e));
	}

	/// <summary>
	/// 命中分组芯片时：中心 50% 为移入，左右各 25% 为在该分组前/后插入。
	/// </summary>
	(Button btn, bool intoGroup) hitbookmarkgroupzone(DragEventArgs e) {
		if (pbookmarkitems == null) return (null, false);
		try {
			var pos = e.GetPosition(pbookmarkitems);
			foreach (UIElement u in pbookmarkitems.Children) {
				if (u is not Button b || b.Tag is not BookmarkNode n) continue;
				if (n.Kind != BookmarkKind.Group) continue;
				var tl = b.TranslatePoint(new Point(0, 0), pbookmarkitems);
				var w = Math.Max(1, b.ActualWidth);
				if (pos.X < tl.X || pos.X > tl.X + w) continue;
				if (pos.Y < tl.Y - 6 || pos.Y > tl.Y + b.ActualHeight + 6) continue;
				var rel = (pos.X - tl.X) / w;
				// 两侧 22%：当作排序插入点
				if (rel < 0.22 || rel > 0.78) return (b, false);
				return (b, true);
			}
		} catch { /* ignore */ }
		return (null, false);
	}

	/// <summary>是否落在左侧文件夹/目录区域（优先把目录当工作区打开）。</summary>
	bool isleftdroptarget(object sender) {
		if (sender == null) return false;
		if (ReferenceEquals(sender, pleft) || ReferenceEquals(sender, treeFiles)
			|| ReferenceEquals(sender, treeOutline) || ReferenceEquals(sender, sideTabs)
			|| ReferenceEquals(sender, tabExplorer) || ReferenceEquals(sender, tabOutline))
			return true;
		// 子元素（TreeViewItem 等）
		if (sender is DependencyObject d) {
			var p = d;
			for (var i = 0; i < 12 && p != null; i++) {
				if (ReferenceEquals(p, pleft) || ReferenceEquals(p, treeFiles)
					|| ReferenceEquals(p, treeOutline) || ReferenceEquals(p, sideTabs))
					return true;
				p = System.Windows.Media.VisualTreeHelper.GetParent(p)
					?? LogicalTreeHelper.GetParent(p);
			}
		}
		return false;
	}

	/// <summary>
	/// 处理拖入路径：文件夹（含 .lnk 指向的目录）→ 打开工作区；文件 → 打开标签。
	/// preferFolderWorkspace=true 时（左侧栏）切到「文件夹」Tab 并展开侧栏。
	/// </summary>
	void opendroppedpaths(string[] paths, bool preferFolderWorkspace) {
		if (paths == null) return;
		string lastFolder = null;
		foreach (var raw in paths) {
			if (string.IsNullOrWhiteSpace(raw)) continue;
			var f = raw.Trim().Trim('"');
			try {
				// .lnk → 目标（文件夹快捷方式 / 文件快捷方式）
				var resolved = ShellLink.Resolve(f);
				if (!string.IsNullOrWhiteSpace(resolved))
					f = resolved;
				if (Directory.Exists(f)) {
					lastFolder = Path.GetFullPath(f);
					setworkspace(lastFolder, rebuild: true);
					continue;
				}
				if (File.Exists(f)) {
					openpath(f, loadNow: true);
					continue;
				}
				// 仍是 .lnk 且解析失败：尝试按原路径打开（openpath 内会再解析）
				if (raw.Trim().Trim('"').EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
					openpath(raw.Trim().Trim('"'), loadNow: true);
			} catch (Exception ex) {
				DocLog.Warn($"drop path {f}: {ex.Message}");
			}
		}
		if (lastFolder != null) {
			if (!leftSideVisible) {
				leftSideVisible = true;
				applyleftsideui();
			}
			if (sideTabs != null)
				sideTabs.SelectedIndex = 0; // 文件夹 Tab
			if (lbstatus != null)
				lbstatus.Text = "已打开文件夹: " + lastFolder;
			persisttabs();
		}
	}

	/// <summary>供 WebView2 等 HWND 宿主把文件拖放转回主窗打开。</summary>
	public void OpenDroppedFiles(string[] files) {
		opendroppedpaths(files, preferFolderWorkspace: false);
		try { bringtofront(); } catch { /* ignore */ }
	}

	/// <summary>
	/// 给 Viewer 根/WebView2 等挂文件拖放→打开。
	/// HWND 宿主（WebView2）收不到窗口级 PreviewDrop，需单独绑定。
	/// </summary>
	public static void WireFileDropTarget(UIElement el) {
		if (el == null) return;
		try {
			el.AllowDrop = true;
			el.PreviewDragEnter += onwirefiledrag;
			el.PreviewDragOver += onwirefiledrag;
			el.PreviewDrop += onwirefiledrop;
			el.DragEnter += onwirefiledrag;
			el.DragOver += onwirefiledrag;
			el.Drop += onwirefiledrop;
		} catch (Exception ex) {
			DocLog.Warn($"WireFileDropTarget: {ex.Message}");
		}
	}

	static void onwirefiledrag(object sender, DragEventArgs e) {
		try {
			if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
				e.Effects = DragDropEffects.Copy;
			else
				e.Effects = DragDropEffects.None;
			e.Handled = true;
		} catch { /* ignore */ }
	}

	static void onwirefiledrop(object sender, DragEventArgs e) {
		try {
			if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
			var files = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (files == null || files.Length == 0) return;
			e.Handled = true;
			e.Effects = DragDropEffects.Copy;
			MainWindow host = null;
			if (sender is DependencyObject d)
				host = Window.GetWindow(d) as MainWindow;
			if (host != null)
				host.OpenDroppedFiles(files);
			else
				HandleExternalOpen(files);
		} catch (Exception ex) {
			DocLog.Warn($"wire file drop: {ex.Message}");
		}
	}

	void openargs() {
		var args = Environment.GetCommandLineArgs();
		string zoomTestPath = null;
		string pdfEditTestPath = null;
		DocTab focus = null;
		for (var i = 1; i < args.Length; i++) {
			var a = args[i];
			if (string.IsNullOrWhiteSpace(a)) continue;
			// --zoomtest [pdf路径]：打开后自动连滚缩放，日志见 logs/docviewwpf_*.log
			if (string.Equals(a, "--zoomtest", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(a, "-zoomtest", StringComparison.OrdinalIgnoreCase)) {
				if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) {
					zoomTestPath = args[++i];
				} else {
					zoomTestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "tmp", "sample.pdf");
					if (!File.Exists(zoomTestPath))
						zoomTestPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tmp", "sample.pdf"));
				}
				continue;
			}
			// --test-pdf-edit [pdf]：无 UI 跑改字+渲染+保存，日志写 SelfTestReplace
			if (string.Equals(a, "--test-pdf-edit", StringComparison.OrdinalIgnoreCase)) {
				if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
					pdfEditTestPath = args[++i];
				continue;
			}
			if (a.StartsWith("-") || a.StartsWith("/")) continue;
			openpath(a, loadNow: true);
			// 跟踪命令行打开的最后一个有效 Tab（含 .lnk 解析后）
			var t = findtabafterarg(a);
			if (t != null) focus = t;
		}
		// 有命令行文件时强制跳到该 Tab，并覆盖会话恢复的延迟选中
		if (focus != null) {
			pendingPinTab = focus;
			activatetab(focus, loadNow: true);
			DocLog.Info($"openargs focus path={focus.Path}");
		}
		if (!string.IsNullOrWhiteSpace(pdfEditTestPath)) {
			runpdfedittest(pdfEditTestPath);
			return;
		}
		if (!string.IsNullOrWhiteSpace(zoomTestPath))
			runzoomtest(zoomTestPath);
	}

	/// <summary>根据命令行参数解析出已打开的 Tab（支持 .lnk）。</summary>
	DocTab findtabafterarg(string arg) {
		var path = pathnorm(arg);
		if (path == null) return null;
		var resolved = pathnorm(ShellLink.Resolve(path));
		if (resolved != null) path = resolved;
		return findtab(path);
	}

	void runpdfedittest(string path) {
		try {
			path = Path.GetFullPath(path);
			DocLog.Info("=== --test-pdf-edit begin " + path);
			var err = PdfProEngine.SelfTestReplace(path);
			if (err == null) {
				DocLog.Info("=== --test-pdf-edit PASS");
				lbstatus.Text = "改字自测通过（见日志）";
			} else {
				DocLog.Warn("=== --test-pdf-edit FAIL " + err);
				lbstatus.Text = "改字自测失败: " + err;
			}
		} catch (Exception ex) {
			DocLog.Error("--test-pdf-edit", ex);
			try { lbstatus.Text = "改字自测异常: " + ex.Message; } catch { /* ignore */ }
		}
	}

	/// <summary>调试：打开 PDF 后程序化连续缩放，观察闪白/跳动日志。</summary>
	void runzoomtest(string path) {
		try {
			path = Path.GetFullPath(path);
			DocLog.Info($"zoomtest open {path}");
			if (!File.Exists(path)) {
				DocLog.Warn($"zoomtest file missing: {path}");
				return;
			}
			openpath(path, loadNow: true);
			// 会话恢复会异步加载其它标签；轮询直到目标文件成为当前 PdfViewer
			var tries = 0;
			var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
			poll.Tick += (_, _) => {
				tries++;
				try {
					var tab = findtab(path);
					if (tab != null)
						activatetab(tab, loadNow: true);
					var v = currentviewer() as PdfViewer;
					var ok = v != null
						&& string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase);
					if (!ok && tries < 40) return;
					try { poll.Stop(); } catch { /* ignore */ }
					if (v == null) {
						DocLog.Warn("zoomtest: current is not PdfViewer");
						return;
					}
					DocLog.Info($"zoomtest viewer path={v.FilePath} tries={tries}");
					v.DebugRunZoomTest(10);
					var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
					t.Tick += (_, _) => {
						try { t.Stop(); } catch { /* ignore */ }
						DocLog.Info("zoomtest shutdown");
						try { Application.Current?.Shutdown(); } catch { /* ignore */ }
					};
					t.Start();
				} catch (Exception ex) {
					try { poll.Stop(); } catch { /* ignore */ }
					DocLog.Error("zoomtest poll", ex);
				}
			};
			poll.Start();
		} catch (Exception ex) {
			DocLog.Error("runzoomtest", ex);
		}
	}

	/// <summary>
	/// 次实例经管道转发：前置已有窗口，并在该窗打开路径（无路径则仅激活）。
	/// </summary>
	public static void HandleExternalOpen(string[] paths) {
		try {
			var host = pickhostwindow();
			if (host == null) return;
			host.bringtofront();
			if (paths == null || paths.Length == 0) return;
			foreach (var p in paths) {
				if (string.IsNullOrWhiteSpace(p)) continue;
				// 若其它窗口已打开同一文件，跳到那个标签
				var exist = findtabany(p);
				if (exist.win != null && exist.doc != null) {
					exist.win.bringtofront();
					exist.win.activatetab(exist.doc, loadNow: true);
					exist.win.rememberrecent(exist.doc.Path);
					exist.win.persisttabs();
					continue;
				}
				host.openpath(p, loadNow: true);
			}
		} catch (Exception ex) {
			DocLog.Error("HandleExternalOpen", ex);
		}
	}

	static MainWindow pickhostwindow() {
		if (liveWindows.Count == 0) return null;
		foreach (var w in liveWindows) {
			if (w != null && w.IsActive) return w;
		}
		foreach (var w in liveWindows) {
			if (w != null && !w.isSecondary) return w;
		}
		return liveWindows[0];
	}

	static (MainWindow win, DocTab doc) findtabany(string path) {
		path = pathnorm(path);
		if (path == null) return (null, null);
		foreach (var w in liveWindows) {
			if (w == null) continue;
			var doc = w.findtab(path);
			if (doc != null) return (w, doc);
		}
		return (null, null);
	}

	void bringtofront() {
		try {
			if (!IsVisible) Show();
			if (WindowState == WindowState.Minimized)
				WindowState = WindowState.Normal;
			Activate();
			// 短暂 Topmost 提高前置成功率
			Topmost = true;
			Topmost = false;
			Focus();
			try {
				var hwnd = new WindowInteropHelper(this).Handle;
				if (hwnd != IntPtr.Zero)
					SingleInstance.PublishHwnd(hwnd);
			} catch { /* ignore */ }
		} catch (Exception ex) {
			DocLog.Warn($"bringtofront: {ex.Message}");
		}
	}

	/// <summary>从会话恢复标签壳，并选中上次的 Tab，仅加载该文件。</summary>
	void restoresession() {
		var data = SessionStore.Load();
		if (data.Tabs == null || data.Tabs.Count == 0) return;

		var wantPath = pathnorm(data.SelectedPath);
		restoring = true;
		suppressTabLoad = true;
		try {
			DocTab selectDoc = null;
			for (var i = 0; i < data.Tabs.Count; i++) {
				var path = pathnorm(data.Tabs[i]);
				if (path == null || !File.Exists(path)) continue;
				var kind = DocKindUtil.FromPath(path);
				if (kind == DocKind.Unknown) continue;
				if (findtab(path) != null) continue;

				var doc = addtabshell(path, kind);
				// 优先按路径匹配上次选中
				if (wantPath != null && string.Equals(path, wantPath, StringComparison.OrdinalIgnoreCase))
					selectDoc = doc;
				else if (selectDoc == null && i == data.Selected)
					selectDoc = doc;
			}
			if (opentabs.Count == 0) return;

			if (selectDoc == null && wantPath != null)
				selectDoc = findtab(wantPath);
			if (selectDoc == null)
				selectDoc = opentabs[Math.Min(Math.Max(0, data.Selected), opentabs.Count - 1)];

			tabs.SelectedItem = selectDoc.Tab;
			DocLog.Info($"restoresession select path={selectDoc.Path}");
		} finally {
			suppressTabLoad = false;
			restoring = false;
		}

		// 再强制一次选中（避免 TabControl 尚未就绪）
		var cur = current() ?? (opentabs.Count > 0 ? opentabs[0] : null);
		pendingPinTab = cur;
		if (cur != null) {
			try {
				tabs.SelectedItem = cur.Tab;
				ensureloaded(cur);
			} catch (Exception ex) {
				DocLog.Error("restoresession load", ex);
			}
		}
		syncempty();
		updatestatus();
		// UI 就绪后再钉一次选中；若 openargs 已改 pendingPinTab，则钉命令行文件
		Dispatcher.BeginInvoke(new Action(() => {
			var pin = pendingPinTab;
			if (pin?.Tab == null || !tabs.Items.Contains(pin.Tab)) return;
			if (!ReferenceEquals(tabs.SelectedItem, pin.Tab)) {
				suppressTabLoad = true;
				try { tabs.SelectedItem = pin.Tab; }
				finally { suppressTabLoad = false; }
				try { ensureloaded(pin); } catch { /* ignore */ }
			}
			updatestatus();
			// 会话恢复后补一次 TOC（loadasync 可能已完成或仍在进行）
			if (leftSideVisible && pin.Loaded)
				rebuildmainoutline();
			DocLog.Info($"startup pin path={pin.Path}");
		}), System.Windows.Threading.DispatcherPriority.Loaded);

		DocLog.Info($"restoresession tabs={opentabs.Count} selected={opentabs.IndexOf(cur)}");
	}

	/// <summary>汇总所有窗口标签并落盘。</summary>
	/// <param name="allowEmpty">
	/// true：允许写入空列表（用户关光全部标签）。
	/// false：当前无标签时跳过，避免多窗关闭顺序 / Exit 用空会话覆盖有效记录。
	/// </param>
	void savesession(bool allowEmpty = false) {
		// 汇总所有窗口的标签路径（多窗并存）
		var paths = new List<string>();
		string selPath = null;
		var sel = 0;
		foreach (var w in liveWindows.ToList()) {
			if (w == null) continue;
			foreach (var t in w.opentabs) {
				if (string.IsNullOrWhiteSpace(t?.Path)) continue;
				var full = pathnorm(t.Path) ?? t.Path;
				if (!paths.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
					paths.Add(full);
			}
		}
		if (paths.Count == 0 && !allowEmpty) {
			DocLog.Info("savesession skip empty (keep previous session)");
			return;
		}
		var cur = current();
		if (cur != null) selPath = cur.Path;
		else {
			foreach (var w in liveWindows) {
				var c = w.current();
				if (c != null) { selPath = c.Path; break; }
			}
		}
		if (!string.IsNullOrWhiteSpace(selPath)) {
			var full = pathnorm(selPath) ?? selPath;
			var ix = paths.FindIndex(t => string.Equals(t, full, StringComparison.OrdinalIgnoreCase));
			if (ix >= 0) sel = ix;
		}
		var sideTab = 0;
		try { if (sideTabs != null) sideTab = sideTabs.SelectedIndex; } catch { /* ignore */ }
		SessionStore.Save(paths, sel, selPath,
			closedTabs: ClosedTabsStore.Snapshot(),
			workspaceFolder: workspaceFolder,
			leftSideVisible: leftSideVisible,
			leftSideTab: sideTab);
	}

	static int countalltabs() {
		var n = 0;
		foreach (var w in liveWindows.ToList()) {
			if (w != null) n += w.opentabs.Count;
		}
		return n;
	}

	void saveprogress(IDocViewer v) {
		if (v == null || string.IsNullOrWhiteSpace(v.FilePath)) return;
		try {
			v.CaptureViewState(out var h, out var vv, out var z, out var sp);
			bool? side = null;
			int? mdMode = null;
			if (v.HasOutline)
				side = v.SidePanelVisible;
			if (v is MdViewer md)
				mdMode = md.EditMode ? (int)md.EditLayout + 1 : 0;
			ReadingProgressStore.Set(v.FilePath, h, vv, z, sp, v.CurrentPage, side, mdMode);
		} catch (Exception ex) {
			DocLog.Warn($"saveprogress: {ex.Message}");
		}
	}

	void saveallprogress() {
		foreach (var d in opentabs) {
			if (d?.Viewer != null)
				saveprogress(d.Viewer);
		}
	}

	void restoreprogress(IDocViewer v) {
		if (v == null || string.IsNullOrWhiteSpace(v.FilePath)) return;
		var p = ReadingProgressStore.Get(v.FilePath);
		try {
			// 文档内嵌目录已并入主窗左侧「章节列表」，不再恢复 Viewer 内嵌侧栏
			try { v.SetSidePanelVisible(false); } catch { /* ignore */ }
			if (p == null) return;
			// MD 先恢复模式，再滚位置（切换预览/编辑会重建 UI）
			if (v is MdViewer md)
				restoremdmode(md, p);
			// xlsx=表索引；md=旧布局编码；image=旋转档(rotQuarter 存于 Sheet)；其它=页码
			// 注意：图片 CurrentPage 恒为 1，若误用 Page 会每次打开都转 90°
			var sheetOrPage = v.Kind == DocKind.Xlsx ? p.Sheet
				: v.Kind == DocKind.Md ? p.Sheet
				: v.Kind == DocKind.Image ? p.Sheet
				: p.Page;
			v.RestoreViewState(p.H, p.V, p.Zoom > 0.05 ? p.Zoom : 1, sheetOrPage);
			DocLog.Info($"restoreprogress path={v.FilePath} h={p.H:F0} v={p.V:F0} z={p.Zoom:F2} side={p.Side} md={p.MdMode}");
		} catch (Exception ex) {
			DocLog.Warn($"restoreprogress: {ex.Message}");
		}
	}

	/// <summary>按 reading_progress 恢复 MD 预览/编辑布局（含旧 Sheet 编码）。</summary>
	static void restoremdmode(MdViewer md, ReadingProgress p) {
		if (md == null || p == null) return;
		var mode = p.MdMode;
		if (mode == null && p.Sheet >= 10)
			mode = (p.Sheet - 10) + 1; // 10/11/12 → 1/2/3
		if (mode == null) return;
		var m = mode.Value;
		if (m <= 0) {
			md.EditMode = false;
			return;
		}
		var lay = m - 1;
		if (lay < 0) lay = 0;
		if (lay > 2) lay = 2;
		md.EditLayout = (MdEditLayout)lay;
		md.EditMode = true;
	}

	void scheduleprogresssave(IDocViewer v) {
		progressViewer = v;
		if (progressTimer == null) {
			progressTimer = new DispatcherTimer {
				Interval = TimeSpan.FromMilliseconds(600),
			};
			progressTimer.Tick += (_, _) => {
				progressTimer.Stop();
				var pv = progressViewer;
				if (pv != null) saveprogress(pv);
			};
		}
		progressTimer.Stop();
		progressTimer.Start();
	}

	/// <summary>打开/关闭标签后立即落盘，避免异常退出丢会话。</summary>
	void persisttabs() {
		try {
			// 仅在「全进程确实没有标签」时允许写空（用户关光）；拆窗瞬间的空窗不写空
			savesession(allowEmpty: countalltabs() == 0);
		} catch { /* ignore */ }
	}

	void openfiles() {
		var dlg = new OpenFileDialog {
			Filter = DocKindUtil.Filter,
			Multiselect = true,
			Title = "打开文档",
		};
		if (dlg.ShowDialog(this) != true) return;
		foreach (var f in dlg.FileNames)
			openpath(f, loadNow: true);
	}

	/// <summary>
	/// MD 内链接：在本窗标签页打开目标文档（不再弹独立窗口）；保持预览/编辑模式；可选 #锚点。
	/// </summary>
	void onmdopennewwindow(string path, bool editMode, MdEditLayout layout, string anchor) {
		try {
			path = pathnorm(ShellLink.Resolve(pathnorm(path) ?? path));
			if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
				MessageBox.Show(this, "链接目标不存在:\n" + path, "DocviewWPF",
					MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			pendingMdEdit = editMode;
			pendingMdLayout = layout;
			pendingMdAnchor = anchor;
			openpath(path, loadNow: true, preview: false);
			DocLog.Info($"onmdopenintab path={path} edit={editMode} layout={layout} anchor={anchor}");
		} catch (Exception ex) {
			DocLog.Error("onmdopennewwindow", ex);
			MessageBox.Show(this, "无法打开链接: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>http(s) / 浏览器新窗口请求 → 本窗浏览器标签。</summary>
	void openurlintab(string url) {
		if (string.IsNullOrWhiteSpace(url)) return;
		url = url.Trim();
		try {
			// 已有同一 URL 的浏览器标签则激活
			foreach (var t in opentabs) {
				if (t?.Kind != DocKind.Browser || string.IsNullOrEmpty(t.Path)) continue;
				if (string.Equals(t.Path, url, StringComparison.OrdinalIgnoreCase)) {
					activatetab(t, loadNow: true);
					if (t.Viewer is BrowserViewer bvExist)
						bvExist.Navigate(url);
					return;
				}
			}
			browserSeq++;
			var doc = addtabshell(url, DocKind.Browser, isPreview: false);
			if (doc.TitleLabel != null)
				doc.TitleLabel.Text = "加载中…";
			activatetab(doc, loadNow: true);
			DocLog.Info($"openurlintab {url}");
		} catch (Exception ex) {
			DocLog.Error("openurlintab", ex);
			MessageBox.Show(this, "无法打开网页: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>恢复左侧栏展开状态与 文件夹/章节 Tab 选中（拆窗后用）。</summary>
	void restoresidechrome(bool sideVisible, int sideTabIndex) {
		try {
			leftSideVisible = sideVisible;
			applyleftsideui();
			if (sideTabs == null || sideTabIndex < 0 || sideTabIndex >= sideTabs.Items.Count)
				return;
			if (sideTabs.Items[sideTabIndex] is TabItem ti
				&& ti.Visibility != Visibility.Collapsed)
				sideTabs.SelectedIndex = sideTabIndex;
		} catch { /* ignore */ }
	}

	void applypendingmd(MdViewer m) {
		if (m == null) return;
		try {
			var wantEdit = pendingMdEdit;
			var wantLayout = pendingMdLayout;
			var anchor = pendingMdAnchor;
			pendingMdEdit = null;
			pendingMdLayout = null;
			pendingMdAnchor = null;
			if (wantEdit == true) {
				m.EditMode = true;
				if (wantLayout != null)
					m.EditLayout = wantLayout.Value;
			}
			if (!string.IsNullOrWhiteSpace(anchor)) {
				// 布局完成后再跳锚点
				Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
					try { m.JumpToAnchor(anchor); } catch { /* ignore */ }
				}));
			}
			synctxtmdui();
			updatestatus();
		} catch (Exception ex) {
			DocLog.Warn($"applypendingmd: {ex.Message}");
		}
	}

	/// <param name="preview">
	/// true=预览模式（文件夹浏览）：共用一个斜体预览 Tab，可被下一个预览文件替换；
	/// false=普通打开（菜单/拖放/命令行等）。
	/// </param>
	void openpath(string path, bool loadNow = true, bool preview = false) {
		try {
			path = pathnorm(path);
			if (path == null) return;
			// .lnk → 目标文件（拖放/命令行/打开对话框均可）
			var resolved = pathnorm(ShellLink.Resolve(path));
			if (resolved != null && !string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase)) {
				DocLog.Info($"openpath lnk resolve {path} -> {resolved}");
				path = resolved;
			}
			if (!File.Exists(path)) {
				RecentFilesStore.Remove(path);
				MessageBox.Show($"文件不存在:\n{path}", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			// 已作为普通（或预览）标签打开：只跳转
			var exist = findtab(path);
			if (exist != null) {
				DocLog.Info($"openpath reuse tab path={path} preview={exist.IsPreview}");
				// 普通方式再次打开预览 Tab → 钉住
				if (!preview && exist.IsPreview)
					pinpreviewtab(exist);
				activatetab(exist, loadNow);
				rememberrecent(path);
				persisttabs();
				if (leftSideVisible) rebuildmainoutline();
				return;
			}

			var kind = DocKindUtil.FromPath(path);
			if (kind == DocKind.Unknown) {
				MessageBox.Show(string.Format(Loc.T("unsupported_type"), Path.GetFileName(path)),
					"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}

			if (preview) {
				openaspreview(path, kind, loadNow);
			} else {
				var doc = addtabshell(path, kind, isPreview: false);
				activatetab(doc, loadNow);
			}
			syncempty();
			updatestatus();
			rememberrecent(path);
			persisttabs();
			trysetworkspacefromfile(path);
			if (leftSideVisible) rebuildmainoutline();
		} catch (Exception ex) {
			DocLog.Error($"openpath fail path={path}", ex);
			App.ShowError(ex, "打开文件");
			lbstatus.Text = "打开失败";
		}
	}

	/// <summary>文件夹浏览：打开/替换唯一预览 Tab（斜体标题）。</summary>
	void openaspreview(string path, DocKind kind, bool loadNow) {
		var prev = findpreviewtab();
		if (prev != null) {
			// 预览已改未保存 → 先钉住，再开新预览
			if (isviewdirty(prev.Viewer)) {
				pinpreviewtab(prev);
				var doc = addtabshell(path, kind, isPreview: true);
				activatetab(doc, loadNow);
				return;
			}
			// 同一预览槽换文件
			reusepreviewtab(prev, path, kind, loadNow);
			return;
		}
		var shell = addtabshell(path, kind, isPreview: true);
		activatetab(shell, loadNow);
	}

	DocTab findpreviewtab() {
		foreach (var t in opentabs) {
			if (t != null && t.IsPreview)
				return t;
		}
		return null;
	}

	/// <summary>预览 Tab 钉为普通标签（标题正体）。</summary>
	void pinpreviewtab(DocTab doc) {
		if (doc == null || !doc.IsPreview) return;
		doc.IsPreview = false;
		applypreviewtitlestyle(doc);
		refreshtabtitle(doc);
		DocLog.Info($"pin preview tab path={doc.Path}");
	}

	/// <summary>在已有预览 Tab 上换文件（释放旧 Viewer）。</summary>
	void reusepreviewtab(DocTab doc, string path, DocKind kind, bool loadNow) {
		if (doc == null) return;
		path = pathnorm(path) ?? path;
		try { if (doc.Viewer != null) saveprogress(doc.Viewer); } catch { /* ignore */ }
		stopfilewatch(doc);
		doc.LoadGen++;
		doc.Loading = false;
		try { doc.Viewer?.Dispose(); } catch { /* ignore */ }
		doc.Viewer = null;
		doc.Loaded = false;
		doc.Path = path;
		doc.Kind = kind;
		doc.IsPreview = true;
		doc.FindText = "";
		doc.FindResultText = "";
		if (doc.Tab != null) {
			doc.Tab.Tag = path;
			doc.Tab.Content = makeplaceholder(path);
		}
		applypreviewtitlestyle(doc);
		refreshtabtitle(doc);
		startfilewatch(doc);
		activatetab(doc, loadNow);
		DocLog.Info($"reuse preview tab path={path}");
	}

	void applypreviewtitlestyle(DocTab doc) {
		if (doc?.TitleLabel == null) return;
		try {
			doc.TitleLabel.FontStyle = doc.IsPreview ? FontStyles.Italic : FontStyles.Normal;
			// 预览略淡，钉住后恢复
			doc.TitleLabel.Opacity = doc.IsPreview ? 0.92 : 1.0;
		} catch { /* ignore */ }
	}

	void rememberrecent(string path) {
		try {
			RecentFilesStore.Add(path);
		} catch { /* ignore */ }
	}

	/// <summary>规范化路径，便于同一文件比对。浏览器 URL / browser: 伪路径原样返回。</summary>
	static string pathnorm(string path) {
		if (string.IsNullOrWhiteSpace(path)) return null;
		try {
			path = path.Trim().Trim('"');
			if (path.Length == 0) return null;
			if (isbrowserpath(path) || isconsolepath(path)) return path;
			path = Path.GetFullPath(path);
			// 去掉末尾分隔符（根路径除外）
			if (path.Length > 3)
				path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return path;
		} catch {
			return null;
		}
	}

	/// <summary>浏览器标签伪路径或 http(s)/about: URL。</summary>
	static bool isbrowserpath(string path) {
		if (string.IsNullOrWhiteSpace(path)) return false;
		if (path.StartsWith("browser:", StringComparison.OrdinalIgnoreCase)) return true;
		if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return true;
		if (path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
		if (path.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
		return false;
	}

	static bool isconsolepath(string path) {
		return !string.IsNullOrWhiteSpace(path)
			&& path.StartsWith("console:", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>无磁盘文件的虚拟标签（浏览器 / 命令行）。</summary>
	static bool isvirtualtab(DocTab doc) {
		if (doc == null) return false;
		if (doc.Kind == DocKind.Browser || doc.Kind == DocKind.Console) return true;
		return isbrowserpath(doc.Path) || isconsolepath(doc.Path);
	}

	/// <summary>
	/// 标签显示名。虚拟路径含 : | 等非法文件名字符，不可调用 Path.GetFileName。
	/// </summary>
	static string tabdisplayname(string path, DocKind kind = DocKind.Unknown) {
		if (kind == DocKind.Browser || isbrowserpath(path)) {
			if (string.IsNullOrWhiteSpace(path)
				|| path.StartsWith("browser:", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(path, "about:blank", StringComparison.OrdinalIgnoreCase))
				return "新标签页";
			return path.Length > 48 ? path.Substring(0, 45) + "…" : path;
		}
		if (kind == DocKind.Console || isconsolepath(path)) {
			if (string.IsNullOrWhiteSpace(path)
				|| path.StartsWith("console:new", StringComparison.OrdinalIgnoreCase))
				return "命令行";
			// console:cmd|C:\work
			try {
				var rest = path.StartsWith("console:", StringComparison.OrdinalIgnoreCase)
					? path.Substring("console:".Length) : path;
				var parts = rest.Split(new[] { '|' }, 2);
				var sh = (parts.Length > 0 ? parts[0] : "").Trim();
				if (string.Equals(sh, "powershell", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(sh, "pwsh", StringComparison.OrdinalIgnoreCase))
					return "PowerShell";
				if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) {
					var leaf = parts[1].Trim().TrimEnd('\\', '/');
					var i = leaf.LastIndexOfAny(new[] { '\\', '/' });
					if (i >= 0 && i < leaf.Length - 1) leaf = leaf.Substring(i + 1);
					if (!string.IsNullOrEmpty(leaf)) return "cmd · " + leaf;
				}
				return string.IsNullOrEmpty(sh) ? "命令行" : sh;
			} catch {
				return "命令行";
			}
		}
		if (string.IsNullOrWhiteSpace(path)) return "文档";
		try {
			var name = Path.GetFileName(path);
			return string.IsNullOrEmpty(name) ? path : name;
		} catch {
			return path;
		}
	}

	/// <summary>标题栏 + ：新建 WebView2 浏览器标签（空白页，可输入 URL）。</summary>
	void openbrowsertab() {
		try {
			browserSeq++;
			var path = "browser:new-" + browserSeq;
			var doc = addtabshell(path, DocKind.Browser, isPreview: false);
			if (doc.TitleLabel != null)
				doc.TitleLabel.Text = "新标签页";
			activatetab(doc, loadNow: true);
			DocLog.Info($"open browser tab {path}");
		} catch (Exception ex) {
			DocLog.Error("openbrowsertab", ex);
			MessageBox.Show(this, "无法打开浏览器标签: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>标题栏 + ：新建命令行标签（WPF 模拟终端）。</summary>
	void openconsoletab() {
		try {
			consoleSeq++;
			// 伪路径勿拼工作区：| : 等会触发 Path API「非法字符」
			var path = "console:new-" + consoleSeq;
			var doc = addtabshell(path, DocKind.Console, isPreview: false);
			if (doc.TitleLabel != null)
				doc.TitleLabel.Text = "命令行";
			// 工作区作为 PreferredWorkDir 在 loadasync 里注入
			activatetab(doc, loadNow: true);
			DocLog.Info($"open console tab {path}");
		} catch (Exception ex) {
			DocLog.Error("openconsoletab", ex);
			MessageBox.Show(this, "无法打开命令行标签: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	DocTab findtab(string path) {
		path = pathnorm(path);
		if (path == null) return null;
		foreach (var t in opentabs) {
			if (t?.Path == null) continue;
			if (string.Equals(pathnorm(t.Path) ?? t.Path, path, StringComparison.OrdinalIgnoreCase))
				return t;
		}
		return null;
	}

	void activatetab(DocTab doc, bool loadNow) {
		if (doc?.Tab == null) return;
		suppressTabLoad = true;
		try {
			tabs.SelectedItem = doc.Tab;
			doc.Tab.BringIntoView();
		} finally {
			suppressTabLoad = false;
		}
		if (loadNow)
			ensureloaded(doc);
		else if (!doc.Loaded)
			showplaceholder(doc);
		syncempty();
		updatestatus();
		// 已加载：立刻刷新 TOC；未加载：loadasync 完成时再刷
		if (leftSideVisible && doc.Loaded)
			rebuildmainoutline();
	}

	DocTab addtabshell(string path, DocKind kind, bool isPreview = false) {
		if (kind == DocKind.Browser || kind == DocKind.Console) {
			if (string.IsNullOrWhiteSpace(path))
				path = kind == DocKind.Console ? "console:new" : "browser:new";
		} else {
			path = pathnorm(path) ?? path;
		}
		var tab = new TabItem { Tag = path, Header = null };
		tab.Content = makeplaceholder(path, kind);

		var doc = new DocTab {
			Path = path,
			Kind = kind,
			Tab = tab,
			Viewer = null,
			Loaded = false,
			IsPreview = isPreview,
		};
		// 标题栏 Tab 芯片
		var title = tabdisplayname(path, kind);
		doc.HeaderUI = buildtabheader(title, tab, doc);
		applypreviewtitlestyle(doc);
		opentabs.Add(doc);
		tabs.Items.Add(tab);
		if (ptabs != null)
			ptabs.Children.Add(doc.HeaderUI);
		if (kind != DocKind.Browser && kind != DocKind.Console)
			startfilewatch(doc);
		synctabheaders();
		return doc;
	}

	static FrameworkElement makeplaceholder(string path, DocKind kind = DocKind.Unknown) {
		var name = tabdisplayname(path, kind);
		return new Border {
			Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
			Child = new TextBlock {
				Text = $"未加载\n{name}\n\n切换到此标签时自动打开",
				TextAlignment = TextAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
				Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
				FontSize = 14,
				LineHeight = 24,
			},
		};
	}

	/// <summary>打开中占位：先出 UI，再异步加载文档。</summary>
	static FrameworkElement makeloading(string path, DocKind kind = DocKind.Unknown) {
		var name = tabdisplayname(path, kind);
		var panel = new StackPanel {
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		panel.Children.Add(new TextBlock {
			Text = "加载中…",
			FontSize = 20,
			FontWeight = FontWeights.SemiBold,
			Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(0, 0, 0, 10),
		});
		panel.Children.Add(new TextBlock {
			Text = name,
			FontSize = 13,
			Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
			HorizontalAlignment = HorizontalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 420,
			Margin = new Thickness(0, 0, 0, 16),
		});
		panel.Children.Add(new ProgressBar {
			IsIndeterminate = true,
			Width = 220,
			Height = 4,
			BorderThickness = new Thickness(0),
		});
		return new Border {
			Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
			Child = panel,
		};
	}

	void showplaceholder(DocTab doc) {
		if (doc?.Tab == null) return;
		doc.Tab.Content = makeplaceholder(doc.Path, doc.Kind);
	}

	void showloading(DocTab doc) {
		if (doc?.Tab == null) return;
		doc.Tab.Content = makeloading(doc.Path, doc.Kind);
	}

	/// <summary>
	/// 按需打开文件。立即显示「加载中」，下一帧再解析，避免打开时 UI 假死。
	/// </summary>
	void ensureloaded(DocTab doc) {
		if (doc == null) return;
		if (doc.Loaded && doc.Viewer != null) return;
		if (doc.Loading) return;

		// 浏览器 / 命令行标签无磁盘文件
		if (!isvirtualtab(doc) && !File.Exists(doc.Path)) {
			lbstatus.Text = "文件不存在，已关闭标签";
			closetab(doc.Tab);
			return;
		}

		doc.Loading = true;
		var gen = ++doc.LoadGen;
		showloading(doc);
		if (lbstatus != null) {
			lbstatus.Text = doc.Kind == DocKind.Browser ? "加载浏览器…"
				: doc.Kind == DocKind.Console ? "启动命令行…"
				: $"加载中… {tabdisplayname(doc.Path, doc.Kind)}";
		}
		// 异步：先让「加载中」画出来，再 Load
		loadasync(doc, gen);
	}

	async void loadasync(DocTab doc, int gen) {
		try {
			// 等到当前帧渲染完，界面先可交互并显示加载中
			await dispatcheryield(DispatcherPriority.Loaded);
			if (!loadstillvalid(doc, gen)) return;

			DocLog.Info($"ensureloaded path={doc.Path} kind={doc.Kind}");
			var path = doc.Path;
			var kind = doc.Kind;
			IDocViewer viewer;

			if (kind == DocKind.Xlsx) {
				// 稠密解析在后台线程，UI 只装网格
				var prep = await Task.Run(() => XlsxViewer.Prepare(path));
				if (!loadstillvalid(doc, gen)) return;
				var xv = new XlsxViewer();
				xv.ApplyPrepared(prep);
				viewer = xv;
			} else if (kind == DocKind.Docx) {
				// 直接共享打开解析（不再整文件预读拷贝，避免多耗 ~1–2s）
				// OpenXml→WPF 须在 UI 线程；内部稀疏 UiPump 保活拖窗
				if (!loadstillvalid(doc, gen)) return;
				var dv = new DocxViewer();
				dv.Load(path);
				viewer = dv;
			} else if (kind == DocKind.Pdf) {
				// PDF 体积大时后台读字节有收益
				var bytes = await Task.Run(() => {
					var b = PdfIo.TryLoadBytes(path);
					return b ?? DocFileIo.ReadAllBytesShared(path);
				});
				if (!loadstillvalid(doc, gen)) return;
				var pv = new PdfViewer();
				pv.Load(path, bytes);
				viewer = pv;
			} else if (kind == DocKind.Txt) {
				if (!loadstillvalid(doc, gen)) return;
				var tv = new TextViewer();
				tv.Load(path);
				viewer = tv;
			} else if (kind == DocKind.Md) {
				if (!loadstillvalid(doc, gen)) return;
				var mv = new MdViewer();
				mv.OpenMarkdownNewWindow += onmdopennewwindow;
				mv.OpenUrlInApp += openurlintab;
				mv.Load(path);
				viewer = mv;
			} else if (kind == DocKind.Image) {
				if (!loadstillvalid(doc, gen)) return;
				var iv = new ImageViewer();
				iv.Load(path);
				viewer = iv;
			} else if (kind == DocKind.Csv) {
				if (!loadstillvalid(doc, gen)) return;
				var cv = new CsvViewer();
				cv.Load(path);
				viewer = cv;
			} else if (kind == DocKind.Browser) {
				if (!loadstillvalid(doc, gen)) return;
				var bv = new BrowserViewer();
				// 空白新标签 / 已有 URL
				bv.Load(path);
				bv.OpenInNewTab += openurlintab;
				bv.MetaChanged += () => {
					try {
						if (!opentabs.Contains(doc)) return;
						var u = bv.FilePath;
						if (!string.IsNullOrEmpty(u)
							&& !u.StartsWith("browser:", StringComparison.OrdinalIgnoreCase)) {
							doc.Path = u;
							if (doc.Tab != null) doc.Tab.Tag = u;
						}
						refreshtabtitle(doc);
						if (current()?.Viewer == bv)
							updatestatus();
					} catch { /* ignore */ }
				};
				viewer = bv;
			} else if (kind == DocKind.Console) {
				if (!loadstillvalid(doc, gen)) return;
				var cv = new ConsoleViewer();
				// 工作区作默认 cwd（勿写入伪路径字符串）
				if (!string.IsNullOrEmpty(workspaceFolder) && Directory.Exists(workspaceFolder))
					cv.PreferredWorkDir = workspaceFolder;
				cv.Load(path);
				cv.MetaChanged += () => {
					try {
						if (!opentabs.Contains(doc)) return;
						// FilePath 仅为 console:cmd 等安全伪路径
						var u = cv.FilePath;
						if (!string.IsNullOrEmpty(u)) {
							doc.Path = u;
							if (doc.Tab != null) doc.Tab.Tag = u;
						}
						refreshtabtitle(doc);
						if (current()?.Viewer == cv)
							updatestatus();
					} catch { /* ignore */ }
				};
				viewer = cv;
			} else {
				viewer = ViewerFactory.Create(kind);
				viewer.Load(path);
			}

			if (!loadstillvalid(doc, gen)) {
				try { viewer.Dispose(); } catch { /* ignore */ }
				return;
			}

			viewer.StatusChanged += () => {
				if (current()?.Viewer == viewer)
					updatestatus();
				// 滚动/翻页/改模式/目录时防抖写入进度
				if (viewer.Kind != DocKind.Browser && viewer.Kind != DocKind.Console)
					scheduleprogresssave(viewer);
			};
			// 章节高亮：直接复用 Viewer 内原有 applytocsync 结果（防抖/选中逻辑不变）
			if (viewer is MdViewer mvHl)
				mvHl.OutlineHighlightChanged += onvieweroutlinehighlight;
			else if (viewer is PdfViewer pvHl)
				pvHl.OutlineHighlightChanged += onvieweroutlinehighlight;
			else if (viewer is DocxViewer dvHl)
				dvHl.OutlineHighlightChanged += onvieweroutlinehighlight;
			doc.Viewer = viewer;
			doc.Loaded = true;
			doc.Loading = false;
			doc.Tab.Content = viewer.View;
			// 恢复阅读位置 + 目录 + MD 模式；链接新开窗的 pending 覆盖模式
			if (kind != DocKind.Browser && kind != DocKind.Console) {
				try { restoreprogress(viewer); } catch { /* ignore */ }
			}
			if (viewer is MdViewer mdJust)
				applypendingmd(mdJust);
			try { syncsideui(); } catch { /* ignore */ }
			try { synctxtmdui(); } catch { /* ignore */ }
			try { refreshtabtitle(doc); } catch { /* ignore */ }
			// 记录磁盘时间戳，供外部变更检测
			if (kind != DocKind.Browser && kind != DocKind.Console) {
				capturefilestamp(doc);
				if (doc.Watcher == null)
					startfilewatch(doc);
			}
			DocLog.Info($"ensureloaded ok title={viewer.Title}");
			updatestatus();
			// 异步加载完成后再建主窗章节列表（此前调用时 Viewer 尚未就绪会误藏 Tab）
			if (leftSideVisible && kind != DocKind.Browser && kind != DocKind.Console
				&& ReferenceEquals(current(), doc))
				rebuildmainoutline();
			// 命令行：内容挂上后立刻准备 IME
			if (kind == DocKind.Console && viewer is ConsoleViewer cvLoad
				&& ReferenceEquals(current(), doc))
				cvLoad.PrepareImeFocus();
		} catch (Exception ex) {
			if (!loadstillvalid(doc, gen) && !opentabs.Contains(doc)) return;
			DocLog.Error($"ensureloaded fail path={doc.Path}", ex);
			doc.Loading = false;
			doc.Loaded = false;
			doc.Viewer = null;
			if (opentabs.Contains(doc)) {
				App.ShowError(ex, "打开文件");
				lbstatus.Text = "打开失败";
				showplaceholder(doc);
				updatestatus();
			}
		}
	}

	bool loadstillvalid(DocTab doc, int gen) {
		if (doc == null) return false;
		if (gen != doc.LoadGen) return false;
		if (!opentabs.Contains(doc)) return false;
		return true;
	}

	/// <summary>让出 UI 线程一拍，便于先绘制「加载中」。</summary>
	Task dispatcheryield(DispatcherPriority priority) {
		var tcs = new TaskCompletionSource<object>();
		try {
			Dispatcher.BeginInvoke(priority, new Action(() => tcs.TrySetResult(null)));
		} catch (Exception ex) {
			tcs.TrySetException(ex);
		}
		return tcs.Task;
	}

	/// <summary>未保存时在 Tab 名后加「 *」；保存后去掉；预览标题斜体。</summary>
	void refreshtabtitle(DocTab doc) {
		if (doc?.TitleLabel == null) return;
		try {
			string name;
			if (doc.Kind == DocKind.Browser) {
				name = doc.Viewer?.Title;
				if (string.IsNullOrWhiteSpace(name)
					|| string.Equals(name, "about:blank", StringComparison.OrdinalIgnoreCase))
					name = "新标签页";
			} else if (doc.Kind == DocKind.Console) {
				name = doc.Viewer?.Title;
				if (string.IsNullOrWhiteSpace(name))
					name = tabdisplayname(doc.Path, DocKind.Console);
			} else {
				name = tabdisplayname(doc.Path, doc.Kind);
			}
			var dirty = isviewdirty(doc.Viewer);
			// 预览 Tab 一旦编辑变脏 → 自动钉住（对齐 VS Code）
			if (dirty && doc.IsPreview)
				pinpreviewtab(doc);
			var text = dirty ? name + " *" : name;
			doc.TitleLabel.Text = text;
			applypreviewtitlestyle(doc);
			var tip = doc.Kind == DocKind.Browser || doc.Kind == DocKind.Console
				? name + "\n" + (doc.Path ?? "")
				: doc.IsPreview
					? name + "（预览）\n双击标签钉住；再从文件夹打开其它文件将替换本标签"
					: dirty
						? name + "（未保存）\n拖动可排序；拖出窗口外可拆分为独立窗口"
						: name + "\n拖动可排序；拖出窗口外可拆分为独立窗口";
			doc.TitleLabel.ToolTip = tip;
			if (doc.HeaderUI is Border bd)
				bd.ToolTip = tip;
		} catch { /* ignore */ }
	}

	static bool isviewdirty(IDocViewer v) {
		if (v == null) return false;
		if (v is MdViewer m) return m.IsDirty;
		if (v is TextViewer t) return t.IsDirty;
		if (v is XlsxViewer x) return x.IsDirty;
		if (v is PdfViewer p) return p.IsDirty || p.AnnotDirty;
		return false;
	}

	/// <summary>标题栏 Tab 芯片（Sumatra 风）；支持拖排序 / 拖出独立窗 / 拖入合并。</summary>
	FrameworkElement buildtabheader(string title, TabItem tab, DocTab doc) {
		var panel = new StackPanel {
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
		};
		var lb = new TextBlock {
			Text = title,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 4, 0),
			MaxWidth = 160,
			FontSize = 11,
			TextTrimming = TextTrimming.CharacterEllipsis,
			ToolTip = title + "\n拖动可排序；拖出窗口外可拆分为独立窗口",
			Foreground = TryFindResource("TextPrimary") as Brush
				?? new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
		};
		if (doc != null) doc.TitleLabel = lb;
		var bclose = new Button {
			Content = "×",
			Style = TryFindResource("CloseTabBtn") as Style,
			ToolTip = "关闭",
			Tag = tab,
			VerticalAlignment = VerticalAlignment.Center,
		};
		bclose.Click += (_, e) => {
			e.Handled = true;
			if (bclose.Tag is TabItem ti)
				closetab(ti);
		};
		panel.Children.Add(lb);
		panel.Children.Add(bclose);

		var bd = new Border {
			Child = panel,
			Tag = doc, // 拖拽用 DocTab
			Style = TryFindResource("TitleTab") as Style,
			ToolTip = title + "\n拖动可排序；拖出可独立窗口",
			Cursor = Cursors.Arrow,
		};
		// 标题栏在 WindowChrome 非客户区：Tab 必须可命中才能切换/关闭
		WindowChrome.SetIsHitTestVisibleInChrome(bd, true);
		bd.PreviewMouseLeftButtonDown += (s, e) => {
			// 点关闭按钮不启动拖
			if (e.OriginalSource is DependencyObject d) {
				var p = d;
				while (p != null) {
					if (ReferenceEquals(p, bclose)) return;
					p = VisualTreeHelper.GetParent(p);
				}
			}
			// 双击预览 Tab → 钉为普通标签
			if (e.ClickCount == 2) {
				if (doc != null && doc.IsPreview) {
					pinpreviewtab(doc);
					if (lbstatus != null)
						lbstatus.Text = "已钉住: " + Path.GetFileName(doc.Path);
				}
				e.Handled = true;
				return;
			}
			if (tabs.SelectedItem != tab)
				tabs.SelectedItem = tab;
			tabDragDoc = doc;
			tabDragHeader = bd;
			tabDragStart = e.GetPosition(null);
			tabDragGrabInHeader = e.GetPosition(bd);
			tabDragging = false;
			tabDragFloated = false;
			tabDragLiveAdj = -1;
			tabDragOverWin = null;
			tabDragInsert = -1;
			try { bd.CaptureMouse(); } catch { /* ignore */ }
			e.Handled = true;
		};
		bd.PreviewMouseMove += (s, e) => {
			if (tabDragDoc == null || !ReferenceEquals(tabDragDoc, doc)) return;
			if (e.LeftButton != MouseButtonState.Pressed) {
				endtabdrag(commit: false);
				return;
			}
			var pos = e.GetPosition(null);
			var dx = pos.X - tabDragStart.X;
			var dy = pos.Y - tabDragStart.Y;
			if (!tabDragging) {
				if (dx * dx + dy * dy < TAB_DRAG_THRESHOLD * TAB_DRAG_THRESHOLD) return;
				tabDragging = true;
				bd.Opacity = 0.72;
				bd.Cursor = Cursors.SizeAll;
				Panel.SetZIndex(bd, 100);
			}
			updatetabdrag(bd.PointToScreen(e.GetPosition(bd)));
			e.Handled = true;
		};
		bd.PreviewMouseLeftButtonUp += (s, e) => {
			if (tabDragDoc == null || !ReferenceEquals(tabDragDoc, doc)) return;
			var screen = bd.PointToScreen(e.GetPosition(bd));
			var was = tabDragging;
			endtabdrag(commit: was, screen);
			e.Handled = true;
		};
		bd.LostMouseCapture += (_, _) => {
			if (tabDragTransferring) return;
			if (tabDragDoc != null && ReferenceEquals(tabDragDoc, doc))
				endtabdrag(commit: false);
		};
		bd.MouseDown += (_, e) => {
			if (e.ChangedButton == MouseButton.Middle) {
				closetab(tab);
				e.Handled = true;
			}
		};
		return bd;
	}

	// ---------- Tab 拖拽：排序 / 拆窗 / 合并 ----------

	/// <summary>设备像素 → DIP（Window.Left/Top 使用 DIP；PointToScreen 为设备像素）。</summary>
	static Point screentodip(Visual v, Point screenPx) {
		try {
			var src = PresentationSource.FromVisual(v);
			if (src?.CompositionTarget != null)
				return src.CompositionTarget.TransformFromDevice.Transform(screenPx);
		} catch { /* ignore */ }
		return screenPx;
	}

	void updatetabdrag(Point screenPx) {
		if (tabDragDoc == null) return;

		// 已拆成浮动窗：窗口跟手；若再次进入某窗标签栏则合并回去
		if (tabDragFloated) {
			movefloatwin(screenPx);
			MainWindow over = null;
			var insert = -1;
			foreach (var w in liveWindows.ToList()) {
				if (w == null || !w.IsVisible || ReferenceEquals(w, this)) continue;
				if (!isovertabstrip(w, screenPx)) continue;
				over = w;
				insert = hitinsertindex(w, screenPx);
				break;
			}
			tabDragOverWin = over;
			tabDragInsert = insert;
			if (over != null)
				redockmiddrag(over, insert, screenPx);
			return;
		}

		tabDragOverWin = null;
		tabDragInsert = -1;
		MainWindow hit = null;
		var hitInsert = -1;
		foreach (var w in liveWindows.ToList()) {
			if (w == null || !w.IsVisible) continue;
			if (!isovertabstrip(w, screenPx)) continue;
			hit = w;
			hitInsert = hitinsertindex(w, screenPx, exclude: ReferenceEquals(w, this) ? tabDragHeader : null);
			break;
		}

		if (hit != null) {
			tabDragOverWin = hit;
			tabDragInsert = hitInsert;
			if (ReferenceEquals(hit, this)) {
				// 本窗内：live 排序 + 跟手位移
				livereorder(hitInsert, animate: true);
				followdraggedtab(screenPx);
			} else {
				// 拖到其它窗口标签栏：立即合并并在目标窗继续拖
				transferdragtowindow(hit, hitInsert, screenPx);
			}
		} else {
			// 离开所有标签栏：立刻拆成独立窗口（不等松开）
			// 仅 1 个标签时禁止拆窗（会留下空窗 / 状态异常）
			if (opentabs.Count > 1)
				undockmiddrag(screenPx);
		}
	}

	void followdraggedtab(Point screenPx) {
		var hdr = tabDragHeader;
		if (hdr == null || ptabs == null) return;
		try {
			// 暂清位移以测布局位置
			var tt = hdr.RenderTransform as TranslateTransform;
			if (tt == null) {
				tt = new TranslateTransform();
				hdr.RenderTransform = tt;
			}
			tt.BeginAnimation(TranslateTransform.XProperty, null);
			tt.BeginAnimation(TranslateTransform.YProperty, null);
			tt.X = 0;
			tt.Y = 0;
			ptabs.UpdateLayout();
			var layout = hdr.TransformToAncestor(ptabs).Transform(new Point(0, 0));
			var mouse = ptabs.PointFromScreen(screenPx);
			tt.X = mouse.X - tabDragGrabInHeader.X - layout.X;
			tt.Y = -2;
		} catch { /* ignore */ }
	}

	void livereorder(int newIndex, bool animate) {
		var doc = tabDragDoc;
		if (doc?.HeaderUI == null) return;
		var old = opentabs.IndexOf(doc);
		if (old < 0) return;
		if (newIndex < 0) newIndex = 0;
		if (newIndex > opentabs.Count) newIndex = opentabs.Count;
		var adj = newIndex;
		if (adj > old) adj--;
		if (adj == old) {
			tabDragLiveAdj = adj;
			return;
		}
		if (tabDragLiveAdj == adj) return;

		// FLIP：记录兄弟芯片当前视觉 X，重排后从旧位置滑到新槽位
		Dictionary<FrameworkElement, double> before = null;
		if (animate && ptabs != null) {
			before = new Dictionary<FrameworkElement, double>();
			foreach (UIElement u in ptabs.Children) {
				if (u is not FrameworkElement fe) continue;
				if (ReferenceEquals(fe, tabDragHeader)) continue;
				try {
					before[fe] = fe.TransformToAncestor(ptabs).Transform(new Point(0, 0)).X;
				} catch { /* ignore */ }
				// 停掉旧动画，避免叠算
				if (fe.RenderTransform is TranslateTransform oldTt) {
					oldTt.BeginAnimation(TranslateTransform.XProperty, null);
					oldTt.X = 0;
				}
			}
		}

		reordertab(doc, newIndex, persist: false);
		tabDragLiveAdj = adj;
		Panel.SetZIndex(doc.HeaderUI, 100);

		if (animate && before != null && ptabs != null) {
			try { ptabs.UpdateLayout(); } catch { /* ignore */ }
			foreach (UIElement u in ptabs.Children) {
				if (u is not FrameworkElement fe) continue;
				if (ReferenceEquals(fe, tabDragHeader)) continue;
				if (!before.TryGetValue(fe, out var oldX)) continue;
				double newX;
				try {
					// 此时 RenderTransform 已清零，为纯布局位置
					newX = fe.TransformToAncestor(ptabs).Transform(new Point(0, 0)).X;
				} catch { continue; }
				var delta = oldX - newX;
				if (Math.Abs(delta) < 0.5) {
					cleartt(fe);
					continue;
				}
				var tt = fe.RenderTransform as TranslateTransform;
				if (tt == null) {
					tt = new TranslateTransform();
					fe.RenderTransform = tt;
				}
				tt.BeginAnimation(TranslateTransform.XProperty, null);
				tt.X = delta;
				var anim = new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(TAB_SLIDE_MS)) {
					EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
					FillBehavior = FillBehavior.Stop,
				};
				anim.Completed += (_, _) => {
					if (fe.RenderTransform is TranslateTransform t2) {
						t2.BeginAnimation(TranslateTransform.XProperty, null);
						t2.X = 0;
					}
				};
				tt.BeginAnimation(TranslateTransform.XProperty, anim);
			}
		}
	}

	static void cleartt(FrameworkElement fe) {
		if (fe?.RenderTransform is not TranslateTransform tt) {
			if (fe != null) fe.RenderTransform = Transform.Identity;
			return;
		}
		tt.BeginAnimation(TranslateTransform.XProperty, null);
		tt.BeginAnimation(TranslateTransform.YProperty, null);
		tt.X = 0;
		tt.Y = 0;
		fe.RenderTransform = Transform.Identity;
	}

	void cleartabreorderanims() {
		if (ptabs == null) return;
		foreach (UIElement u in ptabs.Children) {
			if (u is FrameworkElement fe)
				cleartt(fe);
		}
	}

	void movefloatwin(Point screenPx) {
		try {
			var dip = screentodip(this, screenPx);
			Left = dip.X - tabDragGrabInWin.X;
			Top = dip.Y - tabDragGrabInWin.Y;
		} catch { /* ignore */ }
	}

	/// <summary>拖出标签栏：立即拆窗并跟手（不等 MouseUp）。</summary>
	void undockmiddrag(Point screenPx) {
		var doc = tabDragDoc;
		var hdr = tabDragHeader;
		if (doc == null || tabDragFloated) return;
		// 仅 1 个标签时禁止拆成独立窗口
		if (opentabs.Count <= 1) return;

		// 记录光标相对原窗口的抓取点，新窗放在同一光标下
		Point grabInWin;
		try {
			var winTlPx = PointToScreen(new Point(0, 0));
			var winTl = screentodip(this, winTlPx);
			var cur = screentodip(this, screenPx);
			grabInWin = new Point(cur.X - winTl.X, cur.Y - winTl.Y);
			// 限制在标题栏高度内，避免抓点过低导致新窗飞出
			if (grabInWin.Y < 4) grabInWin.Y = 4;
			if (grabInWin.Y > 28) grabInWin.Y = 14;
			if (grabInWin.X < 8) grabInWin.X = 8;
		} catch {
			grabInWin = new Point(80, 14);
		}

		var w = Math.Max(640, ActualWidth * 0.85);
		var h = Math.Max(420, ActualHeight * 0.85);
		var dip = screentodip(this, screenPx);

		tabDragTransferring = true;
		try {
			cleartabreorderanims();
			if (hdr != null) {
				try { hdr.ReleaseMouseCapture(); } catch { /* ignore */ }
			}
			// 清空本窗拖拽状态（避免 LostCapture 取消）
			tabDragDoc = null;
			tabDragHeader = null;
			tabDragging = false;

			// 记录本窗侧栏状态（detachtab 后勿再改本窗 leftSideVisible）
			var keepSide = leftSideVisible;
			var keepSideTab = 0;
			try { if (sideTabs != null) keepSideTab = sideTabs.SelectedIndex; } catch { /* ignore */ }

			detachtab(doc);
			// detachtab / 切 Tab 后恢复本窗侧栏显示与选中 Tab
			restoresidechrome(keepSide, keepSideTab);

			var nw = new MainWindow(secondary: true) {
				Width = w,
				Height = h,
				WindowStartupLocation = WindowStartupLocation.Manual,
				Left = dip.X - grabInWin.X,
				Top = dip.Y - grabInWin.Y,
			};
			// 新窗继承侧栏显示状态，避免 TOC 栏「突然出现/消失」
			nw.leftSideVisible = keepSide;
			nw.Show();
			nw.attachtab(doc, 0, activate: true);
			// attachtab 会 rebuild TOC，侧栏状态放在其后恢复
			nw.restoresidechrome(keepSide, keepSideTab);
			nw.takefloatdrag(doc, grabInWin);
			nw.Activate();

			if (opentabs.Count == 0 && isSecondary)
				Close();
			else
				syncempty();
		} catch (Exception ex) {
			DocLog.Error("undockmiddrag", ex);
		} finally {
			tabDragTransferring = false;
		}
	}

	/// <summary>承接拆窗后的跟手拖动。</summary>
	void takefloatdrag(DocTab doc, Point grabInWin) {
		tabDragDoc = doc;
		tabDragHeader = doc?.HeaderUI;
		tabDragging = true;
		tabDragFloated = true;
		tabDragGrabInWin = grabInWin;
		tabDragGrabInHeader = new Point(Math.Min(40, grabInWin.X), Math.Min(14, grabInWin.Y));
		tabDragLiveAdj = 0;
		tabDragStart = new Point(0, 0);
		tabDragOverWin = null;
		tabDragInsert = -1;
		if (tabDragHeader != null) {
			tabDragHeader.Opacity = 0.72;
			tabDragHeader.Cursor = Cursors.SizeAll;
			Panel.SetZIndex(tabDragHeader, 100);
			try { tabDragHeader.CaptureMouse(); } catch { /* ignore */ }
		}
	}

	/// <summary>浮动窗拖回其它窗口标签栏：立即合并并继续排序拖。</summary>
	void redockmiddrag(MainWindow target, int insert, Point screenPx) {
		var doc = tabDragDoc;
		if (doc == null || target == null || ReferenceEquals(target, this)) return;

		Point grab = tabDragGrabInHeader;
		tabDragTransferring = true;
		try {
			if (tabDragHeader != null) {
				try { tabDragHeader.ReleaseMouseCapture(); } catch { /* ignore */ }
			}
			tabDragDoc = null;
			tabDragHeader = null;
			tabDragging = false;
			tabDragFloated = false;

			detachtab(doc);
			target.attachtab(doc, insert, activate: true);
			target.takedragreorder(doc, grab, screenPx);
			target.Activate();

			if (opentabs.Count == 0 && isSecondary)
				Close();
		} catch (Exception ex) {
			DocLog.Error("redockmiddrag", ex);
		} finally {
			tabDragTransferring = false;
		}
	}

	/// <summary>跨窗拖入后，在目标窗继续条内排序拖。</summary>
	void takedragreorder(DocTab doc, Point grabInHeader, Point screenPx) {
		tabDragDoc = doc;
		tabDragHeader = doc?.HeaderUI;
		tabDragging = true;
		tabDragFloated = false;
		tabDragGrabInHeader = grabInHeader;
		tabDragLiveAdj = opentabs.IndexOf(doc);
		tabDragStart = new Point(0, 0);
		tabDragOverWin = this;
		tabDragInsert = tabDragLiveAdj;
		if (tabDragHeader != null) {
			tabDragHeader.Opacity = 0.72;
			tabDragHeader.Cursor = Cursors.SizeAll;
			Panel.SetZIndex(tabDragHeader, 100);
			try { tabDragHeader.CaptureMouse(); } catch { /* ignore */ }
		}
		followdraggedtab(screenPx);
	}

	/// <summary>条内拖到其它窗口：立即转移。</summary>
	void transferdragtowindow(MainWindow target, int insert, Point screenPx) {
		var doc = tabDragDoc;
		if (doc == null || target == null || ReferenceEquals(target, this)) return;
		var grab = tabDragGrabInHeader;
		tabDragTransferring = true;
		try {
			cleartabreorderanims();
			if (tabDragHeader != null) {
				try { tabDragHeader.ReleaseMouseCapture(); } catch { /* ignore */ }
			}
			tabDragDoc = null;
			tabDragHeader = null;
			tabDragging = false;

			detachtab(doc);
			target.attachtab(doc, insert, activate: true);
			target.takedragreorder(doc, grab, screenPx);
			target.Activate();

			if (opentabs.Count == 0 && isSecondary)
				Close();
		} catch (Exception ex) {
			DocLog.Error("transferdragtowindow", ex);
		} finally {
			tabDragTransferring = false;
		}
	}

	void endtabdrag(bool commit, Point screen = default) {
		var doc = tabDragDoc;
		var hdr = tabDragHeader;
		var over = tabDragOverWin;
		var insert = tabDragInsert;
		var dragging = tabDragging;
		var floated = tabDragFloated;

		tabDragDoc = null;
		tabDragHeader = null;
		tabDragging = false;
		tabDragOverWin = null;
		tabDragInsert = -1;
		tabDragLiveAdj = -1;
		tabDragFloated = false;

		if (hdr != null) {
			hdr.Opacity = 1;
			hdr.Cursor = Cursors.Arrow;
			Panel.SetZIndex(hdr, 0);
			cleartt(hdr);
			try { hdr.ReleaseMouseCapture(); } catch { /* ignore */ }
		}
		cleartabreorderanims();

		if (!commit || doc == null) {
			try { persisttabs(); } catch { /* ignore */ }
			return;
		}

		try {
			if (floated) {
				// 已在拖出过程中成窗并跟手，松手即落位
				persisttabs();
				return;
			}
			if (over != null) {
				if (ReferenceEquals(over, this)) {
					// live reorder 已在移动中完成，补一次持久化
					// 若未触发过 live（极短拖动），这里再排一次
					if (insert >= 0)
						reordertab(doc, insert, persist: true);
					else
						persisttabs();
				} else {
					movetabtowindow(doc, over, insert);
				}
			} else {
				// 理论上已 mid-undock；兜底松手拆窗
				undocktab(doc, screen);
			}
		} catch (Exception ex) {
			DocLog.Error("endtabdrag", ex);
		}
	}

	static bool isovertabstrip(MainWindow w, Point screen) {
		try {
			var el = (FrameworkElement)w.svtabs ?? w.ptabs;
			if (el == null || !el.IsVisible) return false;
			var tl = el.PointToScreen(new Point(0, 0));
			var br = el.PointToScreen(new Point(Math.Max(1, el.ActualWidth), Math.Max(28, el.ActualHeight)));
			// 略放大命中区；向下拖出较易触发拆窗
			return screen.X >= tl.X - 12 && screen.X <= br.X + 12
				&& screen.Y >= tl.Y - 10 && screen.Y <= br.Y + 14;
		} catch {
			return false;
		}
	}

	static int hitinsertindex(MainWindow w, Point screen, FrameworkElement exclude = null) {
		if (w?.ptabs == null || w.ptabs.Children.Count == 0) return 0;
		try {
			for (var i = 0; i < w.ptabs.Children.Count; i++) {
				if (w.ptabs.Children[i] is not FrameworkElement fe) continue;
				if (exclude != null && ReferenceEquals(fe, exclude)) continue;
				// 布局中点（尽量忽略自身跟手 Translate，避免抖动）
				Point tl;
				if (fe.RenderTransform is TranslateTransform tt && Math.Abs(tt.X) > 0.01) {
					var parent = w.ptabs;
					var layout = fe.TransformToAncestor(parent).Transform(new Point(0, 0));
					// TransformToAncestor 含 RenderTransform，扣掉 tt.X
					var layoutX = layout.X - tt.X;
					var origin = parent.PointToScreen(new Point(layoutX, layout.Y));
					tl = origin;
				} else {
					tl = fe.PointToScreen(new Point(0, 0));
				}
				var mid = tl.X + fe.ActualWidth * 0.5;
				if (screen.X < mid) return i;
			}
			return w.ptabs.Children.Count;
		} catch {
			return w.ptabs.Children.Count;
		}
	}

	void reordertab(DocTab doc, int newIndex, bool persist = true) {
		var old = opentabs.IndexOf(doc);
		if (old < 0 || doc.HeaderUI == null || doc.Tab == null) return;
		if (newIndex < 0) newIndex = 0;
		if (newIndex > opentabs.Count) newIndex = opentabs.Count;
		// 插入点在自身之后时校正
		var adj = newIndex;
		if (adj > old) adj--;
		if (adj == old) return;

		opentabs.RemoveAt(old);
		opentabs.Insert(adj, doc);

		ptabs.Children.Remove(doc.HeaderUI);
		if (adj >= ptabs.Children.Count)
			ptabs.Children.Add(doc.HeaderUI);
		else
			ptabs.Children.Insert(adj, doc.HeaderUI);

		var ti = tabs.Items.IndexOf(doc.Tab);
		if (ti >= 0) {
			suppressTabLoad = true;
			try {
				tabs.Items.Remove(doc.Tab);
				if (adj >= tabs.Items.Count)
					tabs.Items.Add(doc.Tab);
				else
					tabs.Items.Insert(adj, doc.Tab);
				tabs.SelectedItem = doc.Tab;
			} finally {
				suppressTabLoad = false;
			}
		}
		synctabheaders();
		if (persist)
			persisttabs();
	}

	/// <summary>从本窗拆离标签（不 Dispose Viewer）。</summary>
	void detachtab(DocTab doc) {
		if (doc == null) return;
		try { if (doc.Viewer != null) saveprogress(doc.Viewer); } catch { /* ignore */ }
		opentabs.Remove(doc);
		if (doc.Tab != null && tabs.Items.Contains(doc.Tab)) {
			suppressTabLoad = true;
			try { tabs.Items.Remove(doc.Tab); }
			finally { suppressTabLoad = false; }
		}
		if (doc.HeaderUI != null) {
			if (ptabs != null && ptabs.Children.Contains(doc.HeaderUI))
				ptabs.Children.Remove(doc.HeaderUI);
			doc.HeaderUI = null;
		}
		// Tab 内容保留 Viewer.View，勿 Dispose
		synctabheaders();
		syncempty();
		if (tabs.Items.Count > 0 && tabs.SelectedItem == null)
			tabs.SelectedIndex = Math.Max(0, tabs.Items.Count - 1);
		persisttabs();
	}

	/// <summary>接收标签到本窗指定插入位置。</summary>
	void attachtab(DocTab doc, int insertIndex, bool activate = true) {
		if (doc == null) return;
		// 路径已在本窗则仅激活
		var exist = findtab(doc.Path);
		if (exist != null && !ReferenceEquals(exist, doc)) {
			// 另一份同路径：丢弃拖入的（保留已有）
			try { doc.Viewer?.Dispose(); } catch { /* ignore */ }
			activatetab(exist, true);
			return;
		}
		if (opentabs.Contains(doc)) {
			reordertab(doc, insertIndex);
			return;
		}

		if (doc.Tab == null) {
			doc.Tab = new TabItem { Tag = doc.Path, Header = null };
			if (doc.Viewer != null)
				doc.Tab.Content = doc.Viewer.View;
			else if (doc.Loaded)
				doc.Tab.Content = makeplaceholder(doc.Path, doc.Kind);
			else
				doc.Tab.Content = makeplaceholder(doc.Path, doc.Kind);
		}
		// 重建芯片（事件绑定到本窗）
		doc.HeaderUI = buildtabheader(tabdisplayname(doc.Path, doc.Kind), doc.Tab, doc);

		if (insertIndex < 0) insertIndex = opentabs.Count;
		if (insertIndex > opentabs.Count) insertIndex = opentabs.Count;
		opentabs.Insert(insertIndex, doc);

		if (insertIndex >= tabs.Items.Count)
			tabs.Items.Add(doc.Tab);
		else
			tabs.Items.Insert(insertIndex, doc.Tab);

		if (ptabs != null) {
			if (insertIndex >= ptabs.Children.Count)
				ptabs.Children.Add(doc.HeaderUI);
			else
				ptabs.Children.Insert(insertIndex, doc.HeaderUI);
		}

		// 拆窗/并窗后确保监视仍在（可能在上一窗已停）
		if (doc.Watcher == null)
			startfilewatch(doc);

		if (activate)
			activatetab(doc, loadNow: !doc.Loaded);
		else {
			synctabheaders();
			syncempty();
		}
		persisttabs();
	}

	void movetabtowindow(DocTab doc, MainWindow target, int insertIndex) {
		if (doc == null || target == null) return;
		if (ReferenceEquals(target, this)) {
			reordertab(doc, insertIndex);
			return;
		}
		detachtab(doc);
		target.attachtab(doc, insertIndex, activate: true);
		target.Activate();
		// 本窗已空且为次要窗 → 关闭
		if (opentabs.Count == 0 && isSecondary)
			Close();
	}

	/// <summary>松手兜底拆窗（正常路径走 undockmiddrag）。</summary>
	void undocktab(DocTab doc, Point screenPx) {
		if (doc == null) return;
		// 仅 1 个标签时禁止拆成独立窗口
		if (opentabs.Count <= 1) return;
		var dip = screentodip(this, screenPx);
		var grabX = tabDragGrabInHeader.X > 0 ? tabDragGrabInHeader.X : 80;
		var grabY = 14.0;
		var keepSide = leftSideVisible;
		var keepSideTab = 0;
		try { if (sideTabs != null) keepSideTab = sideTabs.SelectedIndex; } catch { /* ignore */ }
		detachtab(doc);
		restoresidechrome(keepSide, keepSideTab);
		var nw = new MainWindow(secondary: true) {
			Width = Math.Max(640, ActualWidth * 0.85),
			Height = Math.Max(420, ActualHeight * 0.85),
			WindowStartupLocation = WindowStartupLocation.Manual,
			Left = dip.X - grabX,
			Top = dip.Y - grabY,
		};
		nw.leftSideVisible = keepSide;
		nw.Show();
		nw.attachtab(doc, 0, activate: true);
		nw.restoresidechrome(keepSide, keepSideTab);
		nw.Activate();
		if (opentabs.Count == 0 && isSecondary)
			Close();
		else
			syncempty();
	}

	/// <summary>同步标题栏 Tab 激活样式。</summary>
	void synctabheaders() {
		var sel = tabs?.SelectedItem as TabItem;
		foreach (var d in opentabs) {
			if (d?.HeaderUI is not Border bd) continue;
			var active = ReferenceEquals(d.Tab, sel);
			bd.Style = TryFindResource(active ? "TitleTabActive" : "TitleTab") as Style;
		}
	}

	// Tag 现为 DocTab；查找用 Path / Tab 引用

	void onwindowclosing(object sender, CancelEventArgs e) {
		// 每个有修改的文件逐个询问：是=保存 / 否=丢弃 / 取消=不关窗
		if (!confirmsavealldirty()) {
			e.Cancel = true;
			return;
		}
		try { stopallfilewatches(); } catch { /* ignore */ }
		try { saveallprogress(); } catch { /* ignore */ }
		if (!isSecondary) {
			try { savewindowbounds(); } catch { /* ignore */ }
		}
		// 关窗时保存会话；空列表勿覆盖其它窗
		try { savesession(allowEmpty: false); } catch { /* ignore */ }
	}

	/// <summary>
	/// 本窗所有脏标签依次提示保存。返回 false=用户取消（中止关闭）。
	/// </summary>
	bool confirmsavealldirty() {
		foreach (var d in opentabs.ToList()) {
			if (d == null || !isviewdirty(d.Viewer)) continue;
			var r = promptsavedirty(d);
			if (r == MessageBoxResult.Cancel)
				return false;
			if (r == MessageBoxResult.Yes) {
				if (!trysavedoc(d))
					return false; // 保存失败则中止关闭
			}
		}
		return true;
	}

	/// <summary>单个脏文件：是/否/取消。</summary>
	MessageBoxResult promptsavedirty(DocTab doc) {
		if (doc == null) return MessageBoxResult.No;
		try {
			// 切到该标签，便于用户辨认
			if (doc.Tab != null && tabs != null && tabs.Items.Contains(doc.Tab))
				tabs.SelectedItem = doc.Tab;
		} catch { /* ignore */ }
		var name = tabdisplayname(doc.Path, doc.Kind);
		var path = doc.Path ?? "";
		return MessageBox.Show(this,
			Loc.Tf("confirm_save_file", name, path),
			Loc.T("confirm_save_title"),
			MessageBoxButton.YesNoCancel,
			MessageBoxImage.Question,
			MessageBoxResult.Yes);
	}

	/// <summary>按查看器类型保存；失败弹窗并返回 false。</summary>
	bool trysavedoc(DocTab doc) {
		if (doc?.Viewer == null) return true;
		try {
			markselfwrite(doc);
			var v = doc.Viewer;
			if (v is MdViewer m) {
				m.Save();
			} else if (v is TextViewer t) {
				t.Save();
			} else if (v is XlsxViewer x) {
				x.Save();
			} else if (v is PdfViewer p) {
				if (p.IsDirty)
					p.SaveEdits();
				if (p.AnnotDirty)
					p.SaveAnnots();
			} else {
				return true;
			}
			markselfwrite(doc);
			try { refreshtabtitle(doc); } catch { /* ignore */ }
			if (lbstatus != null)
				lbstatus.Text = Loc.Tf("saved", Path.GetFileName(doc.Path) ?? doc.Path);
			updatestatus();
			return true;
		} catch (Exception ex) {
			DocLog.Error("trysavedoc", ex);
			MessageBox.Show(this,
				Loc.Tf("save_failed", ex.Message),
				"DocviewWPF",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
			return false;
		}
	}

	void closetab(TabItem tab) {
		var doc = opentabs.FirstOrDefault(t => t.Tab == tab);
		if (doc == null) {
			tabs.Items.Remove(tab);
			syncempty();
			return;
		}
		// 关标签前：脏文件提示保存
		if (isviewdirty(doc.Viewer)) {
			var r = promptsavedirty(doc);
			if (r == MessageBoxResult.Cancel)
				return;
			if (r == MessageBoxResult.Yes && !trysavedoc(doc))
				return;
		}
		// 取消进行中的异步加载
		doc.LoadGen++;
		doc.Loading = false;
		stopfilewatch(doc);
		try {
			if (doc.Viewer != null && doc.Kind != DocKind.Browser && doc.Kind != DocKind.Console)
				saveprogress(doc.Viewer);
		} catch { /* ignore */ }
		// 虚拟空标签不进「最近关闭」；已访问的 http(s) 可重开
		try {
			if (doc.Kind == DocKind.Console) {
				/* 不入关闭栈 */
			} else if (doc.Kind != DocKind.Browser
				|| (!string.IsNullOrEmpty(doc.Path)
					&& (doc.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
						|| doc.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))))
				ClosedTabsStore.Push(doc.Path);
		} catch { /* ignore */ }
		// 分屏若指向本文件则关闭分屏侧
		if (splitOn && splitPath != null
			&& string.Equals(pathnorm(splitPath), pathnorm(doc.Path), StringComparison.OrdinalIgnoreCase))
			closesplit();
		var idx = tabs.Items.IndexOf(tab);
		tabs.Items.Remove(tab);
		opentabs.Remove(doc);
		if (doc.HeaderUI != null && ptabs != null)
			ptabs.Children.Remove(doc.HeaderUI);
		doc.HeaderUI = null;
		try { doc.Viewer?.Dispose(); } catch { /* ignore */ }
		doc.Viewer = null;
		doc.Loaded = false;

		if (tabs.Items.Count > 0) {
			if (idx >= tabs.Items.Count) idx = tabs.Items.Count - 1;
			if (idx < 0) idx = 0;
			// 切换会触发 ensureloaded
			tabs.SelectedIndex = idx;
		}
		synctabheaders();
		syncempty();
		updatestatus();
		persisttabs();
		if (splitOn) refreshsplitlist();
	}

	void closecurrent() {
		if (tabs.SelectedItem is TabItem ti)
			closetab(ti);
	}

	void closeall() {
		// 先统一问完所有脏文件，避免关一半再取消时状态混乱
		if (!confirmsavealldirty())
			return;
		foreach (var d in opentabs.ToList())
			closetabforce(d.Tab);
	}

	/// <summary>已确认保存/丢弃后的关闭（不再二次提示）。</summary>
	void closetabforce(TabItem tab) {
		var doc = opentabs.FirstOrDefault(t => t.Tab == tab);
		if (doc == null) {
			if (tab != null) tabs.Items.Remove(tab);
			syncempty();
			return;
		}
		doc.LoadGen++;
		doc.Loading = false;
		stopfilewatch(doc);
		try {
			if (doc.Viewer != null && doc.Kind != DocKind.Browser && doc.Kind != DocKind.Console)
				saveprogress(doc.Viewer);
		} catch { /* ignore */ }
		try {
			if (doc.Kind != DocKind.Console && doc.Kind != DocKind.Browser)
				ClosedTabsStore.Push(doc.Path);
			else if (doc.Kind == DocKind.Browser
				&& !string.IsNullOrEmpty(doc.Path)
				&& (doc.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
					|| doc.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
				ClosedTabsStore.Push(doc.Path);
		} catch { /* ignore */ }
		var idx = tabs.Items.IndexOf(tab);
		tabs.Items.Remove(tab);
		opentabs.Remove(doc);
		if (doc.HeaderUI != null && ptabs != null)
			ptabs.Children.Remove(doc.HeaderUI);
		doc.HeaderUI = null;
		try { doc.Viewer?.Dispose(); } catch { /* ignore */ }
		doc.Viewer = null;
		doc.Loaded = false;
		if (tabs.Items.Count > 0) {
			if (idx >= tabs.Items.Count) idx = tabs.Items.Count - 1;
			if (idx < 0) idx = 0;
			tabs.SelectedIndex = idx;
		}
		synctabheaders();
		syncempty();
		updatestatus();
		persisttabs();
		if (splitOn) refreshsplitlist();
	}

	void syncempty() {
		var has = tabs.Items.Count > 0;
		tabs.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
		pempty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
	}

	void ontabchanged(object sender, SelectionChangedEventArgs e) {
		// 切走前保存离开标签的进度 + 查找框内容（共享 efind 属于离开的 Tab）
		try {
			if (e.RemovedItems != null) {
				foreach (var it in e.RemovedItems) {
					if (it is not TabItem ti) continue;
					var d = opentabs.FirstOrDefault(t => t.Tab == ti);
					if (d == null) continue;
					if (d.Viewer != null) saveprogress(d.Viewer);
					savefindtotab(d);
				}
			}
		} catch { /* ignore */ }
		// 恢复当前 Tab 的查找框（独立内容）
		restorefindfromtab(current());
		synctabheaders();
		if (suppressTabLoad || restoring) {
			updatestatus();
			return;
		}
		var cur = current();
		if (cur != null && !cur.Loaded)
			ensureloaded(cur);
		updatestatus();
		if (leftSideVisible) rebuildmainoutline();
		// 命令行标签：强制聚焦终端并启用 IME（首次进入否则中文要再切一次 Tab）
		if (cur?.Viewer is ConsoleViewer cvTab)
			cvTab.PrepareImeFocus();
	}

	void savefindtotab(DocTab d) {
		if (d == null) return;
		d.FindText = efind?.Text ?? "";
		d.FindCase = bcase != null && bcase.IsChecked == true;
		d.FindResultText = lbfind?.Text ?? "";
	}

	void restorefindfromtab(DocTab d) {
		findBoxSilent = true;
		try {
			if (efind != null)
				efind.Text = d?.FindText ?? "";
			if (bcase != null)
				bcase.IsChecked = d?.FindCase == true;
			if (lbfind != null)
				lbfind.Text = d?.FindResultText ?? "";
		} catch { /* ignore */ }
		finally { findBoxSilent = false; }
	}

	void updatestatus() {
		var cur = current();
		if (cur == null) {
			Title = "DocviewWPF";
			lbstatus.Text = "就绪 · 打开 PDF / DOCX / XLSX / CSV / 代码 / 图片 / MD";
			lbpath.Text = "";
			if (lbenc != null) { lbenc.Text = ""; lbenc.Visibility = Visibility.Collapsed; }
			if (lbpagetotal != null) lbpagetotal.Text = "/ 0";
			syncsideui();
			syncxlsxeditui();
			synctxtmdui();
			syncpdfeditui();
			if (!epage.IsKeyboardFocusWithin) {
				pageBoxSilent = true;
				epage.Text = "";
				pageBoxSilent = false;
			}
			return;
		}

		if (cur.Loading) {
			var name = tabdisplayname(cur.Path, cur.Kind);
			Title = $"{name} - DocviewWPF";
			lbstatus.Text = cur.Kind == DocKind.Browser ? "加载浏览器…"
				: cur.Kind == DocKind.Console ? "启动命令行…"
				: $"加载中… {name}";
			lbpath.Text = cur.Path ?? "";
			if (lbenc != null) { lbenc.Text = ""; lbenc.Visibility = Visibility.Collapsed; }
			if (lbpagetotal != null) lbpagetotal.Text = "/ …";
			if (!epage.IsKeyboardFocusWithin) {
				pageBoxSilent = true;
				epage.Text = "";
				pageBoxSilent = false;
			}
			return;
		}

		if (!cur.Loaded || cur.Viewer == null) {
			var name = tabdisplayname(cur.Path, cur.Kind);
			Title = $"{name} - DocviewWPF";
			lbstatus.Text = "未加载 · 切换到此标签时打开";
			lbpath.Text = cur.Path ?? "";
			if (lbenc != null) { lbenc.Text = ""; lbenc.Visibility = Visibility.Collapsed; }
			if (lbpagetotal != null) lbpagetotal.Text = "/ -";
			if (!epage.IsKeyboardFocusWithin) {
				pageBoxSilent = true;
				epage.Text = "";
				pageBoxSilent = false;
			}
			return;
		}

		Title = $"{cur.Viewer.Title} - DocviewWPF";
		lbstatus.Text = cur.Viewer.StatusText;
		lbpath.Text = cur.Path;
		// 文本/MD：状态栏显示编码并可点切换
		try {
			if (lbenc != null) {
				if (cur.Viewer is TextViewer tv) {
					lbenc.Text = tv.EncodingName;
					lbenc.Visibility = Visibility.Visible;
				} else if (cur.Viewer is MdViewer mv) {
					lbenc.Text = mv.EncodingName;
					lbenc.Visibility = Visibility.Visible;
				} else {
					lbenc.Text = "";
					lbenc.Visibility = Visibility.Collapsed;
				}
			}
		} catch { /* ignore */ }
		if (lbpagetotal != null) lbpagetotal.Text = $"/ {cur.Viewer.PageCount}";
		syncsideui();
		syncxlsxeditui();
		synctxtmdui();
		syncpdfeditui();
		if (!epage.IsKeyboardFocusWithin) {
			pageBoxSilent = true;
			epage.Text = cur.Viewer.CurrentPage > 0 ? cur.Viewer.CurrentPage.ToString() : "";
			pageBoxSilent = false;
		}
	}

	void jumppage() {
		if (pageBoxSilent) return;
		var cur = current();
		if (cur == null) return;
		ensureloaded(cur);
		var v = cur.Viewer;
		if (v == null) return;
		var s = epage.Text?.Trim() ?? "";
		if (!int.TryParse(s, out var n)) {
			updatestatus();
			return;
		}
		v.GoToPage(n);
		updatestatus();
	}

	void focuspagebox() {
		epage.Focus();
		epage.SelectAll();
	}

	DocTab current() {
		if (tabs.SelectedItem is not TabItem ti) return null;
		return opentabs.FirstOrDefault(t => t.Tab == ti);
	}

	IDocViewer currentviewer() {
		var cur = current();
		if (cur == null) return null;
		ensureloaded(cur);
		return cur.Viewer;
	}

	void zoomin() {
		currentviewer()?.ZoomIn();
		updatestatus();
	}

	void zoomout() {
		currentviewer()?.ZoomOut();
		updatestatus();
	}

	void setzoom(double z) {
		currentviewer()?.SetZoom(z);
		updatestatus();
	}

	void fitwidth() {
		currentviewer()?.ZoomFitWidth();
		updatestatus();
	}

	void fitpage() {
		currentviewer()?.ZoomFitPage();
		updatestatus();
	}

	/// <summary>旋转当前文档视图；deltaQuarterTurns&gt;0 顺时针。</summary>
	void rotateview(int deltaQuarterTurns) {
		currentviewer()?.RotateBy(deltaQuarterTurns);
		updatestatus();
	}

	/// <summary>F4 / 工具栏：切换主窗左侧栏（文件夹 + 目录）。</summary>
	void toggleside() {
		leftSideVisible = !leftSideVisible;
		applyleftsideui();
		// 侧栏重新打开时补建一次 TOC（关闭期间切文档不会刷新）
		if (leftSideVisible) rebuildmainoutline();
		persisttabs();
	}

	void applyleftsideui() {
		try {
			if (colleft != null)
				colleft.Width = leftSideVisible ? new GridLength(260) : new GridLength(0);
			if (colleftsplit != null)
				colleftsplit.Width = leftSideVisible ? new GridLength(4) : new GridLength(0);
			if (pleft != null)
				pleft.Visibility = leftSideVisible ? Visibility.Visible : Visibility.Collapsed;
			if (spleft != null)
				spleft.Visibility = leftSideVisible ? Visibility.Visible : Visibility.Collapsed;
			if (mnside != null) mnside.IsChecked = leftSideVisible;
			if (bside != null) {
				bside.IsChecked = leftSideVisible;
				bside.ToolTip = leftSideVisible ? "隐藏侧栏 (F4)" : "显示侧栏 (F4)";
			}
		} catch { /* ignore */ }
	}

	// ---------- 书签栏（Chrome 风格） ----------
	const string BookmarkDragFormat = "DocviewWPF.BookmarkId";
	Brush bookmarkBarBgNormal;
	/// <summary>当前拖入高亮的分组芯片。</summary>
	Button bookmarkDropGroupBtn;
	bool bookmarkChipDragSuppressClick;
	/// <summary>当前排序插入下标（-1=隐藏）。</summary>
	int bookmarkInsertIndex = -1;
	/// <summary>分组弹出面板（可拖排序 / 移入移出）。</summary>
	Popup bookmarkGroupPopup;
	StackPanel pgrouppopupitems;
	TextBlock lbgrouppopuptitle;
	Button bgrouppopupback;
	/// <summary>当前弹出的分组；拖排序目标列表为该分组 Children。</summary>
	BookmarkNode groupPopupNode;
	/// <summary>分组弹层内的插入线。</summary>
	Border groupPopupInsertMark;
	/// <summary>重锚弹层时会先关再开，避免 Closed 清空 groupPopupNode。</summary>
	bool reattachingGroupPopup;

	void initbookmarks() {
		if (pbookmarks != null) {
			bookmarkBarBgNormal = pbookmarks.Background;
			// Preview 事件覆盖子控件，空白与芯片上方均可拖入
			pbookmarks.AllowDrop = true;
			pbookmarks.PreviewDragEnter += onbookmarkbardragenter;
			pbookmarks.PreviewDragOver += onbookmarkbardragover;
			pbookmarks.PreviewDragLeave += onbookmarkbardragleave;
			pbookmarks.PreviewDrop += onbookmarkbardrop;
			// 空白处右键：添加分组 / 添加书签（芯片有自己的 ContextMenu）
			pbookmarks.ContextMenu = buildbookmarkbarctx();
		}
		// 芯片缝隙 / 空白处也要更新插入线（不仅依赖窗口级路由）
		foreach (var el in new UIElement[] { pbookmarklayer, pbookmarkitems, svbookmarks }) {
			if (el == null) continue;
			try {
				el.AllowDrop = true;
				el.PreviewDragOver -= onbookmarkbardragover;
				el.PreviewDragOver += onbookmarkbardragover;
				el.PreviewDrop -= onbookmarkbardrop;
				el.PreviewDrop += onbookmarkbardrop;
			} catch { /* ignore */ }
		}
		if (pbookmarkinsertlayer != null)
			Panel.SetZIndex(pbookmarkinsertlayer, 100);
		if (bbookmarkmore != null) {
			bbookmarkmore.Click += (_, e) => {
				var cm = buildbookmarkmoremenu();
				cm.PlacementTarget = bbookmarkmore;
				cm.IsOpen = true;
				e.Handled = true;
			};
		}
		ensuregrouppopup();
		refreshbookmarksbar();
	}

	void ensuregrouppopup() {
		if (bookmarkGroupPopup != null) return;
		bgrouppopupback = new Button {
			Content = "←",
			Width = 28,
			Height = 22,
			Margin = new Thickness(0, 0, 4, 0),
			Padding = new Thickness(0),
			ToolTip = "返回上级分组",
			Cursor = Cursors.Hand,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			FontSize = 13,
			Visibility = Visibility.Collapsed,
		};
		bgrouppopupback.Click += (_, _) => {
			if (groupPopupNode == null) return;
			var pid = BookmarksStore.GetParentId(groupPopupNode.Id);
			if (string.IsNullOrEmpty(pid)) {
				// 已在顶层分组，关闭
				closegrouppopup();
				return;
			}
			var parent = BookmarksStore.FindById(pid);
			if (parent != null && parent.Kind == BookmarkKind.Group)
				opengrouppopup(parent, bookmarkGroupPopup.PlacementTarget as UIElement);
			else
				closegrouppopup();
		};
		lbgrouppopuptitle = new TextBlock {
			FontSize = 12,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 200,
		};
		var titleBar = new DockPanel { Margin = new Thickness(6, 4, 6, 2) };
		DockPanel.SetDock(bgrouppopupback, Dock.Left);
		titleBar.Children.Add(bgrouppopupback);
		titleBar.Children.Add(lbgrouppopuptitle);

		pgrouppopupitems = new StackPanel {
			Orientation = Orientation.Vertical,
			Margin = new Thickness(4, 0, 4, 4),
			MinWidth = 160,
			MaxWidth = 280,
		};
		pgrouppopupitems.AllowDrop = true;
		pgrouppopupitems.PreviewDragOver += ongrouppopupdragover;
		pgrouppopupitems.PreviewDrop += ongrouppopupdrop;
		pgrouppopupitems.PreviewDragLeave += (_, e) => {
			// 离开弹层内容时清插入线
			try {
				var pos = e.GetPosition(pgrouppopupitems);
				if (pos.X < 0 || pos.Y < 0
					|| pos.X > pgrouppopupitems.ActualWidth
					|| pos.Y > pgrouppopupitems.ActualHeight)
					hidegrouppopupinsertmark();
			} catch { hidegrouppopupinsertmark(); }
		};

		var dock = new DockPanel { LastChildFill = true };
		DockPanel.SetDock(titleBar, Dock.Top);
		var sep = new Border {
			Height = 1,
			Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
			Margin = new Thickness(4, 0, 4, 2),
		};
		DockPanel.SetDock(sep, Dock.Top);
		var scroll = new ScrollViewer {
			Content = pgrouppopupitems,
			MaxHeight = 320,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		dock.Children.Add(titleBar);
		dock.Children.Add(sep);
		dock.Children.Add(scroll);
		var body = new Border {
			Background = Brushes.White,
			BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(6),
			Padding = new Thickness(2),
			Child = dock,
			Effect = new System.Windows.Media.Effects.DropShadowEffect {
				BlurRadius = 8,
				ShadowDepth = 2,
				Opacity = 0.25,
				Color = Colors.Black,
			},
		};

		bookmarkGroupPopup = new Popup {
			AllowsTransparency = true,
			StaysOpen = false,
			Placement = PlacementMode.Bottom,
			Child = body,
			PopupAnimation = PopupAnimation.Fade,
		};
		bookmarkGroupPopup.Closed += (_, _) => {
			// 重锚时会短暂 IsOpen=false，勿清空状态
			if (reattachingGroupPopup) return;
			groupPopupNode = null;
			hidegrouppopupinsertmark();
		};
	}

	void opengrouppopup(BookmarkNode group, UIElement place) {
		if (group == null || group.Kind != BookmarkKind.Group) return;
		ensuregrouppopup();
		groupPopupNode = group;
		// 从弹层内点进子分组：不要改 PlacementTarget（否则旧芯片被销毁后弹层会飞到桌面）
		var placeInsidePopup = place != null && isunder(place, bookmarkGroupPopup?.Child as DependencyObject);
		if (!placeInsidePopup && place != null)
			bookmarkGroupPopup.PlacementTarget = place;
		else if (bookmarkGroupPopup.PlacementTarget == null)
			bookmarkGroupPopup.PlacementTarget = place ?? pbookmarks;
		rebuildgrouppopupcontent();
		// 强制重新测量定位（重锚标记防止 Closed 清状态）
		reattachingGroupPopup = true;
		try {
			bookmarkGroupPopup.IsOpen = false;
			bookmarkGroupPopup.IsOpen = true;
		} catch {
			try { bookmarkGroupPopup.IsOpen = true; } catch { /* ignore */ }
		} finally {
			reattachingGroupPopup = false;
		}
	}

	static bool isunder(DependencyObject child, DependencyObject root) {
		if (child == null || root == null) return false;
		var p = child;
		for (var i = 0; i < 24 && p != null; i++) {
			if (ReferenceEquals(p, root)) return true;
			p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p);
		}
		return false;
	}

	void closegrouppopup() {
		if (bookmarkGroupPopup != null)
			bookmarkGroupPopup.IsOpen = false;
		groupPopupNode = null;
		hidegrouppopupinsertmark();
	}

	void rebuildgrouppopupcontent() {
		if (pgrouppopupitems == null || groupPopupNode == null) return;
		pgrouppopupitems.Children.Clear();
		groupPopupInsertMark = null;
		// 重新从 store 取节点（移出后 Children 已变）
		var g = BookmarksStore.FindById(groupPopupNode.Id);
		if (g == null || g.Kind != BookmarkKind.Group) {
			closegrouppopup();
			return;
		}
		groupPopupNode = g;
		if (lbgrouppopuptitle != null)
			lbgrouppopuptitle.Text = "▾ " + (g.Title ?? "分组");
		var pid = BookmarksStore.GetParentId(g.Id);
		if (bgrouppopupback != null)
			bgrouppopupback.Visibility = string.IsNullOrEmpty(pid)
				? Visibility.Collapsed : Visibility.Visible;

		if (g.Children == null || g.Children.Count == 0) {
			pgrouppopupitems.Children.Add(new TextBlock {
				Text = "（空 · 可把书签拖入此处分组）",
				FontSize = 11,
				Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
				Margin = new Thickness(8, 6, 8, 6),
			});
			return;
		}
		foreach (var c in g.Children) {
			if (c == null) continue;
			pgrouppopupitems.Children.Add(makebookmarkchip(c, inGroupPopup: true));
		}
	}

	/// <summary>书签栏刷新后：弹层 PlacementTarget 可能已销毁，重新锚到根分组芯片。</summary>
	void reattachgrouppopupifopen() {
		if (bookmarkGroupPopup == null || groupPopupNode == null) return;
		var g = BookmarksStore.FindById(groupPopupNode.Id);
		if (g == null || g.Kind != BookmarkKind.Group) {
			closegrouppopup();
			return;
		}
		groupPopupNode = g;
		// 锚点：当前分组在根栏上的芯片，或向上找到根分组芯片
		var anchorId = findrootgroupid(g.Id);
		var place = findbookmarkchipbutton(anchorId) as UIElement
			?? findbookmarkchipbutton(g.Id) as UIElement
			?? pbookmarks;
		bookmarkGroupPopup.PlacementTarget = place;
		rebuildgrouppopupcontent();
		reattachingGroupPopup = true;
		try {
			bookmarkGroupPopup.HorizontalOffset = 0;
			bookmarkGroupPopup.VerticalOffset = 0;
			bookmarkGroupPopup.IsOpen = false;
			bookmarkGroupPopup.IsOpen = true;
		} catch {
			try { bookmarkGroupPopup.IsOpen = true; } catch { /* ignore */ }
		} finally {
			reattachingGroupPopup = false;
		}
	}

	/// <summary>沿父链找到位于书签栏根上的分组 Id。</summary>
	static string findrootgroupid(string id) {
		if (string.IsNullOrEmpty(id)) return id;
		var cur = id;
		for (var i = 0; i < 32; i++) {
			var p = BookmarksStore.GetParentId(cur);
			if (string.IsNullOrEmpty(p)) return cur;
			cur = p;
		}
		return id;
	}

	Button findbookmarkchipbutton(string id) {
		if (pbookmarkitems == null || string.IsNullOrEmpty(id)) return null;
		foreach (UIElement u in pbookmarkitems.Children) {
			if (u is Button b && b.Tag is BookmarkNode n
				&& string.Equals(n.Id, id, StringComparison.Ordinal))
				return b;
		}
		return null;
	}

	void togglebookmarksbar() {
		BookmarksStore.BarVisible = !BookmarksStore.BarVisible;
		refreshbookmarksbar();
		// 其它窗口同步
		foreach (var w in liveWindows.ToList()) {
			if (w == null || ReferenceEquals(w, this)) continue;
			try { w.refreshbookmarksbar(); } catch { /* ignore */ }
		}
	}

	void refreshbookmarksbar() {
		var popupOpen = bookmarkGroupPopup != null && bookmarkGroupPopup.IsOpen && groupPopupNode != null;
		var popupGroupId = popupOpen ? groupPopupNode.Id : null;

		var show = BookmarksStore.BarVisible;
		if (pbookmarks != null)
			pbookmarks.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		if (mnbookmarks != null)
			mnbookmarks.IsChecked = show;
		if (pbookmarkitems == null) return;
		pbookmarkitems.Children.Clear();
		bookmarkDropGroupBtn = null;
		bookmarkInsertIndex = -1;
		clearbookmarkdrophighlight();
		hidebookmarkinsertmark();
		if (!show) {
			if (popupOpen) closegrouppopup();
			return;
		}
		var n = 0;
		foreach (var item in BookmarksStore.Root) {
			if (item == null) continue;
			pbookmarkitems.Children.Add(makebookmarkchip(item));
			n++;
		}
		// 空栏时显示拖入提示
		if (lbbookmarkhint != null)
			lbbookmarkhint.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
		// 右键菜单挂在栏上（刷新后重挂，避免被清掉）
		if (pbookmarks != null)
			pbookmarks.ContextMenu = buildbookmarkbarctx();

		// 重建芯片后旧 PlacementTarget 已销毁，必须重锚，否则弹层飞到屏幕角落
		if (popupOpen && !string.IsNullOrEmpty(popupGroupId)) {
			groupPopupNode = BookmarksStore.FindById(popupGroupId);
			if (groupPopupNode != null && groupPopupNode.Kind == BookmarkKind.Group)
				reattachgrouppopupifopen();
			else
				closegrouppopup();
		}
	}

	/// <param name="inGroupPopup">true=分组弹层内纵向芯片。</param>
	FrameworkElement makebookmarkchip(BookmarkNode node, bool inGroupPopup = false) {
		var title = string.IsNullOrWhiteSpace(node.Title) ? "书签" : node.Title;
		var glyph = node.Kind == BookmarkKind.Group ? "▾ "
			: node.Kind == BookmarkKind.Folder ? "📁 "
			: "📄 ";
		var btn = new Button {
			Content = glyph + title,
			ToolTip = node.Kind == BookmarkKind.Group
				? title + "（分组 · 可拖入）\n拖动排序 · 点开子项"
				: title + "\n" + (node.Path ?? "") + "\n拖动排序 / 拖到分组上移入 / 拖到栏上移出",
			Height = inGroupPopup ? 26 : 22,
			Padding = new Thickness(inGroupPopup ? 10 : 8, 0, inGroupPopup ? 10 : 8, 0),
			Margin = inGroupPopup ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0),
			FontSize = 12,
			Cursor = Cursors.Hand,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			HorizontalContentAlignment = inGroupPopup ? HorizontalAlignment.Left : HorizontalAlignment.Center,
			HorizontalAlignment = inGroupPopup ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
			Foreground = TryFindResource("TextPrimary") as Brush
				?? new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
			MaxWidth = inGroupPopup ? 260 : 180,
			Tag = node,
			AllowDrop = true,
		};
		btn.Template = bookmarkchiptemplate();
		// 拖动排序 / 移入分组
		Point chipDragStart = default;
		var chipDragArmed = false;
		btn.PreviewMouseLeftButtonDown += (_, e) => {
			if (e.ChangedButton != MouseButton.Left) return;
			chipDragStart = e.GetPosition(null);
			chipDragArmed = true;
		};
		btn.PreviewMouseMove += (_, e) => {
			if (!chipDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
			var p = e.GetPosition(null);
			if (Math.Abs(p.X - chipDragStart.X) < 5 && Math.Abs(p.Y - chipDragStart.Y) < 5)
				return;
			chipDragArmed = false;
			try {
				// 拖时保持分组弹层不自动关
				if (bookmarkGroupPopup != null) bookmarkGroupPopup.StaysOpen = true;
				var data = new DataObject(BookmarkDragFormat, node.Id ?? "");
				bookmarkChipDragSuppressClick = true;
				DragDrop.DoDragDrop(btn, data, DragDropEffects.Move);
			} catch (Exception ex) {
				DocLog.Warn($"bookmark chip drag: {ex.Message}");
			} finally {
				if (bookmarkGroupPopup != null) bookmarkGroupPopup.StaysOpen = false;
				Dispatcher.BeginInvoke(new Action(() => bookmarkChipDragSuppressClick = false),
					DispatcherPriority.Input);
			}
		};
		btn.PreviewMouseLeftButtonUp += (_, _) => { chipDragArmed = false; };
		btn.Click += (_, e) => {
			if (bookmarkChipDragSuppressClick) {
				e.Handled = true;
				return;
			}
			if (node.Kind == BookmarkKind.Group) {
				opengrouppopup(node, btn);
			} else {
				closegrouppopup();
				openbookmark(node);
			}
			e.Handled = true;
		};
		// 拖到芯片上：分组中心=移入；两侧/普通芯片=目标位置插入线
		btn.PreviewDragOver += (_, e) => {
			if (!e.Data.GetDataPresent(BookmarkDragFormat)) return;
			var dragId = e.Data.GetData(BookmarkDragFormat) as string;
			if (string.IsNullOrEmpty(dragId) || string.Equals(dragId, node.Id, StringComparison.Ordinal))
				return;
			// 弹层内：整钮移入子分组
			if (node.Kind == BookmarkKind.Group && inGroupPopup) {
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
				setgroupdrophighlight(btn, true);
				hidegrouppopupinsertmark();
				return;
			}
			// 栏上分组：中心移入，两侧排序
			if (node.Kind == BookmarkKind.Group && !inGroupPopup) {
				var zone = hitbookmarkgroupzone(e);
				if (zone.btn == null || !ReferenceEquals(zone.btn, btn)) return;
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
				if (zone.intoGroup) {
					setgroupdrophighlight(btn, true);
					hidebookmarkinsertmark();
				} else {
					setgroupdrophighlight(btn, false);
					updatebookmarkbarinsertdragui(e);
				}
				hidegrouppopupinsertmark();
				return;
			}
			// 普通芯片：始终在目标缝隙显示插入提示
			e.Effects = DragDropEffects.Move;
			e.Handled = true;
			if (inGroupPopup) {
				hidebookmarkinsertmark();
				setgroupdrophighlight(null, false);
				showgrouppopupinsertmark(hitgrouppopupinsertindex(e));
			} else {
				updatebookmarkbarinsertdragui(e);
			}
		};
		btn.PreviewDragLeave += (_, _) => {
			if (ReferenceEquals(bookmarkDropGroupBtn, btn))
				setgroupdrophighlight(btn, false);
		};
		btn.PreviewDrop += (_, e) => {
			if (!e.Data.GetDataPresent(BookmarkDragFormat)) return;
			var dragId = e.Data.GetData(BookmarkDragFormat) as string;
			if (string.IsNullOrEmpty(dragId)) return;
			if (node.Kind != BookmarkKind.Group) return;
			// 栏上：仅中心区视为移入；两侧走栏级 drop 排序
			if (!inGroupPopup) {
				var zone = hitbookmarkgroupzone(e);
				if (zone.btn == null || !zone.intoGroup || !ReferenceEquals(zone.btn, btn))
					return; // 不 Handled，让 pbookmarks drop 做排序
			}
			e.Handled = true;
			e.Effects = DragDropEffects.Move;
			clearbookmarkdrophighlight();
			setgroupdrophighlight(btn, false);
			hidebookmarkinsertmark();
			hidegrouppopupinsertmark();
			if (BookmarksStore.Move(dragId, node.Id, -1)) {
				if (inGroupPopup && groupPopupNode != null) {
					rebuildgrouppopupcontent();
					broadcastbookmarksrefresh();
					if (bookmarkGroupPopup != null) bookmarkGroupPopup.IsOpen = true;
				} else {
					broadcastbookmarksrefresh();
					if (groupPopupNode != null && string.Equals(groupPopupNode.Id, node.Id, StringComparison.Ordinal))
						rebuildgrouppopupcontent();
				}
				if (lbstatus != null)
					lbstatus.Text = "已移入分组: " + (node.Title ?? "");
			}
		};
		btn.ContextMenu = buildbookmarkctx(node);
		return btn;
	}

	static ControlTemplate bookmarkchiptemplate() {
		var tmpl = new ControlTemplate(typeof(Button));
		var border = new FrameworkElementFactory(typeof(Border));
		border.Name = "bd";
		border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
		border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
		border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
		var cp = new FrameworkElementFactory(typeof(ContentPresenter));
		cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
		cp.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
		border.AppendChild(cp);
		tmpl.VisualTree = border;
		var t = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
		t.Setters.Add(new Setter(Border.BackgroundProperty,
			new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), "bd"));
		tmpl.Triggers.Add(t);
		return tmpl;
	}

	ContextMenu buildbookmarkctx(BookmarkNode node) {
		var cm = new ContextMenu();
		// 分组：管理菜单仅右键
		if (node.Kind == BookmarkKind.Group) {
			var madd = new MenuItem { Header = "在此分组添加…" };
			madd.Click += (_, _) => addoreditbookmarkdialog(null, node.Id);
			var meditg = new MenuItem { Header = "编辑分组…" };
			meditg.Click += (_, _) => addoreditbookmarkdialog(node, null);
			var mdelg = new MenuItem { Header = "删除分组" };
			mdelg.Click += (_, _) => {
				if (MessageBox.Show(this, "删除分组「" + node.Title + "」及其内容？", "书签",
					MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
				BookmarksStore.RemoveById(node.Id);
				broadcastbookmarksrefresh();
				if (groupPopupNode != null && string.Equals(groupPopupNode.Id, node.Id, StringComparison.Ordinal))
					closegrouppopup();
			};
			cm.Items.Add(madd);
			cm.Items.Add(meditg);
			cm.Items.Add(mdelg);
			return cm;
		}
		var mopen2 = new MenuItem { Header = "打开" };
		mopen2.Click += (_, _) => openbookmark(node);
		cm.Items.Add(mopen2);
		var parentId = BookmarksStore.GetParentId(node.Id);
		if (!string.IsNullOrEmpty(parentId)) {
			var mout = new MenuItem { Header = "移出到书签栏" };
			mout.Click += (_, _) => {
				if (BookmarksStore.Move(node.Id, null, -1)) {
					broadcastbookmarksrefresh();
					if (groupPopupNode != null) rebuildgrouppopupcontent();
					if (lbstatus != null) lbstatus.Text = "已移出到书签栏: " + (node.Title ?? "");
				}
			};
			cm.Items.Add(mout);
			// 移到上级分组（若有祖父）
			var grandId = BookmarksStore.GetParentId(parentId);
			if (!string.IsNullOrEmpty(grandId)) {
				var grand = BookmarksStore.FindById(grandId);
				var mup = new MenuItem { Header = "移到上级: " + (grand?.Title ?? "分组") };
				mup.Click += (_, _) => {
					if (BookmarksStore.Move(node.Id, grandId, -1)) {
						broadcastbookmarksrefresh();
						if (groupPopupNode != null) rebuildgrouppopupcontent();
						if (lbstatus != null) lbstatus.Text = "已移到上级分组";
					}
				};
				cm.Items.Add(mup);
			}
		}
		// 移入其它分组（含子分组）
		var groups = new List<BookmarkNode>();
		foreach (var g in BookmarksStore.EnumerateGroups()) {
			if (g == null) continue;
			if (string.Equals(g.Id, node.Id, StringComparison.Ordinal)) continue;
			if (string.Equals(g.Id, parentId, StringComparison.Ordinal)) continue;
			groups.Add(g);
		}
		if (groups.Count > 0) {
			var mmove = new MenuItem { Header = "移入分组" };
			foreach (var g in groups) {
				var mi = new MenuItem { Header = g.Title ?? "分组" };
				var gid = g.Id;
				mi.Click += (_, _) => {
					if (BookmarksStore.Move(node.Id, gid, -1)) {
						broadcastbookmarksrefresh();
						if (groupPopupNode != null) rebuildgrouppopupcontent();
						if (lbstatus != null)
							lbstatus.Text = "已移入分组: " + (g.Title ?? "");
					}
				};
				mmove.Items.Add(mi);
			}
			cm.Items.Add(mmove);
		}
		var medit = new MenuItem { Header = "编辑…" };
		medit.Click += (_, _) => addoreditbookmarkdialog(node, null);
		var mdel = new MenuItem { Header = "删除" };
		mdel.Click += (_, _) => {
			BookmarksStore.RemoveById(node.Id);
			broadcastbookmarksrefresh();
			if (groupPopupNode != null) rebuildgrouppopupcontent();
		};
		cm.Items.Add(medit);
		cm.Items.Add(mdel);
		return cm;
	}

	ContextMenu buildbookmarkmoremenu() {
		var cm = new ContextMenu();
		var madd = new MenuItem { Header = "添加书签… (Ctrl+D)" };
		madd.Click += (_, _) => addoreditbookmarkdialog();
		var mgrp = new MenuItem { Header = "新建分组…" };
		mgrp.Click += (_, _) => newbookmarkgroup();
		var mtog = new MenuItem {
			Header = BookmarksStore.BarVisible ? "隐藏书签栏" : "显示书签栏",
			InputGestureText = "Ctrl+Shift+B",
		};
		mtog.Click += (_, _) => togglebookmarksbar();
		cm.Items.Add(madd);
		cm.Items.Add(mgrp);
		cm.Items.Add(new Separator());
		cm.Items.Add(mtog);
		return cm;
	}

	/// <summary>书签栏空白处右键菜单。</summary>
	ContextMenu buildbookmarkbarctx() {
		var cm = new ContextMenu();
		var mgrp = new MenuItem { Header = "新建分组…" };
		mgrp.Click += (_, _) => newbookmarkgroup();
		var madd = new MenuItem { Header = "添加书签… (Ctrl+D)" };
		madd.Click += (_, _) => addoreditbookmarkdialog();
		var mdrop = new MenuItem { Header = "提示：可拖入文件/文件夹到此栏", IsEnabled = false };
		cm.Items.Add(mgrp);
		cm.Items.Add(madd);
		cm.Items.Add(new Separator());
		cm.Items.Add(mdrop);
		return cm;
	}

	void openbookmark(BookmarkNode node) {
		if (node == null) return;
		try {
			if (node.Kind == BookmarkKind.Folder) {
				var dir = node.Path;
				if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
					MessageBox.Show(this, "文件夹不存在:\n" + dir, "书签",
						MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}
				setworkspace(dir, rebuild: true);
				if (!leftSideVisible) {
					leftSideVisible = true;
					applyleftsideui();
				}
				if (sideTabs != null) sideTabs.SelectedIndex = 0;
				if (lbstatus != null) lbstatus.Text = "已打开文件夹: " + dir;
				return;
			}
			if (node.Kind == BookmarkKind.File) {
				var path = node.Path;
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
					MessageBox.Show(this, "文件不存在:\n" + path, "书签",
						MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}
				openpath(path, loadNow: true, preview: false);
			}
		} catch (Exception ex) {
			DocLog.Warn($"openbookmark: {ex.Message}");
			MessageBox.Show(this, "打开书签失败: " + ex.Message, "书签",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void newbookmarkgroup() {
		var title = promptname("新建书签分组", "新建分组");
		if (string.IsNullOrWhiteSpace(title)) return;
		BookmarksStore.AddRoot(BookmarkNode.NewGroup(title.Trim()));
		broadcastbookmarksrefresh();
		if (!BookmarksStore.BarVisible) {
			BookmarksStore.BarVisible = true;
			broadcastbookmarksrefresh();
		}
	}

	void broadcastbookmarksrefresh() {
		foreach (var w in liveWindows.ToList()) {
			try { w?.refreshbookmarksbar(); } catch { /* ignore */ }
		}
	}

	bool isbookmarkfiledrop(DragEventArgs e) {
		try {
			return e?.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
		} catch {
			return false;
		}
	}

	bool isbookmarkinternaldrag(DragEventArgs e) {
		try {
			return e?.Data != null && e.Data.GetDataPresent(BookmarkDragFormat);
		} catch {
			return false;
		}
	}

	void onbookmarkbardragenter(object sender, DragEventArgs e) {
		if (isbookmarkinternaldrag(e)) {
			e.Effects = DragDropEffects.Move;
			e.Handled = true;
			updatebookmarkbarinsertdragui(e);
			return;
		}
		if (!isbookmarkfiledrop(e)) {
			e.Effects = DragDropEffects.None;
			return;
		}
		e.Effects = DragDropEffects.Copy;
		e.Handled = true;
		setbookmarkdrophighlight(true);
	}

	void onbookmarkbardragover(object sender, DragEventArgs e) {
		// 内部排序 / 移入分组 / 从分组移出到栏
		if (isbookmarkinternaldrag(e)) {
			e.Effects = DragDropEffects.Move;
			e.Handled = true;
			updatebookmarkbarinsertdragui(e);
			return;
		}
		if (!isbookmarkfiledrop(e)) {
			e.Effects = DragDropEffects.None;
			e.Handled = true;
			return;
		}
		e.Effects = DragDropEffects.Copy;
		e.Handled = true;
		hidebookmarkinsertmark();
		hidegrouppopupinsertmark();
		setbookmarkdrophighlight(true);
	}

	void onbookmarkbardragleave(object sender, DragEventArgs e) {
		// 仅真正移出书签栏时取消高亮（在子元素间移动会误触发 Leave）
		if (pbookmarks == null) {
			clearbookmarkdrophighlight();
			hidebookmarkinsertmark();
			setgroupdrophighlight(null, false);
			return;
		}
		try {
			var pos = e.GetPosition(pbookmarks);
			const double pad = 2;
			if (pos.X < -pad || pos.Y < -pad
				|| pos.X > pbookmarks.ActualWidth + pad
				|| pos.Y > pbookmarks.ActualHeight + pad) {
				clearbookmarkdrophighlight();
				hidebookmarkinsertmark();
				setgroupdrophighlight(null, false);
			}
		} catch {
			clearbookmarkdrophighlight();
			hidebookmarkinsertmark();
			setgroupdrophighlight(null, false);
		}
	}

	void setbookmarkdrophighlight(bool on) {
		try {
			if (pbookmarkdropglow != null)
				pbookmarkdropglow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
			if (lbbookmarkdroplabel != null) {
				lbbookmarkdroplabel.Text = "松开以添加书签";
				lbbookmarkdroplabel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
			}
			if (pbookmarks != null) {
				if (on) {
					pbookmarks.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF));
					pbookmarks.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
				} else {
					pbookmarks.Background = bookmarkBarBgNormal
						?? (TryFindResource("BgToolbar") as Brush)
						?? new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
					pbookmarks.BorderBrush = TryFindResource("BorderSoft") as Brush
						?? new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
				}
			}
			// 拖入文件时略淡化已有芯片
			if (pbookmarkitems != null && !isbookmarkreorderui())
				pbookmarkitems.Opacity = on ? 0.35 : 1.0;
			if (!on && pbookmarkitems != null)
				pbookmarkitems.Opacity = 1.0;
			if (lbbookmarkhint != null && !on && pbookmarkitems != null)
				lbbookmarkhint.Visibility = pbookmarkitems.Children.Count == 0
					? Visibility.Visible : Visibility.Collapsed;
			if (on && lbbookmarkhint != null)
				lbbookmarkhint.Visibility = Visibility.Collapsed;
		} catch { /* ignore */ }
	}

	bool isbookmarkreorderui() => bookmarkInsertIndex >= 0
		&& bbookmarkinsert != null
		&& bbookmarkinsert.Visibility == Visibility.Visible;

	void clearbookmarkdrophighlight() {
		setbookmarkdrophighlight(false);
	}

	void setgroupdrophighlight(Button btn, bool on) {
		try {
			if (bookmarkDropGroupBtn != null && !ReferenceEquals(bookmarkDropGroupBtn, btn)) {
				bookmarkDropGroupBtn.Background = Brushes.Transparent;
				bookmarkDropGroupBtn = null;
			}
			if (btn == null || !on) {
				if (bookmarkDropGroupBtn != null) {
					bookmarkDropGroupBtn.Background = Brushes.Transparent;
					bookmarkDropGroupBtn = null;
				}
				return;
			}
			btn.Background = new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE));
			bookmarkDropGroupBtn = btn;
		} catch { /* ignore */ }
	}

	/// <summary>根列表插入下标（按芯片水平中点；不含插入线）。</summary>
	int hitbookmarkinsertindex(DragEventArgs e) {
		if (pbookmarkitems == null) return 0;
		try {
			var x = e.GetPosition(pbookmarkitems).X;
			var idx = 0;
			foreach (UIElement u in pbookmarkitems.Children) {
				if (u is not Button b || b.Tag is not BookmarkNode) continue;
				var mid = b.TranslatePoint(new Point(b.ActualWidth * 0.5, 0), pbookmarkitems).X;
				if (x < mid) return idx;
				idx++;
			}
			return idx;
		} catch {
			return countbookmarkchips();
		}
	}

	int countbookmarkchips() {
		if (pbookmarkitems == null) return 0;
		var n = 0;
		foreach (UIElement u in pbookmarkitems.Children)
			if (u is Button b && b.Tag is BookmarkNode) n++;
		return n;
	}

	Button hitbookmarkgroupchip(DragEventArgs e) {
		var z = hitbookmarkgroupzone(e);
		return z.intoGroup ? z.btn : null;
	}

	/// <summary>在芯片缝隙处显示绝对定位蓝线（相对 Canvas 坐标系，不挤动芯片）。</summary>
	void showbookmarkinsertmark(int index) {
		if (pbookmarkitems == null || bbookmarkinsert == null) return;
		try {
			// 必须相对插入层 Canvas 算坐标；勿写死 Width（会破坏 Stretch 对齐）
			var canvas = pbookmarkinsertlayer;
			FrameworkElement refEl = canvas
				?? (FrameworkElement)pbookmarklayer
				?? pbookmarkitems;
			if (canvas != null) {
				// 与 pbookmarklayer 同格铺满，原点对齐
				canvas.ClearValue(FrameworkElement.WidthProperty);
				canvas.ClearValue(FrameworkElement.HeightProperty);
				canvas.HorizontalAlignment = HorizontalAlignment.Stretch;
				canvas.VerticalAlignment = VerticalAlignment.Stretch;
			}

			var chips = new List<Button>();
			foreach (UIElement u in pbookmarkitems.Children) {
				if (u is Button b && b.Tag is BookmarkNode)
					chips.Add(b);
			}
			if (index < 0) index = 0;
			if (index > chips.Count) index = chips.Count;

			double markH = 22;
			if (chips.Count > 0 && chips[0].ActualHeight > 1)
				markH = Math.Max(18, chips[0].ActualHeight - 2);
			bbookmarkinsert.Height = markH;
			bbookmarkinsert.Width = 3;

			double x;
			double y = 1;
			if (chips.Count == 0) {
				x = 6;
				y = Math.Max(1, ((pbookmarklayer?.ActualHeight ?? 24) - markH) * 0.5);
			} else if (index <= 0) {
				var tl = chips[0].TranslatePoint(new Point(0, 0), refEl);
				x = tl.X - 2;
				y = tl.Y + Math.Max(0, (chips[0].ActualHeight - markH) * 0.5);
			} else if (index >= chips.Count) {
				var last = chips[chips.Count - 1];
				var tr = last.TranslatePoint(new Point(last.ActualWidth, 0), refEl);
				x = tr.X + 1;
				y = tr.Y + Math.Max(0, (last.ActualHeight - markH) * 0.5);
			} else {
				var left = chips[index - 1];
				var right = chips[index];
				var a = left.TranslatePoint(new Point(left.ActualWidth, 0), refEl);
				var b = right.TranslatePoint(new Point(0, 0), refEl);
				x = (a.X + b.X) * 0.5 - 1.5;
				y = a.Y + Math.Max(0, (left.ActualHeight - markH) * 0.5);
			}
			if (double.IsNaN(x) || double.IsInfinity(x)) x = 0;
			if (double.IsNaN(y) || double.IsInfinity(y)) y = 1;
			if (x < -2) x = -2;

			bookmarkInsertIndex = index;
			bbookmarkinsert.Visibility = Visibility.Visible;
			Canvas.SetLeft(bbookmarkinsert, x);
			Canvas.SetTop(bbookmarkinsert, y);
			// 提到最前，避免被芯片盖住（Canvas 与 StackPanel 同层时靠声明顺序，再强制一次）
			if (canvas != null)
				Panel.SetZIndex(canvas, 100);
			Panel.SetZIndex(bbookmarkinsert, 101);
		} catch (Exception ex) {
			DocLog.Warn($"showbookmarkinsertmark: {ex.Message}");
		}
	}

	void hidebookmarkinsertmark() {
		try {
			bookmarkInsertIndex = -1;
			if (bbookmarkinsert != null)
				bbookmarkinsert.Visibility = Visibility.Collapsed;
		} catch { /* ignore */ }
	}

	// ----- 分组弹层内拖排序 -----
	void ongrouppopupdragover(object sender, DragEventArgs e) {
		// 鼠标已到书签栏：弹层不要再画插入线（由栏上显示）
		if (isoverbookmarkbar(e)) {
			hidegrouppopupinsertmark();
			if (isbookmarkinternaldrag(e)) {
				e.Effects = DragDropEffects.Move;
				e.Handled = true;
				updatebookmarkbarinsertdragui(e);
			}
			return;
		}
		if (!isbookmarkinternaldrag(e)) {
			if (isbookmarkfiledrop(e)) {
				e.Effects = DragDropEffects.Copy;
				e.Handled = true;
			}
			return;
		}
		e.Effects = DragDropEffects.Move;
		e.Handled = true;
		// 弹层内排序：清掉栏上的插入线
		hidebookmarkinsertmark();
		// 落在子分组芯片上由 chip 处理
		var overGroup = hitgrouppopupgroupchip(e);
		if (overGroup != null) {
			hidegrouppopupinsertmark();
			return;
		}
		setgroupdrophighlight(null, false);
		var idx = hitgrouppopupinsertindex(e);
		showgrouppopupinsertmark(idx);
	}

	void ongrouppopupdrop(object sender, DragEventArgs e) {
		try {
			if (groupPopupNode == null) return;
			// 文件拖入当前分组
			if (isbookmarkfiledrop(e)) {
				var files = e.Data.GetData(DataFormats.FileDrop) as string[];
				if (files == null) return;
				e.Handled = true;
				var added = 0;
				foreach (var raw in files) {
					if (string.IsNullOrWhiteSpace(raw)) continue;
					var p = raw.Trim().Trim('"');
					try {
						var r = ShellLink.Resolve(p);
						if (!string.IsNullOrWhiteSpace(r)) p = r;
					} catch { /* ignore */ }
					BookmarkNode node = null;
					if (Directory.Exists(p))
						node = BookmarkNode.NewFolder(p);
					else if (File.Exists(p))
						node = BookmarkNode.NewFile(p);
					if (node == null) continue;
					if (BookmarksStore.FindByPath(p) != null) continue;
					addbookmarktonode(node, groupPopupNode.Id);
					added++;
				}
				if (added > 0) {
					rebuildgrouppopupcontent();
					broadcastbookmarksrefresh();
					if (lbstatus != null) lbstatus.Text = $"已向分组添加 {added} 个书签";
				}
				return;
			}
			if (!isbookmarkinternaldrag(e)) return;
			var dragId = e.Data.GetData(BookmarkDragFormat) as string;
			if (string.IsNullOrEmpty(dragId)) return;
			e.Handled = true;
			e.Effects = DragDropEffects.Move;
			// 落在子分组上
			var grpBtn = hitgrouppopupgroupchip(e);
			if (grpBtn?.Tag is BookmarkNode g && g.Kind == BookmarkKind.Group) {
				hidegrouppopupinsertmark();
				setgroupdrophighlight(null, false);
				if (BookmarksStore.Move(dragId, g.Id, -1)) {
					rebuildgrouppopupcontent();
					broadcastbookmarksrefresh();
					if (lbstatus != null) lbstatus.Text = "已移入分组: " + (g.Title ?? "");
				}
				return;
			}
			var idx = hitgrouppopupinsertindex(e);
			hidegrouppopupinsertmark();
			if (BookmarksStore.Move(dragId, groupPopupNode.Id, idx)) {
				rebuildgrouppopupcontent();
				broadcastbookmarksrefresh();
				if (lbstatus != null) lbstatus.Text = "分组内已排序";
			}
		} catch (Exception ex) {
			DocLog.Warn($"group popup drop: {ex.Message}");
			hidegrouppopupinsertmark();
		}
	}

	int hitgrouppopupinsertindex(DragEventArgs e) {
		if (pgrouppopupitems == null) return 0;
		try {
			var y = e.GetPosition(pgrouppopupitems).Y;
			var idx = 0;
			foreach (UIElement u in pgrouppopupitems.Children) {
				if (u is not FrameworkElement fe) continue;
				if (ReferenceEquals(fe, groupPopupInsertMark)) continue;
				if (u is TextBlock) continue; // 空提示
				var mid = fe.TranslatePoint(new Point(0, fe.ActualHeight * 0.5), pgrouppopupitems).Y;
				if (y < mid) return idx;
				idx++;
			}
			return idx;
		} catch {
			return 0;
		}
	}

	Button hitgrouppopupgroupchip(DragEventArgs e) {
		if (pgrouppopupitems == null) return null;
		try {
			var pos = e.GetPosition(pgrouppopupitems);
			foreach (UIElement u in pgrouppopupitems.Children) {
				if (u is not Button b || b.Tag is not BookmarkNode n) continue;
				if (n.Kind != BookmarkKind.Group) continue;
				var tl = b.TranslatePoint(new Point(0, 0), pgrouppopupitems);
				if (pos.X >= tl.X && pos.X <= tl.X + b.ActualWidth
					&& pos.Y >= tl.Y && pos.Y <= tl.Y + b.ActualHeight)
					return b;
			}
		} catch { /* ignore */ }
		return null;
	}

	void showgrouppopupinsertmark(int index) {
		if (pgrouppopupitems == null) return;
		try {
			if (groupPopupInsertMark == null) {
				groupPopupInsertMark = new Border {
					Height = 3,
					Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
					CornerRadius = new CornerRadius(1.5),
					Margin = new Thickness(4, 1, 4, 1),
					HorizontalAlignment = HorizontalAlignment.Stretch,
					IsHitTestVisible = false,
				};
			}
			if (pgrouppopupitems.Children.Contains(groupPopupInsertMark))
				pgrouppopupitems.Children.Remove(groupPopupInsertMark);
			var chipCount = 0;
			var visualIndex = pgrouppopupitems.Children.Count;
			for (var i = 0; i < pgrouppopupitems.Children.Count; i++) {
				var u = pgrouppopupitems.Children[i];
				if (ReferenceEquals(u, groupPopupInsertMark)) continue;
				if (u is TextBlock) continue;
				if (chipCount == index) {
					visualIndex = i;
					break;
				}
				chipCount++;
			}
			if (index >= chipCount)
				pgrouppopupitems.Children.Add(groupPopupInsertMark);
			else
				pgrouppopupitems.Children.Insert(visualIndex, groupPopupInsertMark);
			groupPopupInsertMark.Visibility = Visibility.Visible;
		} catch { /* ignore */ }
	}

	void hidegrouppopupinsertmark() {
		try {
			if (groupPopupInsertMark == null) return;
			groupPopupInsertMark.Visibility = Visibility.Collapsed;
			if (pgrouppopupitems != null && pgrouppopupitems.Children.Contains(groupPopupInsertMark))
				pgrouppopupitems.Children.Remove(groupPopupInsertMark);
		} catch { /* ignore */ }
	}

	void onbookmarkbardrop(object sender, DragEventArgs e) {
		try {
			// 内部：排序 / 移到根
			if (isbookmarkinternaldrag(e)) {
				var dragId = e.Data.GetData(BookmarkDragFormat) as string;
				e.Handled = true;
				e.Effects = DragDropEffects.Move;
				// 优先：落在分组上（chip PreviewDrop 可能已处理；此处兜底）
				var grpBtn = hitbookmarkgroupchip(e);
				if (grpBtn?.Tag is BookmarkNode g && g.Kind == BookmarkKind.Group) {
					var dragNode = BookmarksStore.FindById(dragId);
					if (dragNode != null && dragNode.Kind != BookmarkKind.Group
						&& BookmarksStore.Move(dragId, g.Id, -1)) {
						clearbookmarkdrophighlight();
						hidebookmarkinsertmark();
						setgroupdrophighlight(null, false);
						broadcastbookmarksrefresh();
						if (lbstatus != null)
							lbstatus.Text = "已移入分组: " + (g.Title ?? "");
						return;
					}
				}
				var idx = hitbookmarkinsertindex(e);
				// 插入线占位时 index 已是芯片序号
				hidebookmarkinsertmark();
				setgroupdrophighlight(null, false);
				clearbookmarkdrophighlight();
				if (!string.IsNullOrEmpty(dragId) && BookmarksStore.Move(dragId, null, idx)) {
					broadcastbookmarksrefresh();
					// 若从分组内拖出到栏上，刷新弹层
					if (groupPopupNode != null) rebuildgrouppopupcontent();
					if (lbstatus != null) {
						var stillInGroup = !string.IsNullOrEmpty(BookmarksStore.GetParentId(dragId));
						lbstatus.Text = stillInGroup ? "书签已排序" : "已移出到书签栏 / 已排序";
					}
				}
				return;
			}
			if (!isbookmarkfiledrop(e)) return;
			var files = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (files == null || files.Length == 0) return;
			e.Handled = true;
			e.Effects = DragDropEffects.Copy;
			addfilesasbookmarks(files);
		} catch (Exception ex) {
			DocLog.Warn($"bookmark drop: {ex.Message}");
			clearbookmarkdrophighlight();
			hidebookmarkinsertmark();
			setgroupdrophighlight(null, false);
		}
	}

	/// <summary>将拖入的路径批量加入书签栏（去重）。</summary>
	void addfilesasbookmarks(string[] files) {
		clearbookmarkdrophighlight();
		if (files == null || files.Length == 0) return;
		var added = 0;
		var skipped = 0;
		foreach (var raw in files) {
			if (string.IsNullOrWhiteSpace(raw)) continue;
			var p = raw.Trim().Trim('"');
			try {
				var resolved = ShellLink.Resolve(p);
				if (!string.IsNullOrWhiteSpace(resolved)) p = resolved;
			} catch { /* ignore */ }
			if (Directory.Exists(p)) {
				if (BookmarksStore.FindByPath(p) != null) { skipped++; continue; }
				BookmarksStore.AddRoot(BookmarkNode.NewFolder(p));
				added++;
			} else if (File.Exists(p)) {
				if (BookmarksStore.FindByPath(p) != null) { skipped++; continue; }
				BookmarksStore.AddRoot(BookmarkNode.NewFile(p));
				added++;
			}
		}
		if (added > 0) {
			if (!BookmarksStore.BarVisible) BookmarksStore.BarVisible = true;
			broadcastbookmarksrefresh();
		}
		if (lbstatus != null) {
			if (added > 0 && skipped > 0)
				lbstatus.Text = $"已添加 {added} 个书签（{skipped} 个已存在，已跳过）";
			else if (added > 0)
				lbstatus.Text = $"已添加 {added} 个书签";
			else if (skipped > 0)
				lbstatus.Text = "书签已存在，未重复添加";
			else
				lbstatus.Text = "没有可添加的文件或文件夹";
		}
	}

	/// <summary>
	/// Ctrl+D：添加或编辑书签。edit=已有节点；parentGroupId=新建到某分组。
	/// 默认取当前文档路径，或文件夹树选中项。
	/// </summary>
	void addoreditbookmarkdialog(BookmarkNode edit = null, string parentGroupId = null) {
		try {
			string defPath = null;
			string defTitle = null;
			var defKind = BookmarkKind.File;

			if (edit != null) {
				defPath = edit.Path;
				defTitle = edit.Title;
				defKind = edit.Kind;
			} else {
				// 优先文件夹树选中
				var sel = FolderTree.PathOf(treeFiles?.SelectedItem);
				if (!string.IsNullOrEmpty(sel)) {
					if (Directory.Exists(sel)) {
						defPath = sel;
						defKind = BookmarkKind.Folder;
					} else if (File.Exists(sel)) {
						defPath = sel;
						defKind = BookmarkKind.File;
					}
				}
				if (string.IsNullOrEmpty(defPath)) {
					var cur = current();
					if (cur != null && cur.Kind != DocKind.Browser && cur.Kind != DocKind.Console
						&& !string.IsNullOrEmpty(cur.Path) && File.Exists(cur.Path)) {
						defPath = cur.Path;
						defKind = BookmarkKind.File;
					}
				}
				if (string.IsNullOrEmpty(defPath) && !string.IsNullOrEmpty(workspaceFolder)
					&& Directory.Exists(workspaceFolder)) {
					defPath = workspaceFolder;
					defKind = BookmarkKind.Folder;
				}
				// 已有同路径书签 → 编辑
				if (!string.IsNullOrEmpty(defPath)) {
					var exist = BookmarksStore.FindByPath(defPath);
					if (exist != null) {
						edit = exist;
						defTitle = exist.Title;
						defKind = exist.Kind;
					}
				}
				if (string.IsNullOrEmpty(defTitle) && !string.IsNullOrEmpty(defPath)) {
					try {
						defTitle = Path.GetFileName(defPath.TrimEnd('\\', '/'));
						if (string.IsNullOrEmpty(defTitle)) defTitle = defPath;
					} catch { defTitle = defPath; }
				}
			}

			if (edit != null && edit.Kind == BookmarkKind.Group) {
				// 仅改分组名
				var t = promptname("编辑分组", edit.Title ?? "分组");
				if (string.IsNullOrWhiteSpace(t)) return;
				edit.Title = t.Trim();
				BookmarksStore.Save();
				broadcastbookmarksrefresh();
				return;
			}

			var dlg = new Window {
				Title = edit != null ? "编辑书签" : "添加书签",
				Width = 440,
				SizeToContent = SizeToContent.Height,
				MinHeight = 220,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Owner = this,
				ResizeMode = ResizeMode.NoResize,
				ShowInTaskbar = false,
				Background = Brushes.White,
			};
			var etitle = new TextBox {
				Text = defTitle ?? "",
				Height = 28,
				VerticalContentAlignment = VerticalAlignment.Center,
				Padding = new Thickness(6, 2, 6, 2),
				Margin = new Thickness(0, 0, 0, 10),
			};
			var epath = new TextBox {
				Text = defPath ?? "",
				Height = 28,
				VerticalContentAlignment = VerticalAlignment.Center,
				Padding = new Thickness(6, 2, 6, 2),
				Margin = new Thickness(0, 0, 0, 10),
			};
			var ckind = new ComboBox {
				Height = 28,
				Margin = new Thickness(0, 0, 0, 10),
			};
			ckind.Items.Add("文件");
			ckind.Items.Add("文件夹");
			ckind.SelectedIndex = defKind == BookmarkKind.Folder ? 1 : 0;

			// 父分组
			var cparent = new ComboBox {
				Height = 28,
				Margin = new Thickness(0, 0, 0, 10),
			};
			cparent.Items.Add(new BookmarkParentOpt { Id = null, Title = "书签栏（根）" });
			foreach (var g in BookmarksStore.EnumerateGroups()) {
				if (edit != null && string.Equals(g.Id, edit.Id, StringComparison.Ordinal))
					continue;
				cparent.Items.Add(new BookmarkParentOpt { Id = g.Id, Title = "分组: " + g.Title });
			}
			cparent.DisplayMemberPath = "Title";
			cparent.SelectedIndex = 0;
			if (!string.IsNullOrEmpty(parentGroupId)) {
				for (var i = 0; i < cparent.Items.Count; i++) {
					if (cparent.Items[i] is BookmarkParentOpt o
						&& string.Equals(o.Id, parentGroupId, StringComparison.Ordinal)) {
						cparent.SelectedIndex = i;
						break;
					}
				}
			}

			var bok = new Button { Content = "确定", Width = 72, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
			var bcancel = new Button { Content = "取消", Width = 72, Height = 26, IsCancel = true };
			var actions = new StackPanel {
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Margin = new Thickness(0, 8, 0, 0),
			};
			actions.Children.Add(bok);
			actions.Children.Add(bcancel);
			var body = new StackPanel { Margin = new Thickness(16) };
			body.Children.Add(new TextBlock { Text = "名称", FontSize = 12, Margin = new Thickness(0, 0, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
			body.Children.Add(etitle);
			body.Children.Add(new TextBlock { Text = "路径", FontSize = 12, Margin = new Thickness(0, 0, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
			body.Children.Add(epath);
			body.Children.Add(new TextBlock { Text = "类型", FontSize = 12, Margin = new Thickness(0, 0, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
			body.Children.Add(ckind);
			body.Children.Add(new TextBlock { Text = "位置", FontSize = 12, Margin = new Thickness(0, 0, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) });
			body.Children.Add(cparent);
			body.Children.Add(actions);
			dlg.Content = body;
			bok.Click += (_, _) => { dlg.DialogResult = true; };
			bcancel.Click += (_, _) => { dlg.DialogResult = false; };
			dlg.Loaded += (_, _) => { etitle.Focus(); etitle.SelectAll(); };
			if (dlg.ShowDialog() != true) return;

			var title = (etitle.Text ?? "").Trim();
			var path = (epath.Text ?? "").Trim().Trim('"');
			if (string.IsNullOrEmpty(path)) {
				MessageBox.Show(this, "请填写路径。", "书签", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			try { path = Path.GetFullPath(path); } catch { /* keep */ }
			var isFolder = ckind.SelectedIndex == 1;
			if (isFolder) {
				if (!Directory.Exists(path)) {
					MessageBox.Show(this, "文件夹不存在。", "书签", MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}
			} else if (!File.Exists(path)) {
				MessageBox.Show(this, "文件不存在。", "书签", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			if (string.IsNullOrEmpty(title)) {
				try {
					title = Path.GetFileName(path.TrimEnd('\\', '/'));
					if (string.IsNullOrEmpty(title)) title = path;
				} catch { title = path; }
			}

			var parentId = (cparent.SelectedItem as BookmarkParentOpt)?.Id;

			if (edit != null) {
				// 从原位置移除再插入目标
				BookmarksStore.RemoveById(edit.Id);
				edit.Title = title;
				edit.Path = path;
				edit.Kind = isFolder ? BookmarkKind.Folder : BookmarkKind.File;
				edit.Children = null;
				addbookmarktonode(edit, parentId);
			} else {
				var node = isFolder ? BookmarkNode.NewFolder(path, title) : BookmarkNode.NewFile(path, title);
				addbookmarktonode(node, parentId);
			}
			if (!BookmarksStore.BarVisible) BookmarksStore.BarVisible = true;
			broadcastbookmarksrefresh();
			if (lbstatus != null) lbstatus.Text = "已保存书签: " + title;
		} catch (Exception ex) {
			DocLog.Error("addoreditbookmark", ex);
			MessageBox.Show(this, "保存书签失败: " + ex.Message, "书签",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void addbookmarktonode(BookmarkNode node, string parentGroupId) {
		if (node == null) return;
		if (string.IsNullOrEmpty(parentGroupId)) {
			BookmarksStore.AddRoot(node);
			return;
		}
		var g = BookmarksStore.FindById(parentGroupId);
		if (g == null || g.Kind != BookmarkKind.Group) {
			BookmarksStore.AddRoot(node);
			return;
		}
		if (g.Children == null) g.Children = new List<BookmarkNode>();
		g.Children.Add(node);
		BookmarksStore.Save();
	}

	sealed class BookmarkParentOpt {
		public string Id;
		public string Title;
	}

	void syncsideui() {
		try {
			if (mnside != null) mnside.IsChecked = leftSideVisible;
			if (bside != null) bside.IsChecked = leftSideVisible;
		} catch { /* ignore */ }
		// 注意：不要在此 rebuildmainoutline。
		// StatusChanged（滚动/翻页）会频繁走 updatestatus→syncsideui，
		// 重建会清空树并重置 IsExpanded，表现为 TOC 自动展开/收起。
		// TOC 重建仅在：切文档/加载完成/筛选/切到章节 Tab（见各调用点）。
	}

	// ---------- 工作区文件夹 ----------
	void setexploreracts(bool show) {
		if (pexploreracts == null) return;
		pexploreracts.Opacity = show ? 1 : 0;
		pexploreracts.IsHitTestVisible = show;
	}

	void openfolder() {
		try {
			var dlg = new OpenFileDialog {
				Title = "选择工作区内任意文件以打开该文件夹",
				Filter = "所有文件|*.*",
				CheckFileExists = true,
				Multiselect = false,
			};
			if (!string.IsNullOrEmpty(workspaceFolder) && Directory.Exists(workspaceFolder))
				dlg.InitialDirectory = workspaceFolder;
			if (dlg.ShowDialog(this) != true) return;
			var dir = Path.GetDirectoryName(dlg.FileName);
			if (!string.IsNullOrEmpty(dir))
				setworkspace(dir, rebuild: true);
		} catch (Exception ex) {
			DocLog.Warn($"openfolder: {ex.Message}");
			MessageBox.Show(this, "打开文件夹失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>新建文件目标目录：选中文件夹 / 选中文件的父目录 / 工作区根。</summary>
	string workspacetargetdir() {
		if (string.IsNullOrEmpty(workspaceFolder) || !Directory.Exists(workspaceFolder))
			return null;
		var sel = FolderTree.PathOf(treeFiles?.SelectedItem);
		if (!string.IsNullOrEmpty(sel)) {
			if (Directory.Exists(sel)) return sel;
			if (File.Exists(sel)) {
				var p = Path.GetDirectoryName(sel);
				if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
			}
		}
		return workspaceFolder;
	}

	void newworkspacefile() {
		try {
			var dir = workspacetargetdir();
			if (string.IsNullOrEmpty(dir)) {
				openfolder();
				return;
			}
			var name = promptname("新建文件", "新建文件.md");
			if (string.IsNullOrWhiteSpace(name)) return;
			name = name.Trim();
			foreach (var c in Path.GetInvalidFileNameChars())
				if (name.IndexOf(c) >= 0) {
					MessageBox.Show(this, "文件名包含非法字符。", "DocviewWPF",
						MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}
			var full = Path.Combine(dir, name);
			if (File.Exists(full) || Directory.Exists(full)) {
				MessageBox.Show(this, "已存在同名项: " + name, "DocviewWPF",
					MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			File.WriteAllText(full, "", Encoding.UTF8);
			refreshfoldertree();
			if (DocKindUtil.FromPath(full) != DocKind.Unknown)
				openpath(full, loadNow: true, preview: false);
			if (lbstatus != null) lbstatus.Text = "已创建: " + name;
		} catch (Exception ex) {
			DocLog.Warn($"newworkspacefile: {ex.Message}");
			MessageBox.Show(this, "新建文件失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void newworkspacefolder() {
		try {
			var dir = workspacetargetdir();
			if (string.IsNullOrEmpty(dir)) {
				openfolder();
				return;
			}
			var name = promptname("新建文件夹", "新建文件夹");
			if (string.IsNullOrWhiteSpace(name)) return;
			name = name.Trim();
			foreach (var c in Path.GetInvalidFileNameChars())
				if (name.IndexOf(c) >= 0) {
					MessageBox.Show(this, "文件夹名包含非法字符。", "DocviewWPF",
						MessageBoxButton.OK, MessageBoxImage.Warning);
					return;
				}
			var full = Path.Combine(dir, name);
			if (File.Exists(full) || Directory.Exists(full)) {
				MessageBox.Show(this, "已存在同名项: " + name, "DocviewWPF",
					MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			Directory.CreateDirectory(full);
			refreshfoldertree();
			if (lbstatus != null) lbstatus.Text = "已创建文件夹: " + name;
		} catch (Exception ex) {
			DocLog.Warn($"newworkspacefolder: {ex.Message}");
			MessageBox.Show(this, "新建文件夹失败: " + ex.Message, "DocviewWPF",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void collapsefoldertree() {
		if (treeFiles == null) return;
		FolderTree.CollapseAll(treeFiles);
	}

	/// <summary>简易名称输入框（新建文件/文件夹）。</summary>
	string promptname(string title, string def) {
		var w = new Window {
			Title = title,
			Width = 380,
			SizeToContent = SizeToContent.Height,
			MinHeight = 168,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Background = Brushes.White,
		};
		var e = new TextBox {
			Text = def ?? "",
			Height = 28,
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(6, 2, 6, 2),
		};
		string result = null;
		var bok = new Button { Content = "确定", Width = 72, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
		var bcancel = new Button { Content = "取消", Width = 72, Height = 26, IsCancel = true };
		bok.Click += (_, _) => { result = e.Text; w.DialogResult = true; };
		bcancel.Click += (_, _) => { w.DialogResult = false; };
		var actions = new StackPanel {
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 12, 0, 0),
		};
		actions.Children.Add(bok);
		actions.Children.Add(bcancel);
		var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
		body.Children.Add(new TextBlock {
			Text = "名称",
			FontSize = 12,
			Margin = new Thickness(0, 0, 0, 6),
			Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
		});
		body.Children.Add(e);
		body.Children.Add(actions);
		w.Content = body;
		w.Loaded += (_, _) => { e.Focus(); e.SelectAll(); };
		return w.ShowDialog() == true ? result : null;
	}

	void setworkspace(string folder, bool rebuild) {
		if (string.IsNullOrWhiteSpace(folder)) return;
		try {
			folder = Path.GetFullPath(folder);
			if (!Directory.Exists(folder)) return;
			workspaceFolder = folder;
			if (lbworkspace != null) {
				var n = Path.GetFileName(folder.TrimEnd('\\', '/'));
				lbworkspace.Text = string.IsNullOrEmpty(n) ? folder : n;
				lbworkspace.ToolTip = folder;
			}
			if (rebuild) refreshfoldertree();
			if (leftSideVisible && sideTabs != null)
				sideTabs.SelectedIndex = 0;
			DocLog.Info($"workspace={folder}");
		} catch (Exception ex) {
			DocLog.Warn($"setworkspace: {ex.Message}");
		}
	}

	void trysetworkspacefromfile(string filePath) {
		if (!string.IsNullOrEmpty(workspaceFolder)) return;
		try {
			var dir = Path.GetDirectoryName(pathnorm(filePath) ?? filePath);
			if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
				setworkspace(dir, rebuild: true);
		} catch { /* ignore */ }
	}

	void refreshfoldertree() {
		if (treeFiles == null) return;
		FolderTree.LoadRoot(treeFiles, workspaceFolder);
		if (string.IsNullOrEmpty(workspaceFolder) && lbworkspace != null)
			lbworkspace.Text = "未打开文件夹";
	}

	void onfiletreedoubleclick(object sender, MouseButtonEventArgs e) {
		var path = FolderTree.PathOf(treeFiles?.SelectedItem);
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
		if (DocKindUtil.FromPath(path) == DocKind.Unknown) {
			if (lbstatus != null) lbstatus.Text = "不支持的类型: " + Path.GetFileName(path);
			return;
		}
		// 文件夹浏览：共用预览 Tab（斜体）
		openpath(path, loadNow: true, preview: true);
		e.Handled = true;
	}

	void onfiletreekeydown(object sender, KeyEventArgs e) {
		if (e.Key != Key.Enter) return;
		var path = FolderTree.PathOf(treeFiles?.SelectedItem);
		if (string.IsNullOrEmpty(path)) return;
		if (Directory.Exists(path) && treeFiles.SelectedItem is TreeViewItem tvi) {
			tvi.IsExpanded = !tvi.IsExpanded;
			e.Handled = true;
			return;
		}
		if (File.Exists(path) && DocKindUtil.FromPath(path) != DocKind.Unknown) {
			openpath(path, loadNow: true, preview: true);
			e.Handled = true;
		}
	}

	// ---------- 主窗章节列表 TOC ----------
	/// <summary>重建章节树；无章节时隐藏「章节列表」Tab。</summary>
	void rebuildmainoutline() {
		if (treeOutline == null) return;
		syncOutlineTree = true;
		var hasChapters = false;
		var anyVisible = false;
		try {
			treeOutline.Items.Clear();
			var q = eoutlinefilter?.Text?.Trim() ?? "";
			var v = currentviewer();
			if (v is MdViewer mv) {
				var text = mv.GetRawText() ?? "";
				var doc = MdParser.Parse(text);
				var stack = new List<TreeViewItem>();
				foreach (var b in doc.Blocks) {
					if (b == null || b.Kind != MdBlockKind.Heading) continue;
					hasChapters = true;
					var title = b.Text ?? "";
					if (!string.IsNullOrEmpty(q) && title.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
						continue;
					var item = new TreeViewItem {
						Header = OutlineUi.MakeHeader(title, "", q),
						Tag = b.SourceLine0,
						IsExpanded = b.Level <= 1,
						Padding = new Thickness(Math.Max(0, (b.Level - 1) * 12), 2, 4, 2),
					};
					while (stack.Count > 0 && stack.Count >= b.Level)
						stack.RemoveAt(stack.Count - 1);
					if (stack.Count == 0) treeOutline.Items.Add(item);
					else stack[stack.Count - 1].Items.Add(item);
					stack.Add(item);
					anyVisible = true;
				}
			} else if (v is PdfViewer pv) {
				var snap = pv.GetOutlineSnapshot();
				if (snap != null && snap.Count > 0) hasChapters = true;
				var stack = new List<TreeViewItem>();
				if (snap != null)
				foreach (var (title, depth, page1) in snap) {
					if (!string.IsNullOrEmpty(q) && (title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
						continue;
					var item = new TreeViewItem {
						Header = OutlineUi.MakeHeader(title ?? "", page1 > 0 ? $"p.{page1}" : "", q),
						Tag = page1,
						IsExpanded = depth == 0,
						Padding = new Thickness(Math.Max(0, depth * 12), 2, 4, 2),
					};
					while (stack.Count > 0 && stack.Count > depth)
						stack.RemoveAt(stack.Count - 1);
					if (stack.Count == 0) treeOutline.Items.Add(item);
					else stack[stack.Count - 1].Items.Add(item);
					stack.Add(item);
					anyVisible = true;
				}
			} else if (v is DocxViewer dv) {
				var snap = dv.GetOutlineSnapshot();
				if (snap != null && snap.Count > 0) hasChapters = true;
				var stack = new List<TreeViewItem>();
				if (snap != null)
				foreach (var (title, level, page1) in snap) {
					if (!string.IsNullOrEmpty(q) && (title ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
						continue;
					var item = new TreeViewItem {
						Header = OutlineUi.MakeHeader(title ?? "", page1 > 0 ? $"p.{page1}" : "", q),
						Tag = page1,
						IsExpanded = level <= 1,
						Padding = new Thickness(Math.Max(0, (level - 1) * 12), 2, 4, 2),
					};
					while (stack.Count > 0 && stack.Count >= level)
						stack.RemoveAt(stack.Count - 1);
					if (stack.Count == 0) treeOutline.Items.Add(item);
					else stack[stack.Count - 1].Items.Add(item);
					stack.Add(item);
					anyVisible = true;
				}
			}
			if (lboutlineempty != null)
				lboutlineempty.Visibility = (hasChapters && !anyVisible)
					? Visibility.Visible : Visibility.Collapsed;
			setoutlinetabvisible(hasChapters);
			lastMainOutlineTag = int.MinValue;
		} catch (Exception ex) {
			DocLog.Warn($"rebuildmainoutline: {ex.Message}");
			setoutlinetabvisible(false);
		} finally {
			syncOutlineTree = false;
		}
		// 筛选点击后：展开并定位目标；否则跟当前滚动高亮
		if (pendingOutlineReveal) {
			var tag = pendingOutlineRevealTag;
			pendingOutlineReveal = false;
			revealoutlineitem(tag, center: true);
			return;
		}
		try {
			var v2 = currentviewer();
			if (v2 is MdViewer mv2) {
				var t = mv2.GetActiveOutlineLine();
				if (t >= 0) onvieweroutlinehighlight(t);
			} else if (v2 is PdfViewer pv2) {
				var t = pv2.GetActiveOutlinePage1();
				if (t > 0) onvieweroutlinehighlight(t);
			} else if (v2 is DocxViewer dv2) {
				var t = dv2.GetActiveOutlinePage1();
				if (t > 0) onvieweroutlinehighlight(t);
			}
		} catch { /* ignore */ }
	}

	/// <summary>无章节时隐藏「章节列表」Tab；若当前正选中则退回「文件夹」。</summary>
	void setoutlinetabvisible(bool show) {
		if (tabOutline == null) return;
		try {
			var wasOutline = sideTabs != null
				&& ReferenceEquals(sideTabs.SelectedItem, tabOutline);
			tabOutline.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (!show && wasOutline && sideTabs != null && tabExplorer != null)
				sideTabs.SelectedItem = tabExplorer;
		} catch { /* ignore */ }
	}

	/// <summary>
	/// Viewer 内 applytocsync / applyoutlinesync 已算好理想章节 Tag，
	/// 主窗只做镜像选中（FindVisibleOnPath + 最小滚动），不再另算定位。
	/// </summary>
	void onvieweroutlinehighlight(int tag) {
		if (mainOutlineHlSyncing || syncOutlineTree) return;
		// 用户刚点过章节：跳转滚动过程中的中间高亮一律忽略，避免连点时高亮乱跳
		if (ignoreMainOutlineHlUntil != 0
			&& unchecked(Environment.TickCount - ignoreMainOutlineHlUntil) < 0)
			return;
		if (!leftSideVisible || tabOutline == null
			|| tabOutline.Visibility != Visibility.Visible)
			return;
		if (treeOutline == null || treeOutline.Items.Count == 0) return;
		if (tag < 0) return;
		if (tag == lastMainOutlineTag
			&& treeOutline.SelectedItem is TreeViewItem already
			&& already.Tag is int at && at == tag)
			return;

		// 精确 Tag 优先，否则取 ≤tag 的最大（与 Viewer 选 best 一致）
		TreeViewItem ideal = null;
		walkoutlineitems(treeOutline.Items, item => {
			if (item.Tag is int t && t == tag)
				ideal = item;
		});
		if (ideal == null) {
			var bestTag = int.MinValue;
			walkoutlineitems(treeOutline.Items, item => {
				if (item.Tag is not int t || t > tag) return;
				if (t >= bestTag) {
					bestTag = t;
					ideal = item;
				}
			});
		}
		if (ideal == null) return;

		// 与旧 applytocsync 相同：不强制展开，只选已展开路径上可见节点
		var sel = OutlineUi.FindVisibleOnPath(ideal);
		if (sel == null) return;
		if (ReferenceEquals(treeOutline.SelectedItem, sel)) {
			lastMainOutlineTag = tag;
			return;
		}

		mainOutlineHlSyncing = true;
		syncOutlineTree = true;
		try {
			if (treeOutline.SelectedItem is TreeViewItem old && !ReferenceEquals(old, sel))
				old.IsSelected = false;
			sel.IsSelected = true;
			OutlineUi.ScrollItemIntoView(sel, center: false);
			lastMainOutlineTag = tag;
		} catch { /* ignore */ }
		finally {
			syncOutlineTree = false;
			mainOutlineHlSyncing = false;
		}
	}

	static void walkoutlineitems(ItemCollection items, Action<TreeViewItem> act) {
		if (items == null || act == null) return;
		foreach (var o in items) {
			if (o is not TreeViewItem tvi) continue;
			act(tvi);
			if (tvi.Items.Count > 0)
				walkoutlineitems(tvi.Items, act);
		}
	}

	void onoutlinetreeselected(object sender, RoutedPropertyChangedEventArgs<object> e) {
		if (syncOutlineTree) return;
		if (treeOutline?.SelectedItem is not TreeViewItem ti) return;
		if (ti.Tag is not int tag) return;
		// 锁定当前点击项：滚动到位前不被中间章节抢高亮
		lastMainOutlineTag = tag;
		ignoreMainOutlineHlUntil = unchecked(Environment.TickCount + MAIN_OUTLINE_CLICK_SUPPRESS_MS);
		var v = currentviewer();
		try {
			if (v is MdViewer mv) {
				mv.MoveCaretToLine(tag);
			} else if (v is PdfViewer pv && tag > 0) {
				pv.GoToPage(tag);
			} else if (v is DocxViewer dv && tag > 0) {
				dv.GoToPage(tag);
			}
		} catch (Exception ex) {
			DocLog.Warn($"outline jump: {ex.Message}");
		}
		// 筛选中点击：清空搜索 → 重建完整树 → 展开祖先并定位目标
		var q = eoutlinefilter?.Text?.Trim() ?? "";
		if (q.Length == 0) return;
		pendingOutlineReveal = true;
		pendingOutlineRevealTag = tag;
		if (eoutlinefilter != null)
			eoutlinefilter.Text = ""; // TextChanged → rebuildmainoutline → reveal
	}

	/// <summary>
	/// 展开 ideal 祖先路径，选中并滚入可视区（筛选跳转 / 主动定位用）。
	/// 与滚动高亮不同：这里会 ExpandAncestors。
	/// </summary>
	void revealoutlineitem(int tag, bool center) {
		if (treeOutline == null || treeOutline.Items.Count == 0) return;
		TreeViewItem ideal = null;
		walkoutlineitems(treeOutline.Items, item => {
			if (item.Tag is int t && t == tag)
				ideal = item;
		});
		if (ideal == null) {
			// 页码类可能无精确匹配，取 ≤tag 最大
			var bestTag = int.MinValue;
			walkoutlineitems(treeOutline.Items, item => {
				if (item.Tag is not int t || t > tag) return;
				if (t >= bestTag) {
					bestTag = t;
					ideal = item;
				}
			});
		}
		if (ideal == null) return;

		mainOutlineHlSyncing = true;
		syncOutlineTree = true;
		try {
			OutlineUi.ExpandAncestors(ideal);
			if (treeOutline.SelectedItem is TreeViewItem old && !ReferenceEquals(old, ideal))
				old.IsSelected = false;
			ideal.IsSelected = true;
			try {
				treeOutline.UpdateLayout();
				ideal.UpdateLayout();
			} catch { /* ignore */ }
			OutlineUi.ScrollItemIntoView(ideal, center);
			lastMainOutlineTag = tag;
		} catch (Exception ex) {
			DocLog.Warn($"revealoutline: {ex.Message}");
		} finally {
			syncOutlineTree = false;
			mainOutlineHlSyncing = false;
		}
	}

	void navpage(bool next) {
		var v = currentviewer();
		if (v == null) return;
		if (next) v.GoNextPage();
		else v.GoPrevPage();
		updatestatus();
	}

	/// <param name="clearViewer">true=同时清当前文档的匹配高亮；切 Tab 时仅清计数标签。</param>
	void clearfindui(bool clearViewer = true) {
		if (lbfind != null) lbfind.Text = "";
		if (!clearViewer) return;
		try { currentviewer()?.ClearFind(); } catch { /* ignore */ }
	}

	/// <param name="restart">true=强制重建命中缓存。</param>
	/// <param name="fromView">true=按当前视口起跳（搜索框 Enter）。</param>
	void dofind(bool forward, bool restart = false, bool fromView = false) {
		var v = currentviewer();
		if (v == null) {
			clearfindui();
			return;
		}
		var q = efind.Text?.Trim() ?? "";
		if (q.Length == 0) {
			lbstatus.Text = "请输入查找内容";
			clearfindui();
			return;
		}
		// 查找前若焦点在搜索框，结束后仍收回（防止查看器抢焦点）
		var stayOnFind = efind != null && (efind.IsKeyboardFocusWithin || efind.IsFocused);
		// bcase 按下 = 区分大小写；抬起 = 忽略
		var ignoreCase = bcase == null || bcase.IsChecked != true;
		try {
			var fr = v.Find(q, forward, ignoreCase, restart, fromView);
			applyfindresult(fr, q, ignoreCase);
		} catch (Exception ex) {
			DocLog.Error("dofind", ex);
			lbstatus.Text = "查找失败";
			clearfindui();
		}
		if (stayOnFind) keepfindfocus();
	}

	/// <summary>焦点回到查找框，不 SelectAll，便于连续 Enter。</summary>
	void keepfindfocus() {
		if (efind == null) return;
		try {
			efind.Focus();
			var len = efind.Text?.Length ?? 0;
			efind.CaretIndex = len;
			efind.SelectionLength = 0;
		} catch { /* ignore */ }
	}

	void applyfindresult(FindResult fr, string q, bool ignoreCase) {
		var resultText = fr.Total > 0 ? $"{fr.Current}/{fr.Total}" : "";
		if (lbfind != null)
			lbfind.Text = resultText;
		var cur = current();
		if (cur != null) {
			cur.FindText = efind?.Text ?? q ?? "";
			cur.FindCase = bcase != null && bcase.IsChecked == true;
			cur.FindResultText = resultText;
		}
		updatestatus();
		if (fr.Found && fr.Total > 0) {
			var mode = ignoreCase ? "" : " · 区分大小写";
			lbstatus.Text = $"{cur?.Viewer?.StatusText ?? ""}  ·  「{q}」第 {fr.Current}/{fr.Total}{mode}";
		} else {
			lbstatus.Text = ignoreCase
				? $"未找到: {q}"
				: $"未找到: {q}（区分大小写）";
		}
	}

	/// <summary>
	/// 命令行文本输入：IME 中文 + 中文输入法下的 ASCII（Key 为 ImeProcessed 时 KeyDown 不发字符）。
	/// </summary>
	void onpreviewtextinput(object sender, TextCompositionEventArgs e) {
		// 命令行：文字由 ConsoleViewer 透明 IME TextBox 的 TextChanged 写入，主窗勿再转发（会双字）
		if (currentviewer() is ConsoleViewer cv && cv.IsCapturingKeys) {
			if (!cv.IsTerminalFocused)
				cv.PrepareImeFocus();
			return;
		}
	}

	void onpreviewkeydown(object sender, KeyEventArgs e) {
		var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
		// Alt+键时 WPF 常把 Key 设为 System，真实键在 SystemKey
		var key = e.Key == Key.System ? e.SystemKey : e.Key;

		// 文档区图片预览层：Esc 关闭；其它快捷键交给层，勿触发保存文档/全屏等
		if (ImageOverlay.IsOpen) {
			if (key == Key.Escape) {
				ImageOverlay.CloseIfOpen();
				e.Handled = true;
				return;
			}
			// 不 Handled，隧道继续到预览层
			return;
		}

		// 命令行标签：输入优先走透明 IME TextBox；勿在主窗 Handled 抢走中文组字
		if (currentviewer() is ConsoleViewer cv && cv.IsCapturingKeys) {
			// Alt+F4 关闭窗口：绝不能交给终端吞掉
			if (alt && !ctrl && key == Key.F4)
				return;
			if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
				|| key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin
				|| key == Key.CapsLock || key == Key.NumLock || key == Key.Scroll) {
				return;
			}
			if (!cv.IsTerminalFocused)
				cv.PrepareImeFocus();

			// 组字/可打印：完全放行给 imeBox + 系统 IME（不 Handled）
			if (key == Key.ImeProcessed || key == Key.DeadCharProcessed)
				return;
			if (!ctrl && !alt && isconsoleprintablekey(key))
				return;
			// 中文 IME 打开时：回车/空格/方向/退格也放行（选字），勿注入 PTY
			if (!ctrl && !alt && cv.IsImeOpen)
				return;

			// Ctrl 组合 / 无 IME 的功能键 → 终端（imeBox 内 PreviewKeyDown 也会处理）
			var ok = cv.TryHandleKey(key, Keyboard.Modifiers);
			if (ok) e.Handled = true;
			return;
		}

		// PDF 跳转历史：Alt+← 后退 / Alt+→ 前进（目录、书内链接、页码跳转）
		if (alt && !ctrl && (key == Key.Left || key == Key.Right)
			&& currentviewer() is PdfViewer pdfNav) {
			var ok = key == Key.Left ? pdfNav.TryNavBack() : pdfNav.TryNavForward();
			if (ok) {
				updatestatus();
				e.Handled = true;
				return;
			}
		}

		if (ctrl && e.Key == Key.O) {
			openfiles();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.OemComma) {
			opensettings();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.W) {
			closecurrent();
			e.Handled = true;
			return;
		}
		// Ctrl+Shift+T：重新打开关闭的标签
		if (ctrl && shift && e.Key == Key.T) {
			reopenclosedtab();
			e.Handled = true;
			return;
		}
		// Ctrl+Shift+B：书签栏
		if (ctrl && shift && e.Key == Key.B) {
			togglebookmarksbar();
			e.Handled = true;
			return;
		}
		// Ctrl+D：添加/编辑书签（优先于 PDF 标注复制）
		if (ctrl && !shift && e.Key == Key.D && !isinputfocused()) {
			addoreditbookmarkdialog();
			e.Handled = true;
			return;
		}
		// Ctrl+\ ：分屏
		if (ctrl && (e.Key == Key.Oem5 || e.Key == Key.OemBackslash || e.Key == Key.OemPipe)) {
			togglesplit();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.Q) {
			Close();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.G) {
			focuspagebox();
			e.Handled = true;
			return;
		}
		if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) {
			zoomin();
			e.Handled = true;
			return;
		}
		if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) {
			zoomout();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.D0) {
			fitpage();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.D1) {
			setzoom(1.0);
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.D2) {
			fitwidth();
			e.Handled = true;
			return;
		}
		if (!ctrl && !isinputfocused()) {
			if (e.Key == Key.OemPlus || e.Key == Key.Add) {
				zoomin();
				e.Handled = true;
				return;
			}
			if (e.Key == Key.OemMinus || e.Key == Key.Subtract) {
				zoomout();
				e.Handled = true;
				return;
			}
		}
		if (ctrl && e.Key == Key.Tab) {
			cycletab(shift);
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.F) {
			focusfind();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.P) {
			printcurrent();
			e.Handled = true;
			return;
		}
		if (ctrl && e.Key == Key.S) {
			if (currentviewer() is TextViewer || currentviewer() is MdViewer) {
				savecurrenttxtmd();
				e.Handled = true;
				return;
			}
			if (currentviewer() is XlsxViewer) {
				savecurrentxlsx();
				e.Handled = true;
				return;
			}
			if (currentviewer() is PdfViewer pSave) {
				if (pSave.AnnotMode) {
					if (pSave.SaveAnnots())
						lbstatus.Text = "标注已保存: " + (pSave.AnnotFilePath ?? "");
					e.Handled = true;
					return;
				}
				savecurrentpdf();
				e.Handled = true;
				return;
			}
		}
		// 全局 Ctrl+C：PDF 选区复制（焦点常不在查看器上）
		if (ctrl && e.Key == Key.C && !isinputfocused()) {
			var v = currentviewer();
			if (v != null && v.TryCopySelection()) {
				e.Handled = true;
				return;
			}
		}
		if (e.Key == Key.F3) {
			dofind(!shift);
			e.Handled = true;
			return;
		}
		if (e.Key == Key.F4 && !ctrl && !shift) {
			toggleside();
			e.Handled = true;
			return;
		}
		if (e.Key == Key.F11 && !ctrl) {
			togglefullscreen();
			e.Handled = true;
			return;
		}
		if (e.Key == Key.F8 && !ctrl && !shift) {
			toggletopbar();
			e.Handled = true;
			return;
		}
		if (e.Key == Key.F12 && !ctrl && !shift) {
			toggleside();
			e.Handled = true;
			return;
		}
		// PDF 编辑中 Delete 删除对象
		if (!ctrl && !isinputfocused() && e.Key == Key.Delete
			&& currentviewer() is PdfViewer pv && pv.EditMode) {
			pv.EditDeleteSelected();
			e.Handled = true;
			updatestatus();
			return;
		}
		// PDF 标注中 Delete / Ctrl+D / Ctrl+C / Ctrl+V / Ctrl+G
		if (!isinputfocused() && currentviewer() is PdfViewer pa && pa.AnnotMode) {
			if (!ctrl && e.Key == Key.Delete) {
				pa.AnnotDeleteSelected();
				e.Handled = true;
				updatestatus();
				return;
			}
			// Ctrl+D 已用于书签；标注复制请用工具栏
			if (ctrl && e.Key == Key.C) {
				pa.AnnotCopySelected();
				e.Handled = true;
				return;
			}
			if (ctrl && e.Key == Key.V) {
				pa.AnnotPaste();
				e.Handled = true;
				updatestatus();
				return;
			}
			if (ctrl && e.Key == Key.G) {
				if (shift) pa.AnnotUngroupSelected();
				else pa.AnnotGroupSelected();
				e.Handled = true;
				updatestatus();
				return;
			}
		}
		if (!isinputfocused()) {
			if (e.Key == Key.PageDown || e.Key == Key.Space) {
				navpage(true);
				e.Handled = true;
				return;
			}
			if (e.Key == Key.PageUp) {
				navpage(false);
				e.Handled = true;
				return;
			}
			if (e.Key == Key.Home) {
				currentviewer()?.GoToPage(1);
				updatestatus();
				e.Handled = true;
				return;
			}
			if (e.Key == Key.End) {
				var v = currentviewer();
				if (v != null) { v.GoToPage(v.PageCount); updatestatus(); }
				e.Handled = true;
				return;
			}
			// —— Sumatra / Vim 风格单键（非输入框焦点）——
			if (!ctrl && !alt) {
				if (handlevimkey(e.Key, shift)) {
					e.Handled = true;
					return;
				}
			}
		}
	}

	/// <summary>
	/// Sumatra 风格 Vim 键：hjkl 滚动，n/p 翻页，z 循环缩放，x/q 关闭，
	/// c 侧栏，f 全屏，g 跳页，[ ] 逆/顺时针旋转 90°。
	/// </summary>
	bool handlevimkey(Key key, bool shift) {
		// 行滚动量：约 3 行字高
		var fs = FontSize > 1 ? FontSize : 12;
		var line = Math.Max(24, fs * 3);
		var pageStep = Math.Max(120, (currentviewer()?.View?.ActualHeight ?? 480) * 0.9);

		switch (key) {
		case Key.J:
			scrollview(0, line);
			return true;
		case Key.K:
			scrollview(0, -line);
			return true;
		case Key.H:
			scrollview(-line, 0);
			return true;
		case Key.L:
			scrollview(line, 0);
			return true;
		case Key.N:
			navpage(true);
			return true;
		case Key.P:
			navpage(false);
			return true;
		case Key.Z:
			// 适页 → 适宽 → 100% → 适页
			zoomCycle = (zoomCycle + 1) % 3;
			if (zoomCycle == 0) fitpage();
			else if (zoomCycle == 1) fitwidth();
			else setzoom(1.0);
			return true;
		case Key.X:
			// 关闭当前标签
			closecurrent();
			return true;
		case Key.C:
			// Sumatra：continuous；此处映射为目录侧栏开关（最常用）
			toggleside();
			return true;
		case Key.Q:
			// 关标签；若无标签则关窗
			if (opentabs.Count > 0) closecurrent();
			else Close();
			return true;
		case Key.F:
			// Sumatra：全屏
			togglefullscreen();
			return true;
		case Key.G:
			focuspagebox();
			return true;
		case Key.OemOpenBrackets: // [ 逆时针 90°
			rotateview(-1);
			return true;
		case Key.OemCloseBrackets: // ] 顺时针 90°
			rotateview(1);
			return true;
		case Key.Oem2: // / 查找（Sumatra）
		case Key.Divide:
			focusfind();
			return true;
		case Key.R:
			// 重新加载当前文档
			reloadcurrent();
			return true;
		case Key.Down:
		case Key.Up:
		case Key.Left:
		case Key.Right:
			// XLSX：主窗 Preview 先于表格
			if (currentviewer() is XlsxViewer xv) {
				// 单元格编辑中：不拦方向键，交给 TextBox 移光标
				if (xv.IsEditingCell) {
					DocLog.Info($"vimkey xlsx-edit-pass key={key}");
					return false;
				}
				var dr = key == Key.Down ? 1 : key == Key.Up ? -1 : 0;
				var dc = key == Key.Right ? 1 : key == Key.Left ? -1 : 0;
				if (shift) {
					DocLog.Info($"vimkey xlsx-extend key={key} dr={dr} dc={dc}");
					xv.ExtendSelectionBy(dr, dc);
				} else {
					DocLog.Info($"vimkey xlsx-move key={key} dr={dr} dc={dc}");
					xv.MoveSelectionBy(dr, dc);
				}
				return true;
			}
			if (key == Key.Down) scrollview(0, line);
			else if (key == Key.Up) scrollview(0, -line);
			else if (key == Key.Left)
				scrollview(shift ? -pageStep * 0.3 : -line, 0);
			else
				scrollview(shift ? pageStep * 0.3 : line, 0);
			return true;
		}
		return false;
	}

	void focusfind() {
		if (efind == null) return;
		efind.Focus();
		efind.SelectAll();
	}

	void scrollview(double dx, double dy) {
		var sv = findviewscroll();
		if (sv == null) return;
		try {
			if (Math.Abs(dy) > 0.5)
				sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + dy));
			if (Math.Abs(dx) > 0.5)
				sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset + dx));
		} catch { /* ignore */ }
	}

	ScrollViewer findviewscroll() {
		var v = current()?.Viewer;
		if (v?.View == null) return null;
		return findscrollviewer(v.View);
	}

	static ScrollViewer findscrollviewer(DependencyObject root) {
		if (root == null) return null;
		if (root is ScrollViewer sv) return sv;
		if (!(root is Visual) && !(root is System.Windows.Media.Media3D.Visual3D))
			return null;
		try {
			var n = VisualTreeHelper.GetChildrenCount(root);
			for (var i = 0; i < n; i++) {
				var found = findscrollviewer(VisualTreeHelper.GetChild(root, i));
				if (found != null) return found;
			}
		} catch { /* ignore */ }
		return null;
	}

	bool fullscreen;
	bool preFsTopmost;
	WindowState preFsState;
	WindowStyle preFsStyle;
	ResizeMode preFsResize;
	double preFsLeft, preFsTop, preFsWidth, preFsHeight;

	void togglefullscreen() {
		if (!fullscreen) {
			preFsState = WindowState;
			preFsStyle = WindowStyle;
			preFsResize = ResizeMode;
			preFsTopmost = Topmost;
			preFsLeft = Left;
			preFsTop = Top;
			preFsWidth = Width;
			preFsHeight = Height;
			if (ptitle != null) ptitle.Visibility = Visibility.Collapsed;
			if (ptoolbar != null) ptoolbar.Visibility = Visibility.Collapsed;
			if (pstatus != null) pstatus.Visibility = Visibility.Collapsed;

			// 已最大化时再设 Maximized 不会重算尺寸，先回 Normal
			WindowStyle = WindowStyle.None;
			ResizeMode = ResizeMode.NoResize;
			fullscreen = true;
			WindowState = WindowState.Normal;
			// 显式铺满当前显示器（含任务栏），并置顶盖住任务栏
			placefullscreen();
			Topmost = true;
			syncmaxchrome();
		} else {
			fullscreen = false;
			Topmost = preFsTopmost;
			WindowState = WindowState.Normal;
			WindowStyle = preFsStyle;
			ResizeMode = preFsResize;
			if (preFsState == WindowState.Maximized) {
				WindowState = WindowState.Maximized;
			} else {
				Left = preFsLeft;
				Top = preFsTop;
				Width = preFsWidth;
				Height = preFsHeight;
			}
			if (ptitle != null) ptitle.Visibility = Visibility.Visible;
			if (ptoolbar != null) ptoolbar.Visibility = Visibility.Visible;
			if (pstatus != null) pstatus.Visibility = Visibility.Visible;
			syncmaxchrome();
		}
	}

	/// <summary>把窗口放到当前显示器整块屏幕（设备像素 → DIP）。</summary>
	void placefullscreen() {
		try {
			var hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(this).EnsureHandle();
			var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
			if (mon == IntPtr.Zero) return;
			var mi = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
			if (!GetMonitorInfo(mon, ref mi)) return;
			var r = mi.rcMonitor;
			// 屏幕像素 → WPF DIP
			var src = PresentationSource.FromVisual(this);
			var fromDev = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
			var tl = fromDev.Transform(new Point(r.Left, r.Top));
			var br = fromDev.Transform(new Point(r.Right, r.Bottom));
			Left = tl.X;
			Top = tl.Y;
			Width = Math.Max(MinWidth, br.X - tl.X);
			Height = Math.Max(MinHeight, br.Y - tl.Y);
		} catch {
			// 回退：最大化 + 全屏 maxinfo
			try { WindowState = WindowState.Maximized; } catch { /* ignore */ }
		}
	}

	void toggletopbar() {
		if (ptoolbar == null) return;
		ptoolbar.Visibility = ptoolbar.Visibility == Visibility.Visible
			? Visibility.Collapsed
			: Visibility.Visible;
	}

	void reloadcurrent() {
		var cur = current();
		if (cur != null)
			reloaddoc(cur);
	}

	/// <summary>丢弃当前 Viewer 并按磁盘内容重新加载（保留阅读进度）。</summary>
	void reloaddoc(DocTab doc) {
		if (doc == null || string.IsNullOrWhiteSpace(doc.Path)) return;
		try {
			// 抑制本进程写盘误触发；进度先存以便恢复
			markselfwrite(doc);
			try { if (doc.Viewer != null) saveprogress(doc.Viewer); } catch { /* ignore */ }
			doc.LoadGen++;
			doc.Loading = false;
			if (doc.Viewer != null) {
				try { doc.Viewer.Dispose(); } catch { /* ignore */ }
				doc.Viewer = null;
			}
			doc.Loaded = false;
			showloading(doc);
			ensureloaded(doc);
		} catch (Exception ex) {
			DocLog.Error("reloaddoc", ex);
			if (lbstatus != null)
				lbstatus.Text = Loc.T("reload_failed");
		}
	}

	void cycletab(bool backward) {
		var n = tabs.Items.Count;
		if (n <= 1) return;
		var i = tabs.SelectedIndex;
		if (backward) i = (i - 1 + n) % n;
		else i = (i + 1) % n;
		tabs.SelectedIndex = i;
	}

	/// <summary>
	/// 命令行可打印键：不 Handled KeyDown，交给 TextInput / IME（英文与中文统一路径）。
	/// </summary>
	static bool isconsoleprintablekey(Key key) {
		if (key >= Key.A && key <= Key.Z) return true;
		if (key >= Key.D0 && key <= Key.D9) return true;
		if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;
		switch (key) {
			case Key.Space:
			case Key.OemMinus:
			case Key.OemPlus:
			case Key.OemOpenBrackets:
			case Key.OemCloseBrackets:
			case Key.OemPipe:
			case Key.OemSemicolon:
			case Key.OemQuotes:
			case Key.OemComma:
			case Key.OemPeriod:
			case Key.OemQuestion:
			case Key.OemTilde:
			case Key.OemBackslash:
			case Key.Divide:
			case Key.Multiply:
			case Key.Subtract:
			case Key.Add:
			case Key.Decimal:
				return true;
			default:
				return false;
		}
	}

	bool isinputfocused() {
		// 命令行标签整体视为输入中（工具栏除外由 ConsoleViewer 自己判断）
		if (currentviewer() is ConsoleViewer cv && cv.IsCapturingKeys)
			return true;
		var fe = Keyboard.FocusedElement as DependencyObject;
		while (fe != null) {
			// 终端 / 文本框：吞掉 vim 单键（f 全屏、j/k 滚动等），否则命令行无法输入
			if (fe is TextBox || fe is RichTextBox || fe is PasswordBox
				|| fe is TerminalControl || fe is ComboBox)
				return true;
			// Hyperlink/Run/Paragraph 等不是 Visual，不能用 VisualTreeHelper.GetParent
			fe = safevisualparent(fe);
		}
		return false;
	}

	/// <summary>ContentElement（Hyperlink 等）走 LogicalTree，Visual 走 VisualTree。</summary>
	static DependencyObject safevisualparent(DependencyObject d) {
		if (d == null) return null;
		try {
			if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
				return VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
			return LogicalTreeHelper.GetParent(d);
		} catch {
			try { return LogicalTreeHelper.GetParent(d); }
			catch { return null; }
		}
	}

	protected override void OnClosed(EventArgs e) {
		try { stopallfilewatches(); } catch { /* ignore */ }
		foreach (var d in opentabs.ToList()) {
			try { d.Viewer?.Dispose(); } catch { /* ignore */ }
		}
		opentabs.Clear();
		base.OnClosed(e);
	}

	// ――― 外部文件变更监听 ―――

	void initfilewatch() {
		fileWatchTimer = new DispatcherTimer {
			Interval = TimeSpan.FromMilliseconds(200),
		};
		fileWatchTimer.Tick += (_, _) => processfilewatches();
	}

	void ensurefilewatchtimer() {
		if (fileWatchTimer == null) initfilewatch();
		if (!fileWatchTimer.IsEnabled)
			fileWatchTimer.Start();
	}

	void startfilewatch(DocTab doc) {
		if (doc == null || string.IsNullOrWhiteSpace(doc.Path)) return;
		if (isvirtualtab(doc) || isbrowserpath(doc.Path) || isconsolepath(doc.Path)) return;
		stopfilewatch(doc);
		try {
			var full = pathnorm(doc.Path) ?? doc.Path;
			var dir = Path.GetDirectoryName(full);
			var name = Path.GetFileName(full);
			if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
			if (!Directory.Exists(dir)) return;

			var w = new FileSystemWatcher(dir, name) {
				NotifyFilter = NotifyFilters.LastWrite
					| NotifyFilters.Size
					| NotifyFilters.FileName
					| NotifyFilters.CreationTime,
				IncludeSubdirectories = false,
			};
			FileSystemEventHandler onEv = (_, _) => signalfilewatch(doc);
			RenamedEventHandler onRen = (_, _) => signalfilewatch(doc);
			w.Changed += onEv;
			w.Created += onEv;
			w.Deleted += onEv;
			w.Renamed += onRen;
			w.EnableRaisingEvents = true;
			doc.Watcher = w;
			capturefilestamp(doc);
			DocLog.Info($"filewatch start path={full}");
		} catch (Exception ex) {
			DocLog.Warn($"filewatch start fail: {ex.Message}");
			doc.Watcher = null;
		}
	}

	void stopfilewatch(DocTab doc) {
		if (doc == null) return;
		doc.WatchPendingTick = 0;
		var w = doc.Watcher;
		doc.Watcher = null;
		if (w == null) return;
		try {
			w.EnableRaisingEvents = false;
			w.Dispose();
		} catch { /* ignore */ }
	}

	void stopallfilewatches() {
		try { fileWatchTimer?.Stop(); } catch { /* ignore */ }
		foreach (var d in opentabs.ToList())
			stopfilewatch(d);
	}

	/// <summary>Watcher 线程回调：只标记待处理，由 UI 定时器防抖执行。</summary>
	static void signalfilewatch(DocTab doc) {
		if (doc == null) return;
		doc.WatchPendingTick = Environment.TickCount + FILE_WATCH_DEBOUNCE_MS;
		MainWindow owner = null;
		foreach (var w in liveWindows) {
			try {
				if (w.opentabs.Contains(doc)) {
					owner = w;
					break;
				}
			} catch { /* ignore */ }
		}
		if (owner == null) return;
		try {
			owner.Dispatcher.BeginInvoke(new Action(() => {
				try { owner.ensurefilewatchtimer(); } catch { /* ignore */ }
			}));
		} catch { /* ignore */ }
	}

	void processfilewatches() {
		var now = Environment.TickCount;
		var pending = false;
		foreach (var doc in opentabs.ToList()) {
			if (doc.WatchPendingTick == 0) continue;
			if (now - doc.WatchPendingTick < 0) {
				pending = true;
				continue;
			}
			doc.WatchPendingTick = 0;
			try { handleexternalchange(doc); } catch (Exception ex) {
				DocLog.Error("handleexternalchange", ex);
			}
			if (doc.WatchPendingTick != 0)
				pending = true;
		}
		if (!pending && fileWatchTimer != null)
			fileWatchTimer.Stop();
	}

	void handleexternalchange(DocTab doc) {
		if (doc == null || !opentabs.Contains(doc)) return;
		if (doc.Loading) return;
		// 本程序刚保存/重载：忽略短时间内的 watcher 事件
		if (doc.LastSelfWriteTick != 0
			&& Environment.TickCount - doc.LastSelfWriteTick < FILE_WATCH_SELF_SUPPRESS_MS)
			return;
		if (!doc.Loaded || doc.Viewer == null) {
			// 尚未加载：只刷新时间戳，切到标签时会读最新内容
			capturefilestamp(doc);
			return;
		}
		if (!filechangedondisk(doc)) return;
		// 外部写入可能尚未落盘完成
		if (!canreadfile(doc.Path)) {
			doc.WatchPendingTick = Environment.TickCount + 300;
			ensurefilewatchtimer();
			return;
		}

		var dirty = isviewdirty(doc.Viewer);
		if (dirty) {
			if (doc.ExternalPrompting) return;
			doc.ExternalPrompting = true;
			try {
				try { Activate(); } catch { /* ignore */ }
				var r = MessageBox.Show(this,
					Loc.Tf("external_changed_dirty", doc.Path),
					Loc.T("external_changed_title"),
					MessageBoxButton.YesNo,
					MessageBoxImage.Question,
					MessageBoxResult.No);
				if (r != MessageBoxResult.Yes) {
					// 保留本地修改：把当前磁盘状态记为已确认，避免对同一次变更重复弹窗
					capturefilestamp(doc);
					return;
				}
			} finally {
				doc.ExternalPrompting = false;
			}
		}

		DocLog.Info($"external reload path={doc.Path} dirty={dirty}");
		reloaddoc(doc);
		if (lbstatus != null && ReferenceEquals(current(), doc))
			lbstatus.Text = Loc.Tf("external_reloaded", Path.GetFileName(doc.Path));
	}

	static bool filechangedondisk(DocTab doc) {
		if (doc == null || string.IsNullOrWhiteSpace(doc.Path)) return false;
		try {
			if (!File.Exists(doc.Path)) return false;
			var fi = new FileInfo(doc.Path);
			fi.Refresh();
			if (fi.LastWriteTimeUtc != doc.FileStampUtc) return true;
			if (fi.Length != doc.FileSize) return true;
			return false;
		} catch {
			return false;
		}
	}

	static bool canreadfile(string path) {
		try {
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
			using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				return true;
		} catch {
			return false;
		}
	}

	static void capturefilestamp(DocTab doc) {
		if (doc == null || string.IsNullOrWhiteSpace(doc.Path)) return;
		try {
			if (!File.Exists(doc.Path)) {
				doc.FileStampUtc = DateTime.MinValue;
				doc.FileSize = -1;
				return;
			}
			var fi = new FileInfo(doc.Path);
			fi.Refresh();
			doc.FileStampUtc = fi.LastWriteTimeUtc;
			doc.FileSize = fi.Length;
		} catch { /* ignore */ }
	}

	/// <summary>本程序写盘前后调用：抑制 watcher 误触发，并刷新已知时间戳。</summary>
	static void markselfwrite(DocTab doc) {
		if (doc == null) return;
		doc.LastSelfWriteTick = Environment.TickCount;
		capturefilestamp(doc);
	}
}

sealed class DocTab {
	public string Path;
	public DocKind Kind;
	public TabItem Tab;
	/// <summary>标题栏上的 Tab 芯片 UI。</summary>
	public FrameworkElement HeaderUI;
	/// <summary>Tab 芯片上的文件名标签（未保存时追加 *）。</summary>
	public TextBlock TitleLabel;
	/// <summary>
	/// VS Code 风格预览标签：斜体标题；文件夹浏览打开时共用一个；
	/// 双击 Tab 或开始编辑后转为普通标签。
	/// </summary>
	public bool IsPreview;
	public IDocViewer Viewer;
	public bool Loaded;
	/// <summary>正在异步加载中。</summary>
	public bool Loading;
	/// <summary>加载代数：关闭/重载时递增以丢弃过期结果。</summary>
	public int LoadGen;
	/// <summary>本 Tab 查找框文本（各 Tab 独立）。</summary>
	public string FindText = "";
	/// <summary>本 Tab 是否区分大小写。</summary>
	public bool FindCase;
	/// <summary>本 Tab 查找计数文案，如 3/10。</summary>
	public string FindResultText = "";

	/// <summary>磁盘文件监视器（外部修改自动刷新）。</summary>
	public FileSystemWatcher Watcher;
	/// <summary>上次确认的 LastWriteTimeUtc。</summary>
	public DateTime FileStampUtc;
	/// <summary>上次确认的文件长度；-1 表示未知。</summary>
	public long FileSize = -1;
	/// <summary>待处理外部变更：非 0 表示在该 TickCount 时刻或之后检查。</summary>
	public int WatchPendingTick;
	/// <summary>本程序写盘时刻（TickCount），用于抑制误触发。</summary>
	public int LastSelfWriteTick;
	/// <summary>正在弹「外部已改、本地未保存」对话框。</summary>
	public bool ExternalPrompting;
}
