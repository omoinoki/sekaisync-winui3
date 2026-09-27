using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 实体列表区状态（手册 §5.1：空库 / 没选域 / 筛选排空 / 读取失败是四种不同状态）。
/// 界面只消费本 VM 的布尔包装属性（WinUI 3 没有 DataTrigger）。
/// </summary>
public enum EntitiesListState
{
    Loading,

    /// <summary>还没有本地库：不是「库里没有实体」。</summary>
    NoStore,

    /// <summary>没选资产域，或选中的域在本地是 0 行。</summary>
    NoDomain,

    /// <summary>有数据，但搜索把它筛空了。</summary>
    NoResults,

    /// <summary>查询失败。</summary>
    Failed,

    Ready,
}

/// <summary>
/// 实体页：entities 表，12 个资产域（库里还有没归进任何域的 type，见 <see cref="UnmappedText"/>）。
///
/// 「任务」域占近一半行数，但名字全是 `Live mission 269 (period 3, req 25)` 这种程序化占位名，
/// 没有译名价值 —— 所以它默认折叠在列表之外，由用户显式展开（审计 E-1）。
/// </summary>
public partial class EntitiesViewModel : ObservableObject
{
    private readonly CatalogQueryService _catalog = AppServices.Catalog;
    private CancellationTokenSource? _queryCts;
    private int _offset;
    private long _total;

    /// <summary>重建域列表 / 恢复选中时压掉一次自动重查，否则 F5 会把用户选的域打回默认（E-3）。</summary>
    private bool _suppressDomainReload;

    /// <summary>查询真正用的关键词与范围：只有回车 /「搜索」按钮会改关键词（分页沿用），用于如实标注命中位置。</summary>
    private bool _appliedNameOnly;

    private string _readFailure = string.Empty;

    /// <summary>当前 StatusText 是不是「需要留意的事」（决定它进哪个槽、用哪套状态色）。</summary>
    private bool _statusAttention;

    /// <summary>默认折叠在左栏之外的域（当前只有「任务」）。</summary>
    private readonly List<EntitiesDomainRow> _collapsedDomains = [];

    private int _contextSequence;

    public static EntitiesViewModel Shared { get; } = new();

    /// <summary>左栏可见的域；默认折叠组不在里面，按「展开」才并入。</summary>
    public ObservableCollection<EntitiesDomainRow> Domains { get; } = [];

    public ObservableCollection<EntityRow> Items { get; } = [];
    public ObservableCollection<LabelValue> NameRows { get; } = [];
    public ObservableCollection<LabelValue> FactRows { get; } = [];

    [ObservableProperty]
    private EntitiesDomainRow? _selectedDomain;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    private string _appliedSearch = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "正在读取实体域…";

    [ObservableProperty]
    private string _pagerText = "—";

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private EntityRow? _selectedItem;

    /// <summary>当前语言/域实测出来的规模摘要（E-2：数字必须是实测的）。</summary>
    [ObservableProperty]
    private string _domainSummary = "正在统计本地实体规模…";

    /// <summary>InfoBar 标题：域数 / 类型数都取实测值，不再写死 2026-08 的快照。</summary>
    [ObservableProperty]
    private string _domainTitle = "正在读取资产域…";

    /// <summary>没归进任何资产域的 type 与行数（左栏看不见的部分，必须说出来）。</summary>
    [ObservableProperty]
    private string _unmappedText = string.Empty;

    [ObservableProperty]
    private bool _showUnmappedNotice;

    /// <summary>「仅搜名称」：默认不扫 facts_json，免得结果里冒出名字完全不含关键词的行（E-4）。</summary>
    [ObservableProperty]
    private bool _nameOnlySearch = true;

    [ObservableProperty]
    private bool _collapsedDomainsExpanded;

    [ObservableProperty]
    private EntitiesListState _listState = EntitiesListState.Loading;

    [ObservableProperty]
    private string _detailTitle = "选中任一条目查看多语名称与事实表";

    /// <summary>稳定 ID（详情里第一次真的显示出来，可复制）。</summary>
    [ObservableProperty]
    private string _detailId = string.Empty;

    /// <summary>来源 / 区服 / 数据版本一行（E-5）。</summary>
    [ObservableProperty]
    private string _detailContext = string.Empty;

    /// <summary>覆盖边界一行：抓取时间未测量、覆盖区服、是否合成样本（E-5）。</summary>
    [ObservableProperty]
    private string _detailCoverage = string.Empty;

    /// <summary>这条是搜出来的还是翻出来的：命中的字段（E-4）。</summary>
    [ObservableProperty]
    private string _detailSearchHit = string.Empty;

    [ObservableProperty]
    private string _copyResultText = string.Empty;

    [ObservableProperty]
    private bool _showCopyResult;

    public EntitiesViewModel()
    {
        AppServices.StoreInvalidated += (_, _) => _ = InitializeAsync();
    }

    public int PageSize => Math.Max(50, AppServices.Settings.PageSize);

    public bool HasSelection => SelectedItem is not null;

    /// <summary>详情卡自己的空态：没有选中项时不残留上一张卡的多语名称/事实表。</summary>
    public bool ShowDetailEmpty => SelectedItem is null;

    /// <summary>有选中项才渲染两张表，否则屏上只剩表头 + 空表（读起来像「这条没有名称」）。</summary>
    public bool HasDetailContent => SelectedItem is not null;

    /// <summary>列表区四态的布尔包装（Loading 态由工具条上的 ProgressRing 表达，不额外出面板）。</summary>
    public bool IsListNoStore => ListState == EntitiesListState.NoStore;

    public bool IsListNoDomain => ListState == EntitiesListState.NoDomain;

    public bool IsListNoResults => ListState == EntitiesListState.NoResults;

    public bool IsListFailed => ListState == EntitiesListState.Failed;

    public bool ShowListStateOverlay => ListState is not EntitiesListState.Ready and not EntitiesListState.Loading;

    /// <summary>
    /// 分页行有两个槽：常态说明与「需要留意的事」。
    /// 原来两者挤在同一个 `MaxWidth=360 + TextTrimming + Opacity 0.55` 的小字里，
    /// 4 个类型码就能把异常挤出可视区（审计 E-8 / T-12 同族）。
    /// </summary>
    public bool IsAttentionWarning => _statusAttention;

    public bool IsAttentionNormal => !_statusAttention;

    private void SetStatus(string text, bool attention = false)
    {
        StatusText = text;
        _statusAttention = attention;
        OnPropertyChanged(nameof(IsAttentionWarning));
        OnPropertyChanged(nameof(IsAttentionNormal));
    }

    /// <summary>读失败的可读原因。</summary>
    public string FailedText => _readFailure;

    /// <summary>「请选择左侧任一资产域」这一态的下一步说明。</summary>
    public string NoDomainText =>
        SelectedDomain is null
            ? "左侧还没有选中资产域。选中任一资产域即可列出它的实体。"
            : $"「{SelectedDomain.DisplayName}」在本地是 0 行；这不代表这类资产不存在，只是没抓到。";

    public string SearchScopeText => NameOnlySearch
        ? "搜索名称与 ID；按 Enter 提交。"
        : "搜索名称、ID 与属性；按 Enter 提交。";

    public bool HasSearchHit => DetailSearchHit.Length > 0;

    partial void OnDetailSearchHitChanged(string value) => OnPropertyChanged(nameof(HasSearchHit));

    /// <summary>默认折叠组存在且未展开时才出「展开」按钮（E-1）。</summary>
    public bool ShowExpandCollapsedCommandButton => !CollapsedDomainsExpanded && _collapsedDomains.Count > 0;

    /// <summary>已展开时才出「折叠」按钮。</summary>
    public bool ShowCollapseButton => CollapsedDomainsExpanded && _collapsedDomains.Count > 0;

    /// <summary>展开按钮上的文案：把实测行数写清楚，别让用户点开才知道有多少。</summary>
    public string ExpandCollapsedText =>
        _collapsedDomains.Count == 0
            ? "全部分类"
            : $"更多分类（{_collapsedDomains.Count}）";

    public string FilterSummaryText =>
        SelectedDomain is { } domain
            ? $"{domain.DisplayName}{(string.IsNullOrWhiteSpace(AppliedSearch) ? string.Empty : $" · 搜索「{AppliedSearch}」")}"
            : "未选择资产域";

    public async Task InitializeAsync()
    {
        IsLoading = true;
        var previousKey = SelectedDomain?.Key ?? string.Empty;
        var previousId = SelectedItem?.Id ?? string.Empty;
        try
        {
            var domains = await _catalog.LoadEntityDomainsAsync();
            var grandTotal = domains.Sum(d => d.Count);

            // 规模/未归域的量单独实测；读不到不能让整页失败——域列表本身已经拿到了。
            CatalogQueryService.EntityScopeTotals? totals = null;
            try
            {
                totals = await _catalog.LoadEntityScopeTotalsAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"实体规模未测量：{ex.Message}", attention: true);
            }

            Domains.Clear();
            _collapsedDomains.Clear();
            foreach (var domain in domains)
            {
                var row = new EntitiesDomainRow(domain, grandTotal);
                if (domain.DefaultCollapsed)
                {
                    _collapsedDomains.Add(row);
                    if (!CollapsedDomainsExpanded)
                    {
                        continue;
                    }
                }
                Domains.Add(row);
            }

            ApplyScope(totals, domains);
            NotifyCollapsedButtons();

            // E-3：Domains.Clear() 会把 TwoWay 的 SelectedDomain 打成 null，
            // 原来的「if (SelectedDomain is null) 选默认」正好在这儿，于是 F5 必然把用户选的域重置回「角色」。
            _suppressDomainReload = true;
            SelectedDomain = Domains.FirstOrDefault(d => d.Key == previousKey)
                ?? Domains.FirstOrDefault(d => d.Key == "character")
                ?? Domains.FirstOrDefault();
            if (SelectedDomain is null)
            {
                previousId = string.Empty;
            }
            _suppressDomainReload = false;

            await ReloadAsync(previousId);
        }
        catch (Exception ex)
        {
            _readFailure = ex.Message;
            SetStatus($"读取实体域失败：{ex.Message}", attention: true);
            ListState = AppServices.Environment.DatabaseExists
                ? EntitiesListState.Failed
                : EntitiesListState.NoStore;
            DomainTitle = "实体规模未测量";
            DomainSummary = "读取实体域失败：资产域与行数都取不到，不是「库里没有」。";
            ShowUnmappedNotice = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>域规模说明（E-2：InfoBar 上只放实测值，不放 2026-08 的快照数字）。</summary>
    private void ApplyScope(CatalogQueryService.EntityScopeTotals? totals, IReadOnlyList<EntityDomain> domains)
    {
        var visibleDomains = domains.Count;
        var mappedTypes = domains.Sum(d => d.Types.Count);
        var mappedRows = domains.Sum(d => d.Count);

        if (totals is null)
        {
            DomainTitle = $"{visibleDomains} 个资产域 · 合计 {mappedRows:N0} 行";
            DomainSummary = string.Join(" · ", domains.Select(d => $"{d.DisplayName} {d.Count:N0}"));
            return;
        }

        DomainTitle = $"{visibleDomains} 个分类 · {totals.TotalRows:N0} 条记录";
        DomainSummary =
            string.Join(" · ", domains.Select(d => $"{d.DisplayName} {d.Count:N0}")) +
            $"；已归类 {mappedTypes} 种类型，共 {mappedRows:N0} 条。";

        ShowUnmappedNotice = totals.UnmappedTypes > 0;
        UnmappedText =
            $"另有 {totals.UnmappedTypes} 种类型、{totals.UnmappedRows:N0} 条记录尚未归类，可在「数据源 → 原始表」查看。";
    }

    [RelayCommand]
    private Task RefreshAsync() => InitializeAsync();

    [RelayCommand]
    private Task SearchAsync()
    {
        AppliedSearch = SearchText.Trim();
        return ReloadAsync();
    }

    [RelayCommand]
    private Task FirstPageAsync() => LoadPageAsync(0);

    [RelayCommand]
    private Task PreviousPageAsync() => LoadPageAsync(Math.Max(0, _offset - PageSize));

    [RelayCommand]
    private Task NextPageAsync() => LoadPageAsync(_offset + PageSize);

    [RelayCommand]
    private Task LastPageAsync()
    {
        var last = _total <= 0 ? 0 : (int)((_total - 1) / PageSize) * PageSize;
        return LoadPageAsync(last);
    }

    /// <summary>把默认折叠的域并进左栏（E-1：折叠是个可恢复的动作，不是把域藏起来不说）。</summary>
    [RelayCommand]
    private void ExpandCollapsedDomains()
    {
        CollapsedDomainsExpanded = true;
        _suppressDomainReload = true;
        foreach (var domain in _collapsedDomains)
        {
            Domains.Add(domain);
        }
        _suppressDomainReload = false;
        SetStatus("已展开默认折叠的资产域；它们行量大、名称多为程序化占位名。");
    }

    /// <summary>收回去。若当前正选中的就是被折叠的域，选择退回「角色」域并重查。</summary>
    [RelayCommand]
    private void CollapseDomains()
    {
        CollapsedDomainsExpanded = false;
        var dropping = SelectedDomain is { } current && _collapsedDomains.Any(d => d.Key == current.Key);
        _suppressDomainReload = true;
        foreach (var domain in _collapsedDomains)
        {
            Domains.Remove(domain);
        }

        if (dropping)
        {
            SelectedDomain = Domains.FirstOrDefault(d => d.Key == "character") ?? Domains.FirstOrDefault();
            _suppressDomainReload = false;
            SetStatus("「任务」域已折叠，选中项改回角色域。");
            _ = ReloadAsync();
            return;
        }

        _suppressDomainReload = false;
        SetStatus("默认折叠的资产域已收起。");
    }

    [RelayCommand]
    private void ClearFilter()
    {
        SearchText = string.Empty;
        AppliedSearch = string.Empty;
        _ = ReloadAsync();
    }

    /// <summary>复制详情：标题 + 稳定 ID + 多语名称 + 事实表（E-6）。</summary>
    [RelayCommand]
    private void CopyDetail()
    {
        if (SelectedItem is not { } row)
        {
            CopyResultText = "没有可复制的条目：先在列表里选中一行实体。";
            ShowCopyResult = true;
            return;
        }

        var lines = new List<string>
        {
            row.PrimaryName,
            $"ID：{row.Id}",
            $"类型：{row.Type}　区服：{RegionLabelOf(row.Region)}　来源：{row.Source}",
        };
        if (DetailContext.Length > 0)
        {
            lines.Add(DetailContext);
        }
        lines.Add("多语名称：");
        lines.AddRange(NameRows.Select(r => $"　{r.Label}＝{(r.IsMissing ? "暂无该语言记录" : r.Value)}"));
        lines.Add("事实字段：");
        lines.AddRange(FactRows.Select(r => $"　{r.Label}＝{r.Value}"));

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetText(string.Join(Environment.NewLine, lines));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            CopyResultText = "已复制名称、稳定 ID、多语名称与事实表。请粘贴到目标位置并核对内容。";
        }
        catch (Exception ex)
        {
            CopyResultText = $"复制失败：{ex.Message}";
        }
        ShowCopyResult = true;
    }

    partial void OnListStateChanged(EntitiesListState value)
    {
        OnPropertyChanged(nameof(IsListNoStore));
        OnPropertyChanged(nameof(IsListNoDomain));
        OnPropertyChanged(nameof(IsListNoResults));
        OnPropertyChanged(nameof(IsListFailed));
        OnPropertyChanged(nameof(ShowListStateOverlay));
        OnPropertyChanged(nameof(NoDomainText));
    }

    partial void OnSelectedItemChanged(EntityRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowDetailEmpty));
        OnPropertyChanged(nameof(HasDetailContent));
        ShowCopyResult = false;

        if (value is null)
        {
            // 原来这里直接 return，多语名称与事实表会残留上一条实体的内容。
            NameRows.Clear();
            FactRows.Clear();
            DetailTitle = "未在列表中选中实体";
            DetailId = string.Empty;
            DetailContext = string.Empty;
            DetailCoverage = string.Empty;
            DetailSearchHit = string.Empty;
            return;
        }

        NameRows.Clear();
        foreach (var row in TextRenderer.NameTable(value.NamesJson))
        {
            NameRows.Add(row);
        }

        FactRows.Clear();
        foreach (var row in TextRenderer.FactTable(value.FactsJson))
        {
            FactRows.Add(row);
        }

        DetailTitle = value.PrimaryName;
        DetailId = value.Id;
        DetailContext = "正在读取来源信息…";
        DetailCoverage = "抓取时间未记录";
        DetailSearchHit = DescribeHit(value);
        SetStatus($"{NameRows.Count} 项名称 · {FactRows.Count} 项属性");
        OnPropertyChanged(nameof(FilterSummaryText));

        _ = LoadContextAsync(value);
    }

    partial void OnSelectedDomainChanged(EntitiesDomainRow? value)
    {
        OnPropertyChanged(nameof(FilterSummaryText));
        OnPropertyChanged(nameof(NoDomainText));
        if (_suppressDomainReload || value is null)
        {
            return;
        }
        _ = ReloadAsync();
    }

    partial void OnCollapsedDomainsExpandedChanged(bool value) => NotifyCollapsedButtons();

    /// <summary>折叠/展开两个按钮的可见性：`_collapsedDomains` 不是可观察集合，重建后手动通知。</summary>
    private void NotifyCollapsedButtons()
    {
        OnPropertyChanged(nameof(ShowExpandCollapsedCommandButton));
        OnPropertyChanged(nameof(ShowCollapseButton));
        OnPropertyChanged(nameof(ExpandCollapsedText));
    }

    partial void OnNameOnlySearchChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchScopeText));
        // 勾选范围是用户的显式动作，直接重查；不逐击键查库（搜索仍走回车/按钮提交）。
        _ = ReloadAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(SearchScopeText));
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    /// <summary>搜索框里是否还有条件（含已输入未提交的部分，保证「清除筛选」总能按）。</summary>
    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(AppliedSearch);

    private Task ReloadAsync() => LoadPageAsync(0);

    /// <summary>重载时按 Id 找回原选中项：F5 / 翻页回来不该把详情打回第一条。</summary>
    private async Task ReloadAsync(string keepId) => await LoadPageAsync(0, keepId);

    private async Task LoadPageAsync(int offset, string keepId = "")
    {
        var types = SelectedDomain?.Types ?? [];
        if (types.Count == 0)
        {
            // 早退分支也要先掐掉在飞查询，否则旧结果会盖住这个空态（同 S-5 / V-4 的根因）。
            _queryCts?.Cancel();
            _queryCts = null;
            Items.Clear();
            SelectedItem = null;
            PagerText = "—";
            CanGoPrevious = CanGoNext = false;
            SetStatus("请选择左侧任一资产域");
            ListState = AppServices.Environment.DatabaseExists
                ? EntitiesListState.NoDomain
                : EntitiesListState.NoStore;
            OnPropertyChanged(nameof(NoDomainText));
            return;
        }

        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        IsLoading = true;
        ListState = EntitiesListState.Loading;
        var nameOnly = NameOnlySearch;
        var query = AppliedSearch;

        try
        {
            var total = await _catalog.CountEntitiesAsync(types, query, nameOnly, cts.Token);
            var page = await _catalog.QueryEntitiesAsync(types, query, offset, PageSize, nameOnly, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _total = total;
            _offset = page.Offset;
            _readFailure = string.Empty;
            _appliedNameOnly = nameOnly;
            Items.Clear();
            foreach (var row in page.Items)
            {
                Items.Add(row);
            }

            var shown = Items.Count == 0 ? 0 : _offset + Items.Count;
            PagerText = _total == 0
                ? "当前条件下 0 条"
                : $"第 {_offset + 1:N0} – {shown:N0} 条 · 共 {_total:N0} 条 · {page.ElapsedMilliseconds} ms";
            CanGoPrevious = _offset > 0;
            CanGoNext = shown < _total;
            // StatusText 只放「需要留意的事」，筛选描述走 FilterSummaryText 自己的槽（审计 E-8）。
            OnPropertyChanged(nameof(FilterSummaryText));

            var wantedId = keepId.Length > 0 ? keepId : (SelectedItem?.Id ?? string.Empty);
            var restored = wantedId.Length == 0 ? null : Items.FirstOrDefault(r => r.Id == wantedId);
            SelectedItem = restored ?? Items.FirstOrDefault();

            ListState = _total switch
            {
                0 => string.IsNullOrWhiteSpace(AppliedSearch) ? EntitiesListState.NoDomain : EntitiesListState.NoResults,
                _ => EntitiesListState.Ready,
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // 读失败：清空并给失败态，别把上一次查询的行留在屏上冒充当前域的结果。
            _readFailure = ex.Message;
            Items.Clear();
            SelectedItem = null;
            _total = 0;
            _offset = 0;
            CanGoPrevious = CanGoNext = false;
            PagerText = "读取失败";
            SetStatus($"查询失败：{ex.Message}", attention: true);
            ListState = EntitiesListState.Failed;
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// 详情上下文：来源站点、覆盖区服、数据版本。entities 表没有 crawled_at，
    /// 所以「抓取时间」只能显示未测量（E-5 的可执行部分；报告里附了实测列清单）。
    /// </summary>
    private async Task LoadContextAsync(EntityRow row)
    {
        var sequence = ++_contextSequence;
        try
        {
            var context = await _catalog.ReadEntityContextAsync(row.Id);
            if (sequence != _contextSequence || SelectedItem?.Id != row.Id)
            {
                return;
            }

            if (!context.Found)
            {
                DetailCoverage = "这条实体的上下文行已读不到（库里查不到该 id）；可能是本次同步后被替换。";
                return;
            }

            var regions = DescribeRegions(context.RegionsJson, row.Region);
            DetailContext =
                $"来源 {SourceLabelOf(context.Source.Length > 0 ? context.Source : row.Source)} · " +
                $"{RegionLabelOf(context.Region)} · 类型 {context.Type}";
            DetailCoverage =
                $"版本 {FormatVersion(context.Version)} · 覆盖 {regions} · 抓取时间未记录" +
                (context.IsDemo ? " · 合成样本" : string.Empty);

            DetailSearchHit = DescribeHit(row);
        }
        catch (Exception ex)
        {
            if (sequence != _contextSequence)
            {
                return;
            }
            DetailCoverage = $"读取上下文失败：{ex.Message}。名称与事实表仍是上次读到的内容。";
        }
    }

    /// <summary>命中的是哪个字段——搜索可以扫 facts_json，就必须说清为什么这条会出现在结果里。</summary>
    private string DescribeHit(EntityRow row)
    {
        if (AppliedSearch.Length == 0)
        {
            return string.Empty;
        }

        var needle = AppliedSearch;
        var hits = new List<string>();
        if (row.NamesJson.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            hits.Add("名称（names_json）");
        }
        if (row.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            hits.Add("id");
        }
        if (!_appliedNameOnly && row.FactsJson.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            hits.Add("事实字段（facts_json）");
        }

        return hits.Count == 0
            ? $"搜索「{needle}」：这一行的可见字段里没找到该关键词，命中位置无法确定（可能来自其它字段或结果已翻页）。"
            : $"搜索「{needle}」命中：{string.Join("、", hits)}";
    }

    /// <summary>
    /// source 的可读说明。实体表的 source 实测是 `master_db` / `master_db:&lt;区服&gt;`，
    /// 不是两个资料站实例的 altsource_* 值 —— 所以不能拿实例徽章那套解释它（保留原码，另加解释）。
    /// </summary>
    private static string SourceLabelOf(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return "未记录 source";
        }
        if (string.Equals(source, "master_db", StringComparison.Ordinal))
        {
            return "主数据 master_db（多服共用记录，不归属某个资料站实例）";
        }
        if (source.StartsWith("master_db:", StringComparison.Ordinal))
        {
            var region = source["master_db:".Length..];
            var label = SourceModel.RegionByRegion(region)?.DisplayName ?? region;
            return $"主数据 {source}（按 {label} 收窄）";
        }
        return SourceModel.Resolve(source) is { } resolved
            ? $"{SourceModel.GetInstance(resolved.Instance).DisplayName}（{SourceModel.GetInstance(resolved.Instance).ShortName} · {SourceModel.GetInstance(resolved.Instance).Site}）"
            : $"未收录的 source（{source}）";
    }

    /// <summary>
    /// region 的可读说明。master_db 的多服共用行 region 是空串（实测 58,061 行为空），
    /// 列表里那格因此是空白 —— 空白读作「没抓到」还是「多服共用」是两件事，详情必须说清。
    /// </summary>
    private static string RegionLabelOf(string region) =>
        string.IsNullOrEmpty(region)
            ? "多服共用记录（region 为空）"
            : SourceModel.RegionByRegion(region)?.DisplayName ?? region;

    private static string FormatVersion(string version) =>
        string.IsNullOrWhiteSpace(version) ? "未测量" : version;

    private static string DescribeRegions(string regionsJson, string fallback)
    {
        var parts = regionsJson
            .Trim('[', ']', '"', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Trim('"'))
            .Where(r => r.Length > 0)
            .ToList();

        if (parts.Count == 0)
        {
            return SourceModel.RegionByRegion(fallback) is { } one ? $"仅 {one.DisplayName}（regions_json 为空）" : "未记录";
        }

        return string.Join("、", parts.Select(RegionLabelOf));
    }
}
