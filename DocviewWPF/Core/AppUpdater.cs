using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DocviewWPF;

/// <summary>
/// 从 GitHub Releases 检查/下载更新；复制自身到 tmp 后以命令行完成主程序替换。
/// </summary>
static class AppUpdater {
	const string ReleasesApi = "https://api.github.com/repos/cfwang123/DocviewWPF/releases/latest";
	const string ReleasesPage = "https://github.com/cfwang123/DocviewWPF/releases";
	const string UserAgent = "DocviewWPF-Updater";

	/// <summary>命令行：--apply-update &lt;payloadDir&gt; &lt;targetDir&gt; &lt;waitPid&gt;</summary>
	public static bool IsApplyUpdateArg(string[] args) {
		if (args == null) return false;
		foreach (var a in args)
			if (string.Equals(a, "--apply-update", StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	/// <summary>无 UI 替换：等待旧进程退出 → 复制 payload → 启动目标程序。</summary>
	public static int RunApplyUpdate(string[] args) {
		// --apply-update payload target pid
		string payload = null, target = null;
		var pid = 0;
		for (var i = 0; i < args.Length; i++) {
			if (!string.Equals(args[i], "--apply-update", StringComparison.OrdinalIgnoreCase))
				continue;
			if (i + 1 < args.Length) payload = args[++i];
			if (i + 1 < args.Length) target = args[++i];
			if (i + 1 < args.Length) int.TryParse(args[++i], out pid);
			break;
		}
		if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(target)) {
			try { Console.Error.WriteLine("usage: --apply-update <payloadDir> <targetDir> <waitPid>"); } catch { /* ignore */ }
			return 2;
		}
		try {
			payload = Path.GetFullPath(payload);
			target = Path.GetFullPath(target);
		} catch { /* keep */ }

		// 等主进程退出（最多 120s）
		if (pid > 0) {
			var deadline = Environment.TickCount + 120_000;
			while (Environment.TickCount - deadline < 0) {
				try {
					var p = Process.GetProcessById(pid);
					if (p.HasExited) break;
					Thread.Sleep(400);
				} catch {
					break; // 进程已不存在
				}
			}
			Thread.Sleep(500);
		}

		// 复制更新包覆盖安装目录
		try {
			if (!Directory.Exists(payload))
				throw new DirectoryNotFoundException("payload missing: " + payload);
			copydir(payload, target, overwrite: true);
		} catch (Exception ex) {
			try {
				MessageBox.Show("更新替换失败:\n" + ex.Message + "\n\npayload=" + payload + "\ntarget=" + target,
					"DocviewWPF 更新", MessageBoxButton.OK, MessageBoxImage.Error);
			} catch { /* ignore */ }
			return 1;
		}

		// 启动新版本
		try {
			var exe = Path.Combine(target, "DocviewWPF.exe");
			if (!File.Exists(exe)) {
				// payload 可能多一层目录
				var found = findexe(target);
				if (found != null) exe = found;
			}
			if (File.Exists(exe)) {
				Process.Start(new ProcessStartInfo {
					FileName = exe,
					WorkingDirectory = Path.GetDirectoryName(exe) ?? target,
					UseShellExecute = true,
				});
			}
		} catch (Exception ex) {
			try {
				MessageBox.Show("更新完成，但启动失败:\n" + ex.Message, "DocviewWPF 更新",
					MessageBoxButton.OK, MessageBoxImage.Warning);
			} catch { /* ignore */ }
			return 1;
		}
		return 0;
	}

	static string findexe(string root) {
		try {
			foreach (var f in Directory.GetFiles(root, "DocviewWPF.exe", SearchOption.AllDirectories))
				return f;
		} catch { /* ignore */ }
		return null;
	}

	/// <summary>UI 流程：检查 → 询问 → 下载 → 解压 → 复制自身到 tmp → 启动替换。</summary>
	public static async Task RunCheckAndUpdateAsync(Window owner) {
		ensuretls();
		var prog = new UpdateProgressWindow(owner);
		prog.Show();
		prog.SetStatus("正在检查更新…");
		prog.SetProgress(-1);

		try {
			var local = GetLocalVersion();
			var info = await FetchLatestAsync(prog.Cts.Token).ConfigureAwait(true);
			if (prog.Cancelled) {
				prog.SetStatus("已取消");
				prog.MarkDone(false);
				return;
			}
			if (info == null || string.IsNullOrEmpty(info.Tag)) {
				prog.SetStatus("无法获取版本信息。\n请手动访问:\n" + ReleasesPage);
				prog.MarkDone(false);
				return;
			}

			var remote = ParseVersion(info.Tag);
			prog.SetStatus($"当前版本: {local}\n最新版本: {info.Tag}");
			if (remote != null && local != null && remote <= local) {
				prog.SetStatus($"已是最新版本 ({local})。");
				prog.MarkDone(true);
				return;
			}
			if (string.IsNullOrEmpty(info.DownloadUrl)) {
				prog.SetStatus("最新版本未找到可下载资源。\n" + ReleasesPage);
				prog.MarkDone(false);
				return;
			}

			var ask = MessageBox.Show(owner,
				$"发现新版本 {info.Tag}（当前 {local}）。\n\n是否下载并更新？\n\n{info.DownloadUrl}",
				"检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
			if (ask != MessageBoxResult.Yes) {
				prog.SetStatus("已取消更新");
				prog.MarkDone(false);
				return;
			}

			var tmpRoot = GetTmpUpdateDir();
			Directory.CreateDirectory(tmpRoot);
			var dlName = info.AssetName;
			if (string.IsNullOrEmpty(dlName))
				dlName = "update" + Path.GetExtension(new Uri(info.DownloadUrl).AbsolutePath);
			var dlPath = Path.Combine(tmpRoot, "download", dlName);
			Directory.CreateDirectory(Path.GetDirectoryName(dlPath));

			prog.SetStatus("正在下载 " + dlName + " …");
			prog.SetProgress(0);
			await DownloadFileAsync(info.DownloadUrl, dlPath, prog, prog.Cts.Token).ConfigureAwait(true);
			if (prog.Cancelled) {
				prog.SetStatus("已取消");
				prog.MarkDone(false);
				return;
			}

			var payloadDir = Path.Combine(tmpRoot, "payload");
			try {
				if (Directory.Exists(payloadDir))
					Directory.Delete(payloadDir, true);
			} catch { /* ignore */ }
			Directory.CreateDirectory(payloadDir);

			prog.SetStatus("正在解压…");
			prog.SetProgress(-1);
			ExtractPackage(dlPath, payloadDir, prog);
			if (prog.Cancelled) {
				prog.SetStatus("已取消");
				prog.MarkDone(false);
				return;
			}

			// 解压后若只有一层目录，用内层作为 payload
			payloadDir = unwrappayload(payloadDir);

			var targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
				Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var hostDir = Path.Combine(tmpRoot, "host");
			prog.SetStatus("正在复制程序到临时目录…");
			prog.SetProgress(-1);
			try {
				if (Directory.Exists(hostDir))
					Directory.Delete(hostDir, true);
			} catch { /* ignore */ }
			// 复制自身安装目录到 tmp/host（排除 tmp\update 避免递归）
			copydir(targetDir, hostDir, overwrite: true, excludeRel: "tmp");

			var hostExe = Path.Combine(hostDir, "DocviewWPF.exe");
			if (!File.Exists(hostExe)) {
				prog.SetStatus("复制失败：未找到 DocviewWPF.exe");
				prog.MarkDone(false);
				return;
			}

			var pid = Process.GetCurrentProcess().Id;
			prog.SetStatus("即将退出并应用更新…\n请稍候，程序会自动重启。");
			prog.SetProgress(100);

			// 启动临时目录中的自身完成替换
			var psi = new ProcessStartInfo {
				FileName = hostExe,
				Arguments = $"--apply-update \"{payloadDir}\" \"{targetDir}\" {pid}",
				WorkingDirectory = hostDir,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			Process.Start(psi);

			// 释放单实例后退出
			try { SingleInstance.Release(); } catch { /* ignore */ }
			prog.MarkDone(true);
			Application.Current.Shutdown();
		} catch (OperationCanceledException) {
			prog.SetStatus("已取消");
			prog.MarkDone(false);
		} catch (Exception ex) {
			DocLog.Error("AppUpdater", ex);
			prog.SetStatus("更新失败: " + ex.Message);
			prog.MarkDone(false);
		}
	}

	public static Version GetLocalVersion() {
		try {
			var v = Assembly.GetExecutingAssembly().GetName().Version;
			if (v != null) return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
		} catch { /* ignore */ }
		return new Version(0, 0, 0);
	}

	static Version ParseVersion(string tag) {
		if (string.IsNullOrWhiteSpace(tag)) return null;
		tag = tag.Trim();
		if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
			tag = tag.Substring(1);
		// 1.0.1 or 1.0.1.0
		if (Version.TryParse(tag, out var v))
			return new Version(v.Major, v.Minor, Math.Max(0, v.Build < 0 ? 0 : v.Build));
		var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
		if (!m.Success) return null;
		var maj = int.Parse(m.Groups[1].Value);
		var min = int.Parse(m.Groups[2].Value);
		var bld = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
		return new Version(maj, min, bld);
	}

	sealed class ReleaseInfo {
		public string Tag;
		public string DownloadUrl;
		public string AssetName;
	}

	static async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct) {
		var json = await DownloadStringAsync(ReleasesApi, ct).ConfigureAwait(false);
		if (string.IsNullOrEmpty(json)) return null;
		var tag = match1(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
		// 优先 zip，其次 7z，再任意 browser_download_url
		string url = null, name = null;
		foreach (Match m in Regex.Matches(json,
			"\"name\"\\s*:\\s*\"([^\"]+)\"[\\s\\S]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"",
			RegexOptions.IgnoreCase)) {
			var n = m.Groups[1].Value;
			var u = m.Groups[2].Value.Replace("\\u0026", "&");
			if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
				name = n; url = u; break;
			}
			if (url == null && n.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) {
				name = n; url = u;
			}
			if (url == null && (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
				|| n.IndexOf("Docview", StringComparison.OrdinalIgnoreCase) >= 0)) {
				name = n; url = u;
			}
		}
		if (url == null) {
			// 回退：任意 browser_download_url
			url = match1(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
			if (url != null) url = url.Replace("\\u0026", "&");
			if (url != null)
				try { name = Path.GetFileName(new Uri(url).AbsolutePath); } catch { name = "update.bin"; }
		}
		return new ReleaseInfo { Tag = tag, DownloadUrl = url, AssetName = name };
	}

	static string match1(string s, string pattern) {
		var m = Regex.Match(s ?? "", pattern, RegexOptions.IgnoreCase);
		return m.Success ? m.Groups[1].Value : null;
	}

	static void ensuretls() {
		try {
			ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		} catch { /* ignore */ }
	}

	static async Task<string> DownloadStringAsync(string url, CancellationToken ct) {
		// WebClient 无取消时用 Task.Run + 检查
		return await Task.Run(() => {
			ct.ThrowIfCancellationRequested();
			using (var wc = new WebClient()) {
				wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
				wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
				return wc.DownloadString(url);
			}
		}, ct).ConfigureAwait(false);
	}

	static async Task DownloadFileAsync(string url, string path, UpdateProgressWindow prog, CancellationToken ct) {
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
		var tcs = new TaskCompletionSource<bool>();
		using (var wc = new WebClient()) {
			wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
			wc.DownloadProgressChanged += (_, e) => {
				if (ct.IsCancellationRequested) {
					try { wc.CancelAsync(); } catch { /* ignore */ }
					return;
				}
				prog?.SetProgress(e.ProgressPercentage);
				if (e.TotalBytesToReceive > 0)
					prog?.SetStatus($"正在下载… {e.BytesReceived / 1024.0 / 1024.0:F1} / {e.TotalBytesToReceive / 1024.0 / 1024.0:F1} MB");
			};
			wc.DownloadFileCompleted += (_, e) => {
				if (e.Cancelled) tcs.TrySetCanceled();
				else if (e.Error != null) tcs.TrySetException(e.Error);
				else tcs.TrySetResult(true);
			};
			using (ct.Register(() => {
				try { wc.CancelAsync(); } catch { /* ignore */ }
			})) {
				wc.DownloadFileAsync(new Uri(url), path);
				await tcs.Task.ConfigureAwait(false);
			}
		}
	}

	static void ExtractPackage(string archivePath, string destDir, UpdateProgressWindow prog) {
		var ext = Path.GetExtension(archivePath).ToLowerInvariant();
		if (ext == ".zip") {
			ZipFile.ExtractToDirectory(archivePath, destDir);
			return;
		}
		if (ext == ".7z" || ext == ".7zip") {
			var seven = find7z();
			if (seven == null)
				throw new InvalidOperationException(
					"更新包为 7z 格式，未找到 7-Zip。\n请安装 7-Zip 或发布 zip 包。\n" + ReleasesPage);
			prog?.SetStatus("使用 7-Zip 解压…");
			var psi = new ProcessStartInfo {
				FileName = seven,
				Arguments = $"x -y -o\"{destDir}\" \"{archivePath}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			using (var p = Process.Start(psi)) {
				if (p == null) throw new InvalidOperationException("无法启动 7z");
				p.WaitForExit(600_000);
				if (p.ExitCode != 0) {
					var err = "";
					try { err = p.StandardError.ReadToEnd(); } catch { /* ignore */ }
					throw new InvalidOperationException("7z 解压失败 code=" + p.ExitCode + " " + err);
				}
			}
			return;
		}
		// 单文件 exe：直接拷入 payload
		if (ext == ".exe") {
			File.Copy(archivePath, Path.Combine(destDir, Path.GetFileName(archivePath)), true);
			return;
		}
		throw new InvalidOperationException("不支持的更新包格式: " + ext);
	}

	static string find7z() {
		var candidates = new[] {
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
			"7z.exe",
			"7za.exe",
		};
		foreach (var c in candidates) {
			try {
				if (c.IndexOf('\\') >= 0 || c.IndexOf('/') >= 0) {
					if (File.Exists(c)) return c;
				} else {
					// PATH
					var psi = new ProcessStartInfo {
						FileName = "where",
						Arguments = c,
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardOutput = true,
					};
					using (var p = Process.Start(psi)) {
						if (p == null) continue;
						var o = p.StandardOutput.ReadLine();
						p.WaitForExit(3000);
						if (!string.IsNullOrWhiteSpace(o) && File.Exists(o.Trim()))
							return o.Trim();
					}
				}
			} catch { /* ignore */ }
		}
		return null;
	}

	/// <summary>若 payload 仅含一个子目录且其中有 exe，返回该子目录。</summary>
	static string unwrappayload(string payloadDir) {
		try {
			if (File.Exists(Path.Combine(payloadDir, "DocviewWPF.exe")))
				return payloadDir;
			var dirs = Directory.GetDirectories(payloadDir);
			var files = Directory.GetFiles(payloadDir);
			if (dirs.Length == 1 && files.Length == 0) {
				var inner = dirs[0];
				if (File.Exists(Path.Combine(inner, "DocviewWPF.exe"))
					|| findexe(inner) != null)
					return inner;
			}
			// 任意深度找 exe 的父目录
			var exe = findexe(payloadDir);
			if (exe != null)
				return Path.GetDirectoryName(exe) ?? payloadDir;
		} catch { /* ignore */ }
		return payloadDir;
	}

	public static string GetTmpUpdateDir() {
		// 安装目录下 tmp/update
		var baseDir = AppDomain.CurrentDomain.BaseDirectory;
		return Path.Combine(baseDir, "tmp", "update");
	}

	static void copydir(string src, string dst, bool overwrite, string excludeRel = null) {
		src = Path.GetFullPath(src);
		dst = Path.GetFullPath(dst);
		Directory.CreateDirectory(dst);
		foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories)) {
			var rel = dir.Substring(src.Length).TrimStart('\\', '/');
			if (excludeRel != null && (rel.Equals(excludeRel, StringComparison.OrdinalIgnoreCase)
				|| rel.StartsWith(excludeRel + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				|| rel.StartsWith(excludeRel + "/", StringComparison.OrdinalIgnoreCase)))
				continue;
			Directory.CreateDirectory(Path.Combine(dst, rel));
		}
		foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) {
			var rel = file.Substring(src.Length).TrimStart('\\', '/');
			if (excludeRel != null && (rel.Equals(excludeRel, StringComparison.OrdinalIgnoreCase)
				|| rel.StartsWith(excludeRel + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				|| rel.StartsWith(excludeRel + "/", StringComparison.OrdinalIgnoreCase)))
				continue;
			var to = Path.Combine(dst, rel);
			var toDir = Path.GetDirectoryName(to);
			if (!string.IsNullOrEmpty(toDir))
				Directory.CreateDirectory(toDir);
			// 重试拷贝（文件可能短暂占用）
			Exception last = null;
			for (var i = 0; i < 15; i++) {
				try {
					File.Copy(file, to, overwrite);
					last = null;
					break;
				} catch (Exception ex) {
					last = ex;
					Thread.Sleep(200);
				}
			}
			if (last != null) throw last;
		}
	}
}
