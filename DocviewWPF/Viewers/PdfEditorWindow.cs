using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MediaColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfPoint = System.Windows.Point;
using IoPath = System.IO.Path;

namespace DocviewWPF;

/// <summary>
/// PDF 专业编辑窗口（Acrobat 向）：
/// · 页对象选择 / 多选框选 / 移动 / 缩放 / 旋转 / 删除 / 复制
/// · 文字增改（系统字体嵌入）+ 填色 / 图片 / 遮盖 / 矩形
/// · 页增删旋 · 撤销重做 · 矢量 SaveAsCopy
/// </summary>
sealed class PdfEditorWindow : Window {
	const int MAX_OUTLINE = 400; // 避免超多路径对象拖垮 UI
	const double MARQUEE_MIN = 4; // dip，小于此视为点击

	readonly string sourcePath;
	PdfProEngine engine;
	int curPage;
	double zoom = 1.25;
	/// <summary>当前多选集合（同页）。</summary>
	readonly List<PdfProObject> selection = new();
	bool dirty;
	bool suppressUi;
	enum Tool { Select, AddText, AddImage, Whiteout, Rect }
	Tool tool = Tool.Select;

	readonly ListBox ePages;
	readonly ListBox eObjects;
	readonly ScrollViewer scroller;
	readonly Canvas stage;
	readonly System.Windows.Controls.Image pageImage;
	readonly Canvas overlay;
	readonly TextBox eText;
	readonly ComboBox eFont;
	readonly ComboBox eFontSize;
	readonly Button bFillColor;
	readonly TextBlock lbStatus;
	readonly TextBlock lbTitle;
	readonly TextBlock lbTool;

	readonly List<Border> hoverBoxes = new();
	readonly List<Border> selBoxes = new();
	Ellipse resizeHandle;
	bool dragging;
	bool resizing;
	bool marquee;
	WpfPoint dragStart; // 按下时鼠标位置（overlay dip）
	double resizeStartW, resizeStartH, resizeStartX, resizeStartY;
	string pendingImagePath;
	bool drawing;
	WpfPoint drawStart;
	Rectangle drawPreview;
	Rectangle marqueeRect;
	MediaColor fillColor = MediaColor.FromRgb(0, 0, 0);
	/// <summary>拖动幽灵：从页面裁切的预览，跟鼠标移动；松手再写回 PDF。</summary>
	readonly List<DragGhost> dragGhosts = new();

	/// <summary>主选中项（属性面板 / 文字编辑 / 缩放手柄）。</summary>
	PdfProObject primary => selection.Count > 0 ? selection[selection.Count - 1] : null;

	public PdfEditorWindow(Window owner, string pdfPath) {
		if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
			throw new FileNotFoundException("PDF 不存在", pdfPath);
		sourcePath = IoPath.GetFullPath(pdfPath);
		Owner = owner;
		Title = "PDF 专业编辑 - " + IoPath.GetFileName(sourcePath);
		Width = 1360;
		Height = 880;
		MinWidth = 1000;
		MinHeight = 680;
		WindowStartupLocation = owner != null
			? WindowStartupLocation.CenterOwner
			: WindowStartupLocation.CenterScreen;
		ShowInTaskbar = true;
		Background = brush("BgApp") ?? WpfBrushes.White;
		FontSize = AppSettings.Current.UiFontSize;

		var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
		lbTitle = new TextBlock {
			Text = IoPath.GetFileName(sourcePath),
			VerticalAlignment = VerticalAlignment.Center,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0, 0, 12, 0),
			MaxWidth = 180,
			TextTrimming = TextTrimming.CharacterEllipsis,
		};
		toolbar.Children.Add(lbTitle);
		toolbar.Children.Add(mkBtn("保存", () => save(false), "Ctrl+S 矢量 PDF"));
		toolbar.Children.Add(mkBtn("另存为…", () => save(true)));
		toolbar.Children.Add(sep());
		toolbar.Children.Add(mkBtn("撤销", doUndo, "Ctrl+Z"));
		toolbar.Children.Add(mkBtn("重做", doRedo, "Ctrl+Y"));
		toolbar.Children.Add(sep());
		toolbar.Children.Add(mkBtn("选择", () => setTool(Tool.Select), "V · 拖空白框选 · Ctrl+点加减选"));
		toolbar.Children.Add(mkBtn("文字", () => setTool(Tool.AddText), "T · 点击放置"));
		toolbar.Children.Add(mkBtn("图片", addImageTool, "I"));
		toolbar.Children.Add(mkBtn("遮盖", () => setTool(Tool.Whiteout), "拖出白色矩形"));
		toolbar.Children.Add(mkBtn("矩形", () => setTool(Tool.Rect), "拖出填充矩形"));
		toolbar.Children.Add(sep());
		toolbar.Children.Add(mkBtn("删除", deleteSelected, "Delete"));
		toolbar.Children.Add(mkBtn("复制", duplicateSelected, "Ctrl+D"));
		toolbar.Children.Add(mkBtn("旋转90°", () => rotateSelected(90)));
		toolbar.Children.Add(mkBtn("放大", () => scaleSelected(1.15, 1.15)));
		toolbar.Children.Add(mkBtn("缩小", () => scaleSelected(1 / 1.15, 1 / 1.15)));
		toolbar.Children.Add(sep());
		toolbar.Children.Add(mkBtn("－", () => setZoom(zoom / 1.15)));
		toolbar.Children.Add(mkBtn("＋", () => setZoom(zoom * 1.15)));
		toolbar.Children.Add(mkBtn("适宽", fitWidth));
		toolbar.Children.Add(sep());
		toolbar.Children.Add(mkBtn("新页", insertPage));
		toolbar.Children.Add(mkBtn("删页", deletePage));
		toolbar.Children.Add(mkBtn("页旋转", () => rotatePage(1)));
		lbTool = new TextBlock {
			Text = "工具: 选择（框选/多选）", VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(12, 0, 0, 0), Opacity = 0.75, FontSize = 11,
		};
		toolbar.Children.Add(lbTool);
		var topBar = new Border {
			Background = brush("BgToolbar") ?? WpfBrushes.WhiteSmoke,
			BorderBrush = brush("BorderSoft") ?? WpfBrushes.LightGray,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Child = new ScrollViewer {
				Content = toolbar,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
			},
		};

		ePages = new ListBox { BorderThickness = new Thickness(0), Background = brush("BgSoft") ?? WpfBrushes.WhiteSmoke };
		ePages.SelectionChanged += (_, _) => {
			if (suppressUi) return;
			if (ePages.SelectedIndex >= 0) gotoPage(ePages.SelectedIndex);
		};
		var left = new DockPanel { Width = 120 };
		var lbP = new TextBlock { Text = "页面", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 8, 8, 4) };
		DockPanel.SetDock(lbP, Dock.Top);
		left.Children.Add(lbP);
		left.Children.Add(ePages);

		eObjects = new ListBox {
			Height = 220, Margin = new Thickness(0, 8, 0, 0),
			SelectionMode = SelectionMode.Extended,
		};
		eObjects.SelectionChanged += onObjectListSelectionChanged;
		eText = new TextBox {
			AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 6),
		};
		eText.LostKeyboardFocus += (_, _) => applyTextFromUi();
		eFont = new ComboBox { IsEditable = true, Margin = new Thickness(0, 2, 0, 4) };
		foreach (var f in new[] {
			"华文中宋", "STZhongsong", "SimSun", "Microsoft YaHei", "SimHei", "KaiTi",
			"华文宋体", "华文楷体", "Helvetica", "Times-Roman", "Courier", "Arial", "Times New Roman",
		})
			eFont.Items.Add(f);
		eFont.SelectedIndex = 0;
		eFontSize = new ComboBox { IsEditable = true, Margin = new Thickness(0, 2, 0, 4) };
		foreach (var s in new[] { "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48" })
			eFontSize.Items.Add(s);
		eFontSize.SelectedItem = "12";
		bFillColor = new Button {
			Content = "文字/填充颜色", Margin = new Thickness(0, 4, 0, 0),
			Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand,
		};
		bFillColor.Click += (_, _) => pickFillColor();
		updateColorBtn();

		var prop = new StackPanel { Margin = new Thickness(8) };
		prop.Children.Add(new TextBlock { Text = "对象属性", FontWeight = FontWeights.SemiBold });
		prop.Children.Add(new TextBlock { Text = "文字内容（矢量）", Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7, FontSize = 11 });
		prop.Children.Add(eText);
		prop.Children.Add(mkBtn("应用文字修改", applyTextFromUi, "删除旧 CID 文字并以系统字体重建（安全）"));
		prop.Children.Add(new TextBlock { Text = "字体（改字/新建时嵌入）", Opacity = 0.7, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
		prop.Children.Add(eFont);
		prop.Children.Add(new TextBlock { Text = "字号", Opacity = 0.7, FontSize = 11 });
		prop.Children.Add(eFontSize);
		prop.Children.Add(bFillColor);
		prop.Children.Add(mkBtn("应用到选中填色", applyFillToSelected));
		prop.Children.Add(new TextBlock { Text = "本页对象（Ctrl/Shift 多选）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
		prop.Children.Add(eObjects);
		prop.Children.Add(new TextBlock {
			Text = "选择：\n"
				+ "· 空白处拖拽 = 框选\n"
				+ "· Ctrl+点击 = 加减选\n"
				+ "· 拖选中对象 = 整组移动\n"
				+ "· Delete 删除全部选中\n"
				+ "· 中文用微软雅黑等嵌入字体",
			TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
		});
		var right = new Border {
			Width = 290,
			Background = brush("BgSoft") ?? WpfBrushes.WhiteSmoke,
			BorderBrush = brush("BorderSoft") ?? WpfBrushes.LightGray,
			BorderThickness = new Thickness(1, 0, 0, 0),
			Child = new ScrollViewer { Content = prop, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
		};

		pageImage = new System.Windows.Controls.Image { Stretch = Stretch.None, SnapsToDevicePixels = true };
		overlay = new Canvas { Background = WpfBrushes.Transparent, IsHitTestVisible = true };
		resizeHandle = new Ellipse {
			Width = 10, Height = 10,
			Fill = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x63, 0xEB)),
			Stroke = WpfBrushes.White,
			StrokeThickness = 1,
			Visibility = Visibility.Collapsed,
			Cursor = Cursors.SizeNWSE,
		};
		drawPreview = new Rectangle {
			Stroke = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x63, 0xEB)),
			StrokeThickness = 1,
			StrokeDashArray = new DoubleCollection { 4, 2 },
			Fill = new SolidColorBrush(MediaColor.FromArgb(0x40, 0x25, 0x63, 0xEB)),
			Visibility = Visibility.Collapsed,
			IsHitTestVisible = false,
		};
		marqueeRect = new Rectangle {
			Stroke = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x63, 0xEB)),
			StrokeThickness = 1,
			StrokeDashArray = new DoubleCollection { 3, 2 },
			Fill = new SolidColorBrush(MediaColor.FromArgb(0x30, 0x37, 0x99, 0xF5)),
			Visibility = Visibility.Collapsed,
			IsHitTestVisible = false,
		};
		overlay.Children.Add(marqueeRect);
		overlay.Children.Add(drawPreview);
		overlay.Children.Add(resizeHandle);
		overlay.MouseLeftButtonDown += onOverlayDown;
		overlay.MouseMove += onOverlayMove;
		overlay.MouseLeftButtonUp += onOverlayUp;
		resizeHandle.MouseLeftButtonDown += onResizeDown;

		stage = new Canvas();
		stage.Children.Add(pageImage);
		stage.Children.Add(overlay);
		scroller = new ScrollViewer {
			Content = stage,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Background = new SolidColorBrush(MediaColor.FromRgb(0xE5, 0xE7, 0xEB)),
			Padding = new Thickness(16),
		};
		var center = new Border { Child = scroller, Background = scroller.Background };

		var body = new Grid();
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
		Grid.SetColumn(left, 0);
		Grid.SetColumn(center, 1);
		Grid.SetColumn(right, 2);
		body.Children.Add(left);
		body.Children.Add(center);
		body.Children.Add(right);

		lbStatus = new TextBlock { Text = "就绪", Margin = new Thickness(8, 2, 8, 2), FontSize = 11, Opacity = 0.8 };
		var status = new Border {
			Background = brush("BgStatus") ?? WpfBrushes.WhiteSmoke,
			BorderBrush = brush("BorderSoft") ?? WpfBrushes.LightGray,
			BorderThickness = new Thickness(0, 1, 0, 0),
			Child = lbStatus,
		};

		var root = new Grid();
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid.SetRow(topBar, 0);
		Grid.SetRow(body, 1);
		Grid.SetRow(status, 2);
		root.Children.Add(topBar);
		root.Children.Add(body);
		root.Children.Add(status);
		Content = root;

		PreviewKeyDown += onkey;
		Loaded += (_, _) => openDoc();
		Closing += onclosing;
		Closed += (_, _) => {
			try { engine?.Dispose(); } catch { /* ignore */ }
			engine = null;
		};
	}

	void setTool(Tool t) {
		tool = t;
		lbTool.Text = t switch {
			Tool.Select => "工具: 选择（拖空白框选 · Ctrl+点多选）",
			Tool.AddText => "工具: 添加文字（点击放置）",
			Tool.AddImage => "工具: 插入图片（点击放置）",
			Tool.Whiteout => "工具: 遮盖（拖出矩形）",
			Tool.Rect => "工具: 矩形（拖出填充）",
			_ => "工具",
		};
		setStatus(lbTool.Text);
	}

	void openDoc() {
		try {
			DocLog.Info("PdfEditor openDoc begin " + sourcePath);
			engine = PdfProEngine.Open(sourcePath);
			rebuildPageList();
			gotoPage(0);
			setStatus($"矢量编辑引擎就绪 · {engine.PageCount} 页 · 框选多选已启用");
			DocLog.Info("PdfEditor openDoc ok");
		} catch (Exception ex) {
			DocLog.Error("PdfEditor open", ex);
			MessageBox.Show(this, "打开失败: " + ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Error);
			Close();
		}
	}

	void rebuildPageList() {
		suppressUi = true;
		try {
			ePages.Items.Clear();
			for (var i = 0; i < engine.PageCount; i++)
				ePages.Items.Add($"第 {i + 1} 页");
			if (curPage < engine.PageCount)
				ePages.SelectedIndex = curPage;
		} finally { suppressUi = false; }
	}

	void gotoPage(int p) {
		if (engine == null) return;
		if (p < 0) p = 0;
		if (p >= engine.PageCount) p = engine.PageCount - 1;
		dragging = false;
		clearDragGhosts();
		curPage = p;
		clearSelection();
		suppressUi = true;
		try { ePages.SelectedIndex = p; } finally { suppressUi = false; }
		renderAndList();
	}

	/// <summary>拖动中轻量重渲：只更新页面位图与选框，不刷对象列表。</summary>
	void renderPageLive(bool lowQuality) {
		if (engine == null) return;
		try {
			var ptW = Math.Max(1, (double)engine.PageSizesPt[curPage].Width);
			var ptH = Math.Max(1, (double)engine.PageSizesPt[curPage].Height);
			var dipW = ptW * 96.0 / 72.0 * zoom;
			var dipH = ptH * 96.0 / 72.0 * zoom;
			// 拖动预览降低像素密度以跟手
			var sharp = lowQuality ? 1.0 : 1.5;
			var pixelW = Math.Max(1, (int)Math.Round(dipW * sharp));
			var pixelH = Math.Max(1, (int)Math.Round(dipH * sharp));
			if (pixelW > 2800 || pixelH > 2800) {
				var s = Math.Min(2800.0 / pixelW, 2800.0 / pixelH);
				pixelW = Math.Max(1, (int)(pixelW * s));
				pixelH = Math.Max(1, (int)(pixelH * s));
			}
			var dipDpi = pixelW / Math.Max(1e-6, dipW) * 96.0;
			BitmapSource bmp = null;
			try {
				bmp = engine.Render(curPage, pixelW, pixelH, dipDpi);
			} catch (Exception ex) {
				DocLog.Warn("renderPageLive: " + ex.Message);
				return;
			}
			if (bmp != null) {
				pageImage.Source = bmp;
				pageImage.Width = dipW;
				pageImage.Height = dipH;
			}
			// 选框跟对象 bounds（已在 MoveObjects 更新）
			updateSelChrome();
		} catch (Exception ex) {
			DocLog.Warn("renderPageLive: " + ex.Message);
		}
	}

	void renderAndList() {
		if (engine == null) return;
		try {
			var ptW = Math.Max(1, (double)engine.PageSizesPt[curPage].Width);
			var ptH = Math.Max(1, (double)engine.PageSizesPt[curPage].Height);
			var dipW = ptW * 96.0 / 72.0 * zoom;
			var dipH = ptH * 96.0 / 72.0 * zoom;
			var pixelW = Math.Max(1, (int)Math.Round(dipW * 1.5));
			var pixelH = Math.Max(1, (int)Math.Round(dipH * 1.5));
			if (pixelW > 4000 || pixelH > 4000) {
				var s = Math.Min(4000.0 / pixelW, 4000.0 / pixelH);
				pixelW = Math.Max(1, (int)(pixelW * s));
				pixelH = Math.Max(1, (int)(pixelH * s));
			}
			var dipDpi = pixelW / Math.Max(1e-6, dipW) * 96.0;
			BitmapSource bmp = null;
			try {
				bmp = engine.Render(curPage, pixelW, pixelH, dipDpi);
			} catch (Exception ex) {
				DocLog.Error("render", ex);
			}
			pageImage.Source = bmp;
			pageImage.Width = dipW;
			pageImage.Height = dipH;
			stage.Width = dipW;
			stage.Height = dipH;
			overlay.Width = dipW;
			overlay.Height = dipH;
			Canvas.SetLeft(pageImage, 0);
			Canvas.SetTop(pageImage, 0);
			Canvas.SetLeft(overlay, 0);
			Canvas.SetTop(overlay, 0);
			refreshObjectList();
			drawObjectOutlines();
			updateSelChrome();
			setStatus($"第 {curPage + 1}/{engine.PageCount} 页 · 缩放 {(int)(zoom * 100)}% · 对象 {eObjects.Items.Count}"
				+ (selection.Count > 0 ? $" · 已选 {selection.Count}" : "")
				+ (engine.CanUndo ? " · 可撤销" : "")
				+ (dirty || engine.IsDirty ? " · 未保存" : ""));
		} catch (Exception ex) {
			DocLog.Error("renderAndList", ex);
			setStatus("渲染失败: " + ex.Message);
		}
	}

	void refreshObjectList() {
		var list = engine.ListObjects(curPage, forceReload: true);
		// 用新列表对象替换 selection 中的引用
		remapSelection(list);
		suppressUi = true;
		try {
			eObjects.Items.Clear();
			var n = 0;
			foreach (var o in list) {
				if (o.MarkedDelete) continue;
				// 列表中隐藏几乎不可见的退化对象，仍可框选到
				var tiny = o.Width < 0.3 && o.Height < 0.3;
				var label = o.Type switch {
					PdfProObjType.Text => tiny ? "文字(空)" : $"文字: {trim(o.Text, 22)}",
					PdfProObjType.Image => "图片",
					PdfProObjType.Path => "路径/图形",
					_ => "对象",
				};
				eObjects.Items.Add(new ObjRow { Obj = o, Label = $"{++n}. {label}" });
			}
			// 同步列表多选
			eObjects.SelectedItems.Clear();
			foreach (ObjRow r in eObjects.Items) {
				if (isSelected(r.Obj))
					eObjects.SelectedItems.Add(r);
			}
		} finally {
			suppressUi = false;
		}
		updateSelChrome();
		syncProps();
	}

	void remapSelection(List<PdfProObject> list) {
		if (selection.Count == 0) return;
		var next = new List<PdfProObject>();
		foreach (var old in selection) {
			PdfProObject found = null;
			foreach (var o in list) {
				if (o.MarkedDelete) continue;
				if (o.Index == old.Index || (o.Type == old.Type
					&& Math.Abs(o.Left - old.Left) < 2 && Math.Abs(o.Bottom - old.Bottom) < 2)) {
					found = o;
					break;
				}
			}
			if (found != null && !next.Contains(found))
				next.Add(found);
		}
		selection.Clear();
		selection.AddRange(next);
	}

	bool isSelected(PdfProObject o) {
		if (o == null) return false;
		foreach (var s in selection) {
			if (s.UiId == o.UiId || (s.Index == o.Index && s.Page == o.Page))
				return true;
		}
		return false;
	}

	void clearSelection() {
		selection.Clear();
		updateSelChrome();
		syncProps();
	}

	void setSelection(IEnumerable<PdfProObject> items, bool additive) {
		if (!additive) selection.Clear();
		foreach (var o in items) {
			if (o == null || o.MarkedDelete) continue;
			// 去掉已存在再加到末尾作为 primary
			for (var i = selection.Count - 1; i >= 0; i--) {
				if (selection[i].UiId == o.UiId || (selection[i].Index == o.Index && selection[i].Page == o.Page))
					selection.RemoveAt(i);
			}
			selection.Add(o);
		}
		syncListFromSelection();
		drawObjectOutlines();
		updateSelChrome();
		syncProps();
	}

	void toggleSelection(PdfProObject o) {
		if (o == null) return;
		for (var i = 0; i < selection.Count; i++) {
			if (selection[i].UiId == o.UiId || (selection[i].Index == o.Index && selection[i].Page == o.Page)) {
				selection.RemoveAt(i);
				syncListFromSelection();
				drawObjectOutlines();
				updateSelChrome();
				syncProps();
				return;
			}
		}
		selection.Add(o);
		syncListFromSelection();
		drawObjectOutlines();
		updateSelChrome();
		syncProps();
	}

	void selectSingle(PdfProObject o) {
		selection.Clear();
		if (o != null) selection.Add(o);
		syncListFromSelection();
		drawObjectOutlines();
		updateSelChrome();
		syncProps();
	}

	void syncListFromSelection() {
		suppressUi = true;
		try {
			eObjects.SelectedItems.Clear();
			foreach (ObjRow r in eObjects.Items) {
				if (isSelected(r.Obj))
					eObjects.SelectedItems.Add(r);
			}
		} finally { suppressUi = false; }
	}

	void onObjectListSelectionChanged(object sender, SelectionChangedEventArgs e) {
		if (suppressUi) return;
		selection.Clear();
		foreach (ObjRow r in eObjects.SelectedItems)
			selection.Add(r.Obj);
		drawObjectOutlines();
		updateSelChrome();
		syncProps();
	}

	void drawObjectOutlines() {
		foreach (var b in hoverBoxes) {
			try { overlay.Children.Remove(b); } catch { /* ignore */ }
		}
		hoverBoxes.Clear();
		if (engine == null) return;
		var list = engine.ListObjects(curPage);
		var ptH = engine.PageSizesPt[curPage].Height;
		var scale = pageScale();
		var drawn = 0;
		// 优先画文字/图片，路径过多时截断
		IEnumerable<PdfProObject> order = list
			.Where(o => !o.MarkedDelete && o.Width >= 0.3 && o.Height >= 0.3)
			.OrderBy(o => o.Type == PdfProObjType.Path ? 1 : 0);
		foreach (var o in order) {
			if (isSelected(o)) continue;
			if (drawn >= MAX_OUTLINE) break;
			o.ToUi(ptH, out var x, out var y, out var w, out var h);
			var br = mkOutlineBox(
				MediaColor.FromArgb(0x55, 0x6B, 0x72, 0x80),
				x * scale, y * scale, Math.Max(2, w * scale), Math.Max(2, h * scale));
			insertBeforeHandle(br);
			hoverBoxes.Add(br);
			drawn++;
		}
	}

	void updateSelChrome() {
		foreach (var b in selBoxes) {
			try { overlay.Children.Remove(b); } catch { /* ignore */ }
		}
		selBoxes.Clear();
		resizeHandle.Visibility = Visibility.Collapsed;
		if (selection.Count == 0 || engine == null) return;

		var ptH = engine.PageSizesPt[curPage].Height;
		var scale = pageScale();
		double unionL = double.MaxValue, unionT = double.MaxValue, unionR = double.MinValue, unionB = double.MinValue;
		var any = false;
		foreach (var o in selection) {
			if (o.MarkedDelete || o.Page != curPage) continue;
			o.ToUi(ptH, out var x, out var y, out var w, out var h);
			var left = x * scale - 2;
			var top = y * scale - 2;
			var ww = Math.Max(4, w * scale + 4);
			var hh = Math.Max(4, h * scale + 4);
			var isPrimary = o == primary;
			var br = mkOutlineBox(
				isPrimary ? MediaColor.FromRgb(0x25, 0x63, 0xEB) : MediaColor.FromRgb(0x60, 0xA5, 0xFA),
				left, top, ww, hh,
				isPrimary ? MediaColor.FromArgb(0x28, 0x25, 0x63, 0xEB) : MediaColor.FromArgb(0x18, 0x60, 0xA5, 0xFA),
				isPrimary ? 2 : 1.5);
			insertBeforeHandle(br);
			selBoxes.Add(br);
			unionL = Math.Min(unionL, left);
			unionT = Math.Min(unionT, top);
			unionR = Math.Max(unionR, left + ww);
			unionB = Math.Max(unionB, top + hh);
			any = true;
		}
		if (any && selection.Count == 1) {
			// 单选显示缩放手柄
			Canvas.SetLeft(resizeHandle, unionR - 5);
			Canvas.SetTop(resizeHandle, unionB - 5);
			resizeHandle.Visibility = Visibility.Visible;
		}
	}

	Border mkOutlineBox(MediaColor border, double x, double y, double w, double h,
		MediaColor? fill = null, double thickness = 1) {
		var br = new Border {
			BorderBrush = new SolidColorBrush(border),
			BorderThickness = new Thickness(thickness),
			Background = fill.HasValue ? new SolidColorBrush(fill.Value) : WpfBrushes.Transparent,
			IsHitTestVisible = false,
			Width = Math.Max(1, w),
			Height = Math.Max(1, h),
		};
		Canvas.SetLeft(br, x);
		Canvas.SetTop(br, y);
		return br;
	}

	void insertBeforeHandle(UIElement el) {
		var idx = overlay.Children.IndexOf(resizeHandle);
		if (idx < 0) overlay.Children.Add(el);
		else overlay.Children.Insert(idx, el);
	}

	double pageScale() {
		var ptW = Math.Max(1, (double)engine.PageSizesPt[curPage].Width);
		return (pageImage.Width > 1 ? pageImage.Width : 1) / ptW;
	}

	void syncProps() {
		suppressUi = true;
		try {
			var p = primary;
			if (p == null) {
				eText.Text = selection.Count > 1 ? $"（已选 {selection.Count} 个对象）" : "";
				eText.IsEnabled = false;
				return;
			}
			if (selection.Count > 1) {
				eText.Text = $"（多选 {selection.Count} 个 · 文字编辑针对主选中）\n" + (p.Type == PdfProObjType.Text ? (p.Text ?? "") : "");
				eText.IsEnabled = p.Type == PdfProObjType.Text;
			} else if (p.Type != PdfProObjType.Text) {
				eText.Text = p.Type == PdfProObjType.Image
					? "(图片对象 — 可移动/缩放/删除/复制)"
					: "(路径/图形 — 可移动/缩放/填色/删除)";
				eText.IsEnabled = false;
			} else {
				eText.IsEnabled = true;
				eText.Text = p.Text ?? "";
				eText.ToolTip = "应用修改：优先原嵌入字体 → 按字体名匹配系统字体 → 最后才雅黑；定位用原基线";
			}
			if (p.Type == PdfProObjType.Text && p.FontSize > 1)
				eFontSize.Text = p.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
			if (p.Type == PdfProObjType.Text) {
				// 只显示可辨认的系统字体名；禁止把 PDF 乱码 BaseFont 塞进下拉（否则显示 ����）
				var display = mapFontForUi(p.FontName);
				if (display == null && PdfProEngine.isDisplayableFontName(p.FontName))
					display = p.FontName;
				if (string.IsNullOrEmpty(display))
					display = needsCjk(p.Text) || needsCjk(p.FontName) ? "华文中宋" : "Helvetica";
				if (!eFont.Items.Contains(display)) {
					// 仅当可显示时才插入自定义项
					if (PdfProEngine.isDisplayableFontName(display))
						eFont.Items.Insert(0, display);
				}
				if (eFont.Items.Contains(display))
					eFont.SelectedItem = display;
				else
					eFont.Text = display;
			}
			if (p.HasFill) {
				fillColor = p.FillColor;
				updateColorBtn();
			}
		} finally {
			suppressUi = false;
		}
	}

	// —— 交互 ——
	void onOverlayDown(object sender, MouseButtonEventArgs e) {
		if (engine == null) return;
		if (e.OriginalSource == resizeHandle) return;

		var pt = e.GetPosition(overlay);
		var scale = pageScale();
		var xPt = pt.X / scale;
		var yPt = pt.Y / scale;
		var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

		if (tool == Tool.AddText) {
			var font = (eFont.SelectedItem as string) ?? eFont.Text ?? "Microsoft YaHei";
			if (string.IsNullOrWhiteSpace(font)) font = "Microsoft YaHei";
			float fs = 12;
			if (double.TryParse((eFontSize.SelectedItem as string) ?? eFontSize.Text,
				NumberStyles.Float, CultureInfo.InvariantCulture, out var fsd) && fsd >= 6)
				fs = (float)fsd;
			try {
				var sample = needsCjk(font) ? "文字" : "Text";
				var po = engine.AddText(curPage, xPt, yPt, sample, fs, font, fillColor);
				dirty = true;
				setTool(Tool.Select);
				renderAndList();
				if (po != null) selectSingle(po);
				setStatus("已添加文字对象（矢量）");
				markTitle();
			} catch (Exception ex) {
				MessageBox.Show(this, "添加文字失败: " + ex.Message,
					"PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
			e.Handled = true;
			return;
		}

		if (tool == Tool.AddImage) {
			if (string.IsNullOrEmpty(pendingImagePath) || !File.Exists(pendingImagePath)) {
				setStatus("请先选择图片文件");
				return;
			}
			try {
				var bmp = loadBitmap(pendingImagePath);
				var wPt = bmp.PixelWidth * 72.0 / 96.0;
				var hPt = bmp.PixelHeight * 72.0 / 96.0;
				var max = 200.0;
				if (wPt > max || hPt > max) {
					var s = Math.Min(max / wPt, max / hPt);
					wPt *= s; hPt *= s;
				}
				var po = engine.AddImage(curPage, xPt, yPt, wPt, hPt, bmp);
				pendingImagePath = null;
				setTool(Tool.Select);
				dirty = true;
				renderAndList();
				if (po != null) selectSingle(po);
				setStatus("已插入图片对象（矢量）");
				markTitle();
			} catch (Exception ex) {
				MessageBox.Show(this, "插入图片失败: " + ex.Message, "PDF 专业编辑",
					MessageBoxButton.OK, MessageBoxImage.Warning);
			}
			e.Handled = true;
			return;
		}

		if (tool == Tool.Whiteout || tool == Tool.Rect) {
			drawing = true;
			drawStart = pt;
			drawPreview.Visibility = Visibility.Visible;
			Canvas.SetLeft(drawPreview, pt.X);
			Canvas.SetTop(drawPreview, pt.Y);
			drawPreview.Width = 0;
			drawPreview.Height = 0;
			overlay.CaptureMouse();
			e.Handled = true;
			return;
		}

		// —— 选择工具 ——
		var hit = hitTest(xPt, yPt);
		if (hit != null) {
			if (ctrl) {
				toggleSelection(hit);
			} else if (!isSelected(hit)) {
				selectSingle(hit);
			}
			// 已在选中集合内则准备整组拖动
			if (selection.Count > 0) {
				dragging = true;
				dragStart = pt;
				beginDragGhosts();
				overlay.CaptureMouse();
			}
			e.Handled = true;
			return;
		}

		// 空白：开始框选
		marquee = true;
		dragStart = pt;
		marqueeRect.Visibility = Visibility.Visible;
		Canvas.SetLeft(marqueeRect, pt.X);
		Canvas.SetTop(marqueeRect, pt.Y);
		marqueeRect.Width = 0;
		marqueeRect.Height = 0;
		if (!ctrl) clearSelection();
		overlay.CaptureMouse();
		e.Handled = true;
	}

	void onResizeDown(object sender, MouseButtonEventArgs e) {
		if (primary == null || selection.Count != 1) return;
		resizing = true;
		dragStart = e.GetPosition(overlay);
		var ptH = engine.PageSizesPt[curPage].Height;
		primary.ToUi(ptH, out var x, out var y, out var w, out var h);
		resizeStartW = w;
		resizeStartH = h;
		resizeStartX = x;
		resizeStartY = y;
		overlay.CaptureMouse();
		e.Handled = true;
	}

	void onOverlayMove(object sender, MouseEventArgs e) {
		if (marquee) {
			var pt = e.GetPosition(overlay);
			var x = Math.Min(dragStart.X, pt.X);
			var y = Math.Min(dragStart.Y, pt.Y);
			var w = Math.Abs(pt.X - dragStart.X);
			var h = Math.Abs(pt.Y - dragStart.Y);
			Canvas.SetLeft(marqueeRect, x);
			Canvas.SetTop(marqueeRect, y);
			marqueeRect.Width = w;
			marqueeRect.Height = h;
			e.Handled = true;
			return;
		}
		if (drawing) {
			var pt = e.GetPosition(overlay);
			var x = Math.Min(drawStart.X, pt.X);
			var y = Math.Min(drawStart.Y, pt.Y);
			var w = Math.Abs(pt.X - drawStart.X);
			var h = Math.Abs(pt.Y - drawStart.Y);
			Canvas.SetLeft(drawPreview, x);
			Canvas.SetTop(drawPreview, y);
			drawPreview.Width = w;
			drawPreview.Height = h;
			e.Handled = true;
			return;
		}
		if (resizing && primary != null && e.LeftButton == MouseButtonState.Pressed) {
			var pt = e.GetPosition(overlay);
			var scale = pageScale();
			var dx = (pt.X - dragStart.X) / scale;
			var dy = (pt.Y - dragStart.Y) / scale;
			var newW = Math.Max(4, resizeStartW + dx);
			var newH = Math.Max(4, resizeStartH + dy);
			// 仅预览主选框
			if (selBoxes.Count > 0) {
				var box = selBoxes[0];
				Canvas.SetLeft(box, resizeStartX * scale - 2);
				Canvas.SetTop(box, resizeStartY * scale - 2);
				box.Width = newW * scale + 4;
				box.Height = newH * scale + 4;
				Canvas.SetLeft(resizeHandle, Canvas.GetLeft(box) + box.Width - 5);
				Canvas.SetTop(resizeHandle, Canvas.GetTop(box) + box.Height - 5);
			}
			e.Handled = true;
			return;
		}
		if (!dragging || selection.Count == 0 || e.LeftButton != MouseButtonState.Pressed) return;
		var p2 = e.GetPosition(overlay);
		// 相对按下点的总位移（dip）——幽灵预览跟手，不改 PDF 直至松手
		var totalDx = p2.X - dragStart.X;
		var totalDy = p2.Y - dragStart.Y;
		updateDragGhosts(totalDx, totalDy);
		e.Handled = true;
	}

	void onOverlayUp(object sender, MouseButtonEventArgs e) {
		var sc = pageScale();
		if (marquee) {
			marquee = false;
			try { overlay.ReleaseMouseCapture(); } catch { /* ignore */ }
			marqueeRect.Visibility = Visibility.Collapsed;
			var x1 = Canvas.GetLeft(marqueeRect);
			var y1 = Canvas.GetTop(marqueeRect);
			var w1 = marqueeRect.Width;
			var h1 = marqueeRect.Height;
			var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
			if (w1 >= MARQUEE_MIN || h1 >= MARQUEE_MIN) {
				var hits = hitTestRect(x1 / sc, y1 / sc, w1 / sc, h1 / sc);
				setSelection(hits, additive: ctrl);
				setStatus(hits.Count > 0 ? $"框选 {hits.Count} 个对象" : "框选：未命中对象");
			}
			e.Handled = true;
			return;
		}

		if (drawing) {
			drawing = false;
			try { overlay.ReleaseMouseCapture(); } catch { /* ignore */ }
			drawPreview.Visibility = Visibility.Collapsed;
			var x1 = Canvas.GetLeft(drawPreview);
			var y1 = Canvas.GetTop(drawPreview);
			var w1 = drawPreview.Width;
			var h1 = drawPreview.Height;
			if (w1 < 3 || h1 < 3) { e.Handled = true; return; }
			var xPt = x1 / sc;
			var yPt = y1 / sc;
			var wPt = w1 / sc;
			var hPt = h1 / sc;
			try {
				var isWhite = tool == Tool.Whiteout;
				PdfProObject po;
				if (isWhite)
					po = engine.AddWhiteout(curPage, xPt, yPt, wPt, hPt);
				else
					po = engine.AddRect(curPage, xPt, yPt, wPt, hPt, fillColor);
				dirty = true;
				setTool(Tool.Select);
				renderAndList();
				if (po != null) selectSingle(po);
				markTitle();
				setStatus(isWhite ? "已添加遮盖" : "已添加矩形");
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
			e.Handled = true;
			return;
		}

		if (resizing) {
			resizing = false;
			try { overlay.ReleaseMouseCapture(); } catch { /* ignore */ }
			if (primary != null && selection.Count == 1 && selBoxes.Count > 0) {
				var box = selBoxes[0];
				var newW = Math.Max(4, (box.Width - 4) / sc);
				var newH = Math.Max(4, (box.Height - 4) / sc);
				var sx = newW / Math.Max(0.5, resizeStartW);
				var sy = newH / Math.Max(0.5, resizeStartH);
				if (engine.ScaleObject(primary, sx, sy)) {
					var ptH = engine.PageSizesPt[curPage].Height;
					primary.ToUi(ptH, out var nx, out var ny, out _, out _);
					engine.MoveObject(primary, resizeStartX - nx, resizeStartY - ny);
					dirty = true;
				}
			}
			renderAndList();
			markTitle();
			e.Handled = true;
			return;
		}

		if (!dragging) return;
		dragging = false;
		try { overlay.ReleaseMouseCapture(); } catch { /* ignore */ }
		var p2 = e.GetPosition(overlay);
		var totalDxPt = (p2.X - dragStart.X) / sc;
		var totalDyPt = (p2.Y - dragStart.Y) / sc;
		clearDragGhosts();
		if (Math.Abs(totalDxPt) + Math.Abs(totalDyPt) > 0.05 && selection.Count > 0) {
			try {
				engine.SnapshotForUndo();
				if (engine.MoveObjects(selection, totalDxPt, totalDyPt, recordUndo: false) > 0)
					dirty = true;
			} catch (Exception ex) {
				DocLog.Error("drag commit", ex);
			}
		}
		renderAndList();
		if (dirty) markTitle();
		e.Handled = true;
	}

	/// <summary>拖动开始：裁切选区像素作幽灵，原位盖白遮罩，避免只见蓝框不见内容。</summary>
	void beginDragGhosts() {
		clearDragGhosts();
		if (engine == null || selection.Count == 0) return;
		var src = pageImage.Source as BitmapSource;
		var pageDipW = pageImage.Width > 1 ? pageImage.Width : 1;
		var pageDipH = pageImage.Height > 1 ? pageImage.Height : 1;
		var ptH = engine.PageSizesPt[curPage].Height;
		var scale = pageScale();

		foreach (var o in selection) {
			if (o.MarkedDelete || o.Page != curPage) continue;
			o.ToUi(ptH, out var xPt, out var yPt, out var wPt, out var hPt);
			var x = xPt * scale;
			var y = yPt * scale;
			var w = Math.Max(2, wPt * scale);
			var h = Math.Max(2, hPt * scale);

			// 原位白遮罩（盖住未移动的渲染内容）
			var white = new Border {
				Background = WpfBrushes.White,
				Width = w,
				Height = h,
				IsHitTestVisible = false,
				Opacity = 1,
			};
			Canvas.SetLeft(white, x);
			Canvas.SetTop(white, y);
			// 插在选框之下
			var idx = overlay.Children.IndexOf(resizeHandle);
			if (idx < 0) overlay.Children.Add(white);
			else overlay.Children.Insert(Math.Max(0, idx), white);

			System.Windows.Controls.Image ghostImg = null;
			if (src != null && src.PixelWidth > 0 && src.PixelHeight > 0) {
				try {
					var px0 = (int)Math.Floor(x / pageDipW * src.PixelWidth);
					var py0 = (int)Math.Floor(y / pageDipH * src.PixelHeight);
					var px1 = (int)Math.Ceiling((x + w) / pageDipW * src.PixelWidth);
					var py1 = (int)Math.Ceiling((y + h) / pageDipH * src.PixelHeight);
					px0 = Math.Max(0, Math.Min(src.PixelWidth - 1, px0));
					py0 = Math.Max(0, Math.Min(src.PixelHeight - 1, py0));
					px1 = Math.Max(px0 + 1, Math.Min(src.PixelWidth, px1));
					py1 = Math.Max(py0 + 1, Math.Min(src.PixelHeight, py1));
					var rw = px1 - px0;
					var rh = py1 - py0;
					if (rw > 0 && rh > 0) {
						var crop = new CroppedBitmap(src, new Int32Rect(px0, py0, rw, rh));
						crop.Freeze();
						ghostImg = new System.Windows.Controls.Image {
							Source = crop,
							Width = w,
							Height = h,
							Stretch = Stretch.Fill,
							IsHitTestVisible = false,
							Opacity = 1,
							SnapsToDevicePixels = true,
						};
						Canvas.SetLeft(ghostImg, x);
						Canvas.SetTop(ghostImg, y);
						if (idx < 0) overlay.Children.Add(ghostImg);
						else overlay.Children.Insert(Math.Max(0, idx), ghostImg);
					}
				} catch (Exception ex) {
					DocLog.Warn("drag ghost crop: " + ex.Message);
				}
			}

			// 无裁切时用半透明色块代替，仍能看到位移
			if (ghostImg == null) {
				var ph = new Border {
					Background = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x25, 0x63, 0xEB)),
					BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x63, 0xEB)),
					BorderThickness = new Thickness(1),
					Width = w,
					Height = h,
					IsHitTestVisible = false,
				};
				Canvas.SetLeft(ph, x);
				Canvas.SetTop(ph, y);
				if (idx < 0) overlay.Children.Add(ph);
				else overlay.Children.Insert(Math.Max(0, idx), ph);
				ghostImg = null;
				dragGhosts.Add(new DragGhost {
					Whiteout = white,
					Placeholder = ph,
					OrigX = x,
					OrigY = y,
					W = w,
					H = h,
				});
			} else {
				dragGhosts.Add(new DragGhost {
					Whiteout = white,
					Img = ghostImg,
					OrigX = x,
					OrigY = y,
					W = w,
					H = h,
				});
			}
		}
		// 选框提到最前
		foreach (var b in selBoxes) {
			try {
				overlay.Children.Remove(b);
				overlay.Children.Add(b);
			} catch { /* ignore */ }
		}
		if (resizeHandle != null) {
			try {
				overlay.Children.Remove(resizeHandle);
				overlay.Children.Add(resizeHandle);
			} catch { /* ignore */ }
		}
	}

	void updateDragGhosts(double totalDxDip, double totalDyDip) {
		foreach (var g in dragGhosts) {
			var nx = g.OrigX + totalDxDip;
			var ny = g.OrigY + totalDyDip;
			if (g.Img != null) {
				Canvas.SetLeft(g.Img, nx);
				Canvas.SetTop(g.Img, ny);
			}
			if (g.Placeholder != null) {
				Canvas.SetLeft(g.Placeholder, nx);
				Canvas.SetTop(g.Placeholder, ny);
			}
			// 白遮罩留在原位盖住原稿
		}
		updateSelChromeOffset(totalDxDip, totalDyDip);
	}

	/// <summary>选框 = 对象原 UI 位置 + 拖动总位移（不改引擎 bounds）。</summary>
	void updateSelChromeOffset(double dxDip, double dyDip) {
		if (selection.Count == 0 || engine == null) {
			foreach (var b in selBoxes) b.Visibility = Visibility.Collapsed;
			resizeHandle.Visibility = Visibility.Collapsed;
			return;
		}
		// 清掉旧选框，按偏移重画
		foreach (var b in selBoxes) {
			try { overlay.Children.Remove(b); } catch { /* ignore */ }
		}
		selBoxes.Clear();
		var ptH = engine.PageSizesPt[curPage].Height;
		var scale = pageScale();
		double unionR = double.MinValue, unionB = double.MinValue;
		var any = false;
		foreach (var o in selection) {
			if (o.MarkedDelete || o.Page != curPage) continue;
			o.ToUi(ptH, out var x, out var y, out var w, out var h);
			var left = x * scale - 2 + dxDip;
			var top = y * scale - 2 + dyDip;
			var ww = Math.Max(4, w * scale + 4);
			var hh = Math.Max(4, h * scale + 4);
			var isPrimary = o == primary;
			var br = mkOutlineBox(
				isPrimary ? MediaColor.FromRgb(0x25, 0x63, 0xEB) : MediaColor.FromRgb(0x60, 0xA5, 0xFA),
				left, top, ww, hh,
				isPrimary ? MediaColor.FromArgb(0x28, 0x25, 0x63, 0xEB) : MediaColor.FromArgb(0x18, 0x60, 0xA5, 0xFA),
				isPrimary ? 2 : 1.5);
			overlay.Children.Add(br);
			selBoxes.Add(br);
			unionR = Math.Max(unionR, left + ww);
			unionB = Math.Max(unionB, top + hh);
			any = true;
		}
		if (any && selection.Count == 1) {
			Canvas.SetLeft(resizeHandle, unionR - 5);
			Canvas.SetTop(resizeHandle, unionB - 5);
			resizeHandle.Visibility = Visibility.Visible;
			try {
				overlay.Children.Remove(resizeHandle);
				overlay.Children.Add(resizeHandle);
			} catch { /* ignore */ }
		} else {
			resizeHandle.Visibility = Visibility.Collapsed;
		}
	}

	void clearDragGhosts() {
		foreach (var g in dragGhosts) {
			try {
				if (g.Whiteout != null) overlay.Children.Remove(g.Whiteout);
				if (g.Img != null) overlay.Children.Remove(g.Img);
				if (g.Placeholder != null) overlay.Children.Remove(g.Placeholder);
			} catch { /* ignore */ }
		}
		dragGhosts.Clear();
	}

	sealed class DragGhost {
		public Border Whiteout;
		public System.Windows.Controls.Image Img;
		public Border Placeholder;
		public double OrigX, OrigY, W, H;
	}

	PdfProObject hitTest(double uiX, double uiY) {
		var list = engine.ListObjects(curPage);
		var ptH = engine.PageSizesPt[curPage].Height;
		PdfProObject best = null;
		var bestArea = double.MaxValue;
		foreach (var o in list) {
			if (o.MarkedDelete) continue;
			o.ToUi(ptH, out var x, out var y, out var w, out var h);
			// 过小对象扩大命中区
			var pad = (w < 2 || h < 2) ? 3.0 : 1.0;
			if (uiX < x - pad || uiX > x + w + pad || uiY < y - pad || uiY > y + h + pad) continue;
			var area = Math.Max(0.01, w * h);
			if (area < bestArea) {
				bestArea = area;
				best = o;
			}
		}
		return best;
	}

	/// <summary>框选：与矩形相交的对象（UI 左上系 pt）。</summary>
	List<PdfProObject> hitTestRect(double uiX, double uiY, double uiW, double uiH) {
		var result = new List<PdfProObject>();
		var list = engine.ListObjects(curPage);
		var ptH = engine.PageSizesPt[curPage].Height;
		var r2 = uiX + uiW;
		var b2 = uiY + uiH;
		foreach (var o in list) {
			if (o.MarkedDelete) continue;
			// 跳过几乎不可见对象，减少噪声
			if (o.Width < 0.3 && o.Height < 0.3) continue;
			o.ToUi(ptH, out var x, out var y, out var w, out var h);
			var ox2 = x + w;
			var oy2 = y + h;
			if (x > r2 || ox2 < uiX || y > b2 || oy2 < uiY) continue;
			result.Add(o);
		}
		return result;
	}

	void applyTextFromUi() {
		if (suppressUi || primary == null || primary.Type != PdfProObjType.Text) return;
		var t = eText.Text ?? "";
		// 多选提示行去掉
		if (selection.Count > 1 && t.StartsWith("（多选", StringComparison.Ordinal)) {
			var idx = t.IndexOf('\n');
			t = idx >= 0 ? t.Substring(idx + 1) : primary.Text ?? "";
		}
		try {
			var fontHint = (eFont.SelectedItem as string) ?? eFont.Text;
			// 字号：优先面板；若像“越改越小”的残值则交给引擎按原字盒重算
			float fsOverride = 0;
			if (double.TryParse((eFontSize.SelectedItem as string) ?? eFontSize.Text,
				NumberStyles.Float, CultureInfo.InvariantCulture, out var fsd) && fsd >= 6 && fsd <= 200) {
				// 明显小于原字高时忽略面板（防止 12.97 连缩）
				var bh = primary.Height;
				if (bh > 8 && fsd < bh * 0.85)
					fsOverride = 0;
				else
					fsOverride = (float)fsd;
			}
			var neu = engine.ReplaceText(primary, t, fontHint, fsOverride);
			if (neu != null) {
				dirty = true;
				renderAndList();
				selectSingle(neu);
				var fl = neu.FontName ?? "自动";
				setStatus($"文字已更新 · {fl} · 字号 {neu.FontSize:0.#}");
				markTitle();
			} else {
				MessageBox.Show(this, "修改文字失败。可尝试遮盖后用「文字」工具新建。",
					"PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Information);
			}
		} catch (Exception ex) {
			DocLog.Error("applyTextFromUi", ex);
			// 删除可能已执行：重载列表，提示撤销
			try { renderAndList(); } catch { /* ignore */ }
			MessageBox.Show(this,
				"修改文字出错: " + ex.Message + "\n若内容异常请 Ctrl+Z 撤销。",
				"PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void applyFillToSelected() {
		if (selection.Count == 0) return;
		var n = 0;
		foreach (var o in selection.ToList()) {
			try {
				if (engine.SetFillColor(o, fillColor)) n++;
			} catch { /* ignore one */ }
		}
		if (n > 0) {
			dirty = true;
			renderAndList();
			markTitle();
			setStatus($"已填色 {n} 个对象");
		} else {
			MessageBox.Show(this, "设置填色失败", "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void pickFillColor() {
		var win = new Window {
			Title = "选择颜色", Width = 320, Height = 200,
			WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
			ResizeMode = ResizeMode.NoResize,
		};
		var sp = new WrapPanel { Margin = new Thickness(12) };
		MediaColor[] palette = {
			MediaColor.FromRgb(0, 0, 0), MediaColor.FromRgb(255, 255, 255),
			MediaColor.FromRgb(220, 38, 38), MediaColor.FromRgb(234, 88, 12),
			MediaColor.FromRgb(202, 138, 4), MediaColor.FromRgb(22, 163, 74),
			MediaColor.FromRgb(2, 132, 199), MediaColor.FromRgb(37, 99, 235),
			MediaColor.FromRgb(124, 58, 237), MediaColor.FromRgb(219, 39, 119),
			MediaColor.FromRgb(100, 116, 139), MediaColor.FromRgb(255, 255, 0),
		};
		foreach (var c in palette) {
			var col = c;
			var b = new Button {
				Width = 36, Height = 36, Margin = new Thickness(4),
				Background = new SolidColorBrush(col),
				BorderBrush = WpfBrushes.Gray, BorderThickness = new Thickness(1),
			};
			b.Click += (_, _) => {
				fillColor = col;
				updateColorBtn();
				win.Close();
			};
			sp.Children.Add(b);
		}
		win.Content = sp;
		win.ShowDialog();
	}

	void updateColorBtn() {
		bFillColor.Background = new SolidColorBrush(fillColor);
		var lum = 0.299 * fillColor.R + 0.587 * fillColor.G + 0.114 * fillColor.B;
		bFillColor.Foreground = lum > 160 ? WpfBrushes.Black : WpfBrushes.White;
	}

	void deleteSelected() {
		if (selection.Count == 0) return;
		var n = 0;
		foreach (var o in selection.ToList()) {
			if (engine.DeleteObject(o)) n++;
		}
		clearSelection();
		if (n > 0) {
			dirty = true;
			renderAndList();
			markTitle();
			setStatus($"已删除 {n} 个对象");
		} else {
			MessageBox.Show(this, "删除失败", "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void duplicateSelected() {
		if (selection.Count == 0) return;
		var created = new List<PdfProObject>();
		try {
			foreach (var o in selection.ToList()) {
				var po = engine.DuplicateObject(o);
				if (po != null) created.Add(po);
			}
			if (created.Count == 0) {
				MessageBox.Show(this, "无法复制选中对象", "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			dirty = true;
			renderAndList();
			setSelection(created, additive: false);
			markTitle();
			setStatus($"已复制 {created.Count} 个对象");
		} catch (Exception ex) {
			MessageBox.Show(this, ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void rotateSelected(double deg) {
		if (selection.Count == 0) return;
		var n = 0;
		foreach (var o in selection.ToList()) {
			if (engine.RotateObject(o, deg)) n++;
		}
		if (n > 0) {
			dirty = true;
			renderAndList();
			markTitle();
		}
	}

	void scaleSelected(double sx, double sy) {
		if (selection.Count == 0) return;
		var n = 0;
		foreach (var o in selection.ToList()) {
			if (engine.ScaleObject(o, sx, sy)) n++;
		}
		if (n > 0) {
			dirty = true;
			renderAndList();
			markTitle();
		}
	}

	void nudge(double dx, double dy) {
		if (selection.Count == 0) return;
		if (engine.MoveObjects(selection.ToList(), dx, dy) > 0) {
			dirty = true;
			updateSelChrome();
			markTitle();
		}
	}

	void doUndo() {
		if (engine == null || !engine.CanUndo) { setStatus("无可撤销操作"); return; }
		if (engine.Undo()) {
			dirty = true;
			clearSelection();
			if (curPage >= engine.PageCount) curPage = engine.PageCount - 1;
			rebuildPageList();
			renderAndList();
			markTitle();
			setStatus("已撤销");
		}
	}

	void doRedo() {
		if (engine == null || !engine.CanRedo) { setStatus("无可重做操作"); return; }
		if (engine.Redo()) {
			dirty = true;
			clearSelection();
			if (curPage >= engine.PageCount) curPage = engine.PageCount - 1;
			rebuildPageList();
			renderAndList();
			markTitle();
			setStatus("已重做");
		}
	}

	void insertPage() {
		try {
			var w = engine.PageSizesPt[curPage].Width;
			var h = engine.PageSizesPt[curPage].Height;
			engine.InsertBlankPage(curPage + 1, w, h);
			dirty = true;
			rebuildPageList();
			gotoPage(curPage + 1);
			markTitle();
			setStatus("已插入空白页");
		} catch (Exception ex) {
			MessageBox.Show(this, ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void deletePage() {
		if (engine.PageCount <= 1) {
			MessageBox.Show(this, "至少保留一页", "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		if (MessageBox.Show(this, $"确定删除第 {curPage + 1} 页？", "PDF 专业编辑",
			MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
		try {
			var p = curPage;
			engine.DeletePage(p);
			dirty = true;
			if (p >= engine.PageCount) p = engine.PageCount - 1;
			rebuildPageList();
			gotoPage(p);
			markTitle();
			setStatus("已删除页面");
		} catch (Exception ex) {
			MessageBox.Show(this, ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void rotatePage(int quarter) {
		try {
			engine.RotatePage(curPage, quarter);
			dirty = true;
			renderAndList();
			markTitle();
			setStatus("页面已旋转");
		} catch (Exception ex) {
			MessageBox.Show(this, ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	void addImageTool() {
		var dlg = new Microsoft.Win32.OpenFileDialog {
			Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有|*.*",
			Title = "插入图片（矢量写入 PDF）",
		};
		if (dlg.ShowDialog(this) != true) return;
		pendingImagePath = dlg.FileName;
		setTool(Tool.AddImage);
	}

	void setZoom(double z) {
		if (z < 0.35) z = 0.35;
		if (z > 4) z = 4;
		zoom = z;
		renderAndList();
	}

	void fitWidth() {
		var vw = Math.Max(200, scroller.ViewportWidth - 40);
		var ptW = Math.Max(1, (double)engine.PageSizesPt[curPage].Width);
		setZoom(vw / (ptW * 96.0 / 72.0));
	}

	void save(bool saveAs) {
		if (engine == null) return;
		var path = sourcePath;
		if (saveAs) {
			var dlg = new Microsoft.Win32.SaveFileDialog {
				Filter = "PDF|*.pdf",
				FileName = IoPath.GetFileNameWithoutExtension(sourcePath) + "-edited.pdf",
			};
			if (dlg.ShowDialog(this) != true) return;
			path = dlg.FileName;
		}
		try {
			Mouse.OverrideCursor = Cursors.Wait;
			setStatus("正在保存矢量 PDF…");
			engine.SaveTo(path);
			dirty = false;
			markTitle();
			setStatus("已保存（矢量）: " + path);
			MessageBox.Show(this, "已保存为矢量 PDF：\n" + path, "PDF 专业编辑",
				MessageBoxButton.OK, MessageBoxImage.Information);
			if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)) {
				engine.Dispose();
				engine = PdfProEngine.Open(path);
				rebuildPageList();
				gotoPage(curPage);
			}
		} catch (Exception ex) {
			DocLog.Error("vector save failed", ex);
			MessageBox.Show(this, "矢量保存失败: " + ex.Message, "PDF 专业编辑",
				MessageBoxButton.OK, MessageBoxImage.Error);
			setStatus("保存失败");
		} finally {
			Mouse.OverrideCursor = null;
		}
	}

	void markTitle() {
		var name = IoPath.GetFileName(sourcePath);
		var d = dirty || (engine?.IsDirty ?? false);
		Title = (d ? "PDF 专业编辑* - " : "PDF 专业编辑 - ") + name;
		lbTitle.Text = (d ? "* " : "") + name;
	}

	void setStatus(string s) => lbStatus.Text = s ?? "";

	void onkey(object sender, KeyEventArgs e) {
		var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
		var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
		if (ctrl && e.Key == Key.S) { save(false); e.Handled = true; return; }
		if (ctrl && e.Key == Key.Z) { doUndo(); e.Handled = true; return; }
		if (ctrl && e.Key == Key.Y) { doRedo(); e.Handled = true; return; }
		if (ctrl && e.Key == Key.D) { duplicateSelected(); e.Handled = true; return; }
		if (ctrl && e.Key == Key.A && !isTbFocused()) {
			// 全选本页可见对象
			var all = engine.ListObjects(curPage)
				.Where(o => !o.MarkedDelete && (o.Width >= 0.3 || o.Height >= 0.3))
				.ToList();
			setSelection(all, additive: false);
			setStatus($"全选 {all.Count} 个对象");
			e.Handled = true;
			return;
		}
		if (isTbFocused()) return;
		if (e.Key == Key.Delete) { deleteSelected(); e.Handled = true; return; }
		if (e.Key == Key.Escape) {
			setTool(Tool.Select);
			clearSelection();
			e.Handled = true;
			return;
		}
		if (e.Key == Key.V && !ctrl) { setTool(Tool.Select); e.Handled = true; return; }
		if (e.Key == Key.T && !ctrl) { setTool(Tool.AddText); e.Handled = true; return; }
		var step = shift ? 10.0 : 1.0;
		if (e.Key == Key.Left) { nudge(-step, 0); e.Handled = true; }
		else if (e.Key == Key.Right) { nudge(step, 0); e.Handled = true; }
		else if (e.Key == Key.Up) { nudge(0, -step); e.Handled = true; }
		else if (e.Key == Key.Down) { nudge(0, step); e.Handled = true; }
	}

	void onclosing(object sender, System.ComponentModel.CancelEventArgs e) {
		if (!(dirty || (engine?.IsDirty ?? false))) return;
		var r = MessageBox.Show(this, "有未保存修改，是否保存？", "PDF 专业编辑",
			MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
		if (r == MessageBoxResult.Cancel) e.Cancel = true;
		else if (r == MessageBoxResult.Yes) {
			try { save(false); } catch { e.Cancel = true; }
		}
	}

	static bool needsCjk(string font) {
		if (string.IsNullOrEmpty(font)) return true;
		return font.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0
			|| font.IndexOf("Sim", StringComparison.OrdinalIgnoreCase) >= 0
			|| font.IndexOf("Kai", StringComparison.OrdinalIgnoreCase) >= 0
			|| font.IndexOf("宋", StringComparison.Ordinal) >= 0
			|| font.IndexOf("黑", StringComparison.Ordinal) >= 0
			|| font.IndexOf("楷", StringComparison.Ordinal) >= 0
			|| font.IndexOf("微软", StringComparison.Ordinal) >= 0;
	}

	/// <summary>把 PDF 字体名映射到下拉中的系统字体项（仅 UI 显示，绝不返回乱码）。</summary>
	static string mapFontForUi(string pdfName) {
		if (string.IsNullOrEmpty(pdfName) || !PdfProEngine.isDisplayableFontName(pdfName))
			return null;
		var l = pdfName.ToLowerInvariant();
		if (l.Contains("zhongsong") || l.Contains("stzhongs") || pdfName.Contains("中宋")
			|| l.Contains("stzhongsong"))
			return "华文中宋";
		if (l.Contains("yahei") || pdfName.Contains("雅黑") || l.Contains("msyh"))
			return "Microsoft YaHei";
		if (l.Contains("simsun") || l.Contains("nsimsun") || pdfName.Contains("宋体"))
			return "SimSun";
		if (l.Contains("calibri") || l.Contains("arial") || l.Contains("helvetica"))
			return l.Contains("calibri") ? "Arial" : (l.Contains("arial") ? "Arial" : "Helvetica");
		if (l.Contains("song") || pdfName.Contains("宋") || l.Contains("times"))
			return "华文中宋";
		if (l.Contains("simhei") || pdfName.Contains("黑体") || l.Contains("heiti"))
			return "SimHei";
		if (l.Contains("kai") || pdfName.Contains("楷")) return "KaiTi";
		// 可显示的原名若已在下拉列表中则直接用
		return null;
	}

	static bool isTbFocused() {
		var fe = Keyboard.FocusedElement as DependencyObject;
		while (fe != null) {
			if (fe is TextBox) return true;
			fe = VisualTreeHelper.GetParent(fe);
		}
		return false;
	}

	static BitmapSource loadBitmap(string path) {
		var bmp = new BitmapImage();
		bmp.BeginInit();
		bmp.UriSource = new Uri(IoPath.GetFullPath(path));
		bmp.CacheOption = BitmapCacheOption.OnLoad;
		bmp.EndInit();
		bmp.Freeze();
		return bmp;
	}

	static string trim(string s, int n) {
		if (string.IsNullOrEmpty(s)) return "";
		s = s.Replace("\n", " ").Replace("\r", "");
		return s.Length <= n ? s : s.Substring(0, n) + "…";
	}

	static Button mkBtn(string text, Action act, string tip = null) {
		var b = new Button {
			Content = text, Margin = new Thickness(0, 0, 6, 0),
			Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand,
		};
		if (tip != null) b.ToolTip = tip;
		b.Click += (_, _) => {
			try { act(); } catch (Exception ex) {
				DocLog.Error("PdfEditor", ex);
				MessageBox.Show(ex.Message, "PDF 专业编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		};
		return b;
	}

	static FrameworkElement sep() => new Border {
		Width = 1, Height = 18, Margin = new Thickness(4, 0, 8, 0),
		Background = new SolidColorBrush(MediaColor.FromRgb(0xD1, 0xD5, 0xDB)),
		VerticalAlignment = VerticalAlignment.Center,
	};

	static Brush brush(string key) {
		try { return Application.Current?.TryFindResource(key) as Brush; }
		catch { return null; }
	}

	sealed class ObjRow {
		public PdfProObject Obj;
		public string Label;
		public override string ToString() => Label ?? "";
	}

	public static PdfEditorWindow Open(Window owner, string path) {
		path = IoPath.GetFullPath(path);
		foreach (Window w in Application.Current.Windows) {
			if (w is PdfEditorWindow pe && string.Equals(pe.sourcePath, path, StringComparison.OrdinalIgnoreCase)) {
				pe.Activate();
				return pe;
			}
		}
		var win = new PdfEditorWindow(owner, path);
		win.Show();
		return win;
	}
}
