using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 常驻底部状态条。同步率的数值来自 sekaisync progress 写出的 progress.json，
/// 这里只负责展示与触发重算。
///
/// 刻意不做「快速同步」按钮：爬虫受 store/crawl.lock 与 TOS 双重约束，
/// 塞进随手可点的状态栏会绕过同步页的勾选门控。
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly ProgressService _progress = AppServices.Progress;
    private readonly TaskBus _bus = TaskBus.Shared;
    private readonly AppEnvironment _environment = AppServices.Environment;

    public static ShellViewModel Shared { get; } = new();

    [ObservableProperty]
    private ProgressSnapshot _snapshot = ProgressSnapshot.Empty;

    [ObservableProperty]
    private bool _isDetailOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyLabel = string.Empty;

    /// <summary>
    /// 任务输出停靠区是否可见。输出本来就是全应用共享的一份（<see cref="TaskBus"/>），
    /// 原先同步页与智能体接入页各画一个控制台，切页时面板重画、分隔线位置也跳；
    /// 现在收进 shell 一处，只在这两页钉住。
    /// </summary>
    [ObservableProperty]
    private bool _isConsoleVisible;

    /// <summary>停靠区直接绑总线的缓冲，不再经页面 VM 中转。</summary>
    public TaskBus Bus => _bus;

    public string ConsoleScopeRow => "任务输出";

    public string LocalLogPathRow =>
        LogExport.HasLocalDiagnostics
            ? $"本机日志（未脱敏）：{LogExport.LocalDiagnosticsPath}"
            : $"本机日志：{LogExport.LocalDiagnosticsPath}（尚未生成）";

    /// <summary>空控制台的占位文字；本机日志路径收在这里，不再单独占一行。</summary>
    public string ConsolePlaceholder => $"暂无输出。运行任务后，日志会显示在这里。\n{LocalLogPathRow}";

    /// <summary>两页共用一块停靠区，所以措辞要同时容得下任务输出与服务日志。</summary>
    public string ClearOutputConfirmText =>
        "控制台里是全应用共享的一份输出，清空后无法找回（除非先复制或另存）。要现在清空吗？";

    /// <summary>清空输出：调用方已弹过二次确认（这是唯一的失败证据，不给撤销）。</summary>
    public void ClearOutput()
    {
        _bus.ClearOutput();
        _bus.Note("> 已清空当前视图。原始崩溃日志仍在上面那行本机日志文件里，本程序不会自动删除它。");
    }

    [RelayCommand]
    private Task CopyLogAsync()
    {
        _bus.Note($"> {LogExport.CopyToClipboard(_bus.RedactedLogText)}");
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        var note = await LogExport.SaveAsync(_bus.RedactedLogText, $"sekaisync-output-{DateTime.Now:yyyyMMdd-HHmmss}");
        _bus.Note($"> {note}");
    }

    public ShellViewModel()
    {
        _bus.PropertyChanged += OnBusPropertyChanged;
        _bus.Completed += (_, _) => Reload();
        AppServices.StoreInvalidated += (_, _) => Reload();
        Reload();
    }

    public double ProgressValue => Snapshot.OverallPct;

    public double ProgressMaximum => 100;

    /// <summary>左侧锚点标签始终是同步率本身；运行中的命令名另走 <see cref="BusyText"/>，
    /// 否则任务一跑起来，用户就看不到「还差多少」这个常驻信息了。</summary>
    public string BarLabel => Snapshot.BarLabel;

    /// <summary>状态条左标签：数值 + 档位文字。颜色之外必须有文字（手册 A-03）。</summary>
    public string BarLabelWithBand => Snapshot.HasData
        ? $"{Snapshot.BarLabel} · {BandText}"
        : Snapshot.BarLabel;

    public string DetailLabel => IsBusy ? "正在运行…" : Snapshot.StatusText;

    public string GeneratedLabel => Snapshot.GeneratedLabel;

    /// <summary>
    /// 源站确认不提供、已从分母移出的单元说明。无此类条目时为空，UI 据此隐藏。
    /// </summary>
    public string SourceUnavailableLabel => Snapshot.SourceUnavailableLabel;

    public bool HasSourceUnavailable => Snapshot.SourceUnavailable > 0;

    public IReadOnlyList<RegionProgress> Regions => Snapshot.Regions;

    public IReadOnlyList<CategoryGap> Gaps => Snapshot.Gaps;

    public bool HasData => Snapshot.HasData;

    /// <summary>
    /// 顶层常驻上下文：库位置与只读标识（手册 §5.1）。原先「有没有本地库」这件事
    /// 在界面上没有任何地方说，`AppEnvironment.DatabaseExists` 全仓零消费者，
    /// 于是「未建库」「空结果」「读取失败」三种状态都长成一个样。
    /// </summary>
    public bool IsStoreMissing => !_environment.DatabaseExists;

    public string StoreContextText => _environment.DatabaseExists
        ? $"本地库（只读阅览）：{_environment.DatabasePath}"
        : "本地库：未选择或路径下没有 kb/sekaisync.db";

    /// <summary>§6 空态文案契约：事实 + 影响 + 下一步。</summary>
    public string StoreMissingText
        => "尚未选择本地数据库。请选择已有库，或查看初始化步骤。";

    /// <summary>锁状态文字。文件存在不等于有人在写——上游只解锁不删文件。</summary>
    public string CrawlLockText => _environment.ProbeCrawlLock() switch
    {
        CrawlLockState.Held => "检测到有进程正持有 store/crawl.lock：爬虫可能正在写入。等它结束后再操作，本程序不会替你删锁或结束别人的会话。",
        CrawlLockState.Indeterminate => "无法判断 crawl.lock 是否被占用（读取该文件受限）。",
        _ => string.Empty,
    };

    public bool HasCrawlLockNotice => CrawlLockText.Length > 0;

    public string GapsHeader => Gaps.Count == 0
        ? "所有类目均已 100%"
        : $"低于 100% 的类目（{Gaps.Count}）";

    /// <summary>
    /// 分档只决定「用哪一档」，颜色由 XAML 侧的 {ThemeResource} 决定。
    /// 原先在这里 new SolidColorBrush(固定 RGB)，深色主题与高对比下会失配，
    /// 且主题切换不会重算——WinUI 3 没有 DataTrigger，改用档位布尔 + 可见性切换。
    /// </summary>
    public bool IsBandHigh => Snapshot.HasData && Snapshot.OverallPct >= 90;

    public bool IsBandMid => Snapshot.HasData && Snapshot.OverallPct >= 70 && Snapshot.OverallPct < 90;

    public bool IsBandLow => Snapshot.HasData && Snapshot.OverallPct < 70;

    /// <summary>progress.json 与 freshness.json 都没有：不给百分比，也不画 0 的条。</summary>
    public bool IsUnrated => !Snapshot.HasData;

    /// <summary>档位的文字表达。状态不能只靠颜色（手册 §6 / A-03）。</summary>
    public string BandText => !Snapshot.HasData
        ? "未评估"
        : Snapshot.OverallPct switch
        {
            >= 90 => "接近完整",
            >= 70 => "仍有缺口",
            _ => "缺口明显",
        };

    /// <summary>进度条的可访问名称：读屏下条形本身不携带数值。</summary>
    public string ProgressBarName => !Snapshot.HasData
        ? "同步率尚未评估"
        : $"同步率 {Snapshot.OverallPct}%（{BandText}）";

    /// <summary>运行中在状态条上单独占一行文字，不顶替同步率数值。</summary>
    public string BusyText => IsBusy && BusyLabel.Length > 0 ? $"正在运行：{BusyLabel}" : string.Empty;

    public bool CanRun => !IsBusy;

    partial void OnSnapshotChanged(ProgressSnapshot value)
    {
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(BarLabel));
        OnPropertyChanged(nameof(BarLabelWithBand));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(GeneratedLabel));
        OnPropertyChanged(nameof(SourceUnavailableLabel));
        OnPropertyChanged(nameof(HasSourceUnavailable));
        OnPropertyChanged(nameof(Regions));
        OnPropertyChanged(nameof(Gaps));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(GapsHeader));
        OnPropertyChanged(nameof(IsBandHigh));
        OnPropertyChanged(nameof(IsBandMid));
        OnPropertyChanged(nameof(IsBandLow));
        OnPropertyChanged(nameof(IsUnrated));
        OnPropertyChanged(nameof(BandText));
        OnPropertyChanged(nameof(ProgressBarName));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(BarLabel));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(BusyText));
    }

    partial void OnBusyLabelChanged(string value)
    {
        OnPropertyChanged(nameof(BarLabel));
    }

    partial void OnIsDetailOpenChanged(bool value)
    {
        if (!value)
        {
            return;
        }
        Reload(force: true);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshProgressAsync()
    {
        if (IsBusy)
        {
            return;
        }
        await _bus.RunProgressAsync();
        Reload(force: true);
    }

    private void Reload(bool force = false)
    {
        if (force)
        {
            _progress.Invalidate();
        }
        Snapshot = _progress.Load();

        // 库路径与锁状态不在 Snapshot 里，重算时一并通知。
        OnPropertyChanged(nameof(IsStoreMissing));
        OnPropertyChanged(nameof(StoreContextText));
        OnPropertyChanged(nameof(CrawlLockText));
        OnPropertyChanged(nameof(HasCrawlLockNotice));
    }

    private void OnBusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => AppServices.EnqueueUi(() =>
        {
            IsBusy = _bus.IsRunning;
            BusyLabel = _bus.CurrentLabel;
        });
}
