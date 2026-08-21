using System;
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
/// 命令行标签：ConPTY 真伪终端 + VT 渲染，支持 TUI（opencode / grok 等）。
/// </summary>
sealed class ConsoleViewer : IDocViewer {
	const double MIN_ZOOM = 0.7;
	const double MAX_ZOOM = 2.5;

	static readonly Brush Bg = brush(0x0C, 0x0C, 0x0C);
	static readonly Brush Fg = brush(0xCC, 0xCC, 0xCC);
	static readonly Brush FgDim = brush(0x80, 0x80, 0x80);
	static readonly Brush BarBg = brush(0x1E, 0x1E, 0x1E);

	readonly Grid root;
	readonly ComboBox cshell;
	readonly TextBox ecwd;
	readonly Button brestart;
	readonly Button bclear;
	readonly Button bkill;
	readonly TerminalControl term;
	readonly TextBlock lbstatus;
	/// <summary>
	/// 透明 IME 宿主：系统输入法依赖真实 TextBox 才能首次进标签就组字，
	/// 且候选窗会自动出现在该框光标处（跟 TUI 光标同步）。
	/// </summary>
	readonly TextBox imeBox;
	readonly Canvas imeLayer;
	bool imeSilent;

	ConPtySession session;
	bool disposed;
	double zoom = 1.0;
	string shellKind = "cmd"; // cmd | powershell
	string workDir;
	string tabTitle = "命令行";
	int pendingCols = 80;
	int pendingRows = 24;
	bool sizeWired;

	/// <summary>打开标签前可设默认工作目录（勿写入含 | 的 Path 伪路径）。</summary>
	public string PreferredWorkDir;

	public FrameworkElement View => root;
	public string FilePath => "console:" + shellKind;
	public string Title => tabTitle;
	public DocKind Kind => DocKind.Console;
	public double Zoom => zoom;
	public string StatusText {
		get {
			var run = session != null && !session.HasExited ? "运行中" : "已退出";
			var dir = workDir ?? "";
			if (dir.Length > 48) dir = "…" + dir.Substring(dir.Length - 46);
			var sz = $"{pendingCols}x{pendingRows}";
			return $"命令行  ·  {shellKind}  ·  ConPTY  ·  {run}  ·  {sz}  ·  {dir}";
		}
	}
	public int PageCount => 1;
	public int CurrentPage => 1;
	public bool HasOutline => false;
	public bool SidePanelVisible => false;

	public event Action StatusChanged;
	public event Action MetaChanged;

	public ConsoleViewer() {
		workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		try {
			if (!string.IsNullOrEmpty(Environment.CurrentDirectory)
				&& Directory.Exists(Environment.CurrentDirectory))
				workDir = Environment.CurrentDirectory;
		} catch { /* ignore */ }

		cshell = new ComboBox {
			Width = 110,
			Height = 24,
			FontSize = 12,
			VerticalContentAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 6, 0),
		};
		cshell.Items.Add("cmd");
		cshell.Items.Add("powershell");
		cshell.SelectedIndex = 0;
		cshell.SelectionChanged += (_, _) => {
			if (cshell.SelectedItem is string s) shellKind = s;
		};

		ecwd = new TextBox {
			Height = 24,
			FontSize = 12,
			VerticalContentAlignment = VerticalAlignment.Center,
			Padding = new Thickness(6, 1, 6, 1),
			Text = workDir,
			ToolTip = "工作目录（重启后生效）",
			Background = Brushes.White,
			BorderBrush = brush(0x3C, 0x3C, 0x3C),
			BorderThickness = new Thickness(1),
		};
		brestart = toolbtn("重启", "结束并重新启动 Shell");
		bclear = toolbtn("清屏", "向终端发送清屏（Ctrl+L）");
		bkill = toolbtn("结束", "终止当前进程树");
		brestart.Click += (_, _) => restartshell();
		bclear.Click += (_, _) => {
			try {
				session?.Write("\x0c");
			} catch { /* ignore */ }
		};
		bkill.Click += (_, _) => killshell(showMsg: true);

		var bar = new DockPanel {
			Background = BarBg,
			Margin = new Thickness(0),
			LastChildFill = true,
		};
		var right = new StackPanel {
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(4, 4, 6, 4),
		};
		right.Children.Add(brestart);
		right.Children.Add(bclear);
		right.Children.Add(bkill);
		DockPanel.SetDock(right, Dock.Right);
		var left = new StackPanel {
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8, 4, 4, 4),
		};
		left.Children.Add(new TextBlock {
			Text = "Shell",
			Foreground = FgDim,
			FontSize = 11,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 4, 0),
		});
		left.Children.Add(cshell);
		left.Children.Add(new TextBlock {
			Text = "目录",
			Foreground = FgDim,
			FontSize = 11,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8, 0, 4, 0),
		});
		DockPanel.SetDock(left, Dock.Left);
		bar.Children.Add(right);
		bar.Children.Add(left);
		bar.Children.Add(ecwd);

		term = new TerminalControl {
			Focusable = true,
		};
		// 键盘/粘贴 → ConPTY：短包同步写（按键可靠），长包走队列
		term.Output += data => {
			try {
				if (session == null || data == null || data.Length == 0) return;
				if (data.Length <= 256)
					session.WriteSync(data);
				else
					session.Write(data);
			} catch (Exception ex) {
				DocLog.Warn("console write: " + ex.Message);
			}
		};
		term.TitleChanged += () => {
			if (!string.IsNullOrEmpty(term.WindowTitle)) {
				tabTitle = term.WindowTitle;
				raisemeta();
			}
		};
		term.SizeChangedByUser += (c, r) => {
			pendingCols = c;
			pendingRows = r;
			// 通知 ConPTY → 子进程收到窗口尺寸变化 → TUI（opencode 等）重排
			try {
				session?.Resize(c, r);
			} catch (Exception ex) {
				DocLog.Warn($"console resize: {ex.Message}");
			}
			setstatus(session != null && !session.HasExited
				? $"ConPTY 运行中  PID={session.ProcessId}  {c}x{r}  ·  可运行 opencode / grok 等 TUI"
				: lbstatus?.Text ?? "");
			raisestatus();
		};

		lbstatus = new TextBlock {
			Text = "ConPTY 终端 · 支持 TUI（opencode / grok 等）· Ctrl+C 中断 · Ctrl+V 粘贴",
			Foreground = FgDim,
			FontSize = 11,
			Margin = new Thickness(10, 2, 10, 4),
		};

		root = new Grid { Background = Bg };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		var statusBar = new Border {
			Background = BarBg,
			Child = lbstatus,
		};
		// 终端 + 透明 IME 输入框叠层
		imeBox = new TextBox {
			Background = Brushes.Transparent,
			Foreground = Brushes.Transparent,
			// 关掉系统 TextBox 闪烁插入符；终端层画实心光标
			CaretBrush = Brushes.Transparent,
			BorderThickness = new Thickness(0),
			Padding = new Thickness(0),
			Margin = new Thickness(0),
			FontSize = 13,
			FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei Mono, NSimSun"),
			AcceptsReturn = false,
			AcceptsTab = false,
			// 近乎不可见，但可获焦；系统 IME 候选窗锚在此控件上
			Opacity = 0.01,
			Width = 12,
			Height = 18,
			Focusable = true,
			IsTabStop = true,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
		};
		InputMethod.SetIsInputMethodEnabled(imeBox, true);
		try { InputMethod.SetPreferredImeState(imeBox, InputMethodState.DoNotCare); } catch { /* ignore */ }
		imeBox.TextChanged += onimetextchanged;
		imeBox.PreviewKeyDown += onimepreviewkeydown;
		imeBox.PreviewTextInput += onimepreviewtextinput;
		// 组字预览画在终端光标处（透明 TextBox 本身看不见）
		TextCompositionManager.AddPreviewTextInputStartHandler(imeBox, onimecompstart);
		TextCompositionManager.AddPreviewTextInputUpdateHandler(imeBox, onimecompupdate);
		TextCompositionManager.AddPreviewTextInputHandler(imeBox, onimecompcomplete);
		// 获焦后反复 HideCaret：WPF CaretBrush 挡不住系统插入符
		imeBox.GotKeyboardFocus += (_, _) => hideimecaret();
		imeBox.GotFocus += (_, _) => hideimecaret();
		imeBox.LostKeyboardFocus += (_, _) => {
			// 焦点被工具栏抢走时不强制抢回
			try { term.SetImeComposition(""); } catch { /* ignore */ }
		};

		imeLayer = new Canvas {
			Background = Brushes.Transparent,
			IsHitTestVisible = true,
			ClipToBounds = true,
		};
		imeLayer.Children.Add(imeBox);

		// Terminal 可点选聚焦；键盘优先走 imeBox
		term.Focusable = true;
		term.CaretMoved += () => {
			try { syncimeboxpos(); } catch { /* ignore */ }
		};

		var termStack = new Grid {
			Background = Bg,
			ClipToBounds = true,
		};
		termStack.Children.Add(term);
		termStack.Children.Add(imeLayer);

		var termHost = new Border {
			Background = Bg,
			Child = termStack,
			Focusable = false,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
			ClipToBounds = true,
		};
		termHost.MouseDown += (_, e) => {
			// 点终端区域 → 聚焦 IME 框（保证中文输入法可用）
			PrepareImeFocus();
			e.Handled = true;
		};
		termHost.SizeChanged += (_, e) => {
			if (e.NewSize.Width > 10 && e.NewSize.Height > 10) {
				term.NotifyParentSize(e.NewSize.Width, e.NewSize.Height);
				syncimeboxpos();
			}
		};

		Grid.SetRow(bar, 0);
		Grid.SetRow(termHost, 1);
		Grid.SetRow(statusBar, 2);
		root.Children.Add(bar);
		root.Children.Add(termHost);
		root.Children.Add(statusBar);

		root.Loaded += (_, _) => {
			try {
				PrepareImeFocus();
				root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
					if (!sizeWired) {
						sizeWired = true;
						pendingCols = Math.Max(20, term.ViewCols);
						pendingRows = Math.Max(5, term.ViewRows);
					}
					PrepareImeFocus();
				}));
			} catch { /* ignore */ }
		};
		root.IsVisibleChanged += (_, e) => {
			if (e.NewValue is true)
				PrepareImeFocus();
		};
	}

	void onimetextchanged(object sender, TextChangedEventArgs e) {
		if (imeSilent || disposed) return;
		var t = imeBox.Text;
		if (string.IsNullOrEmpty(t)) return;
		// 组字中途 TextBox.Text 有时会带拼音：有活跃组字预览时不要当最终字写入
		if (!string.IsNullOrEmpty(imeCompActive)) {
			return;
		}
		writeptytext(t);
		imeSilent = true;
		try { imeBox.Text = ""; } catch { /* ignore */ }
		imeSilent = false;
		syncimeboxpos();
	}

	/// <summary>当前 IME 组字串（未上屏）；空=无组字。</summary>
	string imeCompActive = "";

	void onimecompstart(object sender, TextCompositionEventArgs e) {
		imeCompActive = e.TextComposition?.CompositionText ?? e.Text ?? "";
		try { term.SetImeComposition(imeCompActive); } catch { /* ignore */ }
		syncimeboxpos();
	}

	void onimecompupdate(object sender, TextCompositionEventArgs e) {
		var c = e.TextComposition?.CompositionText;
		if (c == null) c = e.Text ?? "";
		imeCompActive = c;
		try { term.SetImeComposition(imeCompActive); } catch { /* ignore */ }
		syncimeboxpos();
	}

	void onimecompcomplete(object sender, TextCompositionEventArgs e) {
		var t = e.Text ?? "";
		imeCompActive = "";
		try { term.SetImeComposition(""); } catch { /* ignore */ }
		// 确认字写入 PTY（中文/选词）；与 TextChanged 可能双到，writepty 内幂等靠即时清空
		if (!string.IsNullOrEmpty(t) && t != "\0") {
			writeptytext(t);
			imeSilent = true;
			try { imeBox.Text = ""; } catch { /* ignore */ }
			imeSilent = false;
			e.Handled = true;
		}
		syncimeboxpos();
	}

	void onimepreviewtextinput(object sender, TextCompositionEventArgs e) {
		if (disposed || imeSilent) return;
		// 组字中：最终字由 onimecompcomplete 写；中间预览不写 PTY
		if (!string.IsNullOrEmpty(imeCompActive)) return;
		var t = e.Text;
		if (string.IsNullOrEmpty(t) || t == "\0") return;
		// 无活跃组字：英/数字/IME「英」模式直入
		writeptytext(t);
		imeSilent = true;
		try { imeBox.Text = ""; } catch { /* ignore */ }
		imeSilent = false;
		e.Handled = true;
		syncimeboxpos();
	}

	string lastPtyWrite;
	int lastPtyWriteTick;

	void writeptytext(string t) {
		if (string.IsNullOrEmpty(t) || session == null || session.HasExited) return;
		// 防 TextChanged + TextInput + complete 短时双写
		var now = Environment.TickCount;
		if (t == lastPtyWrite && unchecked(now - lastPtyWriteTick) >= 0 && unchecked(now - lastPtyWriteTick) < 40)
			return;
		lastPtyWrite = t;
		lastPtyWriteTick = now;
		try {
			session.WriteSync(Encoding.UTF8.GetBytes(t));
		} catch (Exception ex) {
			DocLog.Warn("console ime write: " + ex.Message);
		}
	}

	void onimepreviewkeydown(object sender, KeyEventArgs e) {
		if (disposed) return;
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		var mods = Keyboard.Modifiers;
		var ctrl = mods.HasFlag(ModifierKeys.Control);
		var alt = mods.HasFlag(ModifierKeys.Alt);

		// Alt+F4 关闭程序：不 Handled，交给系统/主窗
		if (alt && !ctrl && key == Key.F4)
			return;

		// Ctrl/Alt 组合：交给终端（Ctrl+C 中断、Ctrl+V 粘贴等）
		if (ctrl || alt) {
			if (term.HandleKeyDown(key, mods)) {
				e.Handled = true;
				imeSilent = true;
				try { imeBox.Text = ""; } catch { /* ignore */ }
				imeSilent = false;
				try { term.SetImeComposition(""); } catch { /* ignore */ }
				imeCompActive = "";
			}
			return;
		}

		// 正在组字：纯 ASCII 组字 + Enter → 当英文命令提交（dir/cd 等），避免「字母不显示/回车被 IME 吃掉」
		if (!string.IsNullOrEmpty(imeCompActive)) {
			if (key == Key.Return && ispureascii(imeCompActive)) {
				var ascii = imeCompActive;
				imeCompActive = "";
				try { term.SetImeComposition(""); } catch { /* ignore */ }
				writeptytext(ascii);
				writeptytext("\r");
				imeSilent = true;
				try { imeBox.Text = ""; } catch { /* ignore */ }
				imeSilent = false;
				e.Handled = true;
				syncimeboxpos();
				return;
			}
			// 其余（空格选词、方向、中文组字）交给输入法
			syncimeboxpos();
			return;
		}
		// IME 已开但尚未出组字串：可打印键放行给 IME（中文首键起组字；「英」模式走 TextInput）
		if (term.IsImeOpen && isimeprintablekey(key)) {
			syncimeboxpos();
			return;
		}
		if (term.IsImeOpen && isimeeditkey(key)) {
			syncimeboxpos();
			return;
		}

		// 无 IME：可打印键直接写 PTY（不依赖 TextBox.Text，避免字母丢失）
		if (isimeprintablekey(key)) {
			if (trykeychartoterm(key, mods)) {
				e.Handled = true;
				imeSilent = true;
				try { imeBox.Text = ""; } catch { /* ignore */ }
				imeSilent = false;
				syncimeboxpos();
			}
			return;
		}

		switch (key) {
			case Key.Return:
			case Key.Tab:
			case Key.Escape:
			case Key.Back:
			case Key.Delete:
			case Key.Left:
			case Key.Right:
			case Key.Up:
			case Key.Down:
			case Key.Home:
			case Key.End:
			case Key.PageUp:
			case Key.PageDown:
			case Key.F1: case Key.F2: case Key.F3: case Key.F4:
			case Key.F5: case Key.F6: case Key.F7: case Key.F8:
			case Key.F9: case Key.F10: case Key.F11: case Key.F12:
				if (term.HandleKeyDown(key, mods)) {
					e.Handled = true;
					imeSilent = true;
					try { imeBox.Text = ""; } catch { /* ignore */ }
					imeSilent = false;
					syncimeboxpos();
				}
				break;
		}
	}

	static bool isimeprintablekey(Key key) {
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

	static bool isimeeditkey(Key key) {
		switch (key) {
			case Key.Return: case Key.Escape: case Key.Back:
			case Key.Left: case Key.Right: case Key.Up: case Key.Down:
			case Key.Home: case Key.End: case Key.Space:
				return true;
			default:
				return false;
		}
	}

	static bool ispureascii(string s) {
		if (string.IsNullOrEmpty(s)) return false;
		foreach (var ch in s) {
			if (ch < 32 || ch > 126) return false;
		}
		return true;
	}

	/// <summary>无 IME 时把按键变成字符写入 PTY（字母/数字/符号）。</summary>
	bool trykeychartoterm(Key key, ModifierKeys mods) {
		if (session == null || session.HasExited) return false;
		// 复用终端 ToUnicode 路径：通过 HandleKeyDown 会因「可打印不处理」返回 false，
		// 这里直接走 emit 同款 Write
		try {
			var shift = mods.HasFlag(ModifierKeys.Shift);
			string text = null;
			if (key >= Key.A && key <= Key.Z) {
				var c = (char)('a' + (key - Key.A));
				var caps = false;
				try { caps = Keyboard.IsKeyToggled(Key.CapsLock); } catch { /* ignore */ }
				if (shift ^ caps) c = char.ToUpperInvariant(c);
				text = c.ToString();
			} else if (key >= Key.D0 && key <= Key.D9) {
				if (!shift) text = ((char)('0' + (key - Key.D0))).ToString();
				else text = ")!@#$%^&*("[key - Key.D0].ToString();
			} else if (key >= Key.NumPad0 && key <= Key.NumPad9) {
				text = ((char)('0' + (key - Key.NumPad0))).ToString();
			} else if (key == Key.Space) {
				text = " ";
			} else {
				// 其余 OEM：交给终端 HandleKeyDown 的 fallback 不可用，简单映射
				switch (key) {
					case Key.OemMinus: text = shift ? "_" : "-"; break;
					case Key.OemPlus: text = shift ? "+" : "="; break;
					case Key.OemOpenBrackets: text = shift ? "{" : "["; break;
					case Key.OemCloseBrackets: text = shift ? "}" : "]"; break;
					case Key.OemPipe: text = shift ? "|" : "\\"; break;
					case Key.OemSemicolon: text = shift ? ":" : ";"; break;
					case Key.OemQuotes: text = shift ? "\"" : "'"; break;
					case Key.OemComma: text = shift ? "<" : ","; break;
					case Key.OemPeriod: text = shift ? ">" : "."; break;
					case Key.OemQuestion: text = shift ? "?" : "/"; break;
					case Key.OemTilde: text = shift ? "~" : "`"; break;
					case Key.Divide: text = "/"; break;
					case Key.Multiply: text = "*"; break;
					case Key.Subtract: text = "-"; break;
					case Key.Add: text = "+"; break;
					case Key.Decimal: text = "."; break;
				}
			}
			if (string.IsNullOrEmpty(text)) return false;
			writeptytext(text);
			return true;
		} catch (Exception ex) {
			DocLog.Warn("console keychar: " + ex.Message);
			return false;
		}
	}

	void syncimeboxpos() {
		if (imeBox == null || term == null) return;
		try {
			term.GetCaretDip(out var x, out var y);
			var cw = Math.Max(6, term.CellWidth);
			var ch = Math.Max(12, term.CellHeight);
			// 光标处放一个窄 TextBox，IME 候选窗会锚在这里
			Canvas.SetLeft(imeBox, Math.Max(0, x));
			Canvas.SetTop(imeBox, Math.Max(0, y));
			imeBox.Width = Math.Max(10, cw * 2);
			imeBox.Height = ch;
			imeBox.FontSize = Math.Max(10, ch * 0.85);
			// 保证在最前
			Panel.SetZIndex(imeLayer, 10);
			Panel.SetZIndex(imeBox, 11);
			hideimecaret();
		} catch { /* ignore */ }
	}

	/// <summary>关掉 IME 锚点 TextBox 的系统闪烁插入符（Win32 + WPF）。</summary>
	void hideimecaret() {
		try {
			imeBox.CaretBrush = Brushes.Transparent;
			var src = PresentationSource.FromVisual(imeBox) as HwndSource;
			if (src != null && src.Handle != IntPtr.Zero) {
				HideCaret(src.Handle);
				// 多拍：系统会在布局/IME 后重建插入符
				imeBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
					try {
						var s2 = PresentationSource.FromVisual(imeBox) as HwndSource;
						if (s2 != null) HideCaret(s2.Handle);
					} catch { /* ignore */ }
				}));
				imeBox.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
					try {
						var s2 = PresentationSource.FromVisual(imeBox) as HwndSource;
						if (s2 != null) HideCaret(s2.Handle);
					} catch { /* ignore */ }
				}));
			}
		} catch { /* ignore */ }
	}

	[DllImport("user32.dll")]
	static extern bool HideCaret(IntPtr hWnd);

	bool istoolbarfocused() {
		var fe = Keyboard.FocusedElement as DependencyObject;
		while (fe != null) {
			// IME 宿主不算工具栏
			if (ReferenceEquals(fe, imeBox))
				return false;
			if (ReferenceEquals(fe, ecwd) || ReferenceEquals(fe, cshell)
				|| ReferenceEquals(fe, brestart) || ReferenceEquals(fe, bclear)
				|| ReferenceEquals(fe, bkill))
				return true;
			if (fe is TextBox || fe is ComboBox) {
				// 仅工具栏上的 cwd/shell
				if (ReferenceEquals(fe, ecwd) || ReferenceEquals(fe, cshell))
					return true;
				// 其它 TextBox（不应有）忽略
			}
			try {
				fe = VisualTreeHelper.GetParent(fe) ?? LogicalTreeHelper.GetParent(fe);
			} catch {
				break;
			}
		}
		return false;
	}

	/// <summary>true=键应进终端（非工具栏编辑）。主窗据此放行全部快捷键。</summary>
	public bool IsCapturingKeys => !istoolbarfocused();

	/// <summary>终端/IME 框是否持有键盘焦点。</summary>
	public bool IsTerminalFocused {
		get {
			try {
				return term.IsKeyboardFocusWithin
					|| (imeBox != null && imeBox.IsKeyboardFocusWithin);
			} catch { return false; }
		}
	}

	/// <summary>系统输入法是否打开（中文模式）。</summary>
	public bool IsImeOpen {
		get {
			try {
				if (term.IsImeOpen) return true;
				// 焦点在 imeBox 时 Imm 状态更准
				return InputMethod.Current != null
					&& InputMethod.Current.ImeState == InputMethodState.On;
			} catch { return false; }
		}
	}

	/// <summary>供主窗在切到本标签时调用，抢回键盘焦点。</summary>
	public void FocusTerminal() {
		PrepareImeFocus();
	}

	/// <summary>
	/// 切入命令行：聚焦透明 IME 框（系统输入法锚点）并同步到 TUI 光标。
	/// 必须在首次打开就执行，否则中文要等再切一次 Tab。
	/// </summary>
	public void PrepareImeFocus() {
		if (disposed) return;
		try {
			syncimeboxpos();
			var win = Window.GetWindow(root);
			if (win != null) {
				try { win.Activate(); } catch { /* ignore */ }
				try { FocusManager.SetFocusedElement(win, imeBox); } catch { /* ignore */ }
			}
			InputMethod.SetIsInputMethodEnabled(imeBox, true);
			Keyboard.Focus(imeBox);
			imeBox.Focus();
			// 光标移到 TextBox 末尾（空）
			try { imeBox.CaretIndex = imeBox.Text?.Length ?? 0; } catch { /* ignore */ }
			// 多拍：异步加载/WebView 抢焦点后仍拉回
			void again() {
				if (disposed) return;
				try {
					syncimeboxpos();
					Keyboard.Focus(imeBox);
					imeBox.Focus();
				} catch { /* ignore */ }
			}
			root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(again));
			root.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(again));
			root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(again));
			root.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(again));
		} catch { /* ignore */ }
	}

	/// <summary>主窗 PreviewKeyDown 唯一转发入口（含可打印字符 ToUnicode）。</summary>
	public bool TryHandleKey(Key key, ModifierKeys mods) {
		try {
			if (session == null)
				DocLog.Warn($"console key {key} but session=null");
			return term.HandleKeyDown(key, mods);
		} catch (Exception ex) {
			DocLog.Warn("console key: " + ex.Message);
			return false;
		}
	}

	/// <summary>直接注入字符串（自检 / IME）。</summary>
	public bool TryWriteRaw(string s) {
		try {
			if (string.IsNullOrEmpty(s) || session == null) return false;
			return session.WriteSync(Encoding.UTF8.GetBytes(s));
		} catch {
			return false;
		}
	}

	/// <summary>自检：导出终端可见文本。</summary>
	public string DumpScreenTextForTest() {
		try { return term.DumpScreenText(); } catch { return ""; }
	}

	/// <summary>自检：单元格调试。</summary>
	public string DumpCellsDebugForTest(int maxRows = 5) {
		try { return term.DumpCellsDebug(maxRows); } catch { return ""; }
	}

	/// <summary>自检：截图终端层 PNG。</summary>
	public byte[] CapturePngForTest() {
		try { return term.CapturePng(); } catch { return null; }
	}

	/// <summary>自检：截整页（含 IME 叠层）PNG。</summary>
	public byte[] CaptureFullPngForTest() {
		try {
			root.UpdateLayout();
			var w = (int)Math.Ceiling(Math.Max(1, root.ActualWidth));
			var h = (int)Math.Ceiling(Math.Max(1, root.ActualHeight));
			if (w < 2 || h < 2) return null;
			var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
			rtb.Render(root);
			var enc = new PngBitmapEncoder();
			enc.Frames.Add(BitmapFrame.Create(rtb));
			using var ms = new MemoryStream();
			enc.Save(ms);
			return ms.ToArray();
		} catch {
			return null;
		}
	}

	/// <summary>自检：直接往 VT 缓冲喂字节（不经 ConPTY）。</summary>
	public void FeedVtForTest(byte[] data) {
		try { term.FeedSync(data); } catch { /* ignore */ }
	}

	/// <summary>自检：强制 IME 聚焦并 HideCaret。</summary>
	public void PrepareImeFocusForTest() {
		PrepareImeFocus();
		hideimecaret();
	}

	/// <summary>自检：会话统计。</summary>
	public string DebugSessionStats() {
		var s = session;
		if (s == null) return "session=null";
		return $"pid={s.ProcessId} read={s.BytesRead} write={s.BytesWritten} chunks={s.ReadChunks} exited={s.HasExited} errR={s.LastReadError} errW={s.LastWriteError}";
	}

	/// <summary>IME 中文等：主窗 PreviewTextInput 转发。</summary>
	public bool TryHandleText(string text) {
		try {
			return term.HandleTextInput(text);
		} catch {
			return false;
		}
	}

	static Button toolbtn(string text, string tip) {
		return new Button {
			Content = text,
			ToolTip = tip,
			Height = 24,
			Padding = new Thickness(10, 0, 10, 0),
			Margin = new Thickness(0, 0, 4, 0),
			FontSize = 11,
			Cursor = Cursors.Hand,
			Background = brush(0x2D, 0x2D, 0x2D),
			Foreground = Fg,
			BorderThickness = new Thickness(0),
		};
	}

	static SolidColorBrush brush(byte r, byte g, byte b) {
		var br = new SolidColorBrush(Color.FromRgb(r, g, b));
		if (br.CanFreeze) br.Freeze();
		return br;
	}

	public void Load(string path) {
		parsepath(path);
		try {
			if (!string.IsNullOrWhiteSpace(PreferredWorkDir) && Directory.Exists(PreferredWorkDir))
				workDir = System.IO.Path.GetFullPath(PreferredWorkDir);
		} catch { /* ignore */ }
		cshell.SelectedItem = shellKind == "powershell" ? "powershell" : "cmd";
		ecwd.Text = workDir;
		updatetitle();
		// 延迟到布局后启动（拿准 cols/rows）+ 准备 IME 焦点
		root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
			pendingCols = Math.Max(20, term.ViewCols);
			pendingRows = Math.Max(5, term.ViewRows);
			startshell();
			PrepareImeFocus();
		}));
	}

	void parsepath(string path) {
		if (string.IsNullOrWhiteSpace(path)) return;
		if (!path.StartsWith("console:", StringComparison.OrdinalIgnoreCase)) return;
		var rest = path.Substring("console:".Length).Trim();
		if (rest.StartsWith("new-", StringComparison.OrdinalIgnoreCase)
			|| rest.StartsWith("new", StringComparison.OrdinalIgnoreCase))
			return;
		var bar = rest.IndexOf('|');
		if (bar >= 0) rest = rest.Substring(0, bar);
		var colon = rest.IndexOf(':');
		if (colon >= 0) rest = rest.Substring(0, colon);
		var sh = rest.Trim().ToLowerInvariant();
		if (sh == "powershell" || sh == "pwsh" || sh == "ps")
			shellKind = "powershell";
		else if (sh == "cmd" || sh == "command" || sh.Length == 0)
			shellKind = "cmd";
	}

	void startshell() {
		try {
			killshell(showMsg: false);
			term.Reset();

			if (!ConPtySession.IsSupported) {
				term.FeedText("\x1b[91m当前系统不支持 ConPTY（需要 Windows 10 1809+）。\x1b[0m\r\n");
				setstatus("ConPTY 不可用");
				raisestatus();
				return;
			}

			try {
				workDir = (ecwd.Text ?? "").Trim();
				if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
					workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				ecwd.Text = workDir;
			} catch {
				workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			}

			string fileName;
			string args;
			if (shellKind == "powershell") {
				fileName = "powershell.exe";
				args = "-NoLogo -NoExit";
			} else {
				fileName = "cmd.exe";
				args = null;
			}

			var s = new ConPtySession();
			s.DataReceived += data => {
				try { term.Feed(data); } catch { /* ignore */ }
			};
			s.Exited += () => {
				try {
					root.Dispatcher.BeginInvoke(new Action(() => {
						if (disposed) return;
						term.FeedText("\r\n\x1b[33m[进程已退出]\x1b[0m\r\n");
						setstatus("已退出");
						raisestatus();
					}));
				} catch { /* ignore */ }
			};
			// 限制初始行列，过大易卡渲染
			var cols = Math.Max(20, Math.Min(200, pendingCols));
			var rows = Math.Max(5, Math.Min(80, pendingRows));
			DocLog.Info($"Console ConPTY starting {shellKind} cwd={workDir} {cols}x{rows}");
			s.Start(fileName, args, workDir, cols, rows);
			session = s;
			setstatus($"ConPTY 运行中  PID={s.ProcessId}  {cols}x{rows}  ·  可运行 opencode / grok 等 TUI");
			updatetitle();
			raisestatus();
			raisemeta();
			DocLog.Info($"Console ConPTY start {shellKind} pid={s.ProcessId} cwd={workDir} {cols}x{rows}");
		} catch (Exception ex) {
			try {
				term.FeedText("\x1b[91m启动失败: " + ex.Message + "\x1b[0m\r\n");
				setstatus("启动失败: " + ex.Message);
				raisestatus();
			} catch { /* ignore */ }
			DocLog.Error("Console ConPTY start", ex);
		}
	}

	void restartshell() {
		if (cshell.SelectedItem is string s)
			shellKind = s;
		startshell();
		try { term.FocusTerminal(); } catch { /* ignore */ }
	}

	void killshell(bool showMsg) {
		var s = session;
		session = null;
		if (s != null) {
			try { s.Dispose(); } catch { /* ignore */ }
		}
		if (showMsg) {
			term.FeedText("\r\n\x1b[33m[已结束进程]\x1b[0m\r\n");
			setstatus("已结束");
			raisestatus();
		}
	}

	void setstatus(string s) {
		try { lbstatus.Text = s ?? ""; } catch { /* ignore */ }
	}

	void updatetitle() {
		var name = shellKind == "powershell" ? "PowerShell" : "cmd";
		var leaf = "";
		try { leaf = System.IO.Path.GetFileName(workDir.TrimEnd('\\', '/')); } catch { /* ignore */ }
		if (string.IsNullOrEmpty(term.WindowTitle))
			tabTitle = string.IsNullOrEmpty(leaf) ? name : $"{name} · {leaf}";
	}

	void raisestatus() {
		try { StatusChanged?.Invoke(); } catch { /* ignore */ }
	}

	void raisemeta() {
		try { MetaChanged?.Invoke(); } catch { /* ignore */ }
	}

	public void SetZoom(double z) {
		z = z < MIN_ZOOM ? MIN_ZOOM : (z > MAX_ZOOM ? MAX_ZOOM : z);
		if (Math.Abs(z - zoom) < 1e-9) return;
		zoom = z;
		try { term.SetFontSize(13 * zoom); } catch { /* ignore */ }
		raisestatus();
	}

	public void ZoomBy(double factor) => SetZoom(zoom * factor);
	public void ZoomIn() => SetZoom(zoom * 1.1);
	public void ZoomOut() => SetZoom(zoom / 1.1);
	public void ZoomFitWidth() => SetZoom(1);
	public void ZoomFitPage() => SetZoom(1);
	public void GoPrevPage() { }
	public void GoNextPage() { }
	public void GoToPage(int page1Based) { }
	public void RotateBy(int deltaQuarterTurns) { }
	public void SetSidePanelVisible(bool show) { }

	public void CaptureViewState(out double h, out double v, out double z, out int sheetOrPage) {
		h = 0; v = 0; z = zoom; sheetOrPage = 0;
	}

	public void RestoreViewState(double h, double v, double z, int sheetOrPage) {
		if (z > 0.01) SetZoom(z);
	}

	public FindResult Find(string text, bool forward, bool ignoreCase, bool restart = false, bool fromView = false) =>
		FindResult.Miss();

	public void ClearFind() { }

	public bool TryCopySelection() => false;

	public void Dispose() {
		if (disposed) return;
		disposed = true;
		killshell(showMsg: false);
		try {
			TextCompositionManager.RemovePreviewTextInputStartHandler(imeBox, onimecompstart);
			TextCompositionManager.RemovePreviewTextInputUpdateHandler(imeBox, onimecompupdate);
			TextCompositionManager.RemovePreviewTextInputHandler(imeBox, onimecompcomplete);
		} catch { /* ignore */ }
		try { term.SetImeComposition(""); } catch { /* ignore */ }
		try { term.DisposeResources(); } catch { /* ignore */ }
	}
}
