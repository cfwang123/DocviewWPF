using System;
using System.Threading.Tasks;
using System.Windows;

namespace DocviewWPF;

public partial class App : Application {
	static int showingError;

	protected override void OnStartup(StartupEventArgs e) {
		// 原生库在 x64\ / x86\，须在任何 pdfium/Skia 调用前设置搜索路径
		try { NativeBootstrap.Init(); } catch { /* ignore */ }

		DispatcherUnhandledException += (_, ev) => {
			ev.Handled = true;
			try { DocLog.Error("UI unhandled", ev.Exception); } catch { /* ignore */ }
			ShowError(ev.Exception, "UI");
		};
		AppDomain.CurrentDomain.UnhandledException += (_, ev) => {
			if (ev.ExceptionObject is Exception ex) {
				try { DocLog.Error("Domain unhandled", ex); } catch { /* ignore */ }
				ShowError(ex, "Domain");
			}
		};
		TaskScheduler.UnobservedTaskException += (_, ev) => {
			ev.SetObserved();
			var ex = ev.Exception?.InnerException ?? ev.Exception;
			try { DocLog.Error("Task unhandled", ex); } catch { /* ignore */ }
			ShowError(ex, "Task");
		};

		// 单实例：已有进程则转发路径并退出
		if (!SingleInstance.TryAcquire()) {
			try {
				SingleInstance.SendOpen(e.Args);
			} catch (Exception ex) {
				try { DocLog.Warn($"handoff: {ex.Message}"); } catch { /* ignore */ }
			}
			Shutdown();
			return;
		}

		base.OnStartup(e);
		// 资源就绪后应用已保存主题与语言
		try {
			AppSettings.Load();
			Loc.Init(AppSettings.Current.Language);
			ThemeService.ApplyFromSettings();
		} catch { /* ignore */ }

		Exit += (_, _) => {
			try { SingleInstance.Release(); } catch { /* ignore */ }
		};

		// 管道回调在后台线程，切回 UI 打开文件
		SingleInstance.StartListening(paths => {
			try {
				Dispatcher.BeginInvoke(new Action(() => {
					try { global::DocviewWPF.MainWindow.HandleExternalOpen(paths); } catch (Exception ex) {
						DocLog.Error("HandleExternalOpen", ex);
					}
				}));
			} catch (Exception ex) {
				try { DocLog.Error("SingleInstance dispatch", ex); } catch { /* ignore */ }
			}
		});

		// 不用 StartupUri，主实例在此创建窗口
		var main = new MainWindow();
		MainWindow = main;
		main.Show();
	}

	public static void ShowError(Exception ex, string context = null) {
		if (ex == null) return;
		if (System.Threading.Interlocked.CompareExchange(ref showingError, 1, 0) != 0)
			return;
		try {
			var title = string.IsNullOrEmpty(context)
				? $"DocviewWPF · {ex.GetType().Name}"
				: $"DocviewWPF · {ex.GetType().Name} @ {context}";
			var body = ex.Message;
			var inner = ex.InnerException;
			while (inner != null) {
				body += "\n→ " + inner.Message;
				inner = inner.InnerException;
			}
			if (Current?.Dispatcher != null && !Current.Dispatcher.CheckAccess())
				Current.Dispatcher.Invoke(() => MessageBox.Show(body, title, MessageBoxButton.OK, MessageBoxImage.Error));
			else
				MessageBox.Show(body, title, MessageBoxButton.OK, MessageBoxImage.Error);
		} catch {
			// ignore
		} finally {
			System.Threading.Interlocked.Exchange(ref showingError, 0);
		}
	}
}
