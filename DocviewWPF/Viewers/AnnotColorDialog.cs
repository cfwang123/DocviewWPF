using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MediaColor = System.Windows.Media.Color;

namespace DocviewWPF;

/// <summary>
/// 标注选色：仿 Word 主题色/标准色 + HSV 自定义。
/// </summary>
sealed class AnnotColorDialog : Window {
	MediaColor current;
	readonly Border preview;
	readonly Image svImage;
	readonly Rectangle hueBar;
	readonly Thumb hueThumb;
	readonly Canvas svCanvas;
	readonly Ellipse svCursor;
	readonly Slider sH, sS, sV;
	readonly TextBox eHex;
	bool silent;
	double hue; // 0..360
	double sat; // 0..1
	double val; // 0..1

	public MediaColor Result => current;

	public AnnotColorDialog(Window owner, MediaColor initial) {
		Owner = owner;
		Title = Loc.T("tip_annot_color");
		Width = 420;
		Height = 520;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ResizeMode = ResizeMode.NoResize;
		Background = Brushes.White;
		ShowInTaskbar = false;

		current = initial.A == 0 ? MediaColor.FromRgb(0xE5, 0x39, 0x35) : initial;
		RgbToHsv(current, out hue, out sat, out val);

		var root = new DockPanel { Margin = new Thickness(12) };

		// —— 底部按钮 ——
		var buttons = new StackPanel {
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 10, 0, 0),
		};
		DockPanel.SetDock(buttons, Dock.Bottom);
		var bok = new Button { Content = Loc.T("ok") ?? "确定", Width = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
		var bcancel = new Button { Content = Loc.T("cancel") ?? "取消", Width = 80, Height = 28, IsCancel = true };
		bok.Click += (_, _) => { DialogResult = true; Close(); };
		bcancel.Click += (_, _) => { DialogResult = false; Close(); };
		buttons.Children.Add(bok);
		buttons.Children.Add(bcancel);
		root.Children.Add(buttons);

		var body = new StackPanel();

		// —— 主题色（仿 Word：10 列 × 6 行 深浅）——
		body.Children.Add(mklabel("主题颜色"));
		body.Children.Add(buildthemetable());

		// —— 标准色 ——
		body.Children.Add(mklabel("标准色"));
		body.Children.Add(buildstandardrow());

		// —— HSV ——
		body.Children.Add(mklabel("自定义（HSV）"));
		var hsvRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0), Height = 160 };

		// 色相条
		var hueHost = new Canvas { Width = 22, Height = 150, Margin = new Thickness(10, 0, 0, 0) };
		DockPanel.SetDock(hueHost, Dock.Right);
		hueBar = new Rectangle { Width = 18, Height = 150, RadiusX = 2, RadiusY = 2 };
		hueBar.Fill = makehuebrush();
		Canvas.SetLeft(hueBar, 2);
		hueHost.Children.Add(hueBar);
		hueThumb = new Thumb {
			Width = 22, Height = 8,
			Background = Brushes.White,
			BorderBrush = Brushes.DimGray,
			BorderThickness = new Thickness(1),
		};
		hueThumb.DragDelta += onhuedrag;
		hueHost.Children.Add(hueThumb);
		hueBar.MouseLeftButtonDown += (s, e) => {
			var y = e.GetPosition(hueHost).Y;
			sethuefromy(y);
			hueThumb.CaptureMouse();
			e.Handled = true;
		};
		hueHost.MouseMove += (s, e) => {
			if (e.LeftButton == MouseButtonState.Pressed && hueThumb.IsMouseCaptured)
				sethuefromy(e.GetPosition(hueHost).Y);
		};
		hueHost.MouseLeftButtonUp += (_, _) => {
			try { hueThumb.ReleaseMouseCapture(); } catch { /* ignore */ }
		};
		hsvRow.Children.Add(hueHost);

		// SV 方块
		svCanvas = new Canvas {
			Width = 150, Height = 150,
			ClipToBounds = true,
			Cursor = Cursors.Cross,
		};
		svImage = new Image { Width = 150, Height = 150, Stretch = Stretch.Fill };
		svCanvas.Children.Add(svImage);
		svCursor = new Ellipse {
			Width = 12, Height = 12,
			Stroke = Brushes.White,
			StrokeThickness = 2,
			Fill = Brushes.Transparent,
			IsHitTestVisible = false,
		};
		svCanvas.Children.Add(svCursor);
		svCanvas.MouseLeftButtonDown += onsvdown;
		svCanvas.MouseMove += onsvmove;
		svCanvas.MouseLeftButtonUp += (_, _) => {
			try { svCanvas.ReleaseMouseCapture(); } catch { /* ignore */ }
		};
		DockPanel.SetDock(svCanvas, Dock.Left);
		hsvRow.Children.Add(svCanvas);

		// 右侧：预览 + 滑条 + hex
		var side = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
		preview = new Border {
			Width = 56, Height = 56,
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(4),
			Margin = new Thickness(0, 0, 0, 8),
		};
		side.Children.Add(preview);
		sH = mkslider(0, 360, hue, "H");
		sS = mkslider(0, 100, sat * 100, "S");
		sV = mkslider(0, 100, val * 100, "V");
		sH.ValueChanged += (_, _) => { if (!silent) { hue = sH.Value; rebuildsv(); applyhsv(); } };
		sS.ValueChanged += (_, _) => { if (!silent) { sat = sS.Value / 100.0; applyhsv(); } };
		sV.ValueChanged += (_, _) => { if (!silent) { val = sV.Value / 100.0; applyhsv(); } };
		side.Children.Add(sH);
		side.Children.Add(sS);
		side.Children.Add(sV);
		var hexRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
		hexRow.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
		eHex = new TextBox { Width = 80, Height = 22, VerticalContentAlignment = VerticalAlignment.Center };
		eHex.LostKeyboardFocus += (_, _) => parsehex();
		eHex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { parsehex(); e.Handled = true; } };
		hexRow.Children.Add(eHex);
		side.Children.Add(hexRow);
		hsvRow.Children.Add(side);

		body.Children.Add(hsvRow);
		root.Children.Add(body);
		Content = root;

		rebuildsv();
		syncui();
	}

	static TextBlock mklabel(string t) => new TextBlock {
		Text = t,
		FontWeight = FontWeights.SemiBold,
		Margin = new Thickness(0, 8, 0, 4),
		Foreground = new SolidColorBrush(MediaColor.FromRgb(0x37, 0x41, 0x51)),
	};

	/// <summary>Word 风格主题色：10 基色 × 6 深浅。</summary>
	UniformGrid buildthemetable() {
		// 基色（近似 Office 主题）
		var bases = new[] {
			MediaColor.FromRgb(0xFF, 0xFF, 0xFF), // 白
			MediaColor.FromRgb(0x00, 0x00, 0x00), // 黑
			MediaColor.FromRgb(0xEE, 0xEC, 0xE1), // 棕灰
			MediaColor.FromRgb(0x1F, 0x49, 0x7D), // 深蓝
			MediaColor.FromRgb(0x4F, 0x81, 0xBD), // 蓝
			MediaColor.FromRgb(0xC0, 0x50, 0x4D), // 红
			MediaColor.FromRgb(0x9B, 0xBB, 0x59), // 绿
			MediaColor.FromRgb(0x80, 0x64, 0xA2), // 紫
			MediaColor.FromRgb(0x4B, 0xAC, 0xC6), // 青
			MediaColor.FromRgb(0xF7, 0x96, 0x46), // 橙
		};
		// 相对亮度行：浅→深（白/黑列特殊处理）
		var grid = new UniformGrid { Rows = 6, Columns = 10, Margin = new Thickness(0, 0, 0, 4) };
		for (var row = 0; row < 6; row++) {
			for (var col = 0; col < 10; col++) {
				var b = bases[col];
				MediaColor c;
				if (col == 0) {
					// 白 → 灰
					var g = (byte)(255 - row * 40);
					c = MediaColor.FromRgb(g, g, g);
				} else if (col == 1) {
					// 黑 → 浅灰
					var g = (byte)(row * 42);
					c = MediaColor.FromRgb(g, g, g);
				} else if (row == 0) {
					c = b;
				} else if (row <= 2) {
					// 更浅
					c = mix(b, MediaColor.FromRgb(0xFF, 0xFF, 0xFF), 0.25 * row);
				} else {
					// 更深
					c = mix(b, MediaColor.FromRgb(0x00, 0x00, 0x00), 0.22 * (row - 2));
				}
				grid.Children.Add(swatch(c));
			}
		}
		return grid;
	}

	WrapPanel buildstandardrow() {
		var std = new[] {
			MediaColor.FromRgb(0xC0, 0x00, 0x00),
			MediaColor.FromRgb(0xFF, 0x00, 0x00),
			MediaColor.FromRgb(0xFF, 0xC0, 0x00),
			MediaColor.FromRgb(0xFF, 0xFF, 0x00),
			MediaColor.FromRgb(0x92, 0xD0, 0x50),
			MediaColor.FromRgb(0x00, 0xB0, 0x50),
			MediaColor.FromRgb(0x00, 0xB0, 0xF0),
			MediaColor.FromRgb(0x00, 0x70, 0xC0),
			MediaColor.FromRgb(0x00, 0x20, 0x60),
			MediaColor.FromRgb(0x70, 0x30, 0xA0),
			MediaColor.FromRgb(0xFF, 0x66, 0x00),
			MediaColor.FromRgb(0xFF, 0x99, 0xCC),
			MediaColor.FromRgb(0x99, 0xCC, 0xFF),
			MediaColor.FromRgb(0x33, 0x99, 0x66),
			MediaColor.FromRgb(0x66, 0x66, 0x99),
		};
		var p = new WrapPanel();
		foreach (var c in std)
			p.Children.Add(swatch(c, 22));
		return p;
	}

	Button swatch(MediaColor c, double size = 20) {
		var b = new Button {
			Width = size, Height = size,
			Margin = new Thickness(1),
			Padding = new Thickness(0),
			BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x9C, 0xA3, 0xAF)),
			BorderThickness = new Thickness(1),
			Background = new SolidColorBrush(c),
			Tag = c,
			ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
			Cursor = Cursors.Hand,
		};
		b.Click += (_, _) => {
			current = c;
			RgbToHsv(current, out hue, out sat, out val);
			rebuildsv();
			syncui();
		};
		return b;
	}

	Slider mkslider(double min, double max, double v, string label) {
		var s = new Slider {
			Minimum = min, Maximum = max, Value = v,
			Width = 120, Margin = new Thickness(0, 2, 0, 2),
			TickFrequency = 1, IsSnapToTickEnabled = false,
			ToolTip = label,
		};
		return s;
	}

	static MediaColor mix(MediaColor a, MediaColor b, double t) {
		t = Math.Max(0, Math.Min(1, t));
		return MediaColor.FromRgb(
			(byte)(a.R + (b.R - a.R) * t),
			(byte)(a.G + (b.G - a.G) * t),
			(byte)(a.B + (b.B - a.B) * t));
	}

	static LinearGradientBrush makehuebrush() {
		var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
		for (var i = 0; i <= 6; i++) {
			var h = i * 60.0;
			if (h >= 360) h = 359.9;
			b.GradientStops.Add(new GradientStop(HsvToRgb(h, 1, 1), i / 6.0));
		}
		return b;
	}

	void rebuildsv() {
		const int N = 150;
		var wb = new WriteableBitmap(N, N, 96, 96, PixelFormats.Bgr24, null);
		var stride = N * 3;
		var pixels = new byte[stride * N];
		for (var y = 0; y < N; y++) {
			var v = 1.0 - y / (double)(N - 1);
			for (var x = 0; x < N; x++) {
				var s = x / (double)(N - 1);
				var c = HsvToRgb(hue, s, v);
				var i = y * stride + x * 3;
				pixels[i] = c.B;
				pixels[i + 1] = c.G;
				pixels[i + 2] = c.R;
			}
		}
		wb.WritePixels(new Int32Rect(0, 0, N, N), pixels, stride, 0);
		wb.Freeze();
		svImage.Source = wb;
	}

	void onsvdown(object sender, MouseButtonEventArgs e) {
		svCanvas.CaptureMouse();
		updatesv(e.GetPosition(svCanvas));
		e.Handled = true;
	}

	void onsvmove(object sender, MouseEventArgs e) {
		if (e.LeftButton != MouseButtonState.Pressed || !svCanvas.IsMouseCaptured) return;
		updatesv(e.GetPosition(svCanvas));
	}

	void updatesv(Point p) {
		sat = Math.Max(0, Math.Min(1, p.X / 150.0));
		val = Math.Max(0, Math.Min(1, 1.0 - p.Y / 150.0));
		applyhsv();
	}

	void onhuedrag(object sender, DragDeltaEventArgs e) {
		var y = Canvas.GetTop(hueThumb) + e.VerticalChange;
		sethuefromy(y + 4);
	}

	void sethuefromy(double y) {
		y = Math.Max(0, Math.Min(150, y));
		hue = y / 150.0 * 360.0;
		if (hue >= 360) hue = 359.9;
		rebuildsv();
		applyhsv();
	}

	void applyhsv() {
		current = HsvToRgb(hue, sat, val);
		syncui();
	}

	void syncui() {
		silent = true;
		try {
			preview.Background = new SolidColorBrush(current);
			eHex.Text = $"{current.R:X2}{current.G:X2}{current.B:X2}";
			sH.Value = hue;
			sS.Value = sat * 100;
			sV.Value = val * 100;
			Canvas.SetLeft(svCursor, sat * 150 - 6);
			Canvas.SetTop(svCursor, (1 - val) * 150 - 6);
			Canvas.SetTop(hueThumb, hue / 360.0 * 150 - 4);
			Canvas.SetLeft(hueThumb, 0);
		} finally {
			silent = false;
		}
	}

	void parsehex() {
		var s = (eHex.Text ?? "").Trim().TrimStart('#');
		if (s.Length != 6) return;
		try {
			var r = Convert.ToByte(s.Substring(0, 2), 16);
			var g = Convert.ToByte(s.Substring(2, 2), 16);
			var b = Convert.ToByte(s.Substring(4, 2), 16);
			current = MediaColor.FromRgb(r, g, b);
			RgbToHsv(current, out hue, out sat, out val);
			rebuildsv();
			syncui();
		} catch { /* ignore */ }
	}

	public static MediaColor HsvToRgb(double h, double s, double v) {
		h = ((h % 360) + 360) % 360;
		s = Math.Max(0, Math.Min(1, s));
		v = Math.Max(0, Math.Min(1, v));
		var c = v * s;
		var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
		var m = v - c;
		double rp, gp, bp;
		if (h < 60) { rp = c; gp = x; bp = 0; }
		else if (h < 120) { rp = x; gp = c; bp = 0; }
		else if (h < 180) { rp = 0; gp = c; bp = x; }
		else if (h < 240) { rp = 0; gp = x; bp = c; }
		else if (h < 300) { rp = x; gp = 0; bp = c; }
		else { rp = c; gp = 0; bp = x; }
		return MediaColor.FromRgb(
			(byte)Math.Round((rp + m) * 255),
			(byte)Math.Round((gp + m) * 255),
			(byte)Math.Round((bp + m) * 255));
	}

	public static void RgbToHsv(MediaColor c, out double h, out double s, out double v) {
		var r = c.R / 255.0;
		var g = c.G / 255.0;
		var b = c.B / 255.0;
		var max = Math.Max(r, Math.Max(g, b));
		var min = Math.Min(r, Math.Min(g, b));
		var d = max - min;
		v = max;
		s = max < 1e-9 ? 0 : d / max;
		if (d < 1e-9) h = 0;
		else if (max == r) h = 60 * (((g - b) / d) % 6);
		else if (max == g) h = 60 * ((b - r) / d + 2);
		else h = 60 * ((r - g) / d + 4);
		if (h < 0) h += 360;
	}

	/// <summary>弹出选色；取消返回 null。</summary>
	public static MediaColor? Pick(Window owner, MediaColor initial) {
		var dlg = new AnnotColorDialog(owner, initial);
		if (dlg.ShowDialog() == true)
			return dlg.Result;
		return null;
	}
}
