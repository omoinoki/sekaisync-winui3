using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop;

/// <summary>应用入口。</summary>
public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// 单实例键。应用驻留托盘后，双击 exe 会再起一个进程——两枚托盘图标、
    /// 两个都往同一个 store 下写的子进程竞争者，所以再起时必须让位。
    /// </summary>
    private const string SingleInstanceKey = "sekaisync-desktop-v1";

    private AppInstance? _instance;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"AppDomain.UnhandledException terminating={e.IsTerminating}: {e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        try
        {
            _instance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        }
        catch (Exception ex)
        {
            // 拿不到单实例能力时照常启动，不能因此开不了应用。
            Log($"AppInstance.FindOrRegisterForKey 失败，本次不做单实例约束：{ex.Message}");
        }
    }

    /// <summary>简易文件诊断日志（%TEMP%\sekaisync_desktop.log）。</summary>
    public static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sekaisync_desktop.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch
        {
            // 诊断日志失败不影响运行。
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched");

        if (_instance is { IsCurrent: false })
        {
            // 已有实例驻留在托盘：把这次激活转交给它（它会窗口唤回），本进程立刻退场。
            try
            {
                _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                    .AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log($"第二实例重定向失败：{ex.Message}");
            }
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return;
        }

        if (_instance is not null)
        {
            _instance.Activated += (_, _) => MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow.ShowFromTray());
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"UI UnhandledException handled={e.Handled}: {e.Message}\n{e.Exception}");
        e.Handled = true;
        MainWindow?.ShowCrashInfo(e.Message);
    }
}
