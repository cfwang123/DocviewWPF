using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DocviewWPF;

/// <summary>
/// 原生库放在输出目录 x64\ / x86\，不在根目录。
/// 启动时把 DLL 搜索目录指到对应架构，并预加载 pdfium。
/// </summary>
static class NativeBootstrap {
	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool SetDllDirectory(string lpPathName);

	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern IntPtr LoadLibrary(string lpFileName);

	static bool done;

	public static void Init() {
		if (done) return;
		done = true;
		try {
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;
			var arch = Environment.Is64BitProcess ? "x64" : "x86";
			var dir = Path.Combine(baseDir, arch);
			if (!Directory.Exists(dir)) {
				DocLog.Warn($"NativeBootstrap: missing {arch}\\ under {baseDir}");
				return;
			}
			// 后续 LoadLibrary / DllImport 会搜此目录
			if (!SetDllDirectory(dir))
				DocLog.Warn("NativeBootstrap: SetDllDirectory failed");

			// 显式预载，避免首次 P/Invoke 找不到
			tryload(Path.Combine(dir, "pdfium.dll"));
			tryload(Path.Combine(dir, "libSkiaSharp.dll"));
			DocLog.Info($"NativeBootstrap ok arch={arch} dir={dir}");
		} catch (Exception ex) {
			DocLog.Error("NativeBootstrap", ex);
		}
	}

	static void tryload(string path) {
		if (!File.Exists(path)) {
			DocLog.Warn("NativeBootstrap missing: " + path);
			return;
		}
		var h = LoadLibrary(path);
		if (h == IntPtr.Zero)
			DocLog.Warn($"NativeBootstrap LoadLibrary failed: {path} err={Marshal.GetLastWin32Error()}");
	}
}
