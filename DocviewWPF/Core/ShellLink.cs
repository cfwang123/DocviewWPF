using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DocviewWPF;

/// <summary>
/// 解析 Windows 快捷方式（.lnk）到目标路径（WScript.Shell COM）。
/// </summary>
static class ShellLink {
	/// <summary>
	/// 若 path 为 .lnk 且可解析，返回目标完整路径；否则原样返回。
	/// 目标不存在时仍返回解析到的路径（由调用方判断 File.Exists）。
	/// </summary>
	public static string Resolve(string path) {
		if (string.IsNullOrWhiteSpace(path)) return path;
		path = path.Trim().Trim('"');
		if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			return path;
		try {
			path = Path.GetFullPath(path);
		} catch { /* keep */ }
		if (!File.Exists(path)) return path;

		try {
			var target = resolvelnk(path);
			if (string.IsNullOrWhiteSpace(target)) return path;
			target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
			try {
				if (!string.IsNullOrEmpty(target))
					target = Path.GetFullPath(target);
			} catch { /* keep as-is */ }
			DocLog.Info($"ShellLink {path} -> {target}");
			return target;
		} catch (Exception ex) {
			DocLog.Warn($"ShellLink resolve fail: {ex.Message}");
			return path;
		}
	}

	static string resolvelnk(string lnkPath) {
		var shellType = Type.GetTypeFromProgID("WScript.Shell");
		if (shellType == null) return null;
		object shell = null;
		object shortcut = null;
		try {
			shell = Activator.CreateInstance(shellType);
			shortcut = shellType.InvokeMember(
				"CreateShortcut",
				BindingFlags.InvokeMethod,
				null,
				shell,
				new object[] { lnkPath });
			if (shortcut == null) return null;
			var target = shortcut.GetType().InvokeMember(
				"TargetPath",
				BindingFlags.GetProperty,
				null,
				shortcut,
				null) as string;
			return string.IsNullOrWhiteSpace(target) ? null : target;
		} finally {
			releasecom(shortcut);
			releasecom(shell);
		}
	}

	static void releasecom(object o) {
		if (o == null) return;
		try {
			if (Marshal.IsComObject(o))
				Marshal.FinalReleaseComObject(o);
		} catch { /* ignore */ }
	}
}
