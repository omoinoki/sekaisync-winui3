using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>比对范围 chip。</summary>
public partial class CompareScopeOption : ObservableObject
{
    public CompareScopeOption(string kind, string displayName)
    {
        Kind = kind;
        DisplayName = displayName;
    }

    public string Kind { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>
/// 双实例卡的展示层（审计 SR-2 / SR-9 / SR-10）。
/// 模型的原始字段是 <c>required init</c> 且带「—」占位，直接绑会把
/// 「没测到」显示成「—（0 天前）」，读作今天刚抓过——这里统一换成 §6 的「未测量」口径。
/// </summary>
public sealed class SourceCardItem
{
    private readonly SourceStats _stats;

    public SourceCardItem(SourceStats stats) => _stats = stats;

    public string Source => _stats.Source;

    public string InstanceBadge => _stats.InstanceBadge;

    public string DisplayName => _stats.DisplayName;

    public string Upstream => _stats.Upstream;

    public string Site => _stats.Site;

    public string RowsLabel => _stats.Rows.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>覆盖层规模（§4.6 要求列出，审计 SR-9：原先算出来了却没有任何绑定）。</summary>
    public string OverlayRowsLabel => _stats.OverlayRows > 0
        ? $"覆盖层 {_stats.OverlayRows.ToString("N0", CultureInfo.InvariantCulture)} 行"
        : "覆盖层未测量";

    public string CoverageLabel => $"正文类目 {_stats.TextKindsCovered}/{_stats.TextKindsTotal}";

    public bool HasLastCrawl => TryParseDate(_stats.LastCrawl, out _);

    /// <summary>最近抓取：没有抓取记录就写「未测量」，不写「—（0 天前）」（审计 SR-2）。</summary>
    public string CrawlLabel => HasLastCrawl
        ? $"{_stats.LastCrawl}（{_stats.DaysAgo} 天前）"
        : "未测量";

    /// <summary>首次抓取要有标签，裸日期会被读成最近抓取（审计 SR-10）。</summary>
    public string FirstCrawlLabel => TryParseDate(_stats.FirstCrawl, out _)
        ? $"首次抓取 {_stats.FirstCrawl}"
        : "首次抓取未测量";

    /// <summary>卡片整体的可读性名字（读屏下实例卡是一张卡，不是一堆散文本）。</summary>
    public string CardName => $"{DisplayName}：{RowsLabel} 行，{CrawlLabel}，{OverlayRowsLabel}，{CoverageLabel}";

    public static bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text == "—")
        {
            return false;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
               || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
    }
}

/// <summary>资产域速览的一个入口（§4.6「12 域入口，点击跳实体页并预选」，审计 SR-4）。</summary>
public sealed class DomainShortcut
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string OpenName { get; init; }

    /// <summary>命令挂在条目上：DataTemplate 里的 x:Bind 取不到页面 VM，只能取到本条目。</summary>
    public required ICommand OpenCommand { get; init; }
}

/// <summary>
/// 数据源页：三块。
///   1. 知识库概览（原「概览」页 + meta 管线状态 + 双实例卡 + 资产域速览）
///   2. 跨实例比对
///   3. 原始表（调试）—— 直接托管原来的数据库阅览页
/// </summary>
public partial class SourcesViewModel : ObservableObject
{
    private readonly CatalogQueryService _catalog = AppServices.Catalog;
    private readonly TermQueryService _terms = AppServices.Terms;
    private readonly AppEnvironment _environment = AppServices.Environment;

    private bool _initializing;
    private bool _rerunRequested;
    private CancellationTokenSource? _compareCts;

    public static SourcesViewModel Shared { get; } = new();

    public ObservableCollection<MetaEntry> MetaEntries { get; } = [];

    /// <summary>双实例卡（展示层，取代直接绑 <see cref="SourceStats"/> 模型）。</summary>
    public ObservableCollection<SourceCardItem> SourceCards { get; } = [];

    /// <summary>12 个资产域入口（可点）。</summary>
    public ObservableCollection<DomainShortcut> DomainShortcuts { get; } = [];

    public ObservableCollection<LabelValue> OverlayStatus { get; } = [];

    /// <summary>比对返回的全部行。</summary>
    public ObservableCollection<CompareRow> CompareRows { get; } = [];

    /// <summary>应用「只看差异 / 只看缺口 / 看全部」之后的行，列表绑这个。</summary>
    public ObservableCollection<CompareRow> FilteredCompareRows { get; } = [];

    public ObservableCollection<CompareScopeOption> CompareScopes { get; } = [];

    [ObservableProperty]
    private string _selectedTab = "overview";

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>本地库这一张卡的状态行：就绪 / 未建库 / 读取失败三态之一（审计 SR-1）。</summary>
    [ObservableProperty]
    private string _statusText = "正在读取知识库状态…";

    /// <summary>页面级动作的一次性回执（例如跳实体页失败），不借用状态行。</summary>
    [ObservableProperty]
    private string _actionHintText = string.Empty;

    /// <summary><see cref="ActionHintText"/> 是否有内容（页面用它切换可见性）。</summary>
    public bool HasActionHint => ActionHintText.Length > 0;

    partial void OnActionHintTextChanged(string value) => OnPropertyChanged(nameof(HasActionHint));

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private string _databaseSize = "未测量";

    [ObservableProperty]
    private string _schemaVersion = "未测量";

    /// <summary>本地库这一张卡此刻是不是「可读」；不可读时页面换一套措辞与配色（审计 SR-1）。</summary>
    [ObservableProperty]
    private bool _databaseOk;

    /// <summary>与 <see cref="DatabaseOk"/> 配对，供页面切换可见性（WinUI 3 没有 DataTrigger）。</summary>
    public bool HasDatabaseProblem => !DatabaseOk;

    partial void OnDatabaseOkChanged(bool value) => OnPropertyChanged(nameof(HasDatabaseProblem));

    /// <summary>两实例抓取日期差超过一周时提醒——实测差 14 天。</summary>
    [ObservableProperty]
    private string _freshnessTitle = "两个实例的抓取日期相差较大";

    [ObservableProperty]
    private string _freshnessWarning = string.Empty;

    [ObservableProperty]
    private bool _showFreshnessWarning;

    [ObservableProperty]
    private string _compareSummaryText = string.Empty;

    public bool HasCompareSummary => !string.IsNullOrWhiteSpace(CompareSummaryText);

    partial void OnCompareSummaryTextChanged(string value) => OnPropertyChanged(nameof(HasCompareSummary));

    [ObservableProperty]
    private string _compareEmptyText = "选择类目后，点击「开始比对」。";

    [ObservableProperty]
    private bool _isComparing;

    /// <summary>比对结果的本地筛选：all / gap / diff。不重跑查询。</summary>
    [ObservableProperty]
    private string _compareFilter = "all";

    [ObservableProperty]
    private bool _hasCompareRows;

    [ObservableProperty]
    private bool _isCompareFilterEmpty;

    /// <summary>筛选后零行时的解释——「差异」这一类目前服务层根本不产出行，必须说清楚。</summary>
    [ObservableProperty]
    private string _compareFilterEmptyText = string.Empty;

    [ObservableProperty]
    private bool _hasNoSourceCards;

    public SourcesViewModel()
    {
        foreach (var kind in SourceModel.CanonicalKinds)
        {
            CompareScopes.Add(new CompareScopeOption(kind, SourceModel.KindDisplay(kind)));
        }
        // 失效信号可能来自后台任务：统一排回 UI 线程，并且不并发重入（审计 SR-11）。
        AppServices.StoreInvalidated += (_, _) => AppServices.EnqueueUi(() => _ = InitializeAsync());
    }

    /// <summary>页面据此请求壳层切到别的标签页（参数是导航 Tag）。壳层不可达时页面自己提示。</summary>
    public event Action<string>? NavigateRequested;

    public bool IsOverviewTab => SelectedTab == "overview";

    public bool IsCompareTab => SelectedTab == "compare";

    public bool IsTablesTab => SelectedTab == "tables";

    public Visibility OverviewVisibility => IsOverviewTab ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CompareVisibility => IsCompareTab ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TablesVisibility => IsTablesTab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>版本号统一取自 <see cref="AppEnvironment.ProductVersion"/>，不再各页解析一份。</summary>
    public static string ProductVersion => AppEnvironment.ProductVersion;

    public string VersionLine => $"SekaiSync Desktop {ProductVersion} · 知识库 schema {SchemaVersion}";

    /// <summary>「开始比对」只在没在跑时可点；「取消比对」只在跑时可点（审计 SR-6）。</summary>
    public bool CanRunCompare => !IsComparing;

    public bool CanCancelCompare => IsComparing;

    public bool IsCompareFilterAll => CompareFilter == "all";

    public bool IsCompareFilterGap => CompareFilter == "gap";

    public bool IsCompareFilterDiff => CompareFilter == "diff";

    /// <summary>比对结果区的一种状态：还没跑出任何行（也不在跑）。</summary>
    public bool CompareNotRun => !HasCompareRows && !IsComparing;

    partial void OnHasCompareRowsChanged(bool value) => OnPropertyChanged(nameof(CompareNotRun));

    partial void OnIsComparingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunCompare));
        OnPropertyChanged(nameof(CanCancelCompare));
        OnPropertyChanged(nameof(CompareNotRun));
    }

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsCompareTab));
        OnPropertyChanged(nameof(IsTablesTab));
        OnPropertyChanged(nameof(OverviewVisibility));
        OnPropertyChanged(nameof(CompareVisibility));
        OnPropertyChanged(nameof(TablesVisibility));
    }

    partial void OnSchemaVersionChanged(string value) => OnPropertyChanged(nameof(VersionLine));

    partial void OnCompareFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompareFilterAll));
        OnPropertyChanged(nameof(IsCompareFilterGap));
        OnPropertyChanged(nameof(IsCompareFilterDiff));
        ApplyCompareFilter();
    }

    public async Task InitializeAsync()
    {
        if (_initializing)
        {
            _rerunRequested = true;
            return;
        }
        _initializing = true;

        IsLoading = true;
        ShowFreshnessWarning = false;
        try
        {
            DatabasePath = _environment.DatabasePath;

            // 同步 IO（File.Exists / FileInfo）挪出 UI 线程（审计 SR-11）。
            var (exists, size) = await Task.Run(() =>
            {
                var path = _environment.DatabasePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return (false, 0L);
                }
                try
                {
                    return (true, new FileInfo(path).Length);
                }
                catch (Exception)
                {
                    return (true, -1L);
                }
            });

            DatabaseOk = exists;
            if (exists)
            {
                DatabaseSize = size < 0
                    ? "未测量（读不到文件大小）"
                    : $"{size / 1024.0 / 1024.0:N1} MB";
                StatusText = "只读";
            }
            else
            {
                DatabaseSize = "未测量";
                // §6 未建库文案；壳层已有常驻横幅，这里只把卡片本身说对，
                // 绝不再写「知识库就绪」（审计 SR-1：那是页面级谎报）。
                StatusText = string.IsNullOrEmpty(DatabasePath)
                    ? "尚未选择本地数据库。请选择已有库，或查看初始化步骤。"
                    : $"本地库不存在：{DiagnosticText.Redact(DatabasePath)}。请选择已有库，或查看初始化步骤。";
            }

            // 没有库就到此为止：继续查只会抛一句没人看得懂的 SQLite 错误，
            // 并且会把上面这句「未建库」顶掉（审计 SR-1 的反面教材）。
            if (!exists)
            {
                MetaEntries.Clear();
                SourceCards.Clear();
                DomainShortcuts.Clear();
                OverlayStatus.Clear();
                SchemaVersion = "未测量";
                HasNoSourceCards = false;
                UpdateFreshness(Array.Empty<SourceStats>(), false);
                return;
            }

            // 读与发布分成两段：往绑定集合里增删会在导航途中抛 WinUI 的 E_FAIL
            // （0x80004005，Message 只有「未指定的错误」）。混在同一个 try 里，
            // 「数据读到了、只是这一帧没刷上界面」就会被误报成「读不到知识库」，
            // 还会顺手把 DatabaseOk 改成 false。
            IReadOnlyList<MetaEntry> meta;
            IReadOnlyList<SourceStats> stats;
            IReadOnlyList<EntityDomain> domains;
            IReadOnlyList<LabelValue> overlays;
            try
            {
                meta = await _catalog.LoadMetaAsync();
                stats = await _catalog.LoadSourceStatsAsync();
                domains = await _catalog.LoadEntityDomainsAsync();
                overlays = await _terms.LoadOverlayStatusAsync();
            }
            catch (Exception ex)
            {
                // 读取失败既不是「就绪」也不是「不存在」——三态要分开（手册 §5.1）。
                DatabaseOk = false;
                StatusText = BuildReadFailureMessage(ex, "读取知识库状态失败");
                return;
            }

            try
            {
                MetaEntries.Clear();
                SchemaVersion = "未测量";
                foreach (var entry in meta)
                {
                    MetaEntries.Add(entry);
                    if (string.Equals(entry.Key, "schema_version", StringComparison.OrdinalIgnoreCase))
                    {
                        SchemaVersion = string.IsNullOrWhiteSpace(entry.Value) ? "未测量" : entry.Value;
                    }
                }

                SourceCards.Clear();
                foreach (var item in stats)
                {
                    SourceCards.Add(new SourceCardItem(item));
                }
                HasNoSourceCards = SourceCards.Count == 0;
                UpdateFreshness(stats, exists);

                DomainShortcuts.Clear();
                foreach (var domain in domains)
                {
                    DomainShortcuts.Add(new DomainShortcut
                    {
                        Key = domain.Key,
                        Label = $"{domain.DisplayName} {domain.Count:N0}",
                        OpenName = $"在实体页查看「{domain.DisplayName}」域，{domain.Count:N0} 行",
                        OpenCommand = GoDomainCommand,
                    });
                }

                OverlayStatus.Clear();
                foreach (var item in overlays)
                {
                    OverlayStatus.Add(item);
                }
            }
            catch (Exception ex)
            {
                // 数据是好的，不能谎称读不到库；HRESULT 与堆栈只进诊断日志。
                App.Log($"SourcesViewModel: 数据已读到但界面刷新失败 {ex.GetType().Name} 0x{ex.HResult:X8} {ex}");
                StatusText = "数据已读到，但界面刷新失败。离开本页再回来即可重试。";
            }
        }
        catch (Exception ex)
        {
            DatabaseOk = false;
            StatusText = BuildReadFailureMessage(ex, "读取知识库状态失败");
        }
        finally
        {
            IsLoading = false;
            _initializing = false;
            if (_rerunRequested)
            {
                _rerunRequested = false;
                _ = InitializeAsync();
            }
        }
    }

    /// <summary>
    /// SQLITE_CANTOPEN(14) 在这里的语义就是「以只读方式没能打开这个文件」，
    /// 必须照实说（审计 R-1 的文案半边）；其它异常才是读取本身失败。
    /// </summary>
    private static string BuildReadFailureMessage(Exception ex, string context)
        => ex is SqliteException { SqliteErrorCode: 14 }
            ? $"以只读方式打开失败：{DiagnosticText.Redact(ex.Message)}。请检查路径与读取权限；若正在同步，完成后刷新。"
            : $"{context}：{DiagnosticText.Redact(ex.Message)}";

    /// <summary>页面侧兜底：VM 自己没接住的异常也要有一句话可看，不能静默（同审计 D-3 的要求）。</summary>
    public void ReportEntryFailure(Exception ex)
    {
        DatabaseOk = false;
        StatusText = BuildReadFailureMessage(ex, "打开数据源页失败");
    }

    /// <summary>缺测由实例卡说明；只有已知日期差超过一周时提示同步较旧的实例。</summary>
    private void UpdateFreshness(IReadOnlyList<SourceStats> stats, bool databaseExists)
    {
        ShowFreshnessWarning = false;
        FreshnessWarning = string.Empty;
        if (!databaseExists || stats.Count < 2)
        {
            return;
        }

        var left = stats[0];
        var right = stats[1];
        if (!SourceCardItem.TryParseDate(left.LastCrawl, out var a)
            || !SourceCardItem.TryParseDate(right.LastCrawl, out var b))
        {
            return;
        }

        var days = Math.Abs((a - b).TotalDays);
        if (days > 7)
        {
            var older = a < b ? left : right;
            FreshnessTitle = "两个实例的抓取日期相差较大";
            FreshnessWarning = $"相差 {days:N0} 天，可在「同步」页更新较旧的 {older.DisplayName}。";
            ShowFreshnessWarning = true;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => InitializeAsync();

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (!string.IsNullOrEmpty(tab))
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private Task ToggleScopeAsync(CompareScopeOption? option)
    {
        if (option is not null)
        {
            option.IsSelected = !option.IsSelected;
        }
        return Task.CompletedTask;
    }

    /// <summary>选比对结果的筛选（本地过滤，不重跑查询）。</summary>
    [RelayCommand]
    private void SelectCompareFilter(string? filter)
    {
        if (filter is "all" or "gap" or "diff")
        {
            CompareFilter = filter;
        }
    }

    private void ApplyCompareFilter()
    {
        FilteredCompareRows.Clear();
        foreach (var row in CompareRows)
        {
            var keep = CompareFilter switch
            {
                // 「缺口」= 只存在于一方；「差异」= 两边都有但内容不同。
                "gap" => row.Status.EndsWith("-only", StringComparison.Ordinal),
                "diff" => row.Status == "differed",
                _ => true,
            };
            if (keep)
            {
                FilteredCompareRows.Add(row);
            }
        }

        IsCompareFilterEmpty = CompareRows.Count > 0 && FilteredCompareRows.Count == 0;
        CompareFilterEmptyText = CompareFilter switch
        {
            "diff" => "内容差异见上方汇总；点击「看全部」查看缺口清单。",
            "gap" => "没有可列出的缺口，点击「看全部」查看结果。",
            _ => string.Empty,
        };
    }

    /// <summary>跳到实体页并预选该资产域（§4.6 的 12 域入口，审计 SR-4）。</summary>
    [RelayCommand]
    private async Task GoDomainAsync(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var entities = EntitiesViewModel.Shared;
        var target = entities.Domains.FirstOrDefault(d => d.Key == key);
        if (target is null)
        {
            // 实体页还没建过域清单：先把它的域读出来，否则「预选」无从谈起。
            await entities.InitializeAsync();
            target = entities.Domains.FirstOrDefault(d => d.Key == key);
        }

        if (target is null && !entities.CollapsedDomainsExpanded)
        {
            // 「任务」这类默认折叠的域不在 Domains 里：先按实体页自己的命令展开再找一次，
            // 否则用户点它会被解释成「这个域不存在」。
            entities.ExpandCollapsedDomainsCommand.Execute(null);
            target = entities.Domains.FirstOrDefault(d => d.Key == key);
        }

        if (target is null)
        {
            ActionHintText = $"未找到「{key}」域，请在实体页刷新后重试。";
            return;
        }

        // 预选落在实体页自己的单例 VM 上——壳层的 Navigate 不带参数（见报告「需要主控处理」）。
        entities.SelectedDomain = target;
        ActionHintText = $"已在实体页选中「{target.DisplayName}」域。";
        NavigateRequested?.Invoke("entities");
    }

    /// <summary>覆盖层卡片 → 「用语 · 官方译名」（§4.6，审计 SR-5）。</summary>
    [RelayCommand]
    private void OpenTranslationTab()
    {
        var terms = TermsViewModel.Shared;
        terms.SelectedTab = "translation";
        NavigateRequested?.Invoke("terms");
    }

    [RelayCommand]
    private async Task RunCompareAsync()
    {
        if (IsComparing)
        {
            return;
        }

        var kinds = CompareScopes.Where(s => s.IsSelected).Select(s => s.Kind).ToList();
        if (kinds.Count == 0)
        {
            // 早退也要先取消在飞查询，否则旧结果会覆盖这条提示（实施契约 §3）。
            _compareCts?.Cancel();
            CompareRows.Clear();
            FilteredCompareRows.Clear();
            HasCompareRows = false;
            IsCompareFilterEmpty = false;
            CompareSummaryText = "请选择至少一个类目。";
            CompareEmptyText = "选择类目后，点击「开始比对」。";
            return;
        }

        IsComparing = true;
        CompareRows.Clear();
        FilteredCompareRows.Clear();
        HasCompareRows = false;
        IsCompareFilterEmpty = false;
        var cts = new CancellationTokenSource();
        _compareCts = cts;
        CompareSummaryText = "正在比对所选类目…";
        try
        {
            var (summary, rows) = await _catalog.CompareAsync(kinds, 500, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            CompareRows.Clear();
            foreach (var row in rows)
            {
                CompareRows.Add(row);
            }
            HasCompareRows = CompareRows.Count > 0;
            ApplyCompareFilter();

            CompareSummaryText = summary.Label +
                (rows.Count > 0
                    ? $"　仅 Sekai Viewer 有：列出 {rows.Count:N0} 条（最多 500 条）。"
                    : "　没有仅 Sekai Viewer 存在的记录。");
            CompareEmptyText = "调整类目后可重新比对。";
        }
        catch (OperationCanceledException)
        {
            CompareRows.Clear();
            FilteredCompareRows.Clear();
            HasCompareRows = false;
            IsCompareFilterEmpty = false;
            CompareSummaryText = "比对已取消。";
            CompareEmptyText = "点击「开始比对」重新比对。";
        }
        catch (Exception ex)
        {
            CompareSummaryText = BuildReadFailureMessage(ex, "比对失败");
            CompareEmptyText = "查看上方原因后，点击「开始比对」重试。";
        }
        finally
        {
            if (ReferenceEquals(_compareCts, cts))
            {
                _compareCts = null;
            }
            // 不 Dispose：查询任务可能还在后台跑并持有这个 token，提前释放会让它抛
            // ObjectDisposedException（与 DatabaseViewModel._queryCts 的既有做法一致）。
            IsComparing = false;
        }
    }

    /// <summary>取消在飞比对（审计 SR-6：以前只能等它自己跑完）。</summary>
    [RelayCommand]
    private void CancelCompare()
    {
        CompareSummaryText = "正在请求停止比对…";
        _compareCts?.Cancel();
    }
}
