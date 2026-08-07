using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DocviewWPF;

/// <summary>
/// Windows ConPTY（对齐官方 MiniTerm / EchoCon 样本）。
/// </summary>
sealed class ConPtySession : IDisposable {
	const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
	const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
	const int WRITE_QUEUE_CAP = 512;

	IntPtr hPC = IntPtr.Zero;
	IntPtr hProcess = IntPtr.Zero;
	IntPtr hThread = IntPtr.Zero;
	IntPtr hPipeWrite = IntPtr.Zero; // 我们 → ConPTY
	IntPtr hPipeRead = IntPtr.Zero;  // ConPTY → 我们
	IntPtr attrList = IntPtr.Zero;
	CancellationTokenSource loopCts;
	readonly ConcurrentQueue<byte[]> writeQ = new();
	readonly AutoResetEvent writeSignal = new(false);
	readonly object writeLock = new();
	Thread writeThread;
	Thread readThread;
	volatile bool disposed;
	volatile bool writeDone;
	int cols;
	int rows;
	int writeQueuedBytes;
	long bytesRead;
	long bytesWritten;
	int readChunks;
	string lastWriteErr;
	string lastReadErr;

	public int ProcessId { get; private set; }
	public bool HasExited { get; private set; }
	public long BytesRead => Interlocked.Read(ref bytesRead);
	public long BytesWritten => Interlocked.Read(ref bytesWritten);
	public int ReadChunks => Volatile.Read(ref readChunks);
	public string LastWriteError => lastWriteErr;
	public string LastReadError => lastReadErr;
	public event Action Exited;
	public event Action<byte[]> DataReceived;
	public int Cols => cols;
	public int Rows => rows;

	public static bool IsSupported {
		get {
			try {
				var m = GetModuleHandleW("kernel32.dll");
				if (m == IntPtr.Zero) return false;
				return GetProcAddress(m, "CreatePseudoConsole") != IntPtr.Zero;
			} catch { return false; }
		}
	}

	public void Start(string fileName, string arguments, string workingDirectory, int cols, int rows) {
		if (disposed) throw new ObjectDisposedException(nameof(ConPtySession));
		if (string.IsNullOrWhiteSpace(fileName))
			throw new ArgumentException("fileName");
		this.cols = Math.Max(20, Math.Min(300, cols));
		this.rows = Math.Max(5, Math.Min(120, rows));

		// 若宿主挂了控制台（从终端启动 WinExe），先脱离，避免子 cmd 抢写父控制台
		try { FreeConsole(); } catch { /* ignore */ }

		// EchoCon 管道布局
		if (!CreatePipe(out var hPipePTYIn, out hPipeWrite, IntPtr.Zero, 0))
			throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe A");
		if (!CreatePipe(out hPipeRead, out var hPipePTYOut, IntPtr.Zero, 0))
			throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe B");

		var size = new Coord((short)this.cols, (short)this.rows);
		var hr = CreatePseudoConsole(size, hPipePTYIn, hPipePTYOut, 0, out hPC);
		CloseHandle(hPipePTYIn);
		CloseHandle(hPipePTYOut);
		if (hr != 0) {
			safeclose(ref hPipeWrite);
			safeclose(ref hPipeRead);
			throw new Win32Exception(hr, "CreatePseudoConsole 0x" + hr.ToString("X8"));
		}

		// MiniTerm: ConfigureProcessThread
		var lpSize = IntPtr.Zero;
		var okInit = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
		if (okInit || lpSize == IntPtr.Zero)
			throw new Win32Exception(Marshal.GetLastWin32Error(), "attr size");
		attrList = Marshal.AllocHGlobal(lpSize);
		if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize))
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Init attr list");
		if (!UpdateProcThreadAttribute(attrList, 0,
				(IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
				hPC, (IntPtr)IntPtr.Size,
				IntPtr.Zero, IntPtr.Zero))
			throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

		var siEx = new StartupInfoEx();
		// 与 MiniTerm 一致：cb = sizeof(STARTUPINFOEX)，string 字段 + CharSet.Unicode
		siEx.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
		siEx.lpAttributeList = attrList;

		var exe = resolvexe(fileName);
		var cmdLine = string.IsNullOrEmpty(arguments) ? quote(exe) : quote(exe) + " " + arguments;
		// CreateProcess 可能改写命令行缓冲
		var cmdBuf = new StringBuilder(cmdLine);

		var pSec = new SecurityAttributes { nLength = Marshal.SizeOf(typeof(SecurityAttributes)) };
		var tSec = new SecurityAttributes { nLength = Marshal.SizeOf(typeof(SecurityAttributes)) };
		var cwd = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;

		var ok = CreateProcessW(
			null,
			cmdBuf,
			ref pSec,
			ref tSec,
			false,
			EXTENDED_STARTUPINFO_PRESENT,
			IntPtr.Zero,
			cwd,
			ref siEx,
			out var pi);
		if (!ok)
			throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess " + cmdLine);

		hProcess = pi.hProcess;
		hThread = pi.hThread;
		ProcessId = pi.dwProcessId;
		// 属性列表可在 CreateProcess 返回后释放
		freeattr();
		CloseHandle(hThread);
		hThread = IntPtr.Zero;

		DocLog.Info($"ConPTY Start ok pid={ProcessId} exe={exe} {this.cols}x{this.rows} cwd={cwd}");

		loopCts = new CancellationTokenSource();
		writeDone = false;
		writeThread = new Thread(writeloop) { IsBackground = true, Name = "ConPty-W" };
		readThread = new Thread(readloop) { IsBackground = true, Name = "ConPty-R" };
		writeThread.Start();
		readThread.Start();
		_ = waitexitasync();
	}

	void freeattr() {
		if (attrList == IntPtr.Zero) return;
		try { DeleteProcThreadAttributeList(attrList); } catch { /* ignore */ }
		try { Marshal.FreeHGlobal(attrList); } catch { /* ignore */ }
		attrList = IntPtr.Zero;
	}

	static void safeclose(ref IntPtr h) {
		if (h == IntPtr.Zero) return;
		try { CloseHandle(h); } catch { /* ignore */ }
		h = IntPtr.Zero;
	}

	static string resolvexe(string fileName) {
		if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return fileName;
		try {
			var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
			var cand = Path.Combine(sys, fileName);
			if (File.Exists(cand)) return cand;
			if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
				cand = Path.Combine(sys, fileName + ".exe");
				if (File.Exists(cand)) return cand;
			}
		} catch { /* ignore */ }
		return fileName;
	}

	static string quote(string p) {
		if (string.IsNullOrEmpty(p)) return "\"\"";
		if (p.IndexOf(' ') < 0) return p;
		return "\"" + p + "\"";
	}

	void readloop() {
		var buf = new byte[16384];
		try {
			while (!disposed) {
				int n;
				if (!ReadFile(hPipeRead, buf, buf.Length, out n, IntPtr.Zero) || n <= 0) {
					var err = Marshal.GetLastWin32Error();
					if (err != 0 && err != 109)
						lastReadErr = "ReadFile " + err;
					DocLog.Info("ConPTY read end n=" + n + " err=" + err);
					break;
				}
				Interlocked.Add(ref bytesRead, n);
				Interlocked.Increment(ref readChunks);
				var copy = new byte[n];
				Buffer.BlockCopy(buf, 0, copy, 0, n);
				try { DataReceived?.Invoke(copy); } catch { /* ignore */ }
			}
		} catch (Exception ex) {
			lastReadErr = ex.Message;
			DocLog.Warn("ConPTY readloop: " + ex.Message);
		}
	}

	async Task waitexitasync() {
		try {
			await Task.Run(() => {
				if (hProcess != IntPtr.Zero)
					WaitForSingleObject(hProcess, 0xFFFFFFFF);
			}).ConfigureAwait(false);
		} catch { /* ignore */ }
		HasExited = true;
		try { Exited?.Invoke(); } catch { /* ignore */ }
	}

	public void Write(byte[] data) {
		if (data == null || data.Length == 0 || disposed || writeDone) return;
		if (writeQueuedBytes > 256 * 1024 && data.Length > 64) return;
		var copy = new byte[data.Length];
		Buffer.BlockCopy(data, 0, copy, 0, data.Length);
		writeQ.Enqueue(copy);
		Interlocked.Add(ref writeQueuedBytes, copy.Length);
		while (writeQ.Count > WRITE_QUEUE_CAP && writeQ.TryDequeue(out var drop))
			Interlocked.Add(ref writeQueuedBytes, -drop.Length);
		try { writeSignal.Set(); } catch { /* ignore */ }
	}

	public void Write(string text) {
		if (string.IsNullOrEmpty(text)) return;
		Write(Encoding.UTF8.GetBytes(text));
	}

	public bool WriteSync(byte[] data) {
		if (data == null || data.Length == 0 || disposed || hPipeWrite == IntPtr.Zero) return false;
		try {
			lock (writeLock) {
				if (!WriteFile(hPipeWrite, data, data.Length, out var n, IntPtr.Zero) || n <= 0) {
					lastWriteErr = "WriteFile " + Marshal.GetLastWin32Error();
					return false;
				}
			}
			Interlocked.Add(ref bytesWritten, data.Length);
			return true;
		} catch (Exception ex) {
			lastWriteErr = ex.Message;
			return false;
		}
	}

	void writeloop() {
		try {
			while (!writeDone && !disposed) {
				writeSignal.WaitOne(200);
				while (writeQ.TryDequeue(out var chunk)) {
					Interlocked.Add(ref writeQueuedBytes, -chunk.Length);
					if (disposed || hPipeWrite == IntPtr.Zero) continue;
					try {
						lock (writeLock) {
							if (!WriteFile(hPipeWrite, chunk, chunk.Length, out var n, IntPtr.Zero) || n <= 0) {
								lastWriteErr = "WriteFile " + Marshal.GetLastWin32Error();
								writeDone = true;
								return;
							}
						}
						Interlocked.Add(ref bytesWritten, chunk.Length);
					} catch (Exception ex) {
						lastWriteErr = ex.Message;
						writeDone = true;
						break;
					}
				}
			}
		} catch (Exception ex) {
			lastWriteErr = ex.Message;
		}
	}

	public void Resize(int newCols, int newRows) {
		newCols = Math.Max(20, Math.Min(300, newCols));
		newRows = Math.Max(5, Math.Min(120, newRows));
		if (newCols == cols && newRows == rows) return;
		if (hPC == IntPtr.Zero) { cols = newCols; rows = newRows; return; }
		try {
			var hr = ResizePseudoConsole(hPC, new Coord((short)newCols, (short)newRows));
			if (hr != 0) {
				DocLog.Warn($"ResizePseudoConsole {newCols}x{newRows}: 0x{hr:X8}");
				return;
			}
			cols = newCols;
			rows = newRows;
		} catch (Exception ex) {
			DocLog.Warn("ResizePseudoConsole: " + ex.Message);
		}
	}

	public void Dispose() {
		if (disposed) return;
		disposed = true;
		writeDone = true;
		try { writeSignal.Set(); } catch { /* ignore */ }
		try { loopCts?.Cancel(); } catch { /* ignore */ }
		freeattr();

		if (hPC != IntPtr.Zero) {
			try { ClosePseudoConsole(hPC); } catch { /* ignore */ }
			hPC = IntPtr.Zero;
		}
		safeclose(ref hPipeWrite);
		safeclose(ref hPipeRead);

		var proc = hProcess;
		hProcess = IntPtr.Zero;
		if (proc != IntPtr.Zero) {
			ThreadPool.QueueUserWorkItem(_ => {
				try {
					if (WaitForSingleObject(proc, 200) == 0x00000102)
						try { TerminateProcess(proc, 1); } catch { /* ignore */ }
				} catch { /* ignore */ }
				try { CloseHandle(proc); } catch { /* ignore */ }
			});
		}
		if (hThread != IntPtr.Zero) {
			try { CloseHandle(hThread); } catch { /* ignore */ }
			hThread = IntPtr.Zero;
		}
		try { writeSignal.Dispose(); } catch { /* ignore */ }
	}

	// ---------- P/Invoke：与 MiniTerm 一致 ----------

	[StructLayout(LayoutKind.Sequential)]
	struct Coord {
		public short X, Y;
		public Coord(short x, short y) { X = x; Y = y; }
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct StartupInfo {
		public int cb;
		public string lpReserved;
		public string lpDesktop;
		public string lpTitle;
		public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
		public short wShowWindow, cbReserved2;
		public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct StartupInfoEx {
		public StartupInfo StartupInfo;
		public IntPtr lpAttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct ProcessInformation {
		public IntPtr hProcess, hThread;
		public int dwProcessId, dwThreadId;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct SecurityAttributes {
		public int nLength;
		public IntPtr lpSecurityDescriptor;
		public int bInheritHandle;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool ReadFile(IntPtr hFile, byte[] buffer, int toRead, out int read, IntPtr ov);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool WriteFile(IntPtr hFile, byte[] buffer, int toWrite, out int written, IntPtr ov);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr phPC);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern void ClosePseudoConsole(IntPtr hPC);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value,
		IntPtr size, IntPtr prev, IntPtr retSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool DeleteProcThreadAttributeList(IntPtr list);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool CreateProcessW(
		string appName,
		StringBuilder commandLine,
		ref SecurityAttributes procAttr,
		ref SecurityAttributes threadAttr,
		bool inheritHandles,
		uint creationFlags,
		IntPtr env,
		string currentDir,
		[In] ref StartupInfoEx startupInfo,
		out ProcessInformation processInfo);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern uint WaitForSingleObject(IntPtr h, uint ms);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool TerminateProcess(IntPtr h, uint code);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern IntPtr GetModuleHandleW(string name);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
	static extern IntPtr GetProcAddress(IntPtr mod, string name);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool FreeConsole();
}
