using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DocviewWPF;

[DataContract]
sealed class SessionData {
	[DataMember(Name = "selected")]
	public int Selected;

	/// <summary>优先用路径恢复选中项（索引在缺文件后会错位）。</summary>
	[DataMember(Name = "selectedPath")]
	public string SelectedPath;

	[DataMember(Name = "tabs")]
	public List<string> Tabs = new();
}

/// <summary>关闭时保存 / 启动时恢复的 Tab 会话（仅路径，按需加载文件）。</summary>
static class SessionStore {
	static string FilePath {
		get {
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"DocviewWPF");
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, "session.json");
		}
	}

	public static SessionData Load() {
		try {
			var path = FilePath;
			if (!File.Exists(path)) return new SessionData();
			using var fs = File.OpenRead(path);
			var ser = new DataContractJsonSerializer(typeof(SessionData));
			var data = ser.ReadObject(fs) as SessionData;
			if (data == null) return new SessionData();
			if (data.Tabs == null) data.Tabs = new List<string>();
			// 去掉不存在的文件
			data.Tabs.RemoveAll(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
			if (data.Selected < 0) data.Selected = 0;
			if (data.Selected >= data.Tabs.Count) data.Selected = Math.Max(0, data.Tabs.Count - 1);
			DocLog.Info($"SessionStore.Load tabs={data.Tabs.Count} selected={data.Selected}");
			return data;
		} catch (Exception ex) {
			DocLog.Error("SessionStore.Load", ex);
			return new SessionData();
		}
	}

	public static void Save(IList<string> tabPaths, int selectedIndex, string selectedPath = null) {
		try {
			var data = new SessionData {
				Selected = selectedIndex,
				SelectedPath = selectedPath,
				Tabs = new List<string>(),
			};
			if (tabPaths != null) {
				foreach (var p in tabPaths) {
					if (string.IsNullOrWhiteSpace(p)) continue;
					string full;
					try { full = Path.GetFullPath(p); }
					catch { full = p; }
					// 仍写入列表（启动时再按 Exists 过滤），避免路径短暂不可达丢会话
					if (!data.Tabs.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
						data.Tabs.Add(full);
				}
			}
			if (!string.IsNullOrWhiteSpace(selectedPath)) {
				try { data.SelectedPath = Path.GetFullPath(selectedPath); } catch { data.SelectedPath = selectedPath; }
				var ix = data.Tabs.FindIndex(t =>
					string.Equals(t, data.SelectedPath, StringComparison.OrdinalIgnoreCase));
				if (ix >= 0) data.Selected = ix;
			}
			if (data.Selected < 0) data.Selected = 0;
			if (data.Selected >= data.Tabs.Count) data.Selected = Math.Max(0, data.Tabs.Count - 1);

			var path = FilePath;
			// 先写临时文件再替换，防止写到一半崩溃丢会话
			var tmp = path + ".tmp";
			using (var fs = File.Create(tmp)) {
				var ser = new DataContractJsonSerializer(typeof(SessionData));
				ser.WriteObject(fs, data);
			}
			if (File.Exists(path))
				File.Delete(path);
			File.Move(tmp, path);
			DocLog.Info($"SessionStore.Save tabs={data.Tabs.Count} selected={data.Selected} selPath={data.SelectedPath}");
		} catch (Exception ex) {
			DocLog.Error("SessionStore.Save", ex);
		}
	}
}
