using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DocviewWPF;

/// <summary>更新进度窗：状态文字 + 进度条 + 取消。</summary>
sealed class UpdateProgressWindow : Window {
	readonly TextBlock lbstatus;
	readonly ProgressBar bar;
	readonly Button bcancel;
	readonly Button bclose;
	public CancellationTokenSource Cts { get; private set; } = new CancellationTokenSource();
	public bool Cancelled => Cts != null && Cts.IsCancellationRequested;

	public UpdateProgressWindow(Window owner) {
		Owner = owner;
		Title = "检查更新";
		Width = 440;
		Height = 180;
		MinWidth = 360;
		MinHeight = 160;
		WindowStartupLocation = owner != null
			? WindowStartupLocation.CenterOwner
			: WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.CanMinimize;
		ShowInTaskbar = true;
		Background = Brushes.White;

		var root = new DockPanel { Margin = new Thickness(16) };
		var bottom = new StackPanel {
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 12, 0, 0),
		};
		DockPanel.SetDock(bottom, Dock.Bottom);
		bcancel = new Button {
			Content = "取消",
			Width = 88,
			Height = 28,
			Margin = new Thickness(0, 0, 8, 0),
			IsEnabled = true,
		};
		bcancel.Click += (_, _) => {
			try { Cts?.Cancel(); } catch { /* ignore */ }
			bcancel.IsEnabled = false;
			SetStatus("正在取消…");
		};
		bclose = new Button {
			Content = "关闭",
			Width = 88,
			Height = 28,
			Visibility = Visibility.Collapsed,
			IsDefault = true,
		};
		bclose.Click += (_, _) => Close();
		bottom.Children.Add(bcancel);
		bottom.Children.Add(bclose);
		root.Children.Add(bottom);

		var body = new StackPanel();
		lbstatus = new TextBlock {
			Text = "准备中…",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13,
			Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
			Margin = new Thickness(0, 0, 0, 12),
		};
		bar = new ProgressBar {
			Height = 18,
			Minimum = 0,
			Maximum = 100,
			IsIndeterminate = true,
		};
		body.Children.Add(lbstatus);
		body.Children.Add(bar);
		root.Children.Add(body);
		Content = root;

		Closing += (_, e) => {
			// 进行中点 X = 取消
			if (bcancel.IsEnabled && bclose.Visibility != Visibility.Visible) {
				try { Cts?.Cancel(); } catch { /* ignore */ }
			}
		};
	}

	public void SetStatus(string text) {
		void go() {
			if (lbstatus != null) lbstatus.Text = text ?? "";
		}
		if (Dispatcher.CheckAccess()) go();
		else Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(go));
	}

	/// <summary>0..100；&lt;0 表示不确定进度。</summary>
	public void SetProgress(double percent) {
		void go() {
			if (percent < 0) {
				bar.IsIndeterminate = true;
				return;
			}
			bar.IsIndeterminate = false;
			if (percent > 100) percent = 100;
			if (percent < 0) percent = 0;
			bar.Value = percent;
		}
		if (Dispatcher.CheckAccess()) go();
		else Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(go));
	}

	public void MarkDone(bool ok) {
		void go() {
			bar.IsIndeterminate = false;
			if (ok) bar.Value = 100;
			bcancel.IsEnabled = false;
			bcancel.Visibility = Visibility.Collapsed;
			bclose.Visibility = Visibility.Visible;
			bclose.Focus();
		}
		if (Dispatcher.CheckAccess()) go();
		else Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(go));
	}

	public void ResetCts() {
		try { Cts?.Dispose(); } catch { /* ignore */ }
		Cts = new CancellationTokenSource();
		bcancel.IsEnabled = true;
		bcancel.Visibility = Visibility.Visible;
		bclose.Visibility = Visibility.Collapsed;
		bar.IsIndeterminate = true;
		bar.Value = 0;
	}
}
