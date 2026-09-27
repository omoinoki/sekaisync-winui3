using System;
using System.Collections.Generic;

namespace SekaiSync.Desktop.Services;

/// <summary>应用级服务定位器（单例组合根）。</summary>
public static class AppServices
{
    public static AppSettings Settings { get; } = AppSettings.Load();

    public static AppEnvironment Environment { get; } = new(Settings);

    public static DatabaseService Database { get; } = new(Environment);

    public static ProcessLauncherService Launcher { get; } = new(Environment);

    // ── 新增：按任务域分的只读查询层 ──
    public static ContentQueryService Content { get; } = new(Environment);

    public static TermQueryService Terms { get; } = new(Environment);

    public static CatalogQueryService Catalog { get; } = new(Environment);

    public static ProgressService Progress { get; } = new(Environment);

    public static NewsService News { get; } = new(Environment);

    static AppServices()
    {
        // 实例显示名与主站域名跟随主项目 settings.json 的 sites[]；
        // 静态初始化完成后、任何页面渲染前应用一次。
        SourceModel.ApplySiteProfile(Environment);
    }

    /// <summary>跨页面共享的最近一次数据库刷新结果通知。</summary>
    public static event EventHandler? StoreInvalidated;

    private static readonly List<Action> Subscribers = [];

    /// <summary>注册一个「store 可能变了」的回调（避免事件被 GC，静态持有）。</summary>
    public static void OnStoreInvalidated(Action action) => Subscribers.Add(action);

    public static void RaiseStoreInvalidated()
    {
        StoreInvalidated?.Invoke(typeof(AppServices), EventArgs.Empty);
        foreach (var subscriber in Subscribers.ToArray())
        {
            try
            {
                subscriber();
            }
            catch (Exception ex)
            {
                App.Log($"AppServices: StoreInvalidated 回调失败：{ex.Message}");
            }
        }
    }

    /// <summary>启动时可从外部注入的窗口 Dispatcher 引用（用于子进程输出回到 UI 线程）。</summary>
    public static Func<Action, bool>? UiDispatcher { get; set; }

    public static void EnqueueUi(Action action)
    {
        var dispatch = UiDispatcher;
        if (dispatch is null)
        {
            action();
        }
        else
        {
            dispatch(action);
        }
    }
}
