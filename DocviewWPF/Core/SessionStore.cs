using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

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

	/// <summary>最近关闭标签（最旧在前，最新在末）。</summary>
	[DataMember(Name = "closedTabs")]
	public List<string> ClosedTabs = new();

	/// <summary>工作区文件夹（资源管理器根）。</summary>
	[DataMember(Name = "workspaceFolder")]
	public string WorkspaceFolder;

	/// <summary>主窗左侧栏是否可见。</summary>
	[DataMember(Name = "leftSideVisible")]
	public bool LeftSideVisible = true;

	/// <summary>左侧栏 Tab：0=文件夹 1=目录。</summary>
	[DataMember(Name = "leftSideTab")]
	public int LeftSideTab;
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
			if (data.ClosedTabs == null) data.ClosedTabs = new List<string>();
			// 去掉不存在的文件
			data.Tabs.RemoveAll(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
			// 关闭列表可保留不存在路径（打开时再提示）
			data.ClosedTabs.RemoveAll(string.IsNullOrWhiteSpace);
			if (data.Selected < 0) data.Selected = 0;
			if (data.Selected >= data.Tabs.Count) data.Selected = Math.Max(0, data.Tabs.Count - 1);
			if (data.LeftSideTab < 0 || data.LeftSideTab > 1) data.LeftSideTab = 0;
			if (!string.IsNullOrWhiteSpace(data.WorkspaceFolder)) {
				try {
					if (!Directory.Exists(data.WorkspaceFolder))
						data.WorkspaceFolder = null;
					else
						data.WorkspaceFolder = Path.GetFullPath(data.WorkspaceFolder);
				} catch { data.WorkspaceFolder = null; }
			}
			DocLog.Info($"SessionStore.Load tabs={data.Tabs.Count} closed={data.ClosedTabs.Count} ws={data.WorkspaceFolder}");
			return data;
		} catch (Exception ex) {
			DocLog.Error("SessionStore.Load", ex);
			return new SessionData();
		}
	}

	public static void Save(IList<string> tabPaths, int selectedIndex, string selectedPath = null,
		IList<string> closedTabs = null, string workspaceFolder = null,
		bool? leftSideVisible = null, int? leftSideTab = null) {
		try {
			var data = new SessionData {
				Selected = selectedIndex,
				SelectedPath = selectedPath,
				Tabs = new List<string>(),
				ClosedTabs = new List<string>(),
			};
			if (tabPaths != null) {
				foreach (var p in tabPaths) {
					if (string.IsNullOrWhiteSpace(p)) continue;
					string full;
					try { full = Path.GetFullPath(p); }
					catch { full = p; }
					if (!data.Tabs.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
						data.Tabs.Add(full);
				}
			}
			if (closedTabs != null) {
				foreach (var p in closedTabs) {
					if (string.IsNullOrWhiteSpace(p)) continue;
					string full;
					try { full = Path.GetFullPath(p); }
					catch { full = p; }
					if (!data.ClosedTabs.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
						data.ClosedTabs.Add(full);
				}
			}
			if (!string.IsNullOrWhiteSpace(selectedPath)) {
				try { data.SelectedPath = Path.GetFullPath(selectedPath); } catch { data.SelectedPath = selectedPath; }
				var ix = data.Tabs.FindIndex(t =>
					string.Equals(t, data.SelectedPath, StringComparison.OrdinalIgnoreCase));
				if (ix >= 0) data.Selected = ix;
			}
			if (!string.IsNullOrWhiteSpace(workspaceFolder)) {
				try { data.WorkspaceFolder = Path.GetFullPath(workspaceFolder); }
				catch { data.WorkspaceFolder = workspaceFolder; }
			}
			if (leftSideVisible != null) data.LeftSideVisible = leftSideVisible.Value;
			if (leftSideTab != null) data.LeftSideTab = leftSideTab.Value;
			if (data.Selected < 0) data.Selected = 0;
			if (data.Selected >= data.Tabs.Count) data.Selected = Math.Max(0, data.Tabs.Count - 1);

			var path = FilePath;
			var tmp = path + ".tmp";
			using (var fs = File.Create(tmp)) {
				var ser = new DataContractJsonSerializer(typeof(SessionData));
				ser.WriteObject(fs, data);
			}
			if (File.Exists(path))
				File.Delete(path);
			File.Move(tmp, path);
			DocLog.Info($"SessionStore.Save tabs={data.Tabs.Count} closed={data.ClosedTabs.Count} ws={data.WorkspaceFolder}");
		} catch (Exception ex) {
			DocLog.Error("SessionStore.Save", ex);
		}
	}
}
