using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace DocviewWPF;

/// <summary>
/// VS Code 风格工作区文件树：懒加载展开、双击打开、图标区分文件夹/文件。
/// </summary>
static class FolderTree {
	static readonly Brush FolderFg = brush(0xDC, 0xB6, 0x7A);
	static readonly Brush TextFg = brush(0x33, 0x33, 0x33);
	static readonly Brush TreeBg = brush(0xF3, 0xF3, 0xF3);
	static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) {
		".txt", ".md", ".cs", ".js", ".ts", ".py", ".php", ".lua", ".json", ".xml", ".html", ".css",
		".csv", ".tsv", ".log", ".yml", ".yaml", ".ini", ".sql", ".java", ".go", ".rs", ".c", ".cpp", ".h",
	};

	static SolidColorBrush brush(byte r, byte g, byte b) {
		var br = new SolidColorBrush(Color.FromRgb(r, g, b));
		if (br.CanFreeze) br.Freeze();
		return br;
	}

	public static void ConfigureTree(TreeView tree) {
		if (tree == null) return;
		tree.BorderThickness = new Thickness(0);
		tree.Background = TreeBg;
		tree.Padding = new Thickness(0, 2, 0, 4);
		ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
		ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Auto);
	}

	/// <summary>重建根：workspace 下一级子项。</summary>
	public static void LoadRoot(TreeView tree, string workspace) {
		if (tree == null) return;
		tree.Items.Clear();
		if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return;
		try {
			workspace = IOPath.GetFullPath(workspace);
			foreach (var item in EnumerateChildren(workspace))
				tree.Items.Add(item);
		} catch (Exception ex) {
			DocLog.Warn($"FolderTree.LoadRoot: {ex.Message}");
		}
	}

	static List<TreeViewItem> EnumerateChildren(string dir) {
		var dirs = new List<TreeViewItem>();
		var files = new List<TreeViewItem>();
		try {
			foreach (var d in Directory.GetDirectories(dir)) {
				try {
					var name = IOPath.GetFileName(d);
					if (name == "node_modules" || name == "bin" || name == "obj")
						continue;
					dirs.Add(MakeDirItem(d, name));
				} catch { /* skip */ }
			}
			dirs.Sort((a, b) => string.Compare(
				IOPath.GetFileName(a.Tag as string ?? ""),
				IOPath.GetFileName(b.Tag as string ?? ""),
				StringComparison.OrdinalIgnoreCase));
			foreach (var f in Directory.GetFiles(dir)) {
				try {
					var name = IOPath.GetFileName(f);
					files.Add(MakeFileItem(f, name));
				} catch { /* skip */ }
			}
			files.Sort((a, b) => string.Compare(
				IOPath.GetFileName(a.Tag as string ?? ""),
				IOPath.GetFileName(b.Tag as string ?? ""),
				StringComparison.OrdinalIgnoreCase));
		} catch (Exception ex) {
			DocLog.Warn($"FolderTree.enum {dir}: {ex.Message}");
		}
		var list = new List<TreeViewItem>(dirs.Count + files.Count);
		list.AddRange(dirs);
		list.AddRange(files);
		return list;
	}

	public static TreeViewItem MakeDirItem(string fullPath, string name) {
		var item = new TreeViewItem {
			Tag = fullPath,
			IsExpanded = false,
			Padding = new Thickness(0, 1, 0, 1),
			Header = MakeHeader(name, isFolder: true),
		};
		item.Items.Add(new TreeViewItem { Header = "…", Tag = null });
		item.Expanded += ondirexpanded;
		return item;
	}

	public static TreeViewItem MakeFileItem(string fullPath, string name) {
		return new TreeViewItem {
			Tag = fullPath,
			Padding = new Thickness(0, 1, 0, 1),
			Header = MakeHeader(name, isFolder: false, path: fullPath),
		};
	}

	static void ondirexpanded(object sender, RoutedEventArgs e) {
		if (sender is not TreeViewItem item) return;
		e.Handled = true;
		var path = item.Tag as string;
		if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
		if (item.Items.Count != 1 || (item.Items[0] as TreeViewItem)?.Tag != null)
			return;
		item.Items.Clear();
		try {
			foreach (var child in EnumerateChildren(path))
				item.Items.Add(child);
		} catch (Exception ex) {
			DocLog.Warn($"FolderTree expand: {ex.Message}");
		}
	}

	static FrameworkElement MakeHeader(string name, bool isFolder, string path = null) {
		var sp = new StackPanel {
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
		};
		string glyph;
		if (isFolder) {
			glyph = "📁";
		} else {
			var ext = path != null ? IOPath.GetExtension(path) : "";
			glyph = "📄";
			if (TextExt.Contains(ext)) glyph = "📄";
			if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase))
				glyph = "🖼";
			else if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
				glyph = "📕";
			else if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(ext, ".tsv", StringComparison.OrdinalIgnoreCase))
				glyph = "📊";
			else if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase))
				glyph = "📘";
		}
		sp.Children.Add(new TextBlock {
			Text = glyph,
			FontSize = 13,
			Margin = new Thickness(0, 0, 6, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = isFolder ? FolderFg : TextFg,
		});
		sp.Children.Add(new TextBlock {
			Text = name ?? "",
			FontSize = 12.5,
			Foreground = TextFg,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
		});
		return sp;
	}

	/// <summary>从 TreeViewItem 取完整路径。</summary>
	public static string PathOf(object item) {
		if (item is TreeViewItem tvi)
			return tvi.Tag as string;
		return null;
	}

	/// <summary>折叠全部节点（仿 VS Code Collapse Folders in Explorer）。</summary>
	public static void CollapseAll(TreeView tree) {
		if (tree == null) return;
		foreach (var o in tree.Items)
			if (o is TreeViewItem tvi)
				collapse(tvi);
	}

	static void collapse(TreeViewItem item) {
		if (item == null) return;
		item.IsExpanded = false;
		foreach (var o in item.Items)
			if (o is TreeViewItem tvi)
				collapse(tvi);
	}
}
