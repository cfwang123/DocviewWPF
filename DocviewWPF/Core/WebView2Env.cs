using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace DocviewWPF;

/// <summary>
/// 进程内共享 CoreWebView2Environment（同一 userDataFolder）。
/// 避免多个 WebView2 各自 CreateAsync 后交叉 Ensure 报
/// “already initialized with a different CoreWebView2Environment”。
/// </summary>
static class WebView2Env {
	static readonly object Gate = new();
	static Task<CoreWebView2Environment> envTask;

	public static Task<CoreWebView2Environment> GetAsync() {
		lock (Gate) {
			if (envTask != null) return envTask;
			var ud = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"DocviewWPF", "webview2");
			Directory.CreateDirectory(ud);
			envTask = CoreWebView2Environment.CreateAsync(null, ud);
			return envTask;
		}
	}
}
