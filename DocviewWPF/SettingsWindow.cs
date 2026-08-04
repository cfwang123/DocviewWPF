using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DocviewWPF;

/// <summary>系统参数：主题色、会话恢复、侧栏默认、界面字号、语言等。</summary>
sealed class SettingsWindow : Window {
	readonly AppSettings draft;
	readonly WrapPanel pthemes;
	readonly CheckBox ckrestore;
	readonly CheckBox ckside;
	readonly CheckBox ckwindow;
	readonly CheckBox ckmdheadnum;
	readonly TextBlock lbmdheadnumhint;
	readonly ComboBox cbfont;
	readonly ComboBox cbtab;
	readonly ComboBox cblang;
	readonly TextBlock lbtheme;
	readonly TextBlock lbthemehint;
	readonly TextBlock lbstartup;
	readonly TextBlock lbfontsec;
	readonly TextBlock lbfonthint;
	readonly TextBlock lbfontsize;
	readonly TextBlock lbtabsec;
	readonly TextBlock lbtabhint;
	readonly TextBlock lbtabsize;
	readonly TextBlock lblangsec;
	readonly TextBlock lblanghint;
	readonly TextBlock lbnotes;
	readonly TextBlock lbnotesbody;
	readonly Button bcancel;
	readonly Button bok;
	Border[] themeCards;
	int selectedTheme;

	static readonly double[] FontChoices = { 10, 11, 12, 13, 14, 15, 16 };
	static readonly int[] TabChoices = { 2, 3, 4, 8 };

	public SettingsWindow(Window owner) {
		Owner = owner;
		Width = 560;
		Height = 600;
		MinWidth = 480;
		MinHeight = 460;
		WindowStartupLocation = Owner != null
			? WindowStartupLocation.CenterOwner
			: WindowStartupLocation.CenterScreen;
		ResizeMode = ResizeMode.CanResize;
		ShowInTaskbar = false;
		Background = brushres("BgApp") ?? Brushes.White;

		draft = AppSettings.Current.Clone();
		selectedTheme = draft.ThemeId;

		var root = new DockPanel { Margin = new Thickness(16) };

		var bottom = new StackPanel {
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 12, 0, 0),
		};
		DockPanel.SetDock(bottom, Dock.Bottom);
		bcancel = mkbtn(Loc.T("cancel"), false);
		bcancel.Click += (_, _) => {
			ThemeService.Apply(AppSettings.Current.ThemeId);
			DialogResult = false;
			Close();
		};
		bok = mkbtn(Loc.T("ok"), true);
		bok.Click += (_, _) => {
			draft.ThemeId = selectedTheme;
			draft.RestoreTabs = ckrestore.IsChecked == true;
			draft.ShowSidePanel = ckside.IsChecked == true;
			draft.RememberWindow = ckwindow.IsChecked == true;
			draft.MdHeadingAutoNumber = ckmdheadnum.IsChecked == true;
			if (cbfont.SelectedItem is double fs)
				draft.UiFontSize = fs;
			if (cbtab.SelectedItem is int ts)
				draft.MdTabSize = ts;
			if (cblang.SelectedItem is LangItem li)
				draft.Language = li.Code;
			draft.WinLeft = AppSettings.Current.WinLeft;
			draft.WinTop = AppSettings.Current.WinTop;
			draft.WinWidth = AppSettings.Current.WinWidth;
			draft.WinHeight = AppSettings.Current.WinHeight;
			draft.WinMaximized = AppSettings.Current.WinMaximized;
			AppSettings.Current.CopyFrom(draft);
			AppSettings.Current.Save();
			ThemeService.Apply(selectedTheme);
			Loc.SetLanguage(AppSettings.Current.Language);
			if (Owner != null)
				Owner.FontSize = AppSettings.Current.UiFontSize;
			DialogResult = true;
			Close();
		};
		bottom.Children.Add(bcancel);
		bottom.Children.Add(bok);
		root.Children.Add(bottom);

		var scroll = new ScrollViewer {
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		var body = new StackPanel();

		lbtheme = sectiontitle(Loc.T("theme_section"));
		body.Children.Add(lbtheme);
		lbthemehint = new TextBlock {
			Text = Loc.T("theme_hint"),
			FontSize = 12,
			Foreground = brushres("TextMuted") ?? Brushes.Gray,
			Margin = new Thickness(0, 0, 0, 8),
		};
		body.Children.Add(lbthemehint);
		pthemes = new WrapPanel();
		themeCards = new Border[AppTheme.Count];
		for (var i = 0; i < AppTheme.Count; i++) {
			var t = AppTheme.All[i];
			var card = buildthemecard(t);
			themeCards[i] = card;
			pthemes.Children.Add(card);
		}
		body.Children.Add(pthemes);
		highlighttheme(selectedTheme);

		// 语言
		lblangsec = sectiontitle(Loc.T("lang_section"));
		body.Children.Add(lblangsec);
		lblanghint = new TextBlock {
			Text = Loc.T("lang_hint"),
			FontSize = 12,
			Foreground = brushres("TextMuted") ?? Brushes.Gray,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 6),
		};
		body.Children.Add(lblanghint);
		cblang = new ComboBox {
			Width = 220,
			Height = 28,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0, 0, 0, 8),
		};
		var langIdx = 0;
		for (var i = 0; i < Loc.Languages.Length; i++) {
			var (code, name) = Loc.Languages[i];
			cblang.Items.Add(new LangItem(code, name));
			if (string.Equals(code, draft.Language, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(code, Loc.Lang, StringComparison.OrdinalIgnoreCase))
				langIdx = i;
		}
		cblang.SelectedIndex = langIdx;
		cblang.SelectionChanged += (_, _) => {
			if (cblang.SelectedItem is LangItem li) {
				// 预览语言（确定后才持久化）
				Loc.SetLanguage(li.Code, fire: true);
				refreshlabels();
			}
		};
		body.Children.Add(cblang);

		lbstartup = sectiontitle(Loc.T("startup_section"));
		body.Children.Add(lbstartup);
		ckrestore = mkcheck(Loc.T("restore_tabs"), draft.RestoreTabs);
		ckside = mkcheck(Loc.T("show_side"), draft.ShowSidePanel);
		ckwindow = mkcheck(Loc.T("remember_win"), draft.RememberWindow);
		body.Children.Add(ckrestore);
		body.Children.Add(ckside);
		body.Children.Add(ckwindow);

		lbfontsec = sectiontitle(Loc.T("ui_font_section"));
		body.Children.Add(lbfontsec);
		lbfonthint = new TextBlock {
			Text = Loc.T("ui_font_hint"),
			FontSize = 12,
			Foreground = brushres("TextMuted") ?? Brushes.Gray,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 6),
		};
		body.Children.Add(lbfonthint);
		var fontRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
		lbfontsize = new TextBlock {
			Text = Loc.T("font_size_px"),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 12, 0),
			Foreground = brushres("TextPrimary") ?? Brushes.Black,
			FontSize = 13,
		};
		fontRow.Children.Add(lbfontsize);
		cbfont = new ComboBox {
			Width = 100,
			Height = 28,
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		foreach (var f in FontChoices)
			cbfont.Items.Add(f);
		var want = draft.UiFontSize;
		var best = 0;
		var bestDiff = double.MaxValue;
		for (var i = 0; i < FontChoices.Length; i++) {
			var d = Math.Abs(FontChoices[i] - want);
			if (d < bestDiff) { bestDiff = d; best = i; }
		}
		cbfont.SelectedIndex = best;
		fontRow.Children.Add(cbfont);
		body.Children.Add(fontRow);

		lbtabsec = sectiontitle(Loc.T("md_tab_section"));
		body.Children.Add(lbtabsec);
		lbtabhint = new TextBlock {
			Text = Loc.T("md_tab_hint"),
			FontSize = 12,
			Foreground = brushres("TextMuted") ?? Brushes.Gray,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 6),
		};
		body.Children.Add(lbtabhint);
		var tabRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
		lbtabsize = new TextBlock {
			Text = Loc.T("md_tab_size"),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 12, 0),
			Foreground = brushres("TextPrimary") ?? Brushes.Black,
			FontSize = 13,
		};
		tabRow.Children.Add(lbtabsize);
		cbtab = new ComboBox {
			Width = 100,
			Height = 28,
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		foreach (var t in TabChoices)
			cbtab.Items.Add(t);
		var tabWant = draft.MdTabSize;
		var tabBest = 1; // default 3
		for (var i = 0; i < TabChoices.Length; i++) {
			if (TabChoices[i] == tabWant) { tabBest = i; break; }
		}
		cbtab.SelectedIndex = tabBest;
		tabRow.Children.Add(cbtab);
		body.Children.Add(tabRow);
		ckmdheadnum = mkcheck(Loc.T("md_heading_autonum"), draft.MdHeadingAutoNumber);
		body.Children.Add(ckmdheadnum);
		lbmdheadnumhint = new TextBlock {
			Text = Loc.T("md_heading_autonum_hint"),
			FontSize = 12,
			Foreground = brushres("TextMuted") ?? Brushes.Gray,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(22, 0, 0, 8),
		};
		body.Children.Add(lbmdheadnumhint);

		lbnotes = sectiontitle(Loc.T("notes_section"));
		body.Children.Add(lbnotes);
		lbnotesbody = new TextBlock {
			Text = Loc.T("settings_notes"),
			FontSize = 12,
			Foreground = brushres("TextMuted") ?? Brushes.Gray,
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 20,
			Margin = new Thickness(0, 0, 0, 8),
		};
		body.Children.Add(lbnotesbody);

		scroll.Content = body;
		root.Children.Add(scroll);
		Content = root;
		refreshlabels();

		PreviewKeyDown += (_, e) => {
			if (e.Key == Key.Escape) {
				// 取消时还原语言与主题
				Loc.SetLanguage(AppSettings.Current.Language, fire: true);
				ThemeService.Apply(AppSettings.Current.ThemeId);
				DialogResult = false;
				Close();
				e.Handled = true;
			}
		};
		Closed += (_, _) => {
			// 若未确定就关掉，还原语言
			if (DialogResult != true)
				Loc.SetLanguage(AppSettings.Current.Language, fire: true);
		};
	}

	void refreshlabels() {
		Title = Loc.T("settings_title");
		if (lbtheme != null) lbtheme.Text = Loc.T("theme_section");
		if (lbthemehint != null) lbthemehint.Text = Loc.T("theme_hint");
		if (lblangsec != null) lblangsec.Text = Loc.T("lang_section");
		if (lblanghint != null) lblanghint.Text = Loc.T("lang_hint");
		if (lbstartup != null) lbstartup.Text = Loc.T("startup_section");
		if (ckrestore != null) ckrestore.Content = Loc.T("restore_tabs");
		if (ckside != null) ckside.Content = Loc.T("show_side");
		if (ckwindow != null) ckwindow.Content = Loc.T("remember_win");
		if (lbfontsec != null) lbfontsec.Text = Loc.T("ui_font_section");
		if (lbfonthint != null) lbfonthint.Text = Loc.T("ui_font_hint");
		if (lbfontsize != null) lbfontsize.Text = Loc.T("font_size_px");
		if (lbtabsec != null) lbtabsec.Text = Loc.T("md_tab_section");
		if (lbtabhint != null) lbtabhint.Text = Loc.T("md_tab_hint");
		if (lbtabsize != null) lbtabsize.Text = Loc.T("md_tab_size");
		if (ckmdheadnum != null) ckmdheadnum.Content = Loc.T("md_heading_autonum");
		if (lbmdheadnumhint != null) lbmdheadnumhint.Text = Loc.T("md_heading_autonum_hint");
		if (lbnotes != null) lbnotes.Text = Loc.T("notes_section");
		if (lbnotesbody != null) lbnotesbody.Text = Loc.T("settings_notes");
		if (bcancel != null) bcancel.Content = Loc.T("cancel");
		if (bok != null) bok.Content = Loc.T("ok");
	}

	Border buildthemecard(AppTheme t) {
		var id = t.Id;
		var swatch = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
		swatch.Children.Add(colorswatch(t.BgTitle));
		swatch.Children.Add(colorswatch(t.BgToolbar));
		swatch.Children.Add(colorswatch(t.Accent));
		var name = new TextBlock {
			Text = t.Name,
			FontSize = 12,
			Foreground = brushres("TextPrimary") ?? Brushes.Black,
		};
		var stack = new StackPanel();
		stack.Children.Add(swatch);
		stack.Children.Add(name);
		var card = new Border {
			Width = 118,
			Margin = new Thickness(0, 0, 8, 8),
			Padding = new Thickness(8),
			CornerRadius = new CornerRadius(6),
			BorderThickness = new Thickness(2),
			BorderBrush = Brushes.Transparent,
			Background = brushres("BgPanel") ?? Brushes.White,
			Cursor = Cursors.Hand,
			Child = stack,
			Tag = id,
		};
		card.MouseLeftButtonDown += (_, _) => {
			selectedTheme = id;
			highlighttheme(id);
			ThemeService.Apply(id);
		};
		return card;
	}

	void highlighttheme(int id) {
		if (themeCards == null) return;
		for (var i = 0; i < themeCards.Length; i++) {
			var c = themeCards[i];
			if (c == null) continue;
			var on = i == id;
			c.BorderBrush = on
				? (brushres("Accent") ?? Brushes.DodgerBlue)
				: Brushes.Transparent;
			c.Background = on
				? (brushres("BgSoft") ?? Brushes.WhiteSmoke)
				: (brushres("BgPanel") ?? Brushes.White);
		}
	}

	static Border colorswatch(Color c) => new Border {
		Width = 18, Height = 14, Margin = new Thickness(0, 0, 4, 0),
		Background = new SolidColorBrush(c),
		BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5),
		CornerRadius = new CornerRadius(2),
	};

	static TextBlock sectiontitle(string text) => new TextBlock {
		Text = text,
		FontSize = 14,
		FontWeight = FontWeights.SemiBold,
		Margin = new Thickness(0, 14, 0, 8),
		Foreground = brushres("TextPrimary") ?? Brushes.Black,
	};

	static CheckBox mkcheck(string text, bool on) => new CheckBox {
		Content = text,
		IsChecked = on,
		Margin = new Thickness(0, 4, 0, 4),
		FontSize = 13,
		Foreground = brushres("TextPrimary") ?? Brushes.Black,
	};

	static Button mkbtn(string text, bool primary) {
		var b = new Button {
			Content = text,
			MinWidth = 88,
			Height = 30,
			Margin = new Thickness(8, 0, 0, 0),
			Padding = new Thickness(12, 0, 12, 0),
			Cursor = Cursors.Hand,
		};
		if (primary) {
			b.Background = brushres("Accent") ?? Brushes.DodgerBlue;
			b.Foreground = Brushes.White;
			b.BorderThickness = new Thickness(0);
		}
		return b;
	}

	static Brush brushres(string key) {
		try { return Application.Current?.TryFindResource(key) as Brush; }
		catch { return null; }
	}

	sealed class LangItem {
		public string Code;
		public string Name;
		public LangItem(string code, string name) { Code = code; Name = name; }
		public override string ToString() => Name ?? Code;
	}
}
