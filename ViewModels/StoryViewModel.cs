using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>剧情类型 chip（5 类可多选）。</summary>
public partial class StoryKindOption : ObservableObject
{
    public StoryKindOption(string kind, string displayName)
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
/// 五语对照里的一栏。
///
/// 「本地没有」有四种不同含义（未抓正文 / 无对齐键 / 读取失败 / 真有内容），
/// 必须带机读的 <see cref="MissingReason"/> 并渲染成文字（审计 S-1 / V-3）：
/// 正文层本地覆盖率只有 62–83，所以「本地查不到」远比「该服没有」更可能，
/// 界面不得由本地缺行断言不存在（手册 §6 unknown 行）。
/// </summary>
public sealed class ParallelColumn
{
    public required string RegionLabel { get; init; }
    public required string Language { get; init; }
    public required string Badge { get; init; }
    public required bool Missing { get; init; }
    public required IReadOnlyList<DialogueBlock> Blocks { get; init; }

    /// <summary>缺失原因（机读）。有内容时为 <see cref="ParallelMissingReason.None"/>。</summary>
    public required ParallelMissingReason MissingReason { get; init; }

    /// <summary>量词口径：剧情页「这一话」，台词页「这一句」。</summary>
    public required string MissingSubject { get; init; }

    public Visibility MissingVisibility => Missing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility => Missing ? Visibility.Collapsed : Visibility.Visible;

    public string Header => string.IsNullOrEmpty(Badge) ? RegionLabel : $"{RegionLabel} · {Badge}";

    /// <summary>只说明本地读取结果，不推断源站是否存在内容。</summary>
    public string MissingLabel => MissingReason switch
    {
        ParallelMissingReason.NoAlignmentKey => "缺少跨服对齐键。",
        ParallelMissingReason.ReadFailed => "读取失败，请重试对照。",
        ParallelMissingReason.NotCrawled => "本地尚未抓取" + MissingSubject + "。",
        _ => "本地暂无" + MissingSubject + "的正文。",
    };

    /// <summary>缺失栏的原因短标签（胶囊文字）。</summary>
    public string MissingReasonLabel => MissingReason switch
    {
        ParallelMissingReason.NoAlignmentKey => "无对齐键",
        ParallelMissingReason.ReadFailed => "读取失败",
        ParallelMissingReason.NotCrawled => "未抓取",
        _ => "有正文",
    };
}

/// <summary>阅读模式。</summary>
public sealed class ReadingModeOption
{
    public ReadingModeOption(string label, int columns)
    {
        Label = label;
        Columns = columns;
    }

    public string Label { get; }

    /// <summary>0 = 单语，2 = 日简对照，5 = 五服并排。</summary>
    public int Columns { get; }

    public override string ToString() => Label;
}

/// <summary>
/// 剧情页：5 种有标题的叙事单元（活动 / 卡牌 / 组合 / 特别 / 虚拟 Live）。
/// 只取 overlay = 0、source 限死在两个主数据源上（等价于排除覆盖层，但能吃到索引）。
///
/// 列表区四态（空库 / 无数据 / 筛选排空 / 读取失败）与详情区三态（加载 / 失败 / 空）
/// 各由一组布尔包装属性驱动一个覆盖层——WinUI 3 没有 DataTrigger，
/// 状态判断一律留在 VM 里，XAML 只绑布尔（实施契约 §3）。
/// </summary>
public partial class StoryViewModel : ObservableObject
{
    /// <summary>列表区状态（仅 VM 内部判断用，XAML 只消费下面的布尔包装属性）。</summary>
    private enum ListState
    {
        None,
        NoStore,
        NoData,
        FilteredToZero,
        NoKinds,
        Failed,
    }

    private readonly ContentQueryService _content = AppServices.Content;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _columnsCts;
    private int _offset;
    private long _total;
    private ListState _listState;
    private string _listStateDetail = string.Empty;

    /// <summary>
    /// 批量改写筛选（「清除筛选」一次改 5 个 chip + 3 个下拉）时抑制逐个重查，
    /// 由调用方在收尾处统一触发一次。
    /// </summary>
    private bool _suppressReload;

    /// <summary>用户实际提交（点搜索 / 回车）的关键词；未提交的输入不参与分页（S-14）。</summary>
    private string _appliedSearch = string.Empty;

    /// <summary>详情正文读取失败的原因（非空 = 详情失败态）。</summary>
    private string _detailFailure = string.Empty;

    /// <summary>对照区的状态说明（读取失败 / 五服全未覆盖时非空）。</summary>
    private string _parallelNotice = string.Empty;

    public static StoryViewModel Shared { get; } = new();

    /// <summary>整表替换（一次属性通知，而不是 1 次 Reset + 50 次 Add，XP-6）。</summary>
    public ObservableCollection<StoryRow> Items { get; private set; } = [];

    public ObservableCollection<StoryKindOption> Kinds { get; } = [];
    public ObservableCollection<ParallelColumn> Columns { get; } = [];

    public IReadOnlyList<InstanceOption> InstanceOptions { get; } = SourceModel.InstanceOptions;
    public IReadOnlyList<VersionOption> VersionOptions { get; } = SourceModel.VersionOptions;
    public IReadOnlyList<ReadingModeOption> ReadingModes { get; } =
    [
        new ReadingModeOption("单语", 0),
        new ReadingModeOption("日简对照", 2),
        new ReadingModeOption("五服并排", 5),
    ];

    /// <summary>搜索范围与提交方式，供工具提示和读屏使用。</summary>
    public string SearchScopeHint => "匹配标题和 ID，不含正文。按 Enter 或点击搜索提交；翻页沿用已提交的关键词。";

    [ObservableProperty]
    private InstanceOption _selectedInstance;

    [ObservableProperty]
    private VersionOption _selectedVersion;

    [ObservableProperty]
    private ReadingModeOption _selectedReadingMode;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _onlyUntranslated;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>已提交的搜索词；其他筛选状态由原生控件直接显示。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>需要用户注意的事（异常、早退、复制结果之外的提示）。空 = 不显示。</summary>
    [ObservableProperty]
    private string _attention = string.Empty;

    [ObservableProperty]
    private string _pagerText = "—";

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private StoryRow? _selectedItem;

    [ObservableProperty]
    private StoryDetail? _detail;

    [ObservableProperty]
    private bool _isDetailLoading;

    /// <summary>复制动作的结果回执（说清复制了什么）。</summary>
    [ObservableProperty]
    private string _copyNote = string.Empty;

    public StoryViewModel()
    {
        foreach (var kind in SourceModel.StoryKinds)
        {
            var option = new StoryKindOption(kind, SourceModel.KindDisplay(kind));
            // chip 是 TwoWay 绑到 IsSelected 的，这里补一层观察，勾选即重查。
            option.PropertyChanged += (_, e) =>
            {
                if (!_suppressReload && e.PropertyName == nameof(StoryKindOption.IsSelected))
                {
                    _ = ReloadAsync();
                }
            };
            Kinds.Add(option);
        }

        _selectedInstance = InstanceOptions[0];
        _selectedVersion = VersionOptions[0];
        // 直接写字段而不是走属性，避免启动时就把默认值当成一次「用户改动」写回配置。
        _selectedReadingMode = ReadingModes.FirstOrDefault(
            m => m.Label == AppServices.Settings.StoryReadingMode) ?? ReadingModes[0];
        AppServices.StoreInvalidated += (_, _) => _ = ReloadAsync();
    }

    public int PageSize => Math.Max(50, AppServices.Settings.PageSize);

    public bool ShowColumns => SelectedReadingMode.Columns > 0 && Columns.Count > 0;

    /// <summary>
    /// 对照模式且真取到了栏。单语正文此时让位——三种模式互斥，
    /// 不再把对照栏追加在单语正文后面。
    /// </summary>
    public bool ShowParallelColumns => ShowDetailBody && ShowColumns;

    /// <summary>单语正文：单语模式，或对照模式暂无栏时兜底显示，绝不留空白区。</summary>
    public bool ShowSingleBody => ShowDetailBody && !ShowColumns;

    public bool HasAttention => Attention.Length > 0;

    public bool HasStatusText => StatusText.Length > 0;

    public bool HasCopyNote => CopyNote.Length > 0;

    public string ParallelScopeLabel => SelectedReadingMode.Columns == 2 ? "日简对照" : "五服对照";

    /// <summary>对照区的一行状态说明（读取失败 / 五服全未覆盖时非空，S-1 / S-2）。</summary>
    public bool ShowParallelNotice => _parallelNotice.Length > 0;

    public string ParallelNoticeText => _parallelNotice;

    private void SetParallelNotice(string text)
    {
        _parallelNotice = text;
        OnPropertyChanged(nameof(ShowParallelNotice));
        OnPropertyChanged(nameof(ParallelNoticeText));
    }

    public string DetailHeader => Detail?.Title ?? "剧情详情";

    public string DetailMeta => Detail is null
        ? string.Empty
        : $"{Detail.KindDisplay} · {Detail.RegionLabel} · {SourceModel.ShortBadge(Detail.Source)}";

    // ── 详情字段的 null 安全包装（S-2 / S-3）──────────────────────────────────
    // x:Bind 对 null 源会跳过子路径更新（详情残留上一条的值），
    // News 页已经用这组只读包装属性解决过，剧情页照抄同一套写法。

    public string DetailAlignmentKey => Detail?.AlignmentKey ?? string.Empty;

    public string DetailStableId => Detail?.Id ?? string.Empty;

    public string DetailUrl => Detail?.Url ?? string.Empty;

    public string DetailCrawledLabel => Detail is null
        ? CoverageLabels.Unmeasured
        : $"抓取 {Detail.CrawledLabel}";

    public string DetailTranslationLabel => Detail is null
        ? string.Empty
        : SelectedItem?.Untranslated == true ? "译文：未译"
        : Detail.TranslationNote.Length > 0 ? Detail.TranslationNote : "译文：无标注";

    public string DetailQualityLabel => Detail?.QualityNote ?? string.Empty;

    /// <summary>有选中详情（S-13 点名的死成员，现在真的被 XAML 消费了）。</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>有质量标记才显示那一行警告，不给空行占位。</summary>
    public bool HasDetailQuality => Detail is { QualityNote.Length: > 0 };

    /// <summary>对白块计数合成一句（S-12：数字与单位不再分家，null 时不留半截）。</summary>
    public string DetailBlockLabel => Detail is null
        ? string.Empty
        : $"{Detail.BlockCount:N0} 个对白块";

    public IReadOnlyList<DialogueBlock> DetailBlocks => Detail?.Blocks ?? [];

    // ── 详情区三态（S-2）─────────────────────────────────────────────────────

    public bool ShowDetailBody => Detail is not null && !IsDetailLoading && _detailFailure.Length == 0;

    public bool ShowDetailEmpty => Detail is null && !IsDetailLoading && _detailFailure.Length == 0;

    public bool ShowDetailFailed => _detailFailure.Length > 0 && !IsDetailLoading;

    public string DetailFailureText => _detailFailure;

    // ── 列表区四态（S-6）────────────────────────────────────────────────────

    public bool ShowListOverlay => _listState != ListState.None;

    public bool ShowListStateFailure => _listState == ListState.Failed;

    public bool ShowListStateInfo => ShowListOverlay && !ShowListStateFailure;

    public bool ShowListClearFilter => _listState is ListState.FilteredToZero or ListState.NoKinds;

    public bool ShowListRetry => _listState is ListState.Failed or ListState.NoStore or ListState.NoData;

    public string ListStateTitle => _listState switch
    {
        ListState.NoStore => "尚未选择本地数据库",
        ListState.NoData => "本地暂无剧情",
        ListState.FilteredToZero => "没有匹配的剧情",
        ListState.NoKinds => "请选择剧情类型",
        ListState.Failed => "剧情列表读取失败",
        _ => string.Empty,
    };

    public string ListStateHint => _listState switch
    {
        ListState.NoStore => "选择本地数据库后重试。",
        ListState.NoData => "可到「同步」页抓取剧情，然后重试。",
        ListState.FilteredToZero => "试试其他关键词，或清除筛选。",
        ListState.NoKinds => "勾选至少一种类型，或清除筛选以全选。",
        ListState.Failed => _listStateDetail.Length > 0
            ? $"{_listStateDetail}\n请重试。"
            : "请重试。",
        _ => string.Empty,
    };

    public async Task InitializeAsync()
    {
        if (Items.Count == 0)
        {
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    /// <summary>显式提交搜索词：分页与刷新只用已提交的词，不会静默带上没回车的输入（S-14）。</summary>
    [RelayCommand]
    private Task SearchAsync()
    {
        _appliedSearch = SearchText.Trim();
        return LoadPageAsync(0);
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
        // 有了总数就不用再「最多 500 跳」地盲翻——那是最初版本最要命的一处。
        var last = _total <= 0 ? 0 : (int)((_total - 1) / PageSize) * PageSize;
        return LoadPageAsync(last);
    }

    /// <summary>空态的「可恢复」：一次点回默认档位（类型全选、清关键词、清搜索、合并视图、全部版本）。</summary>
    [RelayCommand]
    private Task ClearFilterAsync()
    {
        // 一次改 5 个 chip + 3 个下拉，全部改完再统一查一次，否则每改一项打一次库。
        _suppressReload = true;
        try
        {
            foreach (var kind in Kinds)
            {
                kind.IsSelected = true;
            }

            SearchText = string.Empty;
            _appliedSearch = string.Empty;
            OnlyUntranslated = false;
            SelectedVersion = VersionOptions[0];
            SelectedInstance = InstanceOptions[0];
        }
        finally
        {
            _suppressReload = false;
        }

        return LoadPageAsync(0);
    }

    [RelayCommand]
    private Task RetryDetailAsync() => SelectedItem is { } row ? LoadDetailAsync(row) : ReloadAsync();

    [RelayCommand]
    private Task RetryColumnsAsync() => RefreshColumnsAsync();

    [RelayCommand]
    private void CopyAlignmentKey() => CopyToClipboard(DetailAlignmentKey, "已复制对齐键");

    [RelayCommand]
    private void CopyStableId() => CopyToClipboard(DetailStableId, "已复制稳定 ID");

    private void CopyToClipboard(string text, string note)
    {
        if (text.Length == 0)
        {
            Attention = "请先选择一条剧情。";
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            CopyNote = note;
            Attention = string.Empty;
        }
        catch (Exception ex)
        {
            Attention = $"复制失败：{DiagnosticText.Redact(ex.Message)}";
        }
    }

    partial void OnAttentionChanged(string value) => OnPropertyChanged(nameof(HasAttention));

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    partial void OnCopyNoteChanged(string value) => OnPropertyChanged(nameof(HasCopyNote));

    partial void OnSelectedInstanceChanged(InstanceOption value)
    {
        OnPropertyChanged(nameof(ParallelScopeLabel));
        if (!_suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedVersionChanged(VersionOption value)
    {
        if (!_suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnOnlyUntranslatedChanged(bool value)
    {
        if (!_suppressReload)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedReadingModeChanged(ReadingModeOption value)
    {
        OnPropertyChanged(nameof(ParallelScopeLabel));
        AppServices.Settings.StoryReadingMode = value.Label;
        AppServices.Settings.Save();
        _ = RefreshColumnsAsync();
    }

    partial void OnIsDetailLoadingChanged(bool value) => NotifyDetailStates();

    partial void OnDetailChanged(StoryDetail? value)
    {
        CopyNote = string.Empty;
        OnPropertyChanged(nameof(DetailHeader));
        OnPropertyChanged(nameof(DetailMeta));
        OnPropertyChanged(nameof(DetailAlignmentKey));
        OnPropertyChanged(nameof(DetailStableId));
        OnPropertyChanged(nameof(DetailUrl));
        OnPropertyChanged(nameof(DetailCrawledLabel));
        OnPropertyChanged(nameof(DetailTranslationLabel));
        OnPropertyChanged(nameof(DetailQualityLabel));
        OnPropertyChanged(nameof(DetailBlockLabel));
        OnPropertyChanged(nameof(DetailBlocks));
        NotifyDetailStates();
    }

    /// <summary>三个阅读模式互斥：改模式或改栏数时一起通知，避免只刷新一个态。</summary>
    private void NotifyReadingModeStates()
    {
        OnPropertyChanged(nameof(ShowColumns));
        OnPropertyChanged(nameof(ShowParallelColumns));
        OnPropertyChanged(nameof(ShowSingleBody));
    }

    private void NotifyDetailStates()
    {
        OnPropertyChanged(nameof(ShowDetailBody));
        OnPropertyChanged(nameof(ShowParallelColumns));
        OnPropertyChanged(nameof(ShowSingleBody));
        OnPropertyChanged(nameof(ShowDetailEmpty));
        OnPropertyChanged(nameof(ShowDetailFailed));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasDetailQuality));
    }

    private void SetListState(ListState state, string detail = "")
    {
        _listState = state;
        _listStateDetail = detail;
        OnPropertyChanged(nameof(ShowListOverlay));
        OnPropertyChanged(nameof(ShowListStateFailure));
        OnPropertyChanged(nameof(ShowListStateInfo));
        OnPropertyChanged(nameof(ShowListClearFilter));
        OnPropertyChanged(nameof(ShowListRetry));
        OnPropertyChanged(nameof(ListStateTitle));
        OnPropertyChanged(nameof(ListStateHint));
    }

    private async Task ReloadAsync() => await LoadPageAsync(0);

    private StoryQuery BuildQuery() => new()
    {
        Kinds = Kinds.Where(k => k.IsSelected).Select(k => k.Kind).ToList(),
        Instance = SelectedInstance.Filter,
        Language = SelectedVersion.Region?.Language,
        Search = _appliedSearch,
        OnlyUntranslated = OnlyUntranslated,
    };

    /// <summary>有没有生效中的筛选条件——决定 0 行时说「筛选排空」还是「库里没有」。</summary>
    private bool HasActiveFilter =>
        _appliedSearch.Length > 0
        || OnlyUntranslated
        || Kinds.Any(k => !k.IsSelected)
        || SelectedVersion.Region is not null
        || SelectedInstance.Filter != InstanceFilter.Merged;

    private async Task LoadPageAsync(int offset)
    {
        var query = BuildQuery();

        if (query.Kinds.Count == 0)
        {
            // 早退分支必须先取消在飞查询：否则旧结果回包后会把「至少选择一个类型」
            // 的空态盖回一列表，且那些行与当前勾选不符（S-5）。
            CancelInFlight();
            ReplaceItems([]);
            _total = 0;
            _offset = 0;
            SelectedItem = null;
            PagerText = "未查询";
            CanGoPrevious = CanGoNext = false;
            IsLoading = false;
            StatusText = DescribeFilter(query);
            Attention = string.Empty;
            SetListState(ListState.NoKinds);
            return;
        }

        if (!_content.HasLocalDatabase)
        {
            CancelInFlight();
            ReplaceItems([]);
            _total = 0;
            _offset = 0;
            SelectedItem = null;
            PagerText = "—";
            CanGoPrevious = CanGoNext = false;
            IsLoading = false;
            StatusText = DescribeFilter(query);
            Attention = string.Empty;
            SetListState(ListState.NoStore);
            return;
        }

        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        IsLoading = true;

        try
        {
            var total = await _content.CountStoriesAsync(query, cts.Token);
            var page = await _content.QueryStoriesAsync(query, offset, PageSize, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _total = total;
            _offset = page.Offset;
            var keepSource = SelectedItem?.Source;
            var keepId = SelectedItem?.Id;
            ReplaceItems(page.Items);

            var shown = Items.Count == 0 ? 0 : _offset + Items.Count;
            PagerText = _total == 0
                ? "共 0 条"
                : $"第 {_offset + 1:N0} – {shown:N0} 条 · 共 {_total:N0} 条 · {page.ElapsedMilliseconds} ms";
            CanGoPrevious = _offset > 0;
            CanGoNext = shown < _total;
            StatusText = DescribeFilter(query);
            Attention = string.Empty;
            SetListState(Items.Count == 0
                ? (HasActiveFilter ? ListState.FilteredToZero : ListState.NoData)
                : ListState.None);

            RestoreSelection(keepSource, keepId);
        }
        catch (OperationCanceledException)
        {
            // 被新查询取代。
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            // 读取失败不能长得像「没有数据」：清掉旧行（它们属于上一个条件），
            // 并在列表区给一条带原因的失败态（S-6）。
            ReplaceItems([]);
            _total = 0;
            CanGoPrevious = CanGoNext = false;
            PagerText = "查询失败";
            SelectedItem = null;
            StatusText = DescribeFilter(query);
            Attention = string.Empty;
            SetListState(ListState.Failed, DiagnosticText.Redact(ex.Message));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void CancelInFlight()
    {
        _queryCts?.Cancel();
        _queryCts = null;
        _columnsCts?.Cancel();
    }

    /// <summary>整表替换：一次属性通知，避免 1 次 Reset + N 次 Add 把无障碍树整片重建（XP-6）。</summary>
    private void ReplaceItems(IReadOnlyList<StoryRow> rows)
    {
        Items = [.. rows];
        OnPropertyChanged(nameof(Items));
    }

    /// <summary>按 Id + Source 恢复选中，不按引用比较（契约 §3：返回列表要保住选中）。</summary>
    private void RestoreSelection(string? source, string? id)
    {
        var restored = source is null || id is null
            ? null
            : Items.FirstOrDefault(r => r.Id == id && r.Source == source);

        if (restored is not null)
        {
            SelectedItem = restored;
            return;
        }

        if (Items.Count > 0 && (SelectedItem is null || !Items.Contains(SelectedItem)))
        {
            SelectedItem = Items[0];
        }
    }

    private static string DescribeFilter(StoryQuery query) => string.IsNullOrWhiteSpace(query.Search)
        ? string.Empty
        : $"已搜索「{query.Search}」";

    partial void OnSelectedItemChanged(StoryRow? value)
    {
        if (value is null)
        {
            _detailCts?.Cancel();
            IsDetailLoading = false;
            Detail = null;
            _detailFailure = string.Empty;
            Columns.Clear();
            NotifyReadingModeStates();
            NotifyDetailStates();
            return;
        }
        _ = LoadDetailAsync(value);
    }

    private async Task LoadDetailAsync(StoryRow row)
    {
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        // 先把上一条的正文与对照清掉：读取失败时屏幕上必须留下「失败」而不是旧对白（S-2）。
        Detail = null;
        _detailFailure = string.Empty;
        Columns.Clear();
        NotifyReadingModeStates();
        IsDetailLoading = true;

        try
        {
            var detail = await _content.LoadStoryAsync(row.Source, row.Id, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, row))
            {
                return;
            }

            if (detail is null)
            {
                // 行在列表里、却读不到整行：多半是列表与库不同步（store 刚换过）。
                _detailFailure = "本地未找到这条正文，请重试或刷新列表。";
            }
            else
            {
                Detail = detail;
            }

            await RefreshColumnsAsync();
        }
        catch (OperationCanceledException)
        {
            // 被新的选中取代。
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            _detailFailure = $"{DiagnosticText.Redact(ex.Message)}\n请重试或刷新列表。";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsDetailLoading = false;
                OnPropertyChanged(nameof(DetailFailureText));
                NotifyDetailStates();
            }
        }
    }

    /// <summary>按阅读模式取同一条资产的其它语言版本。对齐键跨实例通用，实例筛选必须一路带到查询里（S-4）。</summary>
    private async Task RefreshColumnsAsync()
    {
        _columnsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _columnsCts = cts;

        Columns.Clear();
        SetParallelNotice(string.Empty);
        var row = SelectedItem;
        if (row is null || SelectedReadingMode.Columns == 0)
        {
            NotifyReadingModeStates();
            return;
        }

        try
        {
            var lines = await _content.LoadParallelAsync(row.AlignmentKey, previewLength: 0, SelectedInstance.Filter, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var wanted = (SelectedReadingMode.Columns == 2
                ? lines.Where(l => l.Language is "ja" or "zh_hans")
                : lines).ToList();

            var parsed = await Task.Run(() => wanted.Select(line => new ParallelColumn
                {
                    RegionLabel = line.RegionLabel,
                    Language = line.Language,
                    Badge = line.Badge,
                    Missing = line.Missing,
                    MissingReason = line.MissingReason,
                    MissingSubject = "这一话",
                    Blocks = line.Missing
                        ? []
                        : TextRenderer.ParseDialogue(line.Text, overlay: false, cts.Token),
                }).ToList(), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, row)) return;
            foreach (var column in parsed) Columns.Add(column);

            if (wanted.Any(l => l.MissingReason == ParallelMissingReason.ReadFailed))
            {
                SetParallelNotice("部分对照读取失败，请重试。");
            }
            else if (wanted.Count > 0 && wanted.All(l => l.Missing))
            {
                SetParallelNotice("本地暂无对照正文，可切换实例或同步后重试。");
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的对照请求取代。
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            // 兜底：服务层已把失败转成 ReadFailed 占位栏，这里只兜住意外，绝不留空白区。
            App.Log($"StoryViewModel: 对照刷新失败：{ex.Message}");
            foreach (var region in SourceModel.Regions)
            {
                Columns.Add(new ParallelColumn
                {
                    RegionLabel = region.DisplayName,
                    Language = region.Language,
                    Badge = string.Empty,
                    Missing = true,
                    MissingReason = ParallelMissingReason.ReadFailed,
                    MissingSubject = "这一话",
                    Blocks = [],
                });
            }

            SetParallelNotice($"对照读取失败：{DiagnosticText.Redact(ex.Message)}");
        }

        NotifyReadingModeStates();
    }
}
