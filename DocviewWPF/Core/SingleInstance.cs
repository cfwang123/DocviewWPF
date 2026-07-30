using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DocviewWPF;

/// <summary>
/// 单实例：Mutex 互斥；次进程经命名管道把待打开路径发给主进程，并尝试前置主窗。
/// </summary>
static class SingleInstance {
	const string MUTEX_NAME = "Local\\DocviewWPF.SingleInstance.Mutex";
	const string PIPE_NAME = "DocviewWPF.SingleInstance.Pipe";
	const string HWND_MAP_NAME = "Local\\DocviewWPF.SingleInstance.Hwnd";
	const int CONNECT_RETRY = 40;
	const int CONNECT_WAIT_MS = 80;

	static Mutex mutex;
	static bool ownsMutex;
	static CancellationTokenSource cts;
	static Thread listenThread;
	static Action<string[]> onOpen;

	[DllImport("user32.dll")]
	static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	static extern bool IsIconic(IntPtr hWnd);

	const int SW_RESTORE = 9;

	/// <summary>尝试成为主实例；失败表示已有实例在跑。</summary>
	public static bool TryAcquire() {
		try {
			mutex = new Mutex(true, MUTEX_NAME, out var created);
			if (!created) {
				try { mutex.Dispose(); } catch { /* ignore */ }
				mutex = null;
				ownsMutex = false;
				return false;
			}
			ownsMutex = true;
			return true;
		} catch (Exception ex) {
			try { DocLog.Warn($"SingleInstance.TryAcquire: {ex.Message}"); } catch { /* ignore */ }
			// 拿不到互斥时放行，避免误伤无法启动
			ownsMutex = false;
			return true;
		}
	}

	public static void Release() {
		try { StopListening(); } catch { /* ignore */ }
		if (ownsMutex && mutex != null) {
			try { mutex.ReleaseMutex(); } catch { /* ignore */ }
			try { mutex.Dispose(); } catch { /* ignore */ }
		}
		mutex = null;
		ownsMutex = false;
	}

	/// <summary>主进程发布当前应前置的窗口句柄（次进程用来激活）。</summary>
	public static void PublishHwnd(IntPtr hwnd) {
		if (hwnd == IntPtr.Zero) return;
		try {
			using var mmf = MemoryMappedFile.CreateOrOpen(HWND_MAP_NAME, 8);
			using var acc = mmf.CreateViewAccessor(0, 8);
			acc.Write(0, hwnd.ToInt64());
		} catch (Exception ex) {
			try { DocLog.Warn($"SingleInstance.PublishHwnd: {ex.Message}"); } catch { /* ignore */ }
		}
	}

	/// <summary>主进程启动管道监听；onOpen 在后台线程回调，调用方自行切 UI 线程。</summary>
	public static void StartListening(Action<string[]> handler) {
		onOpen = handler ?? throw new ArgumentNullException(nameof(handler));
		StopListening();
		cts = new CancellationTokenSource();
		var token = cts.Token;
		listenThread = new Thread(() => listenloop(token)) {
			IsBackground = true,
			Name = "DocviewWPF.SingleInstance",
		};
		listenThread.Start();
	}

	public static void StopListening() {
		try { cts?.Cancel(); } catch { /* ignore */ }
		// 连一下自己以打断 WaitForConnection
		try {
			using var client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out);
			client.Connect(50);
		} catch { /* ignore */ }
		try {
			if (listenThread != null && listenThread.IsAlive)
				listenThread.Join(500);
		} catch { /* ignore */ }
		listenThread = null;
		try { cts?.Dispose(); } catch { /* ignore */ }
		cts = null;
	}

	/// <summary>次实例：激活主窗并把命令行路径发给主进程。</summary>
	public static bool SendOpen(string[] args) {
		var paths = filterpaths(args);
		tryactivate();
		try {
			using var client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out);
			var connected = false;
			for (var i = 0; i < CONNECT_RETRY; i++) {
				try {
					client.Connect(CONNECT_WAIT_MS);
					connected = true;
					break;
				} catch (TimeoutException) {
					// retry
				} catch (IOException) {
					Thread.Sleep(CONNECT_WAIT_MS);
				}
			}
			if (!connected) {
				try { DocLog.Warn("SingleInstance.SendOpen: pipe connect failed"); } catch { /* ignore */ }
				return false;
			}
			// 无路径也发一帧空内容，表示仅激活
			using var w = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
			foreach (var p in paths)
				w.WriteLine(p);
			w.WriteLine(); // 结束标记
			return true;
		} catch (Exception ex) {
			try { DocLog.Warn($"SingleInstance.SendOpen: {ex.Message}"); } catch { /* ignore */ }
			return false;
		}
	}

	static void tryactivate() {
		try {
			using var mmf = MemoryMappedFile.OpenExisting(HWND_MAP_NAME);
			using var acc = mmf.CreateViewAccessor(0, 8);
			var raw = acc.ReadInt64(0);
			if (raw == 0) return;
			var hwnd = new IntPtr(raw);
			if (IsIconic(hwnd))
				ShowWindow(hwnd, SW_RESTORE);
			SetForegroundWindow(hwnd);
		} catch {
			// 主进程尚未写入或已退出
		}
	}

	static void listenloop(CancellationToken token) {
		while (!token.IsCancellationRequested) {
			NamedPipeServerStream server = null;
			try {
				server = new NamedPipeServerStream(
					PIPE_NAME,
					PipeDirection.In,
					1,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous);
				// 可取消的等待
				var ar = server.BeginWaitForConnection(null, null);
				while (!ar.IsCompleted) {
					if (token.IsCancellationRequested) {
						try { server.Dispose(); } catch { /* ignore */ }
						return;
					}
					ar.AsyncWaitHandle.WaitOne(200);
				}
				try { server.EndWaitForConnection(ar); } catch {
					try { server.Dispose(); } catch { /* ignore */ }
					continue;
				}
				if (token.IsCancellationRequested) {
					try { server.Dispose(); } catch { /* ignore */ }
					return;
				}
				var paths = new List<string>();
				using (var r = new StreamReader(server, Encoding.UTF8)) {
					string line;
					while ((line = r.ReadLine()) != null) {
						if (line.Length == 0) break;
						paths.Add(line);
					}
				}
				try { server.Dispose(); } catch { /* ignore */ }
				server = null;
				var handler = onOpen;
				if (handler != null) {
					try { handler(paths.ToArray()); } catch (Exception ex) {
						try { DocLog.Error("SingleInstance onOpen", ex); } catch { /* ignore */ }
					}
				}
			} catch (Exception ex) {
				if (token.IsCancellationRequested) break;
				try { DocLog.Warn($"SingleInstance.listen: {ex.Message}"); } catch { /* ignore */ }
				try { Thread.Sleep(100); } catch { /* ignore */ }
			} finally {
				try { server?.Dispose(); } catch { /* ignore */ }
			}
		}
	}

	static string[] filterpaths(string[] args) {
		if (args == null || args.Length == 0) return Array.Empty<string>();
		var list = new List<string>();
		foreach (var a in args) {
			if (string.IsNullOrWhiteSpace(a)) continue;
			var t = a.Trim().Trim('"');
			if (t.Length == 0) continue;
			if (t.StartsWith("-") || t.StartsWith("/")) continue;
			list.Add(t);
		}
		return list.ToArray();
	}
}
