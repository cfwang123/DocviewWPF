using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DocviewWPF;

/// <summary>系统参数（%LocalAppData%\DocviewWPF\settings.json）。</summary>
[DataContract]
sealed class AppSettings {
	/// <summary>主题编号 0..9。</summary>
	[DataMember(Name = "themeId")]
	public int ThemeId = 0;

	/// <summary>启动时恢复上次打开的标签。</summary>
	[DataMember(Name = "restoreTabs")]
	public bool RestoreTabs = true;

	/// <summary>打开文档时默认显示目录侧栏。</summary>
	[DataMember(Name = "showSidePanel")]
	public bool ShowSidePanel = true;

	/// <summary>记住窗口位置与大小。</summary>
	[DataMember(Name = "rememberWindow")]
	public bool RememberWindow = true;

	/// <summary>界面字体大小（DIP），默认 12。</summary>
	[DataMember(Name = "uiFontSize")]
	public double UiFontSize = 12;

	/// <summary>界面语言：zh / en / ja / ko。</summary>
	[DataMember(Name = "language")]
	public string Language = "zh";

	[DataMember(Name = "winLeft")]
	public double WinLeft = double.NaN;
	[DataMember(Name = "winTop")]
	public double WinTop = double.NaN;
	[DataMember(Name = "winWidth")]
	public double WinWidth = 1100;
	[DataMember(Name = "winHeight")]
	public double WinHeight = 720;
	[DataMember(Name = "winMax")]
	public bool WinMaximized;

	static string FilePath {
		get {
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"DocviewWPF");
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, "settings.json");
		}
	}

	static AppSettings current;

	public static AppSettings Current {
		get {
			if (current == null) current = Load();
			return current;
		}
	}

	public static AppSettings Load() {
		try {
			var path = FilePath;
			if (!File.Exists(path)) {
				current = new AppSettings();
				return current;
			}
			using var fs = File.OpenRead(path);
			var ser = new DataContractJsonSerializer(typeof(AppSettings));
			var data = ser.ReadObject(fs) as AppSettings ?? new AppSettings();
			data.normalize();
			current = data;
			DocLog.Info($"AppSettings.Load theme={data.ThemeId} restore={data.RestoreTabs} font={data.UiFontSize} lang={data.Language}");
			return data;
		} catch (Exception ex) {
			DocLog.Error("AppSettings.Load", ex);
			current = new AppSettings();
			return current;
		}
	}

	public void Save() {
		try {
			normalize();
			var path = FilePath;
			var tmp = path + ".tmp";
			using (var fs = File.Create(tmp)) {
				var ser = new DataContractJsonSerializer(typeof(AppSettings));
				ser.WriteObject(fs, this);
			}
			if (File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
			current = this;
			DocLog.Info($"AppSettings.Save theme={ThemeId} font={UiFontSize} lang={Language}");
		} catch (Exception ex) {
			DocLog.Error("AppSettings.Save", ex);
		}
	}

	void normalize() {
		if (ThemeId < 0) ThemeId = 0;
		if (ThemeId >= AppTheme.Count) ThemeId = AppTheme.Count - 1;
		if (WinWidth < 640) WinWidth = 640;
		if (WinHeight < 420) WinHeight = 420;
		if (UiFontSize < 10) UiFontSize = 10;
		if (UiFontSize > 18) UiFontSize = 18;
		// 常用档：取整到 0.5
		UiFontSize = Math.Round(UiFontSize * 2) / 2;
		Language = normalizeLang(Language);
	}

	static string normalizeLang(string code) {
		if (string.IsNullOrWhiteSpace(code)) return Loc.Zh;
		code = code.Trim().ToLowerInvariant();
		if (code.StartsWith("zh")) return Loc.Zh;
		if (code.StartsWith("en")) return Loc.En;
		if (code.StartsWith("ja") || code.StartsWith("jp")) return Loc.Ja;
		if (code.StartsWith("ko") || code.StartsWith("kr")) return Loc.Ko;
		return Loc.Zh;
	}

	/// <summary>复制一份供参数窗编辑，确定后再写回。</summary>
	public AppSettings Clone() {
		return new AppSettings {
			ThemeId = ThemeId,
			RestoreTabs = RestoreTabs,
			ShowSidePanel = ShowSidePanel,
			RememberWindow = RememberWindow,
			UiFontSize = UiFontSize,
			Language = Language,
			WinLeft = WinLeft,
			WinTop = WinTop,
			WinWidth = WinWidth,
			WinHeight = WinHeight,
			WinMaximized = WinMaximized,
		};
	}

	public void CopyFrom(AppSettings s) {
		if (s == null) return;
		ThemeId = s.ThemeId;
		RestoreTabs = s.RestoreTabs;
		ShowSidePanel = s.ShowSidePanel;
		RememberWindow = s.RememberWindow;
		UiFontSize = s.UiFontSize;
		Language = s.Language;
		WinLeft = s.WinLeft;
		WinTop = s.WinTop;
		WinWidth = s.WinWidth;
		WinHeight = s.WinHeight;
		WinMaximized = s.WinMaximized;
	}
}
