using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DocviewWPF;

/// <summary>单文件阅读进度（滚动/缩放/表索引）。</summary>
[DataContract]
sealed class ReadingProgress {
	[DataMember(Name = "path")]
	public string Path;

	[DataMember(Name = "h")]
	public double H;

	[DataMember(Name = "v")]
	public double V;

	[DataMember(Name = "zoom")]
	public double Zoom = 1;

	/// <summary>XLSX 工作表 0-based；其它格式可忽略。</summary>
	[DataMember(Name = "sheet")]
	public int Sheet;

	/// <summary>页码 1-based（辅助信息）。</summary>
	[DataMember(Name = "page")]
	public int Page;

	[DataMember(Name = "tick")]
	public long Tick;
}

[DataContract]
sealed class ReadingProgressData {
	[DataMember(Name = "files")]
	public List<ReadingProgress> Files = new();
}

/// <summary>按文件路径记忆滚动位置，再次打开时恢复。</summary>
static class ReadingProgressStore {
	const int MAX_ENTRIES = 80;

	static string FilePath {
		get {
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"DocviewWPF");
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, "reading_progress.json");
		}
	}

	static ReadingProgressData cache;
	static readonly object gate = new object();

	static ReadingProgressData loaddata() {
		if (cache != null) return cache;
		try {
			var path = FilePath;
			if (!File.Exists(path)) {
				cache = new ReadingProgressData();
				return cache;
			}
			using var fs = File.OpenRead(path);
			var ser = new DataContractJsonSerializer(typeof(ReadingProgressData));
			cache = ser.ReadObject(fs) as ReadingProgressData ?? new ReadingProgressData();
			if (cache.Files == null) cache.Files = new List<ReadingProgress>();
		} catch (Exception ex) {
			DocLog.Warn($"ReadingProgressStore.Load: {ex.Message}");
			cache = new ReadingProgressData();
		}
		return cache;
	}

	static void savedata() {
		try {
			var data = loaddata();
			var path = FilePath;
			var tmp = path + ".tmp";
			using (var fs = File.Create(tmp)) {
				var ser = new DataContractJsonSerializer(typeof(ReadingProgressData));
				ser.WriteObject(fs, data);
			}
			if (File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
		} catch (Exception ex) {
			DocLog.Warn($"ReadingProgressStore.Save: {ex.Message}");
		}
	}

	static string norm(string path) {
		if (string.IsNullOrWhiteSpace(path)) return null;
		try { return Path.GetFullPath(path.Trim()); }
		catch { return path.Trim(); }
	}

	public static ReadingProgress Get(string path) {
		path = norm(path);
		if (path == null) return null;
		lock (gate) {
			var data = loaddata();
			foreach (var f in data.Files) {
				if (f != null && string.Equals(norm(f.Path), path, StringComparison.OrdinalIgnoreCase))
					return f;
			}
		}
		return null;
	}

	public static void Set(string path, double h, double v, double zoom = 1, int sheet = 0, int page = 0) {
		path = norm(path);
		if (path == null) return;
		if (double.IsNaN(h) || double.IsInfinity(h)) h = 0;
		if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
		if (h < 0) h = 0;
		if (v < 0) v = 0;
		if (zoom < 0.1 || double.IsNaN(zoom)) zoom = 1;
		lock (gate) {
			var data = loaddata();
			ReadingProgress hit = null;
			foreach (var f in data.Files) {
				if (f != null && string.Equals(norm(f.Path), path, StringComparison.OrdinalIgnoreCase)) {
					hit = f;
					break;
				}
			}
			if (hit == null) {
				hit = new ReadingProgress { Path = path };
				data.Files.Add(hit);
			}
			hit.H = h;
			hit.V = v;
			hit.Zoom = zoom;
			hit.Sheet = sheet;
			hit.Page = page;
			hit.Tick = DateTime.UtcNow.Ticks;
			// LRU：按 Tick 丢弃最旧
			if (data.Files.Count > MAX_ENTRIES) {
				data.Files.Sort((a, b) => (b?.Tick ?? 0).CompareTo(a?.Tick ?? 0));
				while (data.Files.Count > MAX_ENTRIES)
					data.Files.RemoveAt(data.Files.Count - 1);
			}
			savedata();
		}
	}
}
