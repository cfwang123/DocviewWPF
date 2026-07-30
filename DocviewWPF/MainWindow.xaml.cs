using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
	/// <summary>次要窗口：不恢复会话、不解析命令行。</summary>
	readonly bool isSecondary;
	/// <summary>滚动进度防抖保存。</summary>
	DispatcherTimer progressTimer;
	IDocViewer progressViewer;

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
		initpdfedit();
		initdrop();
		if (!isSecondary)
			restorewindowbounds();
		Loaded += (_, _) => {
			applyuifont();
			if (!isSecondary) {
				if (AppSettings.Current.RestoreTabs)
					restoresession();
				openargs();
			}
		};
		Closing += (_, _) => {
			try { saveallprogress(); } catch { /* ignore */ }
			if (!isSecondary) {
				try { savewindowbounds(); } catch { /* ignore */ }
			}
			// 关窗时保存；若本窗已空且其它窗也已清掉，禁止用空列表覆盖（多窗关闭顺序会抹会话）
			try { savesession(allowEmpty: false); } catch { /* ignore */ }
		};
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
			d = VisualTreeHelper.GetParent(d);
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
					: new Thickness(6);
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

	void initmenu() {
		mnopen.Click += (_, _) => openfiles();
		mnrecent.SubmenuOpened += (_, _) => buildrecentmenu();
		if (mnprint != null) mnprint.Click += (_, _) => printcurrent();
		if (mncopypath != null) mncopypath.Click += (_, _) => copyfilepath();
		if (mnshowinexplorer != null) mnshowinexplorer.Click += (_, _) => showinexplorer();
		mnclose.Click += (_, _) => closecurrent();
		mncloseall.Click += (_, _) => closeall();
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
		if (mnpdfeditor != null) mnpdfeditor.Click += (_, _) => openpdfeditor();
		mnsettings.Click += (_, _) => opensettings();
		if (mnlang != null) buildlangmenu();
		mabout.Click += (_, _) => {
			var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
			var verText = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.1";
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
			if (mnrecent != null) mnrecent.Header = Loc.T("recent");
			if (mnprint != null) mnprint.Header = Loc.T("print");
			if (mncopypath != null) mncopypath.Header = Loc.T("copy_path");
			if (mnshowinexplorer != null) mnshowinexplorer.Header = Loc.T("show_in_explorer");
			if (mnclose != null) mnclose.Header = Loc.T("close");
			if (mncloseall != null) mncloseall.Header = Loc.T("close_all");
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
			if (mntools != null) mntools.Header = Loc.T("tools");
			if (mnpdfeditor != null) mnpdfeditor.Header = Loc.T("pdf_pro_edit");
			if (mnsettings != null) mnsettings.Header = Loc.T("settings");
			if (mnhelp != null) mnhelp.Header = Loc.T("help");
			if (mnlang != null) mnlang.Header = Loc.T("language");
			if (mabout != null) mabout.Header = Loc.T("about");

			if (bmin != null) bmin.ToolTip = Loc.T("minimize");
			if (bmax != null)
				bmax.ToolTip = WindowState == WindowState.Maximized ? Loc.T("restore") : Loc.T("maximize");
			if (bclosewin != null) bclosewin.ToolTip = Loc.T("close_window");

			if (bopen != null) bopen.ToolTip = Loc.T("tip_open");
			if (bprint != null) bprint.ToolTip = Loc.T("tip_print");
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
			if (bpfdedit != null) bpfdedit.ToolTip = Loc.T("tip_pdf_edit");
			if (bpdfsave != null) bpdfsave.ToolTip = Loc.T("tip_pdf_save");

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
			}
		} catch (Exception ex) {
			DocLog.Error("opensettings", ex);
			App.ShowError(ex, "系统参数");
		}
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
			p.SaveEdits();
			lbstatus.Text = "已保存: " + p.FilePath;
			syncpdfeditui();
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
		}
		hookedPdf = p;
		if (p == null) return;
		p.EditModeChanged += onpdfeditmode;
		p.DirtyChanged += onpdfdirty;
		p.EditSelectionChanged += onpdfsel;
	}

	void onpdfeditmode() => syncpdfeditui();
	void onpdfdirty() => syncpdfeditui();
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
	void onxlsxdirty() => syncxlsxeditui();

	void togglexlsxedit() {
		var x = currentviewer() as XlsxViewer;
		if (x == null) return;
		x.EditMode = !x.EditMode;
		syncxlsxeditui();
		updatestatus();
	}

	void savecurrentxlsx() {
		var x = currentviewer() as XlsxViewer;
		if (x == null) return;
		try {
			x.Save();
			lbstatus.Text = "已保存: " + x.FilePath;
			syncxlsxeditui();
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
		var editing = isXlsx && x.EditMode;
		if (bxlsxedit != null) {
			bxlsxedit.Visibility = isXlsx ? Visibility.Visible : Visibility.Collapsed;
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

	/// <summary>打印当前文档视图（WPF PrintVisual）。</summary>
	void printcurrent() {
		var v = currentviewer()?.View;
		if (v == null) {
			MessageBox.Show(this, "没有可打印的文档。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			var dlg = new PrintDialog();
			if (dlg.ShowDialog() != true) return;
			var cur = current();
			var title = cur?.Viewer?.Title
				?? (cur?.Path != null ? Path.GetFileName(cur.Path) : null)
				?? "DocviewWPF";
			dlg.PrintVisual(v, title);
		} catch (Exception ex) {
			DocLog.Error("printcurrent", ex);
			MessageBox.Show(this, "打印失败: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
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
			MessageBox.Show(this, "当前没有打开的文件。", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try {
			if (!File.Exists(path)) {
				// 文件已删：尽量打开所在目录
				var dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
					Process.Start("explorer.exe", dir);
				else
					MessageBox.Show(this, "文件不存在:\n" + path, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			// /select, 后需完整路径；带空格时用引号
			Process.Start("explorer.exe", "/select,\"" + path + "\"");
		} catch (Exception ex) {
			DocLog.Error("showinexplorer", ex);
			MessageBox.Show(this, "无法打开资源管理器: " + ex.Message, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	/// <summary>当前标签文件路径；无则 null。</summary>
	string currentfilepath() {
		var cur = current();
		if (cur == null || string.IsNullOrWhiteSpace(cur.Path)) return null;
		return cur.Path;
	}

	void initdrop() {
		DragEnter += (_, e) => {
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
				e.Effects = DragDropEffects.Copy;
			else
				e.Effects = DragDropEffects.None;
			e.Handled = true;
		};
		DragOver += (_, e) => {
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
				e.Effects = DragDropEffects.Copy;
			else
				e.Effects = DragDropEffects.None;
			e.Handled = true;
		};
		Drop += (_, e) => {
			if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
			var files = e.Data.GetData(DataFormats.FileDrop) as string[];
			if (files == null) return;
			foreach (var f in files)
				openpath(f, loadNow: true);
		};
	}

	void openargs() {
		var args = Environment.GetCommandLineArgs();
		string zoomTestPath = null;
		string pdfEditTestPath = null;
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
		}
		if (!string.IsNullOrWhiteSpace(pdfEditTestPath)) {
			runpdfedittest(pdfEditTestPath);
			return;
		}
		if (!string.IsNullOrWhiteSpace(zoomTestPath))
			runzoomtest(zoomTestPath);
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
		// UI 就绪后再钉一次选中
		Dispatcher.BeginInvoke(new Action(() => {
			if (cur?.Tab == null || !tabs.Items.Contains(cur.Tab)) return;
			if (!ReferenceEquals(tabs.SelectedItem, cur.Tab))
				tabs.SelectedItem = cur.Tab;
			updatestatus();
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
		SessionStore.Save(paths, sel, selPath);
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
			ReadingProgressStore.Set(v.FilePath, h, vv, z, sp, v.CurrentPage);
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
		if (p == null) return;
		try {
			// xlsx 的 sheet 存在 Sheet 字段；pdf/docx 主要靠 H/V，Page 作兜底
			var sheetOrPage = v.Kind == DocKind.Xlsx ? p.Sheet : p.Page;
			v.RestoreViewState(p.H, p.V, p.Zoom > 0.05 ? p.Zoom : 1, sheetOrPage);
			DocLog.Info($"restoreprogress path={v.FilePath} h={p.H:F0} v={p.V:F0} z={p.Zoom:F2}");
		} catch (Exception ex) {
			DocLog.Warn($"restoreprogress: {ex.Message}");
		}
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

	/// <param name="loadNow">true=立即加载；false=仅建标签壳</param>
	void openpath(string path, bool loadNow = true) {
		try {
			path = pathnorm(path);
			if (path == null) return;
			if (!File.Exists(path)) {
				RecentFilesStore.Remove(path);
				MessageBox.Show($"文件不存在:\n{path}", "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			// 已打开：只跳转到对应标签，不再新建
			var exist = findtab(path);
			if (exist != null) {
				DocLog.Info($"openpath reuse tab path={path}");
				activatetab(exist, loadNow);
				rememberrecent(path);
				persisttabs();
				return;
			}

			var kind = DocKindUtil.FromPath(path);
			if (kind == DocKind.Unknown) {
				MessageBox.Show($"不支持的文件类型:\n{Path.GetFileName(path)}\n\n支持: .pdf .docx .xlsx",
					"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}

			var doc = addtabshell(path, kind);
			activatetab(doc, loadNow);
			syncempty();
			updatestatus();
			rememberrecent(path);
			persisttabs();
		} catch (Exception ex) {
			DocLog.Error($"openpath fail path={path}", ex);
			App.ShowError(ex, "打开文件");
			lbstatus.Text = "打开失败";
		}
	}

	void rememberrecent(string path) {
		try {
			RecentFilesStore.Add(path);
		} catch { /* ignore */ }
	}

	/// <summary>规范化路径，便于同一文件比对。</summary>
	static string pathnorm(string path) {
		if (string.IsNullOrWhiteSpace(path)) return null;
		try {
			path = path.Trim().Trim('"');
			if (path.Length == 0) return null;
			path = Path.GetFullPath(path);
			// 去掉末尾分隔符（根路径除外）
			if (path.Length > 3)
				path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return path;
		} catch {
			return null;
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
	}

	DocTab addtabshell(string path, DocKind kind) {
		path = pathnorm(path) ?? path;
		var tab = new TabItem { Tag = path, Header = null };
		tab.Content = makeplaceholder(path);

		var doc = new DocTab {
			Path = path,
			Kind = kind,
			Tab = tab,
			Viewer = null,
			Loaded = false,
		};
		// 标题栏 Tab 芯片
		doc.HeaderUI = buildtabheader(Path.GetFileName(path), tab, doc);
		opentabs.Add(doc);
		tabs.Items.Add(tab);
		if (ptabs != null)
			ptabs.Children.Add(doc.HeaderUI);
		synctabheaders();
		return doc;
	}

	static FrameworkElement makeplaceholder(string path) {
		return new Border {
			Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
			Child = new TextBlock {
				Text = $"未加载\n{Path.GetFileName(path)}\n\n切换到此标签时自动打开",
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
	static FrameworkElement makeloading(string path) {
		var name = Path.GetFileName(path) ?? "";
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
		doc.Tab.Content = makeplaceholder(doc.Path);
	}

	void showloading(DocTab doc) {
		if (doc?.Tab == null) return;
		doc.Tab.Content = makeloading(doc.Path);
	}

	/// <summary>
	/// 按需打开文件。立即显示「加载中」，下一帧再解析，避免打开时 UI 假死。
	/// </summary>
	void ensureloaded(DocTab doc) {
		if (doc == null) return;
		if (doc.Loaded && doc.Viewer != null) return;
		if (doc.Loading) return;

		if (!File.Exists(doc.Path)) {
			lbstatus.Text = "文件不存在，已关闭标签";
			closetab(doc.Tab);
			return;
		}

		doc.Loading = true;
		var gen = ++doc.LoadGen;
		showloading(doc);
		if (lbstatus != null)
			lbstatus.Text = $"加载中… {Path.GetFileName(doc.Path)}";
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
			} else {
				viewer = ViewerFactory.Create(kind);
				viewer.Load(path);
			}

			if (!loadstillvalid(doc, gen)) {
				try { viewer.Dispose(); } catch { /* ignore */ }
				return;
			}

			try {
				if (viewer.HasOutline)
					viewer.SetSidePanelVisible(AppSettings.Current.ShowSidePanel);
			} catch { /* ignore */ }

			viewer.StatusChanged += () => {
				if (current()?.Viewer == viewer)
					updatestatus();
				// 滚动/翻页时防抖写入进度
				scheduleprogresssave(viewer);
			};
			doc.Viewer = viewer;
			doc.Loaded = true;
			doc.Loading = false;
			doc.Tab.Content = viewer.View;
			// 恢复上次阅读位置
			try { restoreprogress(viewer); } catch { /* ignore */ }
			DocLog.Info($"ensureloaded ok title={viewer.Title}");
			updatestatus();
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

			detachtab(doc);

			var nw = new MainWindow(secondary: true) {
				Width = w,
				Height = h,
				WindowStartupLocation = WindowStartupLocation.Manual,
				Left = dip.X - grabInWin.X,
				Top = dip.Y - grabInWin.Y,
			};
			nw.Show();
			nw.attachtab(doc, 0, activate: true);
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
				doc.Tab.Content = makeplaceholder(doc.Path);
			else
				doc.Tab.Content = makeplaceholder(doc.Path);
		}
		// 重建芯片（事件绑定到本窗）
		doc.HeaderUI = buildtabheader(Path.GetFileName(doc.Path) ?? "文档", doc.Tab, doc);

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
		var dip = screentodip(this, screenPx);
		var grabX = tabDragGrabInHeader.X > 0 ? tabDragGrabInHeader.X : 80;
		var grabY = 14.0;
		detachtab(doc);
		var nw = new MainWindow(secondary: true) {
			Width = Math.Max(640, ActualWidth * 0.85),
			Height = Math.Max(420, ActualHeight * 0.85),
			WindowStartupLocation = WindowStartupLocation.Manual,
			Left = dip.X - grabX,
			Top = dip.Y - grabY,
		};
		nw.Show();
		nw.attachtab(doc, 0, activate: true);
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

	void closetab(TabItem tab) {
		var doc = opentabs.FirstOrDefault(t => t.Tab == tab);
		if (doc == null) {
			tabs.Items.Remove(tab);
			syncempty();
			return;
		}
		// 取消进行中的异步加载
		doc.LoadGen++;
		doc.Loading = false;
		try { if (doc.Viewer != null) saveprogress(doc.Viewer); } catch { /* ignore */ }
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
	}

	void closecurrent() {
		if (tabs.SelectedItem is TabItem ti)
			closetab(ti);
	}

	void closeall() {
		foreach (var d in opentabs.ToList())
			closetab(d.Tab);
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
			lbstatus.Text = "就绪 · 打开 PDF / DOCX / XLSX，或拖放到窗口";
			lbpath.Text = "";
			if (lbpagetotal != null) lbpagetotal.Text = "/ 0";
			syncsideui();
			syncxlsxeditui();
			syncpdfeditui();
			if (!epage.IsKeyboardFocusWithin) {
				pageBoxSilent = true;
				epage.Text = "";
				pageBoxSilent = false;
			}
			return;
		}

		if (cur.Loading) {
			var name = Path.GetFileName(cur.Path);
			Title = $"{name} - DocviewWPF";
			lbstatus.Text = $"加载中… {name}";
			lbpath.Text = cur.Path ?? "";
			if (lbpagetotal != null) lbpagetotal.Text = "/ …";
			if (!epage.IsKeyboardFocusWithin) {
				pageBoxSilent = true;
				epage.Text = "";
				pageBoxSilent = false;
			}
			return;
		}

		if (!cur.Loaded || cur.Viewer == null) {
			var name = Path.GetFileName(cur.Path);
			Title = $"{name} - DocviewWPF";
			lbstatus.Text = "未加载 · 切换到此标签时打开";
			lbpath.Text = cur.Path;
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
		if (lbpagetotal != null) lbpagetotal.Text = $"/ {cur.Viewer.PageCount}";
		syncsideui();
		syncxlsxeditui();
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

	void toggleside() {
		var v = currentviewer();
		if (v == null) return;
		v.SetSidePanelVisible(!v.SidePanelVisible);
		syncsideui();
	}

	void syncsideui() {
		var v = currentviewer();
		var on = v != null && v.SidePanelVisible;
		try {
			if (mnside != null) mnside.IsChecked = on;
		} catch { /* ignore */ }
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

	void onpreviewkeydown(object sender, KeyEventArgs e) {
		var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

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
			if (currentviewer() is XlsxViewer) {
				savecurrentxlsx();
				e.Handled = true;
				return;
			}
			if (currentviewer() is PdfViewer) {
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
		var n = VisualTreeHelper.GetChildrenCount(root);
		for (var i = 0; i < n; i++) {
			var found = findscrollviewer(VisualTreeHelper.GetChild(root, i));
			if (found != null) return found;
		}
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
		if (cur == null || string.IsNullOrWhiteSpace(cur.Path)) return;
		try {
			// 取消旧加载
			cur.LoadGen++;
			cur.Loading = false;
			if (cur.Viewer != null) {
				try { cur.Viewer.Dispose(); } catch { /* ignore */ }
				cur.Viewer = null;
			}
			cur.Loaded = false;
			showloading(cur);
			ensureloaded(cur);
		} catch (Exception ex) {
			DocLog.Error("reloadcurrent", ex);
			lbstatus.Text = "重新加载失败";
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

	bool isinputfocused() {
		var fe = Keyboard.FocusedElement as DependencyObject;
		while (fe != null) {
			if (fe is TextBox || fe is RichTextBox || fe is PasswordBox)
				return true;
			fe = VisualTreeHelper.GetParent(fe);
		}
		return false;
	}

	protected override void OnClosed(EventArgs e) {
		foreach (var d in opentabs.ToList()) {
			try { d.Viewer?.Dispose(); } catch { /* ignore */ }
		}
		opentabs.Clear();
		base.OnClosed(e);
	}
}

sealed class DocTab {
	public string Path;
	public DocKind Kind;
	public TabItem Tab;
	/// <summary>标题栏上的 Tab 芯片 UI。</summary>
	public FrameworkElement HeaderUI;
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
}
