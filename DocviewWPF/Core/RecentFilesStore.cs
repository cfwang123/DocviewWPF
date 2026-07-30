using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DocviewWPF;

[DataContract]
sealed class RecentData {
	[DataMember(Name = "files")]
	public List<string> Files = new();
}

/// <summary>最近打开文件列表（%LocalAppData%\DocviewWPF\recent.json），最多 20 条，新的在前。</summary>
static class RecentFilesStore {
	public const int MaxCount = 20;

	static List<string> cache;

	static string FilePath {
		get {
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"DocviewWPF");
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, "recent.json");
		}
	}

	public static List<string> Load() {
		if (cache != null) return new List<string>(cache);
		try {
			var path = FilePath;
			if (!File.Exists(path)) {
				cache = new List<string>();
				return new List<string>();
			}
			using var fs = File.OpenRead(path);
			var ser = new DataContractJsonSerializer(typeof(RecentData));
			var data = ser.ReadObject(fs) as RecentData;
			cache = normalize(data?.Files);
			DocLog.Info($"RecentFilesStore.Load count={cache.Count}");
			return new List<string>(cache);
		} catch (Exception ex) {
			DocLog.Error("RecentFilesStore.Load", ex);
			cache = new List<string>();
			return new List<string>();
		}
	}

	/// <summary>记入最近打开（移到最前，去重，截断到 MaxCount）。</summary>
	public static void Add(string path) {
		path = norm(path);
		if (path == null) return;
		var list = Load();
		list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
		list.Insert(0, path);
		if (list.Count > MaxCount)
			list.RemoveRange(MaxCount, list.Count - MaxCount);
		Save(list);
	}

	/// <summary>从列表移除（文件已不存在等）。</summary>
	public static void Remove(string path) {
		path = norm(path);
		if (path == null) return;
		var list = Load();
		var n = list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
		if (n > 0) Save(list);
	}

	public static void Clear() {
		Save(new List<string>());
		DocLog.Info("RecentFilesStore.Clear");
	}

	public static void Save(List<string> files) {
		try {
			var list = normalize(files);
			cache = list;
			var data = new RecentData { Files = list };
			var path = FilePath;
			var tmp = path + ".tmp";
			using (var fs = File.Create(tmp)) {
				var ser = new DataContractJsonSerializer(typeof(RecentData));
				ser.WriteObject(fs, data);
			}
			if (File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
			DocLog.Info($"RecentFilesStore.Save count={list.Count}");
		} catch (Exception ex) {
			DocLog.Error("RecentFilesStore.Save", ex);
		}
	}

	static List<string> normalize(IList<string> src) {
		var list = new List<string>();
		if (src == null) return list;
		foreach (var p in src) {
			var full = norm(p);
			if (full == null) continue;
			if (list.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
				continue;
			list.Add(full);
			if (list.Count >= MaxCount) break;
		}
		return list;
	}

	static string norm(string path) {
		if (string.IsNullOrWhiteSpace(path)) return null;
		try {
			path = path.Trim().Trim('"');
			if (path.Length == 0) return null;
			path = Path.GetFullPath(path);
			if (path.Length > 3)
				path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return path;
		} catch {
			return null;
		}
	}
}
