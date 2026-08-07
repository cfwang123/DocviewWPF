using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DocviewWPF;

/// <summary>
/// 浏览器标签：Edge WebView2 内核 + 地址栏。空白页可输入 URL 加载。
/// </summary>
sealed class BrowserViewer : IDocViewer {
	const double MIN_ZOOM = 0.25;
	const double MAX_ZOOM = 5.0;
	const double WHEEL_FACTOR = 1.1;

	readonly Grid root;
	readonly TextBox eurl;
	readonly Button bback;
	readonly Button bfwd;
	readonly Button breload;
	readonly Button bgo;
	readonly TextBlock lbstatus;
	readonly WebView2 web;

	bool ready;
	bool disposed;
	bool eventsWired;
	string currentUrl = "about:blank";
	string pageTitle = "新标签页";
	double zoom = 1.0;
	string pendingNavigate;
	/// <summary>单飞初始化，避免 Load 与 Loaded 并发 Ensure 导致环境冲突。</summary>
	Task ensureTask;

	public FrameworkElement View => root;
	public string FilePath => currentUrl ?? "about:blank";
	public string Title => string.IsNullOrWhiteSpace(pageTitle) ? "新标签页" : pageTitle;
	public DocKind Kind => DocKind.Browser;
	public double Zoom => zoom;
	public string StatusText {
		get {
			var u = currentUrl ?? "";
			if (u.Length > 80) u = u.Substring(0, 77) + "…";
			return $"浏览器  ·  {(int)(zoom * 100)}%  ·  {u}";
		}
	}
	public int PageCount => 1;
	public int CurrentPage => 1;
	public bool HasOutline => false;
	public bool SidePanelVisible => false;

	public event Action StatusChanged;
	/// <summary>标题或 URL 变化（主窗刷新 Tab 名）。</summary>
	public event Action MetaChanged;
	/// <summary>页面请求新窗口（target=_blank 等）→ 主窗开浏览器标签。</summary>
	public event Action<string> OpenInNewTab;

	public BrowserViewer() {
		bback = navbtn("←", "后退");
		bfwd = navbtn("→", "前进");
		breload = navbtn("↻", "刷新");
		bgo = navbtn("→", "转到");
		bgo.Content = "转到";
		bgo.Width = 48;
		bgo.FontSize = 12;

		eurl = new TextBox {
			Height = 26,
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(8, 2, 8, 2),
			FontSize = 12.5,
			BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
			BorderThickness = new Thickness(1),
			Background = Brushes.White,
			ToolTip = "输入网址后回车加载",
		};
		eurl.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) {
				gonavigate();
				e.Handled = true;
			}
		};
		eurl.GotKeyboardFocus += (_, _) => {
			try { eurl.SelectAll(); } catch { /* ignore */ }
		};

		bback.Click += (_, _) => { try { web.CoreWebView2?.GoBack(); } catch { /* ignore */ } };
		bfwd.Click += (_, _) => { try { web.CoreWebView2?.GoForward(); } catch { /* ignore */ } };
		breload.Click += (_, _) => {
			try {
				if (web.CoreWebView2 != null) web.CoreWebView2.Reload();
				else gonavigate();
			} catch { /* ignore */ }
		};
		bgo.Click += (_, _) => gonavigate();

		var bar = new DockPanel { Margin = new Thickness(6, 4, 6, 4) };
		var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		left.Children.Add(bback);
		left.Children.Add(bfwd);
		left.Children.Add(breload);
		DockPanel.SetDock(left, Dock.Left);
		DockPanel.SetDock(bgo, Dock.Right);
		bar.Children.Add(left);
		bar.Children.Add(bgo);
		bar.Children.Add(eurl);

		lbstatus = new TextBlock {
			Text = "准备就绪 · 在地址栏输入网址",
			FontSize = 11,
			Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
			Margin = new Thickness(10, 0, 10, 4),
			TextTrimming = TextTrimming.CharacterEllipsis,
		};

		web = new WebView2 {
			DefaultBackgroundColor = System.Drawing.Color.White,
		};

		var top = new StackPanel();
		top.Children.Add(bar);
		top.Children.Add(lbstatus);

		root = new Grid { Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)) };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		Grid.SetRow(top, 0);
		Grid.SetRow(web, 1);
		root.Children.Add(top);
		root.Children.Add(web);

		setnavenabled(false);
		// 仅补一次初始化；真正导航由 Load / gonavigate 触发
		root.Loaded += (_, _) => { _ = ensureasync(); };
	}

	static Button navbtn(string text, string tip) {
		return new Button {
			Content = text,
			ToolTip = tip,
			Width = 30,
			Height = 26,
			Margin = new Thickness(0, 0, 4, 0),
			Padding = new Thickness(0),
			FontSize = 13,
			Cursor = Cursors.Hand,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			VerticalAlignment = VerticalAlignment.Center,
		};
	}

	public void Load(string path) {
		// path: about:blank / browser:… / http(s)://…
		if (string.IsNullOrWhiteSpace(path)
			|| path.StartsWith("browser:", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(path, "about:blank", StringComparison.OrdinalIgnoreCase)) {
			currentUrl = "about:blank";
			pageTitle = "新标签页";
			pendingNavigate = null;
			try { eurl.Text = ""; } catch { /* ignore */ }
			raisemeta();
			_ = ensureandshowblankasync();
			return;
		}
		pendingNavigate = path.Trim();
		currentUrl = pendingNavigate;
		try { eurl.Text = pendingNavigate; } catch { /* ignore */ }
		_ = ensureandnavasync(pendingNavigate);
	}

	async Task ensureandshowblankasync() {
		await ensureasync().ConfigureAwait(true);
		if (disposed || web.CoreWebView2 == null) return;
		try {
			web.CoreWebView2.Navigate("about:blank");
		} catch (Exception ex) {
			DocLog.Warn($"Browser blank: {ex.Message}");
		}
		try {
			eurl.Focus();
			eurl.SelectAll();
		} catch { /* ignore */ }
	}

	async Task ensureandnavasync(string url) {
		await ensureasync().ConfigureAwait(true);
		if (disposed || web.CoreWebView2 == null) return;
		try {
			web.CoreWebView2.Navigate(normalizeurl(url));
		} catch (Exception ex) {
			DocLog.Warn($"Browser nav: {ex.Message}");
			lbstatus.Text = "无法打开: " + ex.Message;
		}
	}

	Task ensureasync() {
		if (disposed) return Task.CompletedTask;
		if (ready && web.CoreWebView2 != null) return Task.CompletedTask;
		if (ensureTask != null) return ensureTask;
		ensureTask = ensurecoreasync();
		return ensureTask;
	}

	async Task ensurecoreasync() {
		if (disposed) return;
		try {
			// 已由默认路径初始化过：只接线，勿再传不同 Environment
			if (web.CoreWebView2 != null) {
				wireevents(web.CoreWebView2);
				try { web.ZoomFactor = zoom; } catch { /* ignore */ }
				ready = true;
				return;
			}
			var env = await WebView2Env.GetAsync().ConfigureAwait(true);
			if (disposed) return;
			// 二次进入时 CoreWebView2 可能已就绪
			if (web.CoreWebView2 == null)
				await web.EnsureCoreWebView2Async(env).ConfigureAwait(true);
			else
				DocLog.Info("Browser WebView2 already inited, reuse");
			if (disposed || web.CoreWebView2 == null) return;
			wireevents(web.CoreWebView2);
			try { web.ZoomFactor = zoom; } catch { /* ignore */ }
			ready = true;
			DocLog.Info("Browser WebView2 ready");
			if (!string.IsNullOrEmpty(pendingNavigate)) {
				var u = pendingNavigate;
				pendingNavigate = null;
				try { web.CoreWebView2.Navigate(normalizeurl(u)); } catch { /* ignore */ }
			}
		} catch (Exception ex) {
			DocLog.Warn($"Browser WebView2 init fail: {ex.Message}");
			lbstatus.Text = "WebView2 初始化失败: " + ex.Message;
			// 可恢复：允许下次再试
			ensureTask = null;
			ready = false;
			try {
				MessageBox.Show(
					"无法启动浏览器内核（WebView2）。请确认已安装 Microsoft Edge WebView2 运行时。\n\n" + ex.Message,
					"DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Warning);
			} catch { /* ignore */ }
		}
	}

	void wireevents(CoreWebView2 core) {
		if (core == null || eventsWired) return;
		eventsWired = true;
		core.Settings.AreDefaultContextMenusEnabled = true;
		core.Settings.IsStatusBarEnabled = false;
		core.Settings.AreDevToolsEnabled = true;
		core.Settings.IsZoomControlEnabled = false;
		core.Settings.AreDefaultScriptDialogsEnabled = true;
		core.NavigationStarting += (_, e) => {
			if (string.IsNullOrEmpty(e.Uri)) return;
			try { eurl.Text = e.Uri; } catch { /* ignore */ }
			lbstatus.Text = "加载中… " + e.Uri;
			StatusChanged?.Invoke();
		};
		core.NavigationCompleted += (_, e) => {
			try {
				var src = core.Source ?? currentUrl;
				currentUrl = src;
				try { eurl.Text = string.Equals(src, "about:blank", StringComparison.OrdinalIgnoreCase) ? "" : src; }
				catch { /* ignore */ }
				if (!string.IsNullOrEmpty(core.DocumentTitle)
					&& !string.Equals(core.DocumentTitle, "about:blank", StringComparison.OrdinalIgnoreCase))
					pageTitle = core.DocumentTitle;
				else if (string.Equals(src, "about:blank", StringComparison.OrdinalIgnoreCase))
					pageTitle = "新标签页";
				else
					pageTitle = src;
				lbstatus.Text = e.IsSuccess
					? ("完成  ·  " + pageTitle)
					: ("加载失败  ·  " + (src ?? ""));
				setnavenabled(true);
				raisemeta();
				StatusChanged?.Invoke();
			} catch (Exception ex) {
				DocLog.Warn($"Browser nav done: {ex.Message}");
			}
		};
		core.DocumentTitleChanged += (_, _) => {
			try {
				var t = core.DocumentTitle;
				if (!string.IsNullOrWhiteSpace(t)
					&& !string.Equals(t, "about:blank", StringComparison.OrdinalIgnoreCase)) {
					pageTitle = t;
					raisemeta();
					StatusChanged?.Invoke();
				}
			} catch { /* ignore */ }
		};
		core.HistoryChanged += (_, _) => setnavenabled(true);
		core.SourceChanged += (_, _) => {
			try {
				currentUrl = core.Source ?? currentUrl;
				raisemeta();
			} catch { /* ignore */ }
		};
		// 弹窗 / target=_blank → 应用内标签，勿开系统浏览器或独立窗口
		core.NewWindowRequested += (_, e) => {
			try {
				e.Handled = true;
				var u = e.Uri;
				if (string.IsNullOrWhiteSpace(u)) return;
				if (OpenInNewTab != null)
					OpenInNewTab(u);
				else
					Navigate(u);
			} catch (Exception ex) {
				DocLog.Warn($"Browser NewWindow: {ex.Message}");
			}
		};
	}

	/// <summary>外部请求导航到 url（已 Ensure 后调用）。</summary>
	public void Navigate(string url) {
		if (string.IsNullOrWhiteSpace(url)) return;
		url = normalizeurl(url.Trim());
		currentUrl = url;
		try { eurl.Text = url; } catch { /* ignore */ }
		if (!ready || web.CoreWebView2 == null) {
			pendingNavigate = url;
			_ = ensureandnavasync(url);
			return;
		}
		try { web.CoreWebView2.Navigate(url); } catch (Exception ex) {
			DocLog.Warn($"Browser Navigate: {ex.Message}");
		}
	}

	void gonavigate() {
		var t = (eurl.Text ?? "").Trim();
		if (string.IsNullOrEmpty(t)) {
			try { eurl.Focus(); } catch { /* ignore */ }
			return;
		}
		var url = normalizeurl(t);
		currentUrl = url;
		if (!ready || web.CoreWebView2 == null) {
			pendingNavigate = url;
			_ = ensureandnavasync(url);
			return;
		}
		try {
			web.CoreWebView2.Navigate(url);
		} catch (Exception ex) {
			lbstatus.Text = "无法打开: " + ex.Message;
			DocLog.Warn($"Browser Navigate: {ex.Message}");
		}
	}

	/// <summary>补全协议；含空格或无点的输入走 Bing 搜索。</summary>
	static string normalizeurl(string t) {
		if (string.IsNullOrWhiteSpace(t)) return "about:blank";
		t = t.Trim();
		if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			|| t.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
			|| t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
			|| t.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
			return t;
		// 像域名：example.com / www.x.com/a
		if (t.IndexOf(' ') < 0 && t.IndexOf('.') >= 0 && !t.StartsWith(".", StringComparison.Ordinal))
			return "https://" + t;
		// 搜索
		return "https://www.bing.com/search?q=" + Uri.EscapeDataString(t);
	}

	void setnavenabled(bool on) {
		try {
			var c = web.CoreWebView2;
			bback.IsEnabled = on && c != null && c.CanGoBack;
			bfwd.IsEnabled = on && c != null && c.CanGoForward;
			breload.IsEnabled = on || true;
		} catch {
			bback.IsEnabled = false;
			bfwd.IsEnabled = false;
		}
	}

	void raisemeta() {
		try { MetaChanged?.Invoke(); } catch { /* ignore */ }
	}

	public void SetZoom(double z) {
		z = clamp(z, MIN_ZOOM, MAX_ZOOM);
		if (Math.Abs(z - zoom) < 1e-9) return;
		zoom = z;
		try { if (ready) web.ZoomFactor = zoom; } catch { /* ignore */ }
		StatusChanged?.Invoke();
	}

	public void ZoomBy(double factor) => SetZoom(zoom * factor);
	public void ZoomIn() => SetZoom(zoom * WHEEL_FACTOR);
	public void ZoomOut() => SetZoom(zoom / WHEEL_FACTOR);
	public void ZoomFitWidth() => SetZoom(1);
	public void ZoomFitPage() => SetZoom(1);
	public void GoPrevPage() {
		try { web.CoreWebView2?.GoBack(); } catch { /* ignore */ }
	}
	public void GoNextPage() {
		try { web.CoreWebView2?.GoForward(); } catch { /* ignore */ }
	}
	public void GoToPage(int page1Based) { }
	public void RotateBy(int deltaQuarterTurns) { }
	public void SetSidePanelVisible(bool show) { }

	public void CaptureViewState(out double h, out double v, out double z, out int sheetOrPage) {
		h = 0; v = 0; z = zoom; sheetOrPage = 0;
	}

	public void RestoreViewState(double h, double v, double z, int sheetOrPage) {
		if (z > 0.01) SetZoom(z);
	}

	public FindResult Find(string text, bool forward, bool ignoreCase, bool restart = false, bool fromView = false) {
		// 简单交给页面 Ctrl+F 原生；此处不实现宿主查找
		return FindResult.Miss();
	}

	public void ClearFind() { }

	public bool TryCopySelection() {
		try {
			web.CoreWebView2?.ExecuteScriptAsync(
				"document.execCommand('copy')");
			return true;
		} catch { return false; }
	}

	public void Dispose() {
		if (disposed) return;
		disposed = true;
		try { web.Dispose(); } catch { /* ignore */ }
	}

	static double clamp(double v, double lo, double hi) =>
		v < lo ? lo : (v > hi ? hi : v);
}
