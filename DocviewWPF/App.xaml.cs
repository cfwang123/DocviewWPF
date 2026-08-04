using System;
using System.Threading.Tasks;
using System.Windows;

namespace DocviewWPF;

public partial class App : Application {
	protected override void OnStartup(StartupEventArgs e) {
		// 更新替换模式：不抢单实例、不进主窗
		if (AppUpdater.IsApplyUpdateArg(e.Args)) {
			try {
				var code = AppUpdater.RunApplyUpdate(e.Args);
				Shutdown(code);
			} catch (Exception ex) {
				try { Console.Error.WriteLine(ex); } catch { /* ignore */ }
				try {
					MessageBox.Show(ex.ToString(), "DocviewWPF 更新失败",
						MessageBoxButton.OK, MessageBoxImage.Error);
				} catch { /* ignore */ }
				Shutdown(1);
			}
			return;
		}

		// 命令行自检：不进 UI，验证 TXT/MD 真实入口后退出
		if (SelfTest.IsSelfTestArg(e.Args)) {
			try {
				int n;
				if (SelfTest.IsTyporaClickArg(e.Args))
					n = SelfTest.RunTyporaClickPerf(Console.Out);
				else
					n = SelfTest.RunMd(Console.Out);
				Shutdown(n == 0 ? 0 : 1);
			} catch (Exception ex) {
				try { Console.Error.WriteLine(ex); } catch { /* ignore */ }
				Shutdown(1);
			}
			return;
		}

		// 彩色 emoji：Win11 风格旗帜等
		try {
			Emoji.Wpf.EmojiData.EnableWindowsStyleFlags = true;
		} catch { /* ignore */ }

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
		// 不再用互斥丢弃连续报错：ErrorWindow 同窗追加滚动
		try {
			ErrorWindow.Report(ex, context);
		} catch {
			try {
				var body = ex.ToString();
				if (Current?.Dispatcher != null && !Current.Dispatcher.CheckAccess())
					Current.Dispatcher.Invoke(() => MessageBox.Show(body, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Error));
				else
					MessageBox.Show(body, "DocviewWPF", MessageBoxButton.OK, MessageBoxImage.Error);
			} catch { /* ignore */ }
		}
	}
}
