using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 文字缓存的一个区服档位。一服一语：Region 喂 --regions，Locale 喂 --locales，
/// 两者由同一个勾选一起推导；Label 用玩家叫法，界面上只说区服、不再单列语言。
/// </summary>
public partial class CrawlServerOption : ObservableObject
{
    public CrawlServerOption(string region, string locale, string label)
    {
        Region = region;
        Locale = locale;
        Label = label;
    }

    public string Region { get; }

    public string Locale { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => Label;
}

/// <summary>--depth 的一个取值。ToString 决定 ComboBox 里显示什么，Value 才是喂给 CLI 的。</summary>
public sealed record CrawlDepthOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 同步页面：把 sekaisync CLI 的同步 / 公告 / 状态 / 爬虫命令图形化。
///
/// 状态与输出不再由本 VM 持有：进程生命周期、输出缓冲与结语文案都在
/// <see cref="TaskBus"/>（手册 §5.3 的单一状态机；docs/ui-architecture.md §5.2 推荐做法）。
/// 本页只是总线的一个视图 + 页面独有的前置清单与门控（TOS / 锁 / 唯一子进程槽位）。
/// </summary>
public partial class SyncViewModel : ObservableObject
{
    /// <summary>本页发起任务时在控制台与提示里用的名字。</summary>
    public const string OriginName = "同步页";

    /// <summary>
    /// 文字缓存的区服取值：勾中的区服码。一服一语，所以 Sekai Viewer 侧的 --regions
    /// 与 Moesekai 侧的 --locales 由同一组勾选一起推导，不会各说一套。
    /// </summary>
    public string CrawlRegions => JoinSelected(o => o.Region);

    public const int CrawlLimit = 20;

    /// <summary>CLI 的 --depth 只接受 1–4，含义照抄上游 help，不给超出取值的选项。</summary>
    public IReadOnlyList<CrawlDepthOption> CrawlDepthOptions { get; } =
    [
        new(1, "1 · 主线+活动"),
        new(2, "2 · 含卡牌"),
        new(3, "3 · 含虚拟 Live/主页语音"),
        new(4, "4 · 全部文本"),
    ];

    /// <summary>
    /// 五个区服档位。码与叫法都取自 <see cref="SourceModel.Regions"/>，和剧情 / 台词页是同一套口径。
    /// 默认五服全勾：上游只默认 zh-cn，但那会让文字缓存看上去只覆盖中日两服，与本页「同步五服」的承诺不符。
    /// </summary>
    public IReadOnlyList<CrawlServerOption> CrawlServerOptions { get; } =
        [.. SourceModel.Regions.Select(r => new CrawlServerOption(r.Region, r.MsSegment, r.DisplayName)
        {
            IsSelected = true,
        })];

    public const string SyncAllArguments = "sync --regions jp,en,cn,tc,kr";
    public const string NewsSyncArguments = "news sync";
    public const string NewsCacheArguments = "news sync --with-bodies";
    public const string StatusArguments = "--no-event-check status";
    public const string ProgressArguments = "--no-event-check progress";
    public const string IntegrityArguments = "--no-event-check integrity";

    private readonly AppEnvironment _environment = AppServices.Environment;
    private readonly TaskBus _bus = TaskBus.Shared;

    public static SyncViewModel Shared { get; } = new();

    /// <summary>页面直接绑它：IsRunning / StatusText / StatusTone / OutputText / CurrentLabel。</summary>
    public TaskBus Bus => _bus;

    /// <summary>条款确认。默认 false、不持久化，每次启动回到未勾选；爬虫按钮由它门控。</summary>
    [ObservableProperty]
    private bool _tosAccepted;

    /// <summary>锁的真实状态（探针结果，不是「文件存在 = 有人在写」）。</summary>
    [ObservableProperty]
    private CrawlLockState _lockState = CrawlLockState.Absent;

    /// <summary>缓存深度。默认 4（全部文本），沿用改成可选之前的固定值；在构造函数里取，字段初始化器碰不到实例属性。</summary>
    [ObservableProperty]
    private CrawlDepthOption _selectedCrawlDepth = null!;

    public int CrawlDepth => SelectedCrawlDepth.Value;

    public string CrawlLocales => JoinSelected(o => o.Locale);

    private string JoinSelected(Func<CrawlServerOption, string> pick)
        => string.Join(',', CrawlServerOptions.Where(o => o.IsSelected).Select(pick));

    /// <summary>有勾中区服才放行。空勾选时 --regions / --locales 都是空串，CLI 会白跑一趟。</summary>
    public bool HasCrawlServers => CrawlServerOptions.Any(o => o.IsSelected);

    /// <summary>
    /// 范围摘要。按钮提示、读屏帮助与预检清单共用这一份，避免各处写死互相打脸。
    /// 一服一语，所以只报覆盖几个区服；具体语言码留在「实际命令行」那一行。
    /// </summary>
    public string CrawlScopeSummary
    {
        get
        {
            var picked = CrawlServerOptions.Count(o => o.IsSelected);
            var scope = picked == 0
                ? "区服未选"
                : $"覆盖 {picked} 个区服（{CrawlRegions}）";
            return $"{scope} · 深度 {CrawlDepth} · 每类 {CrawlLimit} 页";
        }
    }

    public string CrawlArguments =>
        $"crawl --depth {CrawlDepth} --limit {CrawlLimit} --regions {CrawlRegions} --locales {CrawlLocales} --accept-tos";

    public string CrawlButtonLabel => "配置文字缓存…";

    public string CrawlTosLabel => "我确认已阅读并同意以上缓存使用声明";

    public string CrawlConsentText =>
        "文本材料仅供学习交流，严禁用于商业用途。请确保自己的使用行为不与《世界计划 缤纷舞台！feat. 初音未来》有关方面的权益相抵触。\n\n" +
        "由于用户选择在 SekaiSync 使用的信息源政策可能各有差异，SekaiSync 无法判别用户行为是否符合信息源站点的有关用户协议。使用缓存功能意味着用户承诺遵循各关联方规则，且不使用本功能从事违反用户协议和各国法律法规的行为。";

    /// <summary>取消确认对话框的后果说明。</summary>
    public string CancelEffectShortText =>
        "取消会直接结束正在运行的进程，当前这一条可能没写完。重跑会接着上次继续，不用从头再来。";

    public SyncViewModel()
    {
        _selectedCrawlDepth = CrawlDepthOptions[3];
        // 只关心生命周期与结语字段：输出行（OutputText）每行都在变，别跟着全量重算。
        _bus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TaskBus.IsRunning)
                or nameof(TaskBus.StatusTone)
                or nameof(TaskBus.CurrentLabel)
                or nameof(TaskBus.CurrentOrigin)
                or nameof(TaskBus.CancelRequested))
            {
                MirrorBus();
            }
        };
        AppServices.StoreInvalidated += (_, _) => AppServices.EnqueueUi(RefreshLockState);
        // 区服是勾选项，不是一次性赋值：改勾就要重算范围摘要与门控，否则文案会和实际命令行脱节。
        foreach (var server in CrawlServerOptions)
        {
            server.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CrawlServerOption.IsSelected))
                {
                    NotifyCrawlScope();
                }
            };
        }
        RefreshLockState();
    }

    partial void OnSelectedCrawlDepthChanged(CrawlDepthOption value) => NotifyCrawlScope();

    /// <summary>范围变了要一起通知的字段：命令串、摘要、条款说明、预检清单、门控与禁用原因。</summary>
    private void NotifyCrawlScope()
    {
        OnPropertyChanged(nameof(CrawlRegions));
        OnPropertyChanged(nameof(CrawlLocales));
        OnPropertyChanged(nameof(HasCrawlServers));
        OnPropertyChanged(nameof(CrawlDepth));
        OnPropertyChanged(nameof(CrawlScopeSummary));
        OnPropertyChanged(nameof(CrawlArguments));
        OnPropertyChanged(nameof(PreflightScopeRow));
        OnPropertyChanged(nameof(PreflightCommandsRow));
        OnPropertyChanged(nameof(DisabledReasonText));
    }

    // ── 门控（S-1/S-4/S-8：三态都要能解释为什么不给点） ─────────────────────

    public bool IsBusy => _bus.IsRunning;

    public Microsoft.UI.Xaml.Visibility IsBusyVisibility =>
        IsBusy ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>只读巡检类命令：只要子进程槽位空着就能跑。</summary>
    public bool CanRun => !_bus.IsRunning;

    /// <summary>会写库的同步类命令：锁被持有时让路（docs/ui-architecture.md §3.1）。</summary>
    public bool CanSync => !_bus.IsRunning && !LockHeld;

    public bool CanCancel => _bus.CanCancel;

    /// <summary>禁用原因（非 tooltip 的常驻文字，§4.2）。</summary>
    public string DisabledReasonText
    {
        get
        {
            if (_bus.IsRunning)
            {
                return $"{_bus.CurrentOrigin}的任务运行中，结束后可启动新任务。";
            }
            if (LockHeld)
            {
                return "按钮暂不可用：有进程正持有 store/crawl.lock，同步与文字缓存要让路。只读巡检（状态 / 完整度 / 完整性）仍可运行。";
            }
            if (!TosAccepted)
            {
                return "文字缓存暂不可用：还没在缓存设置里确认使用声明。未确认时本程序不会传 --accept-tos。";
            }
            if (!HasCrawlServers)
            {
                return "文字缓存暂不可用：一个区服都没勾。范围为空时没有可缓存的正文，请先在缓存设置里至少勾一个区服。";
            }
            return string.Empty;
        }
    }

    // ── 锁（S-3：文件存在不等于有会话在写） ────────────────────────────────

    public bool ShowLockNotice => LockState != CrawlLockState.Absent;

    public bool LockHeld => LockState == CrawlLockState.Held;

    public string LockNoticeTitle => LockState switch
    {
        CrawlLockState.Held => "store/crawl.lock 正被进程持有",
        CrawlLockState.Indeterminate => "无法判断 store/crawl.lock 是否被占用",
        CrawlLockState.Free => "store/crawl.lock 文件存在",
        _ => string.Empty,
    };

    public string LockNoticeMessage => LockState switch
    {
        CrawlLockState.Held =>
            "同步与文字缓存暂不可用，请等待持锁进程结束后重试。状态、完整度与完整性仍可查看。",
        CrawlLockState.Indeterminate =>
            "锁状态：无法确认 crawl.lock 是否被占用；如遇锁冲突，请等待持锁进程结束后重试。",
        CrawlLockState.Free =>
            "锁状态：crawl.lock 文件存在，当前未被占用。",
        _ => "锁状态：未发现 crawl.lock 文件。",
    };

    /// <summary>重新探测锁。页面 Loaded 与停留期间的定时器调用它，别在 UI 线程外调用。</summary>
    public void RefreshLockState()
    {
        try
        {
            LockState = _environment.ProbeCrawlLock();
        }
        catch (Exception)
        {
            LockState = CrawlLockState.Indeterminate;
        }
        RefreshPreflight();
    }

    // ── 前置清单（S-5 / S-6 / S-9 / S-10：手册 §5.3 七项 + 真实命令行） ──────
    //
    // 折叠面板分两层：外层只回答「做什么 / 存到哪 / 要不要联网 / 能不能取消」这类人话，
    // 参数名、表名、事件检测与完整命令行收进内层「技术细节」。
    // 外层曾经把两者混在一段里，读起来像 CLI 手册而不是给用户看的说明。

    public string StorePathText =>
        _environment.StorePath.Length == 0 ? "未确定：设置里没有 store 覆盖路径，也没探测到仓库根" : _environment.StorePath;

    public string PreflightWhatRow =>
        "做什么：同步五服 Master、同步五服官方公告、缓存已同步公告的正文，以及按你勾选的区服缓存文字材料。";

    public string PreflightWhereRow =>
        $"存到哪里：{StorePathText}。文字材料只存文本，不下载图片、音频、Live2D 和视频。";

    public string PreflightNetworkRow =>
        "要联网吗：要。四类任务都会访问 Sekai Viewer 与 Moesekai 两个数据源站点。";

    public string PreflightImpactRow =>
        "会影响已有数据吗：只在这个目录里新增和更新，不删库，也不碰本机其它位置。";

    public string PreflightCancelRow =>
        "中途取消：直接结束进程，正在写的那一条可能没写完；重跑会接着上次继续，不用从头再来。";

    public string PreflightConsentRow =>
        "开始之前：文字缓存要在弹窗里读完使用声明并勾选同意，否则不会开始。";

    // 内层：技术细节。

    public string PreflightScopeRow =>
        $"精确范围：Master 与公告固定五服（jp,en,cn,tc,kr）；文字缓存{CrawlScopeSummary}。一服一语，勾区服即定语言。";

    public string PreflightLayerRow =>
        "写入位置：各服 master 表与索引；公告索引 kb/news；文字材料正文层 web_pages。";

    public string PreflightSourceRow =>
        "数据来源实例：Sekai Viewer（altsource_sv）与 Moesekai（altsource_ms）；实际命中哪些实例由 CLI 的 settings 决定。";

    public string PreflightEventCheckRow =>
        "新事件检测：同步与公告两条命令没有带 --no-event-check，CLI 在正式执行前会先自动检测一次新活动，那一步同样会联网并写库。" +
        "「状态 / 完整度 / 完整性」三条带了该参数，跳过这个前置检测。";

    public string PreflightAtomicRow =>
        "续跑与原子性：跳过本地已缓存页面是 CLI 的既有行为，不是本程序实现的断点续传协议；已有条目的更新粒度由 CLI 决定，" +
        "不保证逐条原子，也不承诺跑坏了可以回滚。OS 级写锁随进程退出自动释放。";

    public string PreflightLockRow => LockNoticeMessage;

    public string PreflightCommandsRow =>
        "实际命令行：\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {SyncAllArguments}\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {NewsSyncArguments}\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {NewsCacheArguments}\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {CrawlArguments}\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {StatusArguments}\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {ProgressArguments}\n" +
        $"  python -m sekaisync --store \"{StorePathText}\" {IntegrityArguments}";

    // ── 状态语气（文字 + 颜色同时给，§6 / A-03） ──────────────────────────

    public bool ToneIsNeutral => _bus.StatusTone == TaskTone.Neutral;

    public bool ToneIsWorking => _bus.StatusTone == TaskTone.Working;

    public bool ToneIsSuccess => _bus.StatusTone == TaskTone.Success;

    public bool ToneIsProblem => _bus.StatusTone == TaskTone.Problem;

    public bool ToneIsCancelled => _bus.StatusTone == TaskTone.Cancelled;

    partial void OnTosAcceptedChanged(bool value) => MirrorGating();

    partial void OnLockStateChanged(CrawlLockState value) => MirrorGating();

    /// <summary>取消（S-1 + S-6）：先说清「正在请求停止」与在途写入会发生什么，再请求。</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!_bus.IsRunning)
        {
            _bus.Note("> 当前没有正在运行的任务，取消不需要执行。");
            return;
        }
        _bus.Note(
            $"> 取消影响：{_bus.CurrentLabel} 的子进程会被直接终止，正在写入的那一条不保证完成，本程序不提供检查点。" +
            "OS 级写锁随进程退出释放；文字缓存默认沿用本地已缓存页面，重跑同一条命令会从上次位置继续。");
        _bus.Cancel();
    }

    [RelayCommand]
    private Task SyncAllAsync() => RunAsync("同步五服 Master Data", SyncAllArguments, requiresTos: false, writesStore: true);

    [RelayCommand]
    private Task NewsSyncAsync() => RunAsync("同步官方公告", NewsSyncArguments, requiresTos: false, writesStore: true);

    [RelayCommand]
    private Task NewsCacheAsync() => RunAsync("缓存已同步公告", NewsCacheArguments, requiresTos: false, writesStore: true);

    [RelayCommand]
    private Task CrawlAsync() => RunAsync(
        $"文字缓存（{CrawlScopeSummary}）",
        CrawlArguments,
        requiresTos: true,
        writesStore: true);

    [RelayCommand]
    private Task ShowStatusAsync() => RunAsync("查看状态", StatusArguments, requiresTos: false, writesStore: false);

    [RelayCommand]
    private Task ShowProgressAsync() => RunAsync("查看五服完整度", ProgressArguments, requiresTos: false, writesStore: false);

    [RelayCommand]
    private Task CheckIntegrityAsync() => RunAsync("完整性校验", IntegrityArguments, requiresTos: false, writesStore: false);

    /// <summary>命令入口再判一次门控：按钮 disabled 只是提示，真正的约束在这里。</summary>
    private async Task RunAsync(string label, string arguments, bool requiresTos, bool writesStore)
    {
        RefreshLockState();

        if (_bus.IsRunning)
        {
            _bus.Note($"> [未执行] {label}：已有任务在运行（{_bus.CurrentLabel}，来自 {_bus.CurrentOrigin}）。");
            return;
        }
        if (requiresTos && !TosAccepted)
        {
            _bus.Note($"> [未执行] {label}：必须先勾选条款确认。未勾选时本程序不会传 --accept-tos。");
            return;
        }
        if (writesStore && LockHeld)
        {
            _bus.Note($"> [未执行] {label}：store/crawl.lock 正被进程持有，写库类命令先让路。等持有者结束后重试。");
            return;
        }

        await _bus.RunAsync(label, arguments, OriginName);
        RefreshLockState();
    }

    private void MirrorBus()
    {
        AppServices.EnqueueUi(MirrorGating);
    }

    private void MirrorGating()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsBusyVisibility));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(DisabledReasonText));
        OnPropertyChanged(nameof(LockHeld));
        OnPropertyChanged(nameof(ShowLockNotice));
        OnPropertyChanged(nameof(LockNoticeTitle));
        OnPropertyChanged(nameof(LockNoticeMessage));
        OnPropertyChanged(nameof(ToneIsNeutral));
        OnPropertyChanged(nameof(ToneIsWorking));
        OnPropertyChanged(nameof(ToneIsSuccess));
        OnPropertyChanged(nameof(ToneIsProblem));
        OnPropertyChanged(nameof(ToneIsCancelled));
    }

    /// <summary>store 路径可能因设置变更而变；连同前置清单一并重算。</summary>
    private void RefreshPreflight()
    {
        OnPropertyChanged(nameof(StorePathText));
        OnPropertyChanged(nameof(PreflightWhatRow));
        OnPropertyChanged(nameof(PreflightWhereRow));
        OnPropertyChanged(nameof(PreflightNetworkRow));
        OnPropertyChanged(nameof(PreflightImpactRow));
        OnPropertyChanged(nameof(PreflightCancelRow));
        OnPropertyChanged(nameof(PreflightConsentRow));
        OnPropertyChanged(nameof(PreflightScopeRow));
        OnPropertyChanged(nameof(PreflightLayerRow));
        OnPropertyChanged(nameof(PreflightSourceRow));
        OnPropertyChanged(nameof(PreflightEventCheckRow));
        OnPropertyChanged(nameof(PreflightAtomicRow));
        OnPropertyChanged(nameof(PreflightLockRow));
        OnPropertyChanged(nameof(PreflightCommandsRow));
    }
}
