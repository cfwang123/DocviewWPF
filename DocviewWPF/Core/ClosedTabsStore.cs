using System;
using System.Collections.Generic;
using System.IO;

namespace DocviewWPF;

/// <summary>最近关闭的标签路径栈（内存 + 随 session 持久化，上限 20）。</summary>
static class ClosedTabsStore {
	const int MAX = 20;
	static readonly List<string> stack = new();
	static readonly object gate = new();

	public static int Count {
		get { lock (gate) return stack.Count; }
	}

	/// <summary>关闭标签时压入（规范化路径；同路径移到栈顶）。</summary>
	public static void Push(string path) {
		if (string.IsNullOrWhiteSpace(path)) return;
		try {
			path = Path.GetFullPath(path.Trim().Trim('"'));
		} catch { return; }
		lock (gate) {
			for (var i = stack.Count - 1; i >= 0; i--) {
				if (string.Equals(stack[i], path, StringComparison.OrdinalIgnoreCase))
					stack.RemoveAt(i);
			}
			stack.Add(path);
			while (stack.Count > MAX)
				stack.RemoveAt(0);
		}
	}

	/// <summary>弹出最近关闭的路径；空则 null。</summary>
	public static string Pop() {
		lock (gate) {
			if (stack.Count == 0) return null;
			var i = stack.Count - 1;
			var p = stack[i];
			stack.RemoveAt(i);
			return p;
		}
	}

	/// <summary>窥视栈顶不弹出。</summary>
	public static string Peek() {
		lock (gate) {
			if (stack.Count == 0) return null;
			return stack[stack.Count - 1];
		}
	}

	/// <summary>会话恢复：整表替换（最旧在前，最新在末 = 栈顶）。</summary>
	public static void ReplaceAll(IList<string> paths) {
		lock (gate) {
			stack.Clear();
			if (paths == null) return;
			foreach (var p in paths) {
				if (string.IsNullOrWhiteSpace(p)) continue;
				string full;
				try { full = Path.GetFullPath(p.Trim().Trim('"')); }
				catch { continue; }
				for (var i = stack.Count - 1; i >= 0; i--) {
					if (string.Equals(stack[i], full, StringComparison.OrdinalIgnoreCase))
						stack.RemoveAt(i);
				}
				stack.Add(full);
			}
			while (stack.Count > MAX)
				stack.RemoveAt(0);
		}
	}

	/// <summary>会话保存：复制当前栈（最旧→最新）。</summary>
	public static List<string> Snapshot() {
		lock (gate) {
			return new List<string>(stack);
		}
	}
}
