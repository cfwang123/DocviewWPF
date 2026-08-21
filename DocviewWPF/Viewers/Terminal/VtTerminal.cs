using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DocviewWPF;

/// <summary>
/// 简易 xterm 兼容 VT 终端：字符格缓冲 + ANSI 解析 + WPF 渲染。
/// 足以驱动多数 TUI（全屏、颜色、光标、备用屏、基础鼠标）。
/// </summary>
sealed class TerminalControl : FrameworkElement {
	const int MaxScrollback = 5000;

	readonly TerminalBuffer buf = new();
	readonly VtParser parser;
	readonly Typeface typeface;
	bool focused;
	double cellW = 8, cellH = 16;
	double fontSize = 13;
	int viewCols = 80, viewRows = 24;
	// 等宽优先；中文回退到 YaHei Mono / 新宋体，避免比例字体把网格挤乱
	string fontName = "Cascadia Mono, Sarasa Mono SC, Microsoft YaHei Mono, NSimSun, Consolas, Courier New";

	// 鼠标报告
	int lastMouseBtn = -1;
	// 输出合并：避免每个 Read 都全量重绘卡死 UI
	readonly object feedLock = new();
	readonly List<byte> feedPending = new();
	bool feedScheduled;
	const int FEED_PENDING_CAP = 512 * 1024; // 积压上限，防止解析/内存爆炸
	const int FEED_SLICE = 48 * 1024;        // 每帧最多解析字节，避免一次卡死 UI
	double dpiPx = 1.0;
	// 窗口缩放 → ConPTY Resize（防抖，结束时一定发最终行列）
	DispatcherTimer resizeNotifyTimer;
	int lastNotifiedCols = -1, lastNotifiedRows = -1;
	int pendingNotifyCols, pendingNotifyRows;
	// 字形缓存：key = (ch, brushHash, wide?) → FormattedText，按格绘制保证等宽网格
	readonly Dictionary<long, FormattedText> glyphCache = new();
	const int GLYPH_CACHE_MAX = 4096;

	// IME 组字串（光标处显示，未上屏前不写入 PTY）
	string imeComp = "";
	HwndSource imeHwndSrc;
	HwndSourceHook imeHook;
	/// <summary>正在主动 SetCandidate，忽略由此触发的 WM_IME_NOTIFY，防止死循环卡死/崩溃。</summary>
	bool imeSettingPos;
	int lastImePosTick;

	public event Action TitleChanged;
	public event Action<byte[]> Output; // 键盘/鼠标 → 写入 PTY
	public string WindowTitle { get; private set; } = "";

	/// <summary>系统输入法是否打开（中文等）。打开时字母键应交 IME，不可 ToUnicode 直发。</summary>
	public bool IsImeOpen {
		get {
			if (imeopen(this)) return true;
			// Imm 在尚未获焦时可能不准，辅以 WPF 状态
			try {
				return InputMethod.Current != null
					&& InputMethod.Current.ImeState == InputMethodState.On;
			} catch {
				return false;
			}
		}
	}

	/// <summary>
	/// 外部 IME 宿主（透明 TextBox）组字预览：在终端光标处画下划线串，不写 PTY。
	/// 传 null/空 清除。
	/// </summary>
	public void SetImeComposition(string text) {
		var t = text ?? "";
		if (imeComp == t) return;
		imeComp = t;
		try { InvalidateVisual(); } catch { /* ignore */ }
	}

	public TerminalControl() {
		parser = new VtParser(buf);
		parser.TitleChanged += t => {
			WindowTitle = t ?? "";
			try { TitleChanged?.Invoke(); } catch { /* ignore */ }
		};
		parser.Bell += () => { try { System.Media.SystemSounds.Beep.Play(); } catch { /* ignore */ } };
		Focusable = true;
		FocusVisualStyle = null;
		ClipToBounds = true;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		VerticalAlignment = VerticalAlignment.Stretch;
		typeface = new Typeface(new FontFamily(fontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;
		// 允许 IME（与 cmd/PowerShell 一致）
		InputMethod.SetIsInputMethodEnabled(this, true);
		try { InputMethod.SetPreferredImeState(this, InputMethodState.DoNotCare); } catch { /* ignore */ }

		// 组字生命周期：候选窗位置 + 组字串预览
		TextCompositionManager.AddPreviewTextInputStartHandler(this, onimeStart);
		TextCompositionManager.AddPreviewTextInputUpdateHandler(this, onimeUpdate);
		TextCompositionManager.AddPreviewTextInputHandler(this, onimeComplete);

		// 缩放停稳后再通知 ConPTY，避免拖窗时 Resize 风暴 + shell 狂刷
		resizeNotifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
		resizeNotifyTimer.Tick += (_, _) => flushsizenotify(force: false);

		Loaded += (_, _) => {
			recomputecell(notify: true);
			flushsizenotify(force: true);
			Keyboard.Focus(this);
			hookime();
			updateimepos();
		};
		Unloaded += (_, _) => unhookime();
		SizeChanged += (_, _) => {
			recomputecell(notify: true);
			if (!string.IsNullOrEmpty(imeComp))
				updateimepos();
		};
	}

	/// <summary>占满父容器，否则 ActualWidth 可能一直为 0，缩放无法驱动行列变化。</summary>
	protected override Size MeasureOverride(Size availableSize) {
		var w = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
			? 640 : Math.Max(0, availableSize.Width);
		var h = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height)
			? 400 : Math.Max(0, availableSize.Height);
		return new Size(w, h);
	}

	protected override Size ArrangeOverride(Size finalSize) {
		// 布局完成时按最终像素重算行列（比 SizeChanged 更可靠）
		applypixelsize(finalSize.Width, finalSize.Height, notify: true);
		return finalSize;
	}

	public void SetFontSize(double size) {
		fontSize = Math.Max(8, Math.Min(36, size));
		glyphCache.Clear();
		recomputecell(notify: true);
		flushsizenotify(force: true);
		InvalidateVisual();
	}

	/// <summary>自检：截图当前控件为 PNG 字节。</summary>
	public byte[] CapturePng() {
		try {
			var w = (int)Math.Ceiling(Math.Max(1, ActualWidth));
			var h = (int)Math.Ceiling(Math.Max(1, ActualHeight));
			if (w < 2 || h < 2) {
				w = Math.Max(w, (int)(viewCols * cellW));
				h = Math.Max(h, (int)(viewRows * cellH));
			}
			var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
			rtb.Render(this);
			var enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(rtb));
			using var ms = new MemoryStream();
			enc.Save(ms);
			return ms.ToArray();
		} catch (Exception ex) {
			DocLog.Warn("CapturePng: " + ex.Message);
			return null;
		}
	}

	/// <summary>自检：前几行单元格调试（含 U+ 与宽标记）。</summary>
	public string DumpCellsDebug(int maxRows = 5) {
		var sb = new StringBuilder();
		var n = Math.Min(maxRows, buf.Rows);
		for (var y = 0; y < n; y++) {
			sb.Append('[').Append(y).Append("] ");
			for (var x = 0; x < buf.Cols; x++) {
				var c = buf.Get(x, y);
				if (c.Ch == 0) { sb.Append('·'); continue; }
				if (c.Ch == ' ') { sb.Append('␣'); continue; }
				if (c.Ch >= 32 && c.Ch < 127) sb.Append(c.Ch);
				else sb.AppendFormat("U+{0:X4}", (int)c.Ch);
				if (TerminalBuffer.IsWide(c.Ch)) sb.Append('Ｗ');
			}
			sb.Append('\n');
		}
		return sb.ToString();
	}

	public int ViewCols => viewCols;
	public int ViewRows => viewRows;
	public double CellWidth => cellW;
	public double CellHeight => cellH;
	public int CaretCol => buf.CursorX;
	public int CaretRow => buf.CursorY;
	/// <summary>光标左上角相对控件的 DIP 坐标（供 IME 宿主定位）。</summary>
	public void GetCaretDip(out double x, out double y) {
		x = Math.Max(0, buf.CursorX) * cellW;
		y = Math.Max(0, buf.CursorY) * cellH;
	}
	/// <summary>光标变化后通知（IME 输入框跟焦）。</summary>
	public event Action CaretMoved;

	/// <summary>自检：把可见缓冲打成纯文本（宽字符续格 \0 跳过，行尾去空格）。</summary>
	public string DumpScreenText() {
		var sb = new StringBuilder(viewCols * viewRows + viewRows);
		for (var y = 0; y < buf.Rows; y++) {
			var lineEnd = buf.Cols;
			while (lineEnd > 0) {
				var c = buf.Get(lineEnd - 1, y).Ch;
				if (c != 0 && c != ' ') break;
				lineEnd--;
			}
			for (var x = 0; x < lineEnd; x++) {
				var ch = buf.Get(x, y).Ch;
				if (ch == 0) continue; // 宽字符第二格
				sb.Append(ch);
			}
			sb.Append('\n');
		}
		return sb.ToString();
	}

	/// <summary>自检：同步喂入并解析（不经 Dispatcher）。</summary>
	public void FeedSync(byte[] data) {
		if (data == null || data.Length == 0) return;
		parser.Feed(data);
		InvalidateVisual();
	}

	public void Reset() {
		buf.Reset(viewCols, viewRows);
		InvalidateVisual();
	}

	/// <summary>父级（Border）尺寸变化时强制按像素重算（FrameworkElement 有时收不到 SizeChanged）。</summary>
	public void NotifyParentSize(double width, double height) {
		applypixelsize(width, height, notify: true);
	}

	public void FocusTerminal() {
		try {
			if (!IsKeyboardFocusWithin)
				Keyboard.Focus(this);
		} catch { /* ignore */ }
	}

	public void Feed(byte[] data) {
		if (data == null || data.Length == 0) return;
		lock (feedLock) {
			// 积压过大：丢掉最旧的一半，保住实时输出（TUI 全屏重绘会自愈）
			if (feedPending.Count + data.Length > FEED_PENDING_CAP) {
				var drop = feedPending.Count / 2;
				if (drop > 0) feedPending.RemoveRange(0, drop);
			}
			feedPending.AddRange(data);
			if (feedScheduled) return;
			feedScheduled = true;
		}
		try {
			// Render 优先级：比 Background 更快响应按键回显，又不抢输入
			Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(flushfeed));
		} catch {
			lock (feedLock) { feedScheduled = false; }
		}
	}

	void flushfeed() {
		byte[] chunk;
		var more = false;
		lock (feedLock) {
			if (feedPending.Count == 0) {
				feedScheduled = false;
				return;
			}
			var take = Math.Min(FEED_SLICE, feedPending.Count);
			chunk = new byte[take];
			feedPending.CopyTo(0, chunk, 0, take);
			feedPending.RemoveRange(0, take);
			more = feedPending.Count > 0;
			if (!more) feedScheduled = false;
		}
		try {
			parser.Feed(chunk);
			InvalidateVisual();
			try { CaretMoved?.Invoke(); } catch { /* ignore */ }
			// 仅组字中才跟光标刷位置，避免每帧 Imm32
			if (focused && !string.IsNullOrEmpty(imeComp))
				updateimepos(force: true);
		} catch (Exception ex) {
			DocLog.Warn($"Terminal feed: {ex.Message}");
		}
		if (more) {
			try {
				Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(flushfeed));
			} catch {
				lock (feedLock) { feedScheduled = false; }
			}
		} else {
			// 解析期间又入队
			lock (feedLock) {
				if (feedPending.Count > 0 && !feedScheduled) {
					feedScheduled = true;
					try {
						Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(flushfeed));
					} catch { feedScheduled = false; }
				}
			}
		}
	}

	public void FeedText(string text) {
		if (string.IsNullOrEmpty(text)) return;
		Feed(Encoding.UTF8.GetBytes(text));
	}

	void recomputecell(bool notify) {
		applypixelsize(ActualWidth, ActualHeight, notify);
	}

	void applypixelsize(double w, double h, bool notify) {
		try {
			dpiPx = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		} catch { dpiPx = 1.0; }
		var ft = new FormattedText(
			"M",
			CultureInfo.CurrentCulture,
			FlowDirection.LeftToRight,
			typeface,
			fontSize,
			Brushes.White,
			dpiPx);
		// 用进一法偏小格子 → 行列略多，更接近真终端可视区
		cellW = Math.Max(5, Math.Floor(ft.WidthIncludingTrailingWhitespace + 0.001));
		if (cellW < 5) cellW = 5;
		cellH = Math.Max(8, Math.Floor(ft.Height + 0.001));
		if (cellH < 8) cellH = 8;
		if (double.IsNaN(w) || double.IsNaN(h) || w < 10 || h < 10) return;
		// 上限兼顾 TUI 与绘制成本（逐格绘制大屏会卡死）
		var nc = Math.Max(20, Math.Min(240, (int)(w / cellW)));
		var nr = Math.Max(5, Math.Min(80, (int)(h / cellH)));
		if (nc == viewCols && nr == viewRows) {
			// 像素变了但行列未变：无需 ResizePseudoConsole
			return;
		}
		viewCols = nc;
		viewRows = nr;
		// 本地缓冲先扩/缩，TUI 收到 ConPTY 尺寸事件后会全屏重绘
		buf.Resize(viewCols, viewRows);
		InvalidateVisual();
		if (!notify) return;
		pendingNotifyCols = viewCols;
		pendingNotifyRows = viewRows;
		// 防抖：拖拽缩放时合并；停稳后 flush
		try {
			resizeNotifyTimer.Stop();
			resizeNotifyTimer.Start();
		} catch {
			flushsizenotify(force: true);
		}
	}

	void flushsizenotify(bool force) {
		try { resizeNotifyTimer?.Stop(); } catch { /* ignore */ }
		var c = pendingNotifyCols > 0 ? pendingNotifyCols : viewCols;
		var r = pendingNotifyRows > 0 ? pendingNotifyRows : viewRows;
		if (c < 20) c = 20;
		if (r < 5) r = 5;
		if (!force && c == lastNotifiedCols && r == lastNotifiedRows)
			return;
		lastNotifiedCols = c;
		lastNotifiedRows = r;
		try { SizeChangedByUser?.Invoke(c, r); } catch { /* ignore */ }
	}

	/// <summary>用户改变终端行列时（供 ConPTY Resize）。</summary>
	public event Action<int, int> SizeChangedByUser;

	protected override void OnRender(DrawingContext dc) {
		base.OnRender(dc);
		var bg = TerminalPalette.DefaultBg;
		dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
		var rows = buf.Rows;
		var cols = buf.Cols;
		// 严格按单元格网格绘制（每字定位到 x*cellW），禁止整串 FormattedText 比例漂移
		// 中文宽字符占 2 格；续格 Ch==0 只画背景。勿用「下格 Ch==0 ⇒ 宽字符」——空格/BCE 空单元也是 Ch==0。
		for (var y = 0; y < rows; y++) {
			var py = y * cellH;
			for (var x = 0; x < cols; ) {
				var cell = buf.Get(x, y);
				// 续格：字头是宽字符时只画背景（属性跟字头）
				if (cell.Ch == 0) {
					if (x > 0) {
						var head = buf.Get(x - 1, y);
						if (head.Ch != 0 && TerminalBuffer.IsWide(head.Ch)) {
							// 字头已按 2 格画过，跳过
							x++;
							continue;
						}
					}
					// 真正的空单元 / BCE 空格被清成 \0 的单元：画背景
					var bgEmpty = TerminalPalette.GetBg(cell);
					if (bgEmpty != null)
						dc.DrawRectangle(bgEmpty, null, new Rect(x * cellW, py, cellW, cellH));
					x++;
					continue;
				}
				var wide = TerminalBuffer.IsWide(cell.Ch);
				var ncells = wide && x + 1 < cols ? 2 : 1;
				var rect = new Rect(x * cellW, py, ncells * cellW, cellH);
				var bgc = TerminalPalette.GetBg(cell);
				if (bgc != null)
					dc.DrawRectangle(bgc, null, rect);
				else if (cell.Inverse)
					dc.DrawRectangle(TerminalPalette.GetFg(cell), null, rect);
				if (cell.Ch != ' ') {
					var useFg = cell.Inverse
						? (bgc ?? TerminalPalette.DefaultBg)
						: TerminalPalette.GetFg(cell);
					// 裁剪在单元格内，防止宽字形盖住后面的 ASCII
					dc.PushClip(new RectangleGeometry(rect));
					drawglyphat(dc, cell.Ch, x * cellW, py, ncells, useFg);
					dc.Pop();
				}
				x += ncells;
			}
		}
		// IME 组字预览（光标处，下划线，未写入 PTY）。焦点常在透明 TextBox，不依赖 focused。
		if (!string.IsNullOrEmpty(imeComp)) {
			var cx = buf.CursorX;
			var cy = buf.CursorY;
			if (cx >= 0 && cy >= 0 && cy < rows) {
				var px = cx * cellW;
				var py = cy * cellH;
				var ft = new FormattedText(
					imeComp,
					CultureInfo.CurrentCulture,
					FlowDirection.LeftToRight,
					typeface,
					fontSize,
					new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
					dpiPx);
				var tw = Math.Max(cellW, ft.WidthIncludingTrailingWhitespace);
				dc.DrawRectangle(
					new SolidColorBrush(Color.FromArgb(0x66, 0x1E, 0x3A, 0x5F)),
					null,
					new Rect(px, py, tw + 2, cellH));
				dc.DrawText(ft, new Point(px, py));
				// 下划线
				dc.DrawRectangle(
					new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
					null,
					new Rect(px, py + cellH - 2, tw, 2));
			}
		}
		// 实心光标：始终绘制、不闪烁。焦点常在透明 IME 框，故不依赖 focused。
		if (buf.CursorVisible && string.IsNullOrEmpty(imeComp)) {
			var cx = Math.Max(0, Math.Min(buf.CursorX, Math.Max(0, cols - 1)));
			var cy = Math.Max(0, Math.Min(buf.CursorY, Math.Max(0, rows - 1)));
			var r = new Rect(cx * cellW, cy * cellH, Math.Max(2, cellW * 0.9), cellH);
			dc.DrawRectangle(TerminalPalette.CursorBrush, null, r);
		}
	}

	void drawglyphat(DrawingContext dc, char ch, double px, double py, int ncells, Brush brush) {
		if (brush == null) return;
		var ft = getglyph(ch, brush);
		// 垂直略居中
		var dy = Math.Max(0, (cellH - ft.Height) * 0.5);
		dc.DrawText(ft, new Point(px, py + dy));
	}

	FormattedText getglyph(char ch, Brush brush) {
		// brush 用颜色 hash；FormattedText 绑定 brush 引用
		var bh = brush.GetHashCode();
		var key = ((long)ch << 32) ^ (uint)bh;
		if (glyphCache.TryGetValue(key, out var hit)) return hit;
		var ft = new FormattedText(
			ch.ToString(),
			CultureInfo.CurrentCulture,
			FlowDirection.LeftToRight,
			typeface,
			fontSize,
			brush,
			dpiPx);
		ft.Trimming = TextTrimming.None;
		ft.MaxLineCount = 1;
		if (glyphCache.Count >= GLYPH_CACHE_MAX)
			glyphCache.Clear();
		glyphCache[key] = ft;
		return ft;
	}

	protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) {
		base.OnGotKeyboardFocus(e);
		focused = true;
		updateimepos();
		InvalidateVisual();
	}

	protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) {
		base.OnLostKeyboardFocus(e);
		focused = false;
		imeComp = "";
		InvalidateVisual();
	}

	protected override void OnMouseDown(MouseButtonEventArgs e) {
		base.OnMouseDown(e);
		Keyboard.Focus(this);
		updateimepos();
		e.Handled = true; // 避免主窗抢焦点/选中
		if (buf.MouseMode != 0) {
			var p = e.GetPosition(this);
			var col = (int)(p.X / cellW) + 1;
			var row = (int)(p.Y / cellH) + 1;
			var btn = e.ChangedButton == MouseButton.Left ? 0
				: e.ChangedButton == MouseButton.Middle ? 1
				: e.ChangedButton == MouseButton.Right ? 2 : 0;
			lastMouseBtn = btn;
			sendmouse(btn, col, row, press: true);
			CaptureMouse();
		}
	}

	protected override void OnMouseUp(MouseButtonEventArgs e) {
		base.OnMouseUp(e);
		if (buf.MouseMode != 0 && lastMouseBtn >= 0) {
			var p = e.GetPosition(this);
			var col = (int)(p.X / cellW) + 1;
			var row = (int)(p.Y / cellH) + 1;
			sendmouse(lastMouseBtn, col, row, press: false);
			lastMouseBtn = -1;
			e.Handled = true;
			if (IsMouseCaptured) ReleaseMouseCapture();
		}
	}

	protected override void OnMouseMove(MouseEventArgs e) {
		base.OnMouseMove(e);
		// 拖动报告（1002）
		if (buf.MouseMode >= 1002 && lastMouseBtn >= 0 && e.LeftButton == MouseButtonState.Pressed) {
			var p = e.GetPosition(this);
			var col = (int)(p.X / cellW) + 1;
			var row = (int)(p.Y / cellH) + 1;
			sendmouse(32 + lastMouseBtn, col, row, press: true); // motion
		}
	}

	void sendmouse(int btn, int col, int row, bool press) {
		col = Math.Max(1, Math.Min(col, viewCols));
		row = Math.Max(1, Math.Min(row, viewRows));
		if (buf.MouseSgr) {
			var b = btn;
			var m = press ? 'M' : 'm';
			if (!press && btn < 32) b = btn;
			emit($"\x1b[<{b};{col};{row}{m}");
		} else {
			// X10 编码有限
			var cb = 32 + btn;
			var cx = 32 + col;
			var cy = 32 + row;
			emit(new[] { (byte)0x1b, (byte)'[', (byte)'M', (byte)cb, (byte)cx, (byte)cy });
		}
	}

	// 输入由 MainWindow / ConsoleViewer 转发；IME 打开时放行组字键
	protected override void OnTextInput(TextCompositionEventArgs e) {
		base.OnTextInput(e);
		if (e.Handled) return;
		if (HandleTextInput(e.Text))
			e.Handled = true;
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e) {
		base.OnPreviewKeyDown(e);
		if (e.Handled) return;
		var k = e.Key == Key.System ? e.SystemKey : e.Key;
		var mods = Keyboard.Modifiers;
		var ctrl = (mods & ModifierKeys.Control) != 0;
		var alt = (mods & ModifierKeys.Alt) != 0;

		// Alt+F4 关闭窗口，勿注入 PTY
		if (alt && !ctrl && k == Key.F4)
			return;

		// 可打印键一律不抢 KeyDown → 走 TextInput（英文）或 IME 组字（中文）
		// 若此处 ToUnicode+Handled，IME 永远收不到键，且首次进标签更明显
		if (!ctrl && !alt && k != Key.ImeProcessed && k != Key.DeadCharProcessed) {
			if (isconsoleprintablekey(k) || (IsImeOpen && isimepassthroughkey(k))) {
				updateimepos(force: true);
				return;
			}
		}
		// Alt 组合用真实键（SystemKey），避免 Key.System 被直接丢掉
		if (HandleKeyDown(k, mods))
			e.Handled = true;
	}

	static bool isconsoleprintablekey(Key key) {
		if (key >= Key.A && key <= Key.Z) return true;
		if (key >= Key.D0 && key <= Key.D9) return true;
		if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;
		switch (key) {
			case Key.Space:
			case Key.OemMinus: case Key.OemPlus:
			case Key.OemOpenBrackets: case Key.OemCloseBrackets:
			case Key.OemPipe: case Key.OemSemicolon: case Key.OemQuotes:
			case Key.OemComma: case Key.OemPeriod: case Key.OemQuestion:
			case Key.OemTilde: case Key.OemBackslash:
			case Key.Divide: case Key.Multiply: case Key.Subtract:
			case Key.Add: case Key.Decimal:
				return true;
			default:
				return false;
		}
	}

	void onimeStart(object sender, TextCompositionEventArgs e) {
		imeComp = e.TextComposition?.CompositionText ?? e.Text ?? "";
		updateimepos(force: true);
		InvalidateVisual();
	}

	void onimeUpdate(object sender, TextCompositionEventArgs e) {
		// CompositionText = 未确认拼音/组字；Text 有时是空
		var c = e.TextComposition?.CompositionText;
		if (c == null) c = e.Text ?? "";
		imeComp = c;
		updateimepos(force: true);
		InvalidateVisual();
	}

	void onimeComplete(object sender, TextCompositionEventArgs e) {
		var t = e.Text;
		imeComp = "";
		InvalidateVisual();
		if (!string.IsNullOrEmpty(t) && t != "\0") {
			emit(t);
			e.Handled = true; // 避免 OnTextInput 再发一次
		}
		updateimepos(force: true);
	}

	/// <summary>
	/// 文本输入入口：IME 确认字 / 无 IME 时的字符。
	/// KeyDown 已 Handled 时 WPF 不再 TextInput，故不双发。
	/// </summary>
	public bool HandleTextInput(string text) {
		if (string.IsNullOrEmpty(text)) return false;
		if (text == "\0") return false;
		// 组字中间由 onimeUpdate 显示；最终字优先 onimeComplete（已 Handled 则不会到这）
		imeComp = "";
		emit(text);
		return true;
	}

	/// <summary>
	/// 按键入口。IME 打开且非 Ctrl 时，可打印/组字键返回 false（主窗勿 Handled）。
	/// 返回 true 表示已写入 PTY（主窗应 e.Handled=true）。
	/// </summary>
	public bool HandleKeyDown(Key key, ModifierKeys mods) {
		if (key == Key.ImeProcessed || key == Key.DeadCharProcessed || key == Key.System)
			return false;
		// 纯修饰键不消费，避免干扰
		if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
			|| key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin
			|| key == Key.CapsLock || key == Key.NumLock || key == Key.Scroll)
			return false;

		var ctrl = (mods & ModifierKeys.Control) != 0;
		var alt = (mods & ModifierKeys.Alt) != 0;
		var shift = (mods & ModifierKeys.Shift) != 0;

		// Windows 关窗：Alt+F4 不进 PTY
		if (alt && !ctrl && key == Key.F4)
			return false;

		// 可打印键不走 KeyDown（TextInput/IME）；双保险
		if (!ctrl && !alt && isconsoleprintablekey(key)) {
			updateimepos(force: true);
			return false;
		}
		// IME 打开时回车/退格/方向等也交给输入法
		if (!ctrl && !alt && IsImeOpen && isimepassthroughkey(key)) {
			updateimepos(force: true);
			return false;
		}

		// Ctrl+字母 / 常用编辑（允许同时 Shift，如 Ctrl+Shift+C 仍当 Ctrl+C 以外的不处理粘贴）
		if (ctrl && !alt) {
			if (key == Key.V && !shift) {
				try {
					if (Clipboard.ContainsText()) {
						var t = Clipboard.GetText();
						// 换行统一为 \r（cmd/ConPTY）
						t = t.Replace("\r\n", "\r").Replace("\n", "\r");
						if (buf.BracketedPaste)
							emit("\x1b[200~" + t + "\x1b[201~");
						else
							emit(t);
					}
				} catch { /* ignore */ }
				return true;
			}
			if (key >= Key.A && key <= Key.Z) {
				// Ctrl+A..Z → 0x01..0x1A（含 Ctrl+C 中断、Ctrl+L 清屏、Ctrl+P 等）
				emit(((char)(key - Key.A + 1)).ToString());
				return true;
			}
			if (key == Key.Space) {
				emit("\0");
				return true;
			}
			if (key == Key.Back) {
				emit("\x7f");
				return true;
			}
			// 其它 Ctrl 组合不映射则仍吞掉，避免触发主窗快捷键
			return true;
		}

		string seq = null;
		var app = buf.ApplicationCursor;
		switch (key) {
			case Key.Return: // 与 Key.Enter 同值
				seq = "\r"; break;
			case Key.Tab:
				seq = shift ? "\x1b[Z" : "\t";
				break;
			// ConPTY/xterm 常见退格为 DEL(0x7f)
			case Key.Back: seq = "\x7f"; break;
			case Key.Escape: seq = "\x1b"; break;
			case Key.Space: seq = " "; break;
			case Key.Up: seq = app ? "\x1bOA" : "\x1b[A"; break;
			case Key.Down: seq = app ? "\x1bOB" : "\x1b[B"; break;
			case Key.Right: seq = app ? "\x1bOC" : "\x1b[C"; break;
			case Key.Left: seq = app ? "\x1bOD" : "\x1b[D"; break;
			case Key.Home: seq = app ? "\x1bOH" : "\x1b[H"; break;
			case Key.End: seq = app ? "\x1bOF" : "\x1b[F"; break;
			case Key.Insert: seq = "\x1b[2~"; break;
			case Key.Delete: seq = "\x1b[3~"; break;
			case Key.PageUp: seq = "\x1b[5~"; break;
			case Key.PageDown: seq = "\x1b[6~"; break;
			case Key.F1: seq = "\x1bOP"; break;
			case Key.F2: seq = "\x1bOQ"; break;
			case Key.F3: seq = "\x1bOR"; break;
			case Key.F4: seq = "\x1bOS"; break;
			case Key.F5: seq = "\x1b[15~"; break;
			case Key.F6: seq = "\x1b[17~"; break;
			case Key.F7: seq = "\x1b[18~"; break;
			case Key.F8: seq = "\x1b[19~"; break;
			case Key.F9: seq = "\x1b[20~"; break;
			case Key.F10: seq = "\x1b[21~"; break;
			case Key.F11: seq = "\x1b[23~"; break;
			case Key.F12: seq = "\x1b[24~"; break;
		}
		if (seq != null) {
			emit(seq);
			return true;
		}

		// 可打印字符：ToUnicode（含 Shift/布局）；主窗 Handled 后 TextInput 不会来
		if (!ctrl && !alt) {
			if (trytounicode(key, out var text) && text.Length > 0) {
				emit(text);
				return true;
			}
			// 回退：A-Z / 0-9（ToUnicode 偶发失败时仍可输入）
			if (trykeyfallback(key, shift, out var fb)) {
				emit(fb);
				return true;
			}
		}
		return false;
	}

	static bool trykeyfallback(Key key, bool shift, out string text) {
		text = null;
		if (key >= Key.A && key <= Key.Z) {
			var c = (char)('a' + (key - Key.A));
			var caps = false;
			try { caps = Keyboard.IsKeyToggled(Key.CapsLock); } catch { /* ignore */ }
			if (shift ^ caps) c = char.ToUpperInvariant(c);
			text = c.ToString();
			return true;
		}
		if (key >= Key.D0 && key <= Key.D9) {
			if (!shift) {
				text = ((char)('0' + (key - Key.D0))).ToString();
				return true;
			}
			// 美式布局 Shift+数字
			var sh = ")!@#$%^&*(";
			text = sh[key - Key.D0].ToString();
			return true;
		}
		if (key >= Key.NumPad0 && key <= Key.NumPad9) {
			text = ((char)('0' + (key - Key.NumPad0))).ToString();
			return true;
		}
		switch (key) {
			case Key.OemMinus: text = shift ? "_" : "-"; return true;
			case Key.OemPlus: text = shift ? "+" : "="; return true;
			case Key.OemOpenBrackets: text = shift ? "{" : "["; return true;
			case Key.OemCloseBrackets: text = shift ? "}" : "]"; return true;
			case Key.OemPipe: text = shift ? "|" : "\\"; return true;
			case Key.OemSemicolon: text = shift ? ":" : ";"; return true;
			case Key.OemQuotes: text = shift ? "\"" : "'"; return true;
			case Key.OemComma: text = shift ? "<" : ","; return true;
			case Key.OemPeriod: text = shift ? ">" : "."; return true;
			case Key.OemQuestion: text = shift ? "?" : "/"; return true;
			case Key.OemTilde: text = shift ? "~" : "`"; return true;
			case Key.Divide: text = "/"; return true;
			case Key.Multiply: text = "*"; return true;
			case Key.Subtract: text = "-"; return true;
			case Key.Add: text = "+"; return true;
			case Key.Decimal: text = "."; return true;
		}
		return false;
	}

	static bool trytounicode(Key key, out string text) {
		text = null;
		try {
			var vk = KeyInterop.VirtualKeyFromKey(key);
			if (vk <= 0) return false;
			// 跳过纯修饰 VK
			if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B || vk == 0x5C)
				return false;
			var state = new byte[256];
			if (!GetKeyboardState(state)) return false;
			// 强制清 Ctrl/Alt 残态，避免 ToUnicode 把字母变成控制符
			state[0x11] = 0; // VK_CONTROL
			state[0x12] = 0; // VK_MENU
			state[0xA2] = 0; state[0xA3] = 0; // L/R Ctrl
			state[0xA4] = 0; state[0xA5] = 0; // L/R Alt
			var scan = MapVirtualKey((uint)vk, 0);
			var sb = new StringBuilder(8);
			var rc = ToUnicode((uint)vk, scan, state, sb, sb.Capacity, 0);
			if (rc < 0) {
				// dead key：再调一次清空状态，本键不输出
				ToUnicode((uint)vk, scan, state, sb, sb.Capacity, 0);
				return false;
			}
			if (rc > 0) {
				text = sb.ToString(0, rc);
				if (text.Length == 1 && char.IsControl(text[0]) && text[0] != '\t' && text[0] != '\r')
					return false;
				return text.Length > 0;
			}
		} catch { /* ignore */ }
		return false;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	static extern bool GetKeyboardState(byte[] lpKeyState);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	static extern uint MapVirtualKey(uint uCode, uint uMapType);

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
		StringBuilder pwszBuff, int cchBuff, uint wFlags);

	void emit(string s) {
		if (string.IsNullOrEmpty(s)) return;
		try { Output?.Invoke(Encoding.UTF8.GetBytes(s)); } catch { /* ignore */ }
	}

	void emit(byte[] data) {
		if (data == null || data.Length == 0) return;
		try { Output?.Invoke(data); } catch { /* ignore */ }
	}

	/// <summary>IME 打开时交还给输入法的键（空格/回车/退格/字母数字/符号）。</summary>
	static bool isimepassthroughkey(Key key) {
		if (key >= Key.A && key <= Key.Z) return true;
		if (key >= Key.D0 && key <= Key.D9) return true;
		if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;
		switch (key) {
			case Key.Space:
			case Key.Return:
			case Key.Back:
			case Key.Delete:
			case Key.Left:
			case Key.Right:
			case Key.Up:
			case Key.Down:
			case Key.Home:
			case Key.End:
			case Key.Escape:
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

	/// <summary>ImmGetOpenStatus：输入法是否处于开启（中文）状态。</summary>
	static bool imeopen(UIElement v) {
		try {
			if (v == null || !v.IsVisible) return false;
			var src = PresentationSource.FromVisual(v) as HwndSource;
			if (src == null || src.Handle == IntPtr.Zero) return false;
			var himc = ImmGetContext(src.Handle);
			if (himc == IntPtr.Zero) return false;
			try {
				return ImmGetOpenStatus(himc);
			} finally {
				ImmReleaseContext(src.Handle, himc);
			}
		} catch {
			return false;
		}
	}

	void hookime() {
		try {
			unhookime();
			imeHwndSrc = PresentationSource.FromVisual(this) as HwndSource;
			if (imeHwndSrc == null) return;
			imeHook = imeWndProc;
			imeHwndSrc.AddHook(imeHook);
		} catch { /* ignore */ }
	}

	void unhookime() {
		try {
			if (imeHwndSrc != null && imeHook != null)
				imeHwndSrc.RemoveHook(imeHook);
		} catch { /* ignore */ }
		imeHook = null;
		imeHwndSrc = null;
	}

	/// <summary>
	/// 拦截 IME 消息。注意：ImmSetCandidateWindow 会再触发 WM_IME_NOTIFY，
	/// 若在 NOTIFY 里再 Set → 死循环卡死 UI 直至崩溃。
	/// </summary>
	IntPtr imeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
		try {
			// 我们自己 Set 时产生的通知一律忽略
			if (imeSettingPos) return IntPtr.Zero;

			switch (msg) {
				case WM_IME_STARTCOMPOSITION:
					// 组字开始：安全地钉一次位置
					updateimepos();
					break;
				case WM_IME_REQUEST:
					// 现代 IME/TSF：询问光标字符位置（不主动 Set，只填结构）
					if (wParam.ToInt32() == IMR_QUERYCHARPOSITION && lParam != IntPtr.Zero) {
						if (fillimecharpos(lParam)) {
							handled = true;
							return (IntPtr)1;
						}
					}
					break;
				// 不再在 WM_IME_NOTIFY / COMPOSITION 里 SetCandidate，避免重入死循环
			}
		} catch { /* ignore */ }
		return IntPtr.Zero;
	}

	/// <summary>响应 IMR_QUERYCHARPOSITION：只写回坐标，不调用 ImmSet*。</summary>
	bool fillimecharpos(IntPtr lParam) {
		try {
			var need = Marshal.SizeOf(typeof(IMECHARPOSITION));
			var pos = Marshal.PtrToStructure<IMECHARPOSITION>(lParam);
			// 结构太小则放弃，避免写爆内存
			if (pos.dwSize != 0 && pos.dwSize < need) return false;
			if (!trycaretclient(out var pt, out var lineH, out var doc))
				return false;
			pos.pt = pt;
			pos.cLineHeight = (uint)Math.Max(1, lineH);
			pos.rcDocument = doc;
			if (pos.dwSize == 0) pos.dwSize = (uint)need;
			Marshal.StructureToPtr(pos, lParam, false);
			return true;
		} catch {
			return false;
		}
	}

	/// <summary>
	/// 终端光标 → HWND 客户区设备像素 + 行高 + 文档矩形。
	/// 用 TransformToAncestor + TransformToDevice，避免 PointToScreen(DIP) 与 ScreenToClient(物理像素) 混用导致选字框飞到左上角。
	/// </summary>
	bool trycaretclient(out POINT pt, out int lineH, out RECT doc) {
		pt = default;
		lineH = (int)Math.Ceiling(cellH);
		doc = default;
		if (!IsVisible || cellW < 1 || cellH < 1) return false;
		var src = PresentationSource.FromVisual(this) as HwndSource;
		if (src?.RootVisual == null || src.CompositionTarget == null) return false;

		var cx = Math.Max(0, Math.Min(buf.CursorX, Math.Max(0, viewCols - 1)));
		var cy = Math.Max(0, Math.Min(buf.CursorY, Math.Max(0, viewRows - 1)));
		var compCols = 0;
		if (!string.IsNullOrEmpty(imeComp)) {
			foreach (var ch in imeComp)
				compCols += TerminalBuffer.IsWide(ch) ? 2 : 1;
		}
		// 光标格左下（下一行顶）— 候选窗通常贴在组字下方
		var local = new Point((cx + compCols) * cellW, (cy + 1) * cellH);

		GeneralTransform toRoot;
		try {
			toRoot = TransformToAncestor(src.RootVisual);
		} catch {
			return false; // 尚未挂到可视树
		}
		var rootPt = toRoot.Transform(local);
		// RootVisual 坐标 = 客户区 DIP → 设备像素（Imm32 要客户区物理像素）
		var toDev = src.CompositionTarget.TransformToDevice;
		var dev = toDev.Transform(rootPt);
		pt = new POINT { X = (int)Math.Round(dev.X), Y = (int)Math.Round(dev.Y) };
		lineH = Math.Max(1, (int)Math.Ceiling(cellH * toDev.M22));

		var r0 = toDev.Transform(toRoot.Transform(new Point(0, 0)));
		var r1 = toDev.Transform(toRoot.Transform(new Point(
			Math.Max(1, ActualWidth), Math.Max(1, ActualHeight))));
		doc = new RECT {
			Left = (int)Math.Round(Math.Min(r0.X, r1.X)),
			Top = (int)Math.Round(Math.Min(r0.Y, r1.Y)),
			Right = (int)Math.Round(Math.Max(r0.X, r1.X)),
			Bottom = (int)Math.Round(Math.Max(r0.Y, r1.Y)),
		};
		return true;
	}

	/// <summary>切入命令行标签时调用：聚焦 + IME 钩子 + 候选窗位置。</summary>
	public void EnsureImeReady() {
		try {
			focused = true;
			InputMethod.SetIsInputMethodEnabled(this, true);
			try { InputMethod.SetPreferredImeState(this, InputMethodState.DoNotCare); } catch { /* ignore */ }
			// 从 WebView2 等 HWND 抢回 WPF 焦点
			var win = Window.GetWindow(this);
			if (win != null) {
				try { win.Activate(); } catch { /* ignore */ }
				try { FocusManager.SetFocusedElement(win, this); } catch { /* ignore */ }
			}
			Keyboard.Focus(this);
			Focus();
			hookime();
			lastImePosTick = 0; // 允许立即定位
			updateimepos(force: true);
		} catch { /* ignore */ }
	}

	/// <summary>把候选窗/组字窗钉到终端光标下方。带防重入 + 节流。</summary>
	void updateimepos(bool force = false) {
		if (imeSettingPos) return;
		var now = Environment.TickCount;
		if (!force && lastImePosTick != 0 && unchecked(now - lastImePosTick) < 50)
			return;
		lastImePosTick = now;

		try {
			if (!IsVisible) return;
			var src = PresentationSource.FromVisual(this) as HwndSource;
			if (src == null || src.Handle == IntPtr.Zero) return;
			if (!trycaretclient(out var pt, out var lineH, out var doc)) return;
			// 防御：坐标异常时不 Set（避免飞到 0,0）
			if (pt.X < -10000 || pt.Y < -10000 || pt.X > 100000 || pt.Y > 100000)
				return;

			var hwnd = src.Handle;
			var himc = ImmGetContext(hwnd);
			if (himc == IntPtr.Zero) return;

			imeSettingPos = true;
			try {
				var form = new COMPOSITIONFORM {
					dwStyle = CFS_POINT | CFS_FORCE_POSITION,
					ptCurrentPos = pt,
					rcArea = doc,
				};
				ImmSetCompositionWindow(himc, ref form);

				for (uint i = 0; i < 4; i++) {
					var cand = new CANDIDATEFORM {
						dwIndex = i,
						dwStyle = CFS_CANDIDATEPOS,
						ptCurrentPos = pt,
						rcArea = doc,
					};
					ImmSetCandidateWindow(himc, ref cand);
				}
			} finally {
				imeSettingPos = false;
				try { ImmReleaseContext(hwnd, himc); } catch { /* ignore */ }
			}
		} catch {
			imeSettingPos = false;
		}
	}

	public void DisposeResources() {
		try { resizeNotifyTimer?.Stop(); } catch { /* ignore */ }
		unhookime();
		try {
			TextCompositionManager.RemovePreviewTextInputStartHandler(this, onimeStart);
			TextCompositionManager.RemovePreviewTextInputUpdateHandler(this, onimeUpdate);
			TextCompositionManager.RemovePreviewTextInputHandler(this, onimeComplete);
		} catch { /* ignore */ }
	}

	// ---------- Imm32 / IME 消息 ----------
	const int WM_IME_STARTCOMPOSITION = 0x010D;
	const int WM_IME_COMPOSITION = 0x010F;
	const int WM_IME_NOTIFY = 0x0282;
	const int WM_IME_REQUEST = 0x0288;
	const int IMR_QUERYCHARPOSITION = 0x0006;
	const int IMN_OPENCANDIDATE = 0x0005;
	const int IMN_CHANGECANDIDATE = 0x0003;
	const int IMN_SETCANDIDATEPOS = 0x0009;
	const int IMN_SETCOMPOSITIONWINDOW = 0x000B;

	const uint CFS_RECT = 0x0001;
	const uint CFS_POINT = 0x0002;
	const uint CFS_FORCE_POSITION = 0x0020;
	const uint CFS_CANDIDATEPOS = 0x0040;
	const uint CFS_EXCLUDE = 0x0080;

	[StructLayout(LayoutKind.Sequential)]
	struct POINT {
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct RECT {
		public int Left, Top, Right, Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct COMPOSITIONFORM {
		public uint dwStyle;
		public POINT ptCurrentPos;
		public RECT rcArea;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct CANDIDATEFORM {
		public uint dwIndex;
		public uint dwStyle;
		public POINT ptCurrentPos;
		public RECT rcArea;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct IMECHARPOSITION {
		public uint dwSize;
		public uint dwCharPos;
		public POINT pt;
		public uint cLineHeight;
		public RECT rcDocument;
	}

	[DllImport("imm32.dll")]
	static extern IntPtr ImmGetContext(IntPtr hWnd);

	[DllImport("imm32.dll")]
	static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

	[DllImport("imm32.dll")]
	static extern bool ImmGetOpenStatus(IntPtr hIMC);

	[DllImport("imm32.dll")]
	static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompForm);

	[DllImport("imm32.dll")]
	static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CANDIDATEFORM lpCandidate);

	[DllImport("user32.dll")]
	static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
}

// ---------- 缓冲 / 解析 / 调色板 ----------

struct TermCell {
	public char Ch;
	public int Fg;   // -1 default, 0-255 indexed, or 0x1000000|RRGGBB
	public int Bg;
	public byte Attr; // bit0 bold, bit1 dim, bit2 underline, bit3 inverse, bit4 italic
	public bool Bold => (Attr & 1) != 0;
	public bool Inverse => (Attr & 8) != 0;

	/// <summary>空单元：默认前景/背景（切勿用 default，否则 Fg/Bg=0 被当成 ANSI 黑）。</summary>
	public static TermCell Empty => new() { Ch = '\0', Fg = -1, Bg = -1, Attr = 0 };
}

sealed class TerminalBuffer {
	public int Cols { get; private set; } = 80;
	public int Rows { get; private set; } = 24;
	public int CursorX { get; set; }
	public int CursorY { get; set; }
	public bool CursorVisible { get; set; } = true;
	public bool ApplicationCursor { get; set; }
	public bool BracketedPaste { get; set; }
	public bool OriginMode { get; set; }
	public bool AutoWrap { get; set; } = true;
	public int MouseMode { get; set; } // 0 off, 1000, 1002, 1003
	public bool MouseSgr { get; set; }
	public int ScrollTop { get; set; }
	public int ScrollBottom { get; set; }

	TermCell[] cells; // row-major
	TermCell[] altCells;
	bool altScreen;
	// 当前画笔
	public int CurFg = -1;
	public int CurBg = -1;
	public byte CurAttr;
	// 保存光标
	int savX, savY, savFg, savBg;
	byte savAttr;

	public TerminalBuffer() {
		cells = new TermCell[Cols * Rows];
		ScrollBottom = Rows - 1;
		clearfull(cells);
	}

	public void Reset(int cols, int rows) {
		Cols = Math.Max(20, cols);
		Rows = Math.Max(5, rows);
		cells = new TermCell[Cols * Rows];
		altCells = null;
		altScreen = false;
		clearfull(cells);
		CursorX = CursorY = 0;
		CurFg = CurBg = -1;
		CurAttr = 0;
		ScrollTop = 0;
		ScrollBottom = Rows - 1;
		CursorVisible = true;
		ApplicationCursor = false;
		BracketedPaste = false;
		MouseMode = 0;
		MouseSgr = false;
	}

	public void Resize(int cols, int rows) {
		cols = Math.Max(20, cols);
		rows = Math.Max(5, rows);
		if (cols == Cols && rows == Rows) return;
		var old = cells;
		var oc = Cols;
		var orr = Rows;
		var nc = new TermCell[cols * rows];
		clearfull(nc);
		// 保留重叠区域内容；TUI 全屏重绘后会覆盖
		var copyR = Math.Min(orr, rows);
		var copyC = Math.Min(oc, cols);
		for (var y = 0; y < copyR; y++)
			for (var x = 0; x < copyC; x++)
				nc[y * cols + x] = old[y * oc + x];
		cells = nc;
		if (altCells != null) {
			var oa = altCells;
			var na = new TermCell[cols * rows];
			clearfull(na);
			for (var y = 0; y < copyR; y++)
				for (var x = 0; x < copyC; x++)
					na[y * cols + x] = oa[y * oc + x];
			altCells = na;
		}
		Cols = cols;
		Rows = rows;
		// 滚动区随窗口变；TUI 会再发 DECSTBM
		ScrollTop = 0;
		ScrollBottom = Rows - 1;
		if (CursorX >= Cols) CursorX = Cols - 1;
		if (CursorY >= Rows) CursorY = Rows - 1;
	}

	static void clearfull(TermCell[] a) {
		var e = TermCell.Empty;
		for (var i = 0; i < a.Length; i++)
			a[i] = e;
	}

	public TermCell Get(int x, int y) {
		if (x < 0 || y < 0 || x >= Cols || y >= Rows) return TermCell.Empty;
		return cells[y * Cols + x];
	}

	/// <summary>BCE：用当前画笔填空格（nvim/TUI 擦除/滚行依赖背景色，不能 default 成透明黑块）。</summary>
	public TermCell BceCell() => new() {
		Ch = ' ',
		Fg = CurFg,
		Bg = CurBg,
		Attr = CurAttr,
	};

	void set(int x, int y, TermCell c) {
		if (x < 0 || y < 0 || x >= Cols || y >= Rows) return;
		cells[y * Cols + x] = c;
	}

	public void PutCellRaw(int x, int y, TermCell c) => set(x, y, c);

	public void PutChar(char ch) {
		if (ch == '\r') { CursorX = 0; return; }
		if (ch == '\n') { linefeed(); return; }
		if (ch == '\b') {
			// 退格：若落在宽字符续格，回到字头并清掉整字
			if (CursorX > 0) CursorX--;
			if (CursorX < Cols && Get(CursorX, CursorY).Ch == 0 && CursorX > 0) {
				CursorX--;
				set(CursorX, CursorY, TermCell.Empty);
				if (CursorX + 1 < Cols) set(CursorX + 1, CursorY, TermCell.Empty);
			}
			return;
		}
		if (ch == '\t') {
			var n = 8 - (CursorX % 8);
			for (var i = 0; i < n; i++) PutChar(' ');
			return;
		}
		if (ch == '\a') return;
		if (ch < 32 && ch != 0) return;

		var wide = IsWide(ch);
		// 宽字符在行尾放不下：先换行（对齐常见终端）
		if (wide && CursorX >= Cols - 1) {
			if (AutoWrap) {
				CursorX = 0;
				linefeed();
			} else {
				CursorX = Math.Max(0, Cols - 2);
			}
		}
		if (CursorX >= Cols) {
			if (AutoWrap) {
				CursorX = 0;
				linefeed();
			} else {
				CursorX = Cols - 1;
			}
		}

		// 覆盖写：清掉被踩到的宽字符头/尾，避免留下孤儿续格导致显示错位
		clearcellslot(CursorX);
		if (wide && CursorX + 1 < Cols)
			clearcellslot(CursorX + 1);

		var cell = new TermCell {
			Ch = ch,
			Fg = CurFg,
			Bg = CurBg,
			Attr = CurAttr,
		};
		set(CursorX, CursorY, cell);
		if (wide) {
			CursorX++;
			if (CursorX < Cols)
				set(CursorX, CursorY, new TermCell { Ch = '\0', Fg = CurFg, Bg = CurBg, Attr = CurAttr });
		}
		CursorX++;
	}

	/// <summary>清一格；若是宽字符头则连续格清，若是续格则连字头清。</summary>
	void clearcellslot(int x) {
		if (x < 0 || x >= Cols) return;
		var c = Get(x, CursorY);
		if (c.Ch == 0) {
			// 续格 → 清字头
			if (x > 0) {
				var h = Get(x - 1, CursorY);
				if (h.Ch != 0 && IsWide(h.Ch))
					set(x - 1, CursorY, TermCell.Empty);
			}
			set(x, CursorY, TermCell.Empty);
			return;
		}
		if (IsWide(c.Ch) && x + 1 < Cols)
			set(x + 1, CursorY, TermCell.Empty);
		set(x, CursorY, TermCell.Empty);
	}

	/// <summary>East Asian Width：W/F 为双列（与 Windows 控制台一致）。</summary>
	public static bool IsWide(char ch) {
		if (ch < 0x1100) return false;
		// Hangul Jamo
		if (ch <= 0x115F) return true;
		if (ch == 0x2329 || ch == 0x232A) return true;
		// CJK Radicals .. Yi
		if (ch >= 0x2E80 && ch <= 0xA4CF) return true;
		// Hangul Syllables
		if (ch >= 0xAC00 && ch <= 0xD7A3) return true;
		// CJK Compatibility Ideographs
		if (ch >= 0xF900 && ch <= 0xFAFF) return true;
		// Vertical forms
		if (ch >= 0xFE10 && ch <= 0xFE19) return true;
		if (ch >= 0xFE30 && ch <= 0xFE6F) return true;
		// Fullwidth Forms
		if (ch >= 0xFF00 && ch <= 0xFF60) return true;
		if (ch >= 0xFFE0 && ch <= 0xFFE6) return true;
		// CJK Unified
		if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
		// Extension A
		if (ch >= 0x3400 && ch <= 0x4DBF) return true;
		return false;
	}

	public void LineFeed() => linefeed();

	void linefeed() {
		if (CursorY >= ScrollBottom) {
			scrollup(1);
			CursorY = ScrollBottom;
		} else {
			CursorY++;
		}
	}

	public void ReverseIndex() {
		if (CursorY <= ScrollTop)
			scrolldown(1);
		else
			CursorY--;
	}

	public void scrollup(int n) {
		n = Math.Max(1, n);
		var top = ScrollTop;
		var bot = ScrollBottom;
		var h = bot - top + 1;
		if (h <= 0 || Cols <= 0) return;
		if (n >= h) {
			for (var y = top; y <= bot; y++)
				clearrow(y);
			return;
		}
		// 整行 Array.Copy，避免逐格拷贝在大量输出时卡死
		var src = (top + n) * Cols;
		var dst = top * Cols;
		var len = (h - n) * Cols;
		Array.Copy(cells, src, cells, dst, len);
		for (var y = bot - n + 1; y <= bot; y++)
			clearrow(y);
	}

	public void scrolldown(int n) {
		n = Math.Max(1, n);
		var top = ScrollTop;
		var bot = ScrollBottom;
		var h = bot - top + 1;
		if (h <= 0 || Cols <= 0) return;
		if (n >= h) {
			for (var y = top; y <= bot; y++)
				clearrow(y);
			return;
		}
		var src = top * Cols;
		var dst = (top + n) * Cols;
		var len = (h - n) * Cols;
		Array.Copy(cells, src, cells, dst, len);
		for (var y = top; y < top + n; y++)
			clearrow(y);
	}

	void clearrow(int y) {
		if (y < 0 || y >= Rows) return;
		// 滚行空白行用 BCE，与 xterm/Windows Terminal 一致（nvim 状态栏/分屏依赖）
		var fill = BceCell();
		for (var x = 0; x < Cols; x++)
			set(x, y, fill);
	}

	public void EraseInDisplay(int mode) {
		var fill = BceCell();
		if (mode == 0) { // 光标到末尾
			EraseInLine(0);
			for (var y = CursorY + 1; y < Rows; y++)
				for (var x = 0; x < Cols; x++) set(x, y, fill);
		} else if (mode == 1) {
			for (var y = 0; y < CursorY; y++)
				for (var x = 0; x < Cols; x++) set(x, y, fill);
			EraseInLine(1);
		} else {
			for (var y = 0; y < Rows; y++)
				for (var x = 0; x < Cols; x++) set(x, y, fill);
			if (mode == 3) { /* 清 scrollback：无 */ }
		}
	}

	public void EraseInLine(int mode) {
		var fill = BceCell();
		if (mode == 0) {
			for (var x = CursorX; x < Cols; x++) set(x, CursorY, fill);
		} else if (mode == 1) {
			for (var x = 0; x <= CursorX && x < Cols; x++) set(x, CursorY, fill);
		} else {
			for (var x = 0; x < Cols; x++) set(x, CursorY, fill);
		}
	}

	public void DeleteChars(int n) {
		n = Math.Max(1, n);
		var fill = BceCell();
		for (var x = CursorX; x < Cols; x++) {
			var src = x + n;
			set(x, CursorY, src < Cols ? Get(src, CursorY) : fill);
		}
	}

	public void InsertChars(int n) {
		n = Math.Max(1, n);
		var fill = BceCell();
		for (var x = Cols - 1; x >= CursorX + n; x--)
			set(x, CursorY, Get(x - n, CursorY));
		for (var x = CursorX; x < CursorX + n && x < Cols; x++)
			set(x, CursorY, fill);
	}

	public void DeleteLines(int n) {
		n = Math.Max(1, n);
		var top = CursorY;
		var bot = ScrollBottom;
		var h = bot - top + 1;
		if (h <= 0) return;
		if (n >= h) {
			for (var y = top; y <= bot; y++) clearrow(y);
			return;
		}
		Array.Copy(cells, (top + n) * Cols, cells, top * Cols, (h - n) * Cols);
		for (var y = bot - n + 1; y <= bot; y++) clearrow(y);
	}

	public void InsertLines(int n) {
		n = Math.Max(1, n);
		var top = CursorY;
		var bot = ScrollBottom;
		var h = bot - top + 1;
		if (h <= 0) return;
		if (n >= h) {
			for (var y = top; y <= bot; y++) clearrow(y);
			return;
		}
		Array.Copy(cells, top * Cols, cells, (top + n) * Cols, (h - n) * Cols);
		for (var y = top; y < top + n; y++) clearrow(y);
	}

	public void SetCursor(int x, int y) {
		CursorX = Math.Max(0, Math.Min(Cols - 1, x));
		CursorY = Math.Max(0, Math.Min(Rows - 1, y));
	}

	public void SaveCursor() {
		savX = CursorX; savY = CursorY;
		savFg = CurFg; savBg = CurBg; savAttr = CurAttr;
	}

	public void RestoreCursor() {
		CursorX = savX; CursorY = savY;
		CurFg = savFg; CurBg = savBg; CurAttr = savAttr;
	}

	public void EnterAltScreen() {
		if (altScreen) return;
		altCells = cells;
		cells = new TermCell[Cols * Rows];
		clearfull(cells);
		altScreen = true;
		CursorX = CursorY = 0;
	}

	public void ExitAltScreen() {
		if (!altScreen) return;
		cells = altCells ?? new TermCell[Cols * Rows];
		altCells = null;
		altScreen = false;
	}
}

sealed class VtParser {
	readonly TerminalBuffer buf;
	enum State { Ground, Esc, Csi, Osc, OscEsc, Dcs, DcsEsc, Charset }
	State state = State.Ground;
	readonly List<int> csiParams = new();
	readonly StringBuilder csiCollect = new();
	/// <summary>OSC 载荷按字节收集，结束时 UTF-8 解码（标题中文否则乱码）。</summary>
	readonly List<byte> oscBytes = new();
	int csiParam = -1;
	bool csiQ;
	byte[] utfBuf = new byte[4];
	int utfNeed, utfGot;
	readonly Decoder utf8 = Encoding.UTF8.GetDecoder();

	public event Action<string> TitleChanged;
	public event Action Bell;

	public VtParser(TerminalBuffer buf) { this.buf = buf; }

	public void Feed(byte[] data) {
		if (data == null) return;
		for (var i = 0; i < data.Length; i++)
			feedbyte(data[i]);
	}

	void feedbyte(byte b) {
		// UTF-8 组装（仅 Ground 态输出字符）
		if (state == State.Ground) {
			if (utfNeed > 0) {
				if ((b & 0xC0) != 0x80) { utfNeed = utfGot = 0; /* 重同步 */ }
				else {
					utfBuf[utfGot++] = b;
					if (utfGot >= utfNeed) {
						var chars = new char[2];
						var n = utf8.GetChars(utfBuf, 0, utfGot, chars, 0);
						utfNeed = utfGot = 0;
						for (var k = 0; k < n; k++)
							buf.PutChar(chars[k]);
					}
					return;
				}
			}
			if (b >= 0x80) {
				if ((b & 0xE0) == 0xC0) { utfBuf[0] = b; utfNeed = 2; utfGot = 1; return; }
				if ((b & 0xF0) == 0xE0) { utfBuf[0] = b; utfNeed = 3; utfGot = 1; return; }
				if ((b & 0xF8) == 0xF0) { utfBuf[0] = b; utfNeed = 4; utfGot = 1; return; }
			}
		}

		switch (state) {
			case State.Ground:
				if (b == 0x1B) { state = State.Esc; return; }
				if (b == 0x07) { try { Bell?.Invoke(); } catch { /* ignore */ } return; }
				if (b == 0x08) { buf.PutChar('\b'); return; }
				if (b == 0x09) { buf.PutChar('\t'); return; }
				if (b == 0x0A || b == 0x0B || b == 0x0C) { buf.LineFeed(); return; }
				if (b == 0x0D) { buf.PutChar('\r'); return; }
				if (b == 0x0E || b == 0x0F) return; // SI/SO
				if (b >= 0x20)
					buf.PutChar((char)b);
				return;

			case State.Esc:
				if (b == '[') {
					state = State.Csi;
					csiParams.Clear();
					csiParam = -1;
					csiQ = false;
					csiCollect.Clear();
					return;
				}
				if (b == ']') { state = State.Osc; oscBytes.Clear(); return; }
				if (b == 'P') { state = State.Dcs; return; }
				if (b == '7') { buf.SaveCursor(); state = State.Ground; return; }
				if (b == '8') { buf.RestoreCursor(); state = State.Ground; return; }
				if (b == 'c') { buf.Reset(buf.Cols, buf.Rows); state = State.Ground; return; }
				if (b == 'D') { buf.LineFeed(); state = State.Ground; return; }
				if (b == 'E') { buf.PutChar('\r'); buf.LineFeed(); state = State.Ground; return; }
				if (b == 'M') { buf.ReverseIndex(); state = State.Ground; return; }
				if (b == '(' || b == ')' || b == '*' || b == '+') { state = State.Charset; return; }
				if (b == '=') { state = State.Ground; return; } // app keypad
				if (b == '>') { state = State.Ground; return; }
				state = State.Ground;
				return;

			case State.Charset:
				state = State.Ground;
				return;

			case State.Csi:
				if (b == '?') { csiQ = true; return; }
				if (b == '>') { csiCollect.Append('>'); return; }
				if (b == '!') { csiCollect.Append('!'); return; }
				if (b >= '0' && b <= '9') {
					if (csiParam < 0) csiParam = 0;
					csiParam = csiParam * 10 + (b - '0');
					return;
				}
				// ';' 标准参数分隔；':' ISO 8613-6 子参数（nvim truecolor: 38:2:R:G:B）
				if (b == ';' || b == ':') {
					csiParams.Add(csiParam < 0 ? 0 : csiParam);
					csiParam = -1;
					return;
				}
				if (b >= 0x20 && b <= 0x2F) { csiCollect.Append((char)b); return; }
				// 终结
				if (csiParam >= 0) csiParams.Add(csiParam);
				csiParam = -1;
				execcsi((char)b);
				state = State.Ground;
				return;

			case State.Osc:
				if (b == 0x07) { execosc(); state = State.Ground; return; }
				if (b == 0x1B) { state = State.OscEsc; return; }
				if (b == 0x00) return;
				oscBytes.Add(b);
				// 限长
				if (oscBytes.Count > 8192) { state = State.Ground; oscBytes.Clear(); }
				return;

			case State.OscEsc:
				if (b == '\\') { execosc(); state = State.Ground; return; }
				state = State.Ground;
				return;

			case State.Dcs:
				if (b == 0x1B) { state = State.DcsEsc; return; }
				return;
			case State.DcsEsc:
				if (b == '\\') { state = State.Ground; return; }
				state = State.Dcs;
				return;
		}
	}

	int p(int idx, int def = 0) {
		if (idx < 0 || idx >= csiParams.Count) return def;
		var v = csiParams[idx];
		return v == 0 && def != 0 ? def : v;
	}

	void execcsi(char cmd) {
		if (csiQ) {
			// 私有模式
			var n = p(0, 0);
			if (cmd == 'h') setmode(n, true);
			else if (cmd == 'l') setmode(n, false);
			return;
		}
		switch (cmd) {
			case 'A': buf.SetCursor(buf.CursorX, buf.CursorY - Math.Max(1, p(0, 1))); break;
			case 'B': buf.SetCursor(buf.CursorX, buf.CursorY + Math.Max(1, p(0, 1))); break;
			case 'C': buf.SetCursor(buf.CursorX + Math.Max(1, p(0, 1)), buf.CursorY); break;
			case 'D': buf.SetCursor(buf.CursorX - Math.Max(1, p(0, 1)), buf.CursorY); break;
			case 'E':
				buf.SetCursor(0, buf.CursorY + Math.Max(1, p(0, 1)));
				break;
			case 'F':
				buf.SetCursor(0, buf.CursorY - Math.Max(1, p(0, 1)));
				break;
			case 'G':
				buf.SetCursor(Math.Max(1, p(0, 1)) - 1, buf.CursorY);
				break;
			case 'H':
			case 'f': {
				var row = Math.Max(1, p(0, 1)) - 1;
				var col = Math.Max(1, p(1, 1)) - 1;
				buf.SetCursor(col, row);
				break;
			}
			case 'J': buf.EraseInDisplay(p(0, 0)); break;
			case 'K': buf.EraseInLine(p(0, 0)); break;
			case 'L': buf.InsertLines(Math.Max(1, p(0, 1))); break;
			case 'M': buf.DeleteLines(Math.Max(1, p(0, 1))); break;
			case 'P': buf.DeleteChars(Math.Max(1, p(0, 1))); break;
			case '@': buf.InsertChars(Math.Max(1, p(0, 1))); break;
			case 'S': buf.scrollup(Math.Max(1, p(0, 1))); break;
			case 'T': buf.scrolldown(Math.Max(1, p(0, 1))); break;
			case 'd':
				buf.SetCursor(buf.CursorX, Math.Max(1, p(0, 1)) - 1);
				break;
			case 'm': appliesgr(); break;
			case 'n':
				// DSR — 忽略或可回报告
				break;
			case 'r': {
				var top = Math.Max(1, p(0, 1)) - 1;
				var bot = Math.Max(1, p(1, buf.Rows)) - 1;
				if (bot < top) bot = top;
				buf.ScrollTop = Math.Min(top, buf.Rows - 1);
				buf.ScrollBottom = Math.Min(bot, buf.Rows - 1);
				buf.SetCursor(0, 0);
				break;
			}
			case 's': buf.SaveCursor(); break;
			case 'u': buf.RestoreCursor(); break;
			case 'X': { // 擦除 n 字符（不移动光标，BCE）
				var n = Math.Max(1, p(0, 1));
				var x0 = buf.CursorX;
				var y0 = buf.CursorY;
				var fill = buf.BceCell();
				for (var i = 0; i < n && x0 + i < buf.Cols; i++)
					buf.PutCellRaw(x0 + i, y0, fill);
				break;
			}
		}
	}

	void setmode(int n, bool set) {
		switch (n) {
			case 1: buf.ApplicationCursor = set; break;
			case 7: buf.AutoWrap = set; break;
			case 25: buf.CursorVisible = set; break;
			case 1000: buf.MouseMode = set ? 1000 : 0; break;
			case 1002: buf.MouseMode = set ? 1002 : 0; break;
			case 1003: buf.MouseMode = set ? 1003 : 0; break;
			case 1006: buf.MouseSgr = set; break;
			case 2004: buf.BracketedPaste = set; break;
			case 1047:
			case 1049:
				if (set) {
					if (n == 1049) buf.SaveCursor();
					buf.EnterAltScreen();
					if (n == 1049) buf.EraseInDisplay(2);
				} else {
					buf.ExitAltScreen();
					if (n == 1049) buf.RestoreCursor();
				}
				break;
			case 1048:
				if (set) buf.SaveCursor(); else buf.RestoreCursor();
				break;
		}
	}

	void appliesgr() {
		if (csiParams.Count == 0) {
			buf.CurAttr = 0; buf.CurFg = -1; buf.CurBg = -1;
			return;
		}
		for (var i = 0; i < csiParams.Count; i++) {
			var p = csiParams[i];
			switch (p) {
				case 0: buf.CurAttr = 0; buf.CurFg = -1; buf.CurBg = -1; break;
				case 1: buf.CurAttr |= 1; break;
				case 2: buf.CurAttr |= 2; break;
				case 3: buf.CurAttr |= 16; break;
				case 4: buf.CurAttr |= 4; break;
				case 7: buf.CurAttr |= 8; break;
				case 22: buf.CurAttr = (byte)(buf.CurAttr & ~3); break;
				case 23: buf.CurAttr = (byte)(buf.CurAttr & ~16); break;
				case 24: buf.CurAttr = (byte)(buf.CurAttr & ~4); break;
				case 27: buf.CurAttr = (byte)(buf.CurAttr & ~8); break;
				case 39: buf.CurFg = -1; break;
				case 49: buf.CurBg = -1; break;
				case int n when n >= 30 && n <= 37: buf.CurFg = n - 30; break;
				case int n when n >= 40 && n <= 47: buf.CurBg = n - 40; break;
				case int n when n >= 90 && n <= 97: buf.CurFg = n - 90 + 8; break;
				case int n when n >= 100 && n <= 107: buf.CurBg = n - 100 + 8; break;
				case 38:
				case 48: {
					var isFg = p == 38;
					if (i + 1 >= csiParams.Count) break;
					var mode = csiParams[++i];
					if (mode == 5 && i + 1 < csiParams.Count) {
						// 256 色：38;5;N 或 38:5:N
						var idx = csiParams[++i];
						if (idx < 0) idx = 0;
						if (idx > 255) idx = 255;
						if (isFg) buf.CurFg = idx; else buf.CurBg = idx;
					} else if (mode == 2) {
						// truecolor：38;2;R;G;B 或 38:2:Cs:R:G:B / 38:2::R:G:B
						var left = csiParams.Count - i - 1;
						if (left >= 4)
							i++; // 跳过 color space id
						if (i + 3 < csiParams.Count) {
							var r = csiParams[++i] & 255;
							var g = csiParams[++i] & 255;
							var b = csiParams[++i] & 255;
							var rgb = 0x1000000 | (r << 16) | (g << 8) | b;
							if (isFg) buf.CurFg = rgb; else buf.CurBg = rgb;
						}
					}
					break;
				}
			}
		}
	}

	void execosc() {
		// ConPTY 标题为 UTF-8；逐字节当 char 会把「管理员」变成 ç®¡çå 乱码
		string s;
		try {
			s = Encoding.UTF8.GetString(oscBytes.ToArray());
		} catch {
			s = Encoding.Default.GetString(oscBytes.ToArray());
		}
		oscBytes.Clear();
		// 0;title  2;title  1;icon
		var semi = s.IndexOf(';');
		if (semi <= 0) return;
		var code = s.Substring(0, semi);
		var val = s.Substring(semi + 1);
		if (code == "0" || code == "2" || code == "1") {
			// 去掉控制字符，避免 Tab 标题异常
			if (!string.IsNullOrEmpty(val)) {
				var sb = new StringBuilder(val.Length);
				foreach (var ch in val) {
					if (ch >= 32 || ch == '\t') sb.Append(ch);
				}
				val = sb.ToString().Trim();
			}
			if (string.IsNullOrEmpty(val)) return;
			try { TitleChanged?.Invoke(val); } catch { /* ignore */ }
		}
	}
}

static class TerminalPalette {
	static readonly Color[] Ansi16 = {
		Color.FromRgb(0x0C, 0x0C, 0x0C),
		Color.FromRgb(0xC5, 0x0F, 0x1F),
		Color.FromRgb(0x13, 0xA1, 0x0E),
		Color.FromRgb(0xC1, 0x9C, 0x00),
		Color.FromRgb(0x00, 0x37, 0xDA),
		Color.FromRgb(0x88, 0x17, 0x98),
		Color.FromRgb(0x3A, 0x96, 0xDD),
		Color.FromRgb(0xCC, 0xCC, 0xCC),
		Color.FromRgb(0x76, 0x76, 0x76),
		Color.FromRgb(0xE7, 0x48, 0x56),
		Color.FromRgb(0x16, 0xC6, 0x0C),
		Color.FromRgb(0xF9, 0xF1, 0xA5),
		Color.FromRgb(0x3B, 0x78, 0xFF),
		Color.FromRgb(0xB4, 0x00, 0x9E),
		Color.FromRgb(0x61, 0xD6, 0xD6),
		Color.FromRgb(0xF2, 0xF2, 0xF2),
	};

	public static readonly SolidColorBrush DefaultBg;
	public static readonly SolidColorBrush DefaultFg;
	public static readonly SolidColorBrush CursorBrush;
	static readonly SolidColorBrush[] brush16 = new SolidColorBrush[16];
	static readonly Dictionary<int, SolidColorBrush> cache = new();

	static TerminalPalette() {
		DefaultBg = freeze(new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)));
		DefaultFg = freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
		CursorBrush = freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xCC, 0xCC, 0xCC)));
		for (var i = 0; i < 16; i++)
			brush16[i] = freeze(new SolidColorBrush(Ansi16[i]));
	}

	static SolidColorBrush freeze(SolidColorBrush b) {
		if (b.CanFreeze) b.Freeze();
		return b;
	}

	public static bool IsDefaultBg(TermCell c) => c.Bg < 0 && !c.Inverse;

	public static Brush GetFg(TermCell c) {
		var v = c.Inverse ? c.Bg : c.Fg;
		// inverse 且对侧为 default：用默认底/字色
		if (c.Inverse && v < 0) return DefaultBg;
		if (!c.Inverse && v < 0) return DefaultFg;
		return colorof(v, DefaultFg, c.Bold && !c.Inverse);
	}

	public static Brush GetBg(TermCell c) {
		var v = c.Inverse ? c.Fg : c.Bg;
		// default 背景不单独填（底层已是 DefaultBg）；inverse 时 default 前景→默认字色作底
		if (v < 0) {
			if (c.Inverse) return DefaultFg;
			return null;
		}
		return colorof(v, DefaultBg, false);
	}

	static Brush colorof(int v, Brush def, bool boldLift) {
		if (v < 0) return def;
		if ((v & 0x1000000) != 0) {
			var rgb = v & 0xFFFFFF;
			lock (cache) {
				if (cache.TryGetValue(rgb | 0x1000000, out var br)) return br;
				var col = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
				br = freeze(new SolidColorBrush(col));
				cache[rgb | 0x1000000] = br;
				return br;
			}
		}
		if (v < 16) {
			var idx = v;
			if (boldLift && idx < 8) idx += 8;
			return brush16[idx];
		}
		// 256 色
		if (v < 256) {
			lock (cache) {
				if (cache.TryGetValue(v, out var br)) return br;
				var col = color256(v);
				br = freeze(new SolidColorBrush(col));
				cache[v] = br;
				return br;
			}
		}
		return def;
	}

	static Color color256(int idx) {
		if (idx < 16) return Ansi16[idx];
		if (idx < 232) {
			idx -= 16;
			var r = idx / 36;
			var g = (idx / 6) % 6;
			var b = idx % 6;
			byte c6(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
			return Color.FromRgb(c6(r), c6(g), c6(b));
		}
		var gray = (byte)(8 + (idx - 232) * 10);
		return Color.FromRgb(gray, gray, gray);
	}
}
