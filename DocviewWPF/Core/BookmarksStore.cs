using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DocviewWPF;

/// <summary>书签类型：文件 / 磁盘文件夹 / 分组（容器）。</summary>
enum BookmarkKind {
	File = 0,
	Folder = 1,
	Group = 2,
}

/// <summary>书签树节点。</summary>
[DataContract]
sealed class BookmarkNode {
	[DataMember(Name = "id")]
	public string Id;

	[DataMember(Name = "title")]
	public string Title;

	/// <summary>file / folder 时为完整路径；group 可空。</summary>
	[DataMember(Name = "path")]
	public string Path;

	/// <summary>0=file 1=folder 2=group</summary>
	[DataMember(Name = "kind")]
	public int KindInt;

	[DataMember(Name = "children")]
	public List<BookmarkNode> Children;

	public BookmarkKind Kind {
		get => (BookmarkKind)KindInt;
		set => KindInt = (int)value;
	}

	public static BookmarkNode NewFile(string path, string title = null) {
		path = normpath(path);
		return new BookmarkNode {
			Id = newid(),
			Title = string.IsNullOrWhiteSpace(title) ? displayname(path) : title.Trim(),
			Path = path,
			Kind = BookmarkKind.File,
		};
	}

	public static BookmarkNode NewFolder(string path, string title = null) {
		path = normpath(path);
		return new BookmarkNode {
			Id = newid(),
			Title = string.IsNullOrWhiteSpace(title) ? displayname(path) : title.Trim(),
			Path = path,
			Kind = BookmarkKind.Folder,
		};
	}

	public static BookmarkNode NewGroup(string title) {
		return new BookmarkNode {
			Id = newid(),
			Title = string.IsNullOrWhiteSpace(title) ? "新建分组" : title.Trim(),
			Kind = BookmarkKind.Group,
			Children = new List<BookmarkNode>(),
		};
	}

	static string newid() => Guid.NewGuid().ToString("N");

	static string normpath(string path) {
		if (string.IsNullOrWhiteSpace(path)) return null;
		try {
			return System.IO.Path.GetFullPath(path.Trim().Trim('"'));
		} catch {
			return path?.Trim();
		}
	}

	static string displayname(string path) {
		if (string.IsNullOrEmpty(path)) return "书签";
		try {
			var n = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
			return string.IsNullOrEmpty(n) ? path : n;
		} catch {
			return path;
		}
	}
}

[DataContract]
sealed class BookmarksData {
	[DataMember(Name = "barVisible")]
	public bool BarVisible = true;

	[DataMember(Name = "items")]
	public List<BookmarkNode> Items = new();
}

/// <summary>Chrome 风格书签（%LocalAppData%\DocviewWPF\bookmarks.json）。</summary>
static class BookmarksStore {
	static BookmarksData cache;

	static string FilePath {
		get {
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"DocviewWPF");
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, "bookmarks.json");
		}
	}

	public static BookmarksData Load() {
		if (cache != null) return cache;
		try {
			var path = FilePath;
			if (!File.Exists(path)) {
				cache = new BookmarksData();
				return cache;
			}
			using var fs = File.OpenRead(path);
			var ser = new DataContractJsonSerializer(typeof(BookmarksData));
			cache = ser.ReadObject(fs) as BookmarksData ?? new BookmarksData();
			if (cache.Items == null) cache.Items = new List<BookmarkNode>();
			normalize(cache.Items);
			DocLog.Info($"BookmarksStore.Load items={cache.Items.Count} bar={cache.BarVisible}");
			return cache;
		} catch (Exception ex) {
			DocLog.Error("BookmarksStore.Load", ex);
			cache = new BookmarksData();
			return cache;
		}
	}

	public static void Save() {
		try {
			var data = Load();
			if (data.Items == null) data.Items = new List<BookmarkNode>();
			normalize(data.Items);
			var path = FilePath;
			var tmp = path + ".tmp";
			using (var fs = File.Create(tmp)) {
				var ser = new DataContractJsonSerializer(typeof(BookmarksData));
				ser.WriteObject(fs, data);
			}
			if (File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
		} catch (Exception ex) {
			DocLog.Error("BookmarksStore.Save", ex);
		}
	}

	public static bool BarVisible {
		get => Load().BarVisible;
		set {
			Load().BarVisible = value;
			Save();
		}
	}

	public static List<BookmarkNode> Root => Load().Items;

	public static void AddRoot(BookmarkNode node) {
		if (node == null) return;
		Load().Items.Add(node);
		Save();
	}

	public static bool RemoveById(string id) {
		if (string.IsNullOrEmpty(id)) return false;
		if (remove(Load().Items, id)) {
			Save();
			return true;
		}
		return false;
	}

	/// <summary>
	/// 移动节点：parentGroupId=null 表示书签栏根；index 为目标列表插入位置（&lt;0 或过大则追加）。
	/// 禁止：把自己移进自己/子孙（可嵌套分组，但不可成环）。
	/// </summary>
	public static bool Move(string id, string parentGroupId, int index) {
		if (string.IsNullOrEmpty(id)) return false;
		var data = Load();
		var node = find(data.Items, id);
		if (node == null) return false;

		// 源列表 + 下标
		List<BookmarkNode> srcList = null;
		var srcIndex = -1;
		if (!tryfindlist(data.Items, id, ref srcList, ref srcIndex) || srcList == null || srcIndex < 0)
			return false;

		// 目标列表
		List<BookmarkNode> target;
		if (string.IsNullOrEmpty(parentGroupId)) {
			target = data.Items;
		} else {
			if (string.Equals(parentGroupId, id, StringComparison.Ordinal))
				return false;
			var parent = find(data.Items, parentGroupId);
			if (parent == null || parent.Kind != BookmarkKind.Group)
				return false;
			// 禁止把节点移进自己的子孙分组（成环）
			if (node.Kind == BookmarkKind.Group && isdescendant(node, parentGroupId))
				return false;
			if (parent.Children == null) parent.Children = new List<BookmarkNode>();
			target = parent.Children;
		}

		if (index < 0 || index > target.Count)
			index = target.Count;

		// 同列表内移动：先删后插，校正 index
		if (ReferenceEquals(srcList, target)) {
			if (srcIndex == index || srcIndex + 1 == index)
				return true; // 位置未变
			srcList.RemoveAt(srcIndex);
			if (index > srcIndex) index--;
			if (index < 0) index = 0;
			if (index > target.Count) index = target.Count;
			target.Insert(index, node);
		} else {
			srcList.RemoveAt(srcIndex);
			if (index > target.Count) index = target.Count;
			target.Insert(index, node);
		}
		Save();
		return true;
	}

	static bool tryfindlist(List<BookmarkNode> list, string id, ref List<BookmarkNode> foundList, ref int foundIndex) {
		if (list == null) return false;
		for (var i = 0; i < list.Count; i++) {
			var n = list[i];
			if (n == null) continue;
			if (string.Equals(n.Id, id, StringComparison.Ordinal)) {
				foundList = list;
				foundIndex = i;
				return true;
			}
			if (n.Kind == BookmarkKind.Group && n.Children != null
				&& tryfindlist(n.Children, id, ref foundList, ref foundIndex))
				return true;
		}
		return false;
	}

	/// <summary>node 是否包含 id 为 childId 的子孙（用于防环）。</summary>
	static bool isdescendant(BookmarkNode node, string childId) {
		if (node == null || string.IsNullOrEmpty(childId)) return false;
		if (node.Kind != BookmarkKind.Group || node.Children == null) return false;
		foreach (var c in node.Children) {
			if (c == null) continue;
			if (string.Equals(c.Id, childId, StringComparison.Ordinal)) return true;
			if (isdescendant(c, childId)) return true;
		}
		return false;
	}

	/// <summary>节点当前所在父分组 Id（根返回 null）；未找到返回 null。</summary>
	public static string GetParentId(string id) {
		if (string.IsNullOrEmpty(id)) return null;
		string foundParent = null;
		bool found = false;
		void walk(List<BookmarkNode> list, string parentId) {
			if (list == null || found) return;
			foreach (var n in list) {
				if (n == null) continue;
				if (string.Equals(n.Id, id, StringComparison.Ordinal)) {
					foundParent = parentId;
					found = true;
					return;
				}
				if (n.Kind == BookmarkKind.Group && n.Children != null)
					walk(n.Children, n.Id);
				if (found) return;
			}
		}
		walk(Load().Items, null);
		return foundParent;
	}

	public static BookmarkNode FindById(string id) {
		if (string.IsNullOrEmpty(id)) return null;
		return find(Load().Items, id);
	}

	/// <summary>按路径查找已存在的文件/文件夹书签（不含分组）。</summary>
	public static BookmarkNode FindByPath(string path) {
		path = BookmarkNode.NewFile(path)?.Path;
		if (string.IsNullOrEmpty(path)) return null;
		return findbypath(Load().Items, path);
	}

	public static IEnumerable<BookmarkNode> EnumerateGroups() {
		foreach (var n in walk(Load().Items))
			if (n != null && n.Kind == BookmarkKind.Group)
				yield return n;
	}

	static void normalize(List<BookmarkNode> list) {
		if (list == null) return;
		foreach (var n in list) {
			if (n == null) continue;
			if (string.IsNullOrEmpty(n.Id)) n.Id = Guid.NewGuid().ToString("N");
			if (n.Kind == BookmarkKind.Group) {
				if (n.Children == null) n.Children = new List<BookmarkNode>();
				normalize(n.Children);
			} else {
				n.Children = null;
			}
		}
	}

	static bool remove(List<BookmarkNode> list, string id) {
		if (list == null) return false;
		for (var i = 0; i < list.Count; i++) {
			var n = list[i];
			if (n == null) continue;
			if (string.Equals(n.Id, id, StringComparison.Ordinal)) {
				list.RemoveAt(i);
				return true;
			}
			if (n.Kind == BookmarkKind.Group && n.Children != null && remove(n.Children, id))
				return true;
		}
		return false;
	}

	static BookmarkNode find(List<BookmarkNode> list, string id) {
		if (list == null) return null;
		foreach (var n in list) {
			if (n == null) continue;
			if (string.Equals(n.Id, id, StringComparison.Ordinal)) return n;
			if (n.Kind == BookmarkKind.Group) {
				var c = find(n.Children, id);
				if (c != null) return c;
			}
		}
		return null;
	}

	static BookmarkNode findbypath(List<BookmarkNode> list, string path) {
		if (list == null) return null;
		foreach (var n in list) {
			if (n == null) continue;
			if (n.Kind != BookmarkKind.Group
				&& !string.IsNullOrEmpty(n.Path)
				&& string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))
				return n;
			if (n.Kind == BookmarkKind.Group) {
				var c = findbypath(n.Children, path);
				if (c != null) return c;
			}
		}
		return null;
	}

	static IEnumerable<BookmarkNode> walk(List<BookmarkNode> list) {
		if (list == null) yield break;
		foreach (var n in list) {
			if (n == null) continue;
			yield return n;
			if (n.Kind == BookmarkKind.Group && n.Children != null)
				foreach (var c in walk(n.Children))
					yield return c;
		}
	}
}
