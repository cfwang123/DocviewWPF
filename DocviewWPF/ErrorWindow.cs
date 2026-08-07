using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DocviewWPF;

/// <summary>
/// 未处理异常窗：详细消息 + 堆栈；同一窗追加连续报错并自动滚到底。
/// </summary>
sealed class ErrorWindow : Window {
	static ErrorWindow live;

	readonly TextBox eException;
	readonly CheckBox cbFullStack;
	readonly Button bCopy;
	readonly Button bClose;
	readonly StringBuilder fullBuf = new();
	readonly StringBuilder filteredBuf = new();
	int errCount;

	ErrorWindow() {
		Title = "DocviewWPF · 错误";
		Width = 800;
		Height = 500;
		MinWidth = 480;
		MinHeight = 320;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xF0));
		ShowInTaskbar = true;

		var root = new Grid { Margin = new Thickness(15) };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var lbHint = new TextBlock {
			Text = "程序发生了未处理的异常，建议截图或复制下方信息后反馈。连续报错会追加显示。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 14,
			FontWeight = FontWeights.Bold,
			Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x00, 0x00)),
			Margin = new Thickness(0, 0, 0, 10),
		};
		Grid.SetRow(lbHint, 0);

		cbFullStack = new CheckBox {
			Content = "显示完整栈回溯",
			IsChecked = false,
			FontSize = 11,
			Margin = new Thickness(0, 0, 0, 6),
		};
		Grid.SetRow(cbFullStack, 1);

		eException = new TextBox {
			IsReadOnly = true,
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xF8)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x99)),
			BorderThickness = new Thickness(1),
		};
		Grid.SetRow(eException, 2);

		var bottom = new StackPanel {
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 10, 0, 0),
		};
		bCopy = new Button {
			Content = "复制到剪贴板",
			Height = 24,
			FontSize = 12,
			Margin = new Thickness(0, 0, 6, 0),
			Padding = new Thickness(10, 0, 10, 0),
		};
		bClose = new Button {
			Content = "关闭",
			Height = 24,
			FontSize = 12,
			Padding = new Thickness(16, 0, 16, 0),
			IsDefault = true,
			IsCancel = true,
		};
		bottom.Children.Add(bCopy);
		bottom.Children.Add(bClose);
		Grid.SetRow(bottom, 3);

		root.Children.Add(lbHint);
		root.Children.Add(cbFullStack);
		root.Children.Add(eException);
		root.Children.Add(bottom);
		Content = root;

		init();
	}

	void init() {
		cbFullStack.Checked += (_, _) => rebuilddisplay();
		cbFullStack.Unchecked += (_, _) => rebuilddisplay();
		bCopy.Click += (_, _) => {
			try {
				Clipboard.SetText(eException.Text ?? "");
				bCopy.Content = "已复制!";
			} catch { /* ignore */ }
		};
		bClose.Click += (_, _) => Close();
		PreviewKeyDown += (_, e) => {
			if (e.Key is Key.Escape or Key.Enter) {
				Close();
				e.Handled = true;
			}
		};
		Closed += (_, _) => {
			if (ReferenceEquals(live, this))
				live = null;
		};
	}

	/// <summary>在 UI 线程追加一条异常；已有窗则滚动追加，否则新建非模态窗。</summary>
	public static void Report(Exception ex, string context = null) {
		if (ex == null) return;
		var app = Application.Current;
		if (app?.Dispatcher == null) return;
		if (!app.Dispatcher.CheckAccess()) {
			app.Dispatcher.BeginInvoke(new Action(() => Report(ex, context)));
			return;
		}
		try {
			if (live == null || !live.IsLoaded) {
				live = new ErrorWindow();
				try {
					if (app.MainWindow != null && app.MainWindow.IsLoaded)
						live.Owner = app.MainWindow;
				} catch { /* ignore */ }
				live.Show();
			}
			live.append(ex, context);
			if (!live.IsActive)
				try { live.Activate(); } catch { /* ignore */ }
		} catch {
			// 最后兜底：避免报错窗本身再炸
			try {
				MessageBox.Show(ex.ToString(), "DocviewWPF · Error", MessageBoxButton.OK, MessageBoxImage.Error);
			} catch { /* ignore */ }
		}
	}

	void append(Exception ex, string context) {
		errCount++;
		Title = errCount <= 1
			? $"DocviewWPF · {ex.GetType().Name}" + (string.IsNullOrEmpty(context) ? "" : $" @ {context}")
			: $"DocviewWPF · 错误 ×{errCount}";

		var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
		var head = string.IsNullOrEmpty(context)
			? $"======== [{stamp}] {ex.GetType().Name} ========"
			: $"======== [{stamp}] {ex.GetType().Name} @ {context} ========";
		var full = ex.ToString() ?? ex.Message ?? "";
		var filtered = filterstack(full);

		if (fullBuf.Length > 0) {
			fullBuf.AppendLine();
			fullBuf.AppendLine();
			filteredBuf.AppendLine();
			filteredBuf.AppendLine();
		}
		fullBuf.AppendLine(head);
		fullBuf.AppendLine($"异常消息：{ex.Message}");
		fullBuf.AppendLine();
		fullBuf.AppendLine("堆栈回溯：");
		fullBuf.Append(full);

		filteredBuf.AppendLine(head);
		filteredBuf.AppendLine($"异常消息：{ex.Message}");
		filteredBuf.AppendLine();
		filteredBuf.AppendLine("堆栈回溯（仅显示本项目代码）：");
		filteredBuf.Append(filtered);

		bCopy.Content = "复制到剪贴板";
		rebuilddisplay();
		try { eException.ScrollToEnd(); } catch { /* ignore */ }
	}

	void rebuilddisplay() {
		eException.Text = cbFullStack.IsChecked == true
			? fullBuf.ToString()
			: filteredBuf.ToString();
	}

	static string filterstack(string raw) {
		if (string.IsNullOrEmpty(raw)) return "";
		var lines = raw.Split('\n');
		var sb = new StringBuilder();
		foreach (var line in lines) {
			var trimmed = line.TrimEnd('\r');
			if (!isframe(trimmed)) {
				sb.AppendLine(trimmed);
				continue;
			}
			if (isprojectframe(trimmed))
				sb.AppendLine(trimmed);
		}
		return sb.ToString().TrimEnd();
	}

	static bool isframe(string line) =>
		line.StartsWith("   at ", StringComparison.Ordinal)
		|| line.StartsWith("   在 ", StringComparison.Ordinal);

	static bool isprojectframe(string line) =>
		line.IndexOf("DocviewWPF", StringComparison.OrdinalIgnoreCase) >= 0;
}
