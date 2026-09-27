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
/// 台词类型 chip（5 类可多选）。
///
/// 禁用必须自带原因（审计 V-6）：<see cref="DisabledReason"/> 同时喂 ToolTip、
/// <c>AutomationProperties.HelpText</c> 和读屏名，读屏用户不再只听到「已禁用」。
/// 原来的 <c>IsDimmed</c> 字段没有任何样式消费它，已删除，由 DisabledReason 承担同一职责。
/// </summary>
public partial class VoiceKindOption : ObservableObject
{
    public VoiceKindOption(string kind, string displayName, bool sekaiViewerOnly)
    {
        Kind = kind;
        DisplayName = displayName;
        SekaiViewerOnly = sekaiViewerOnly;
    }

    public string Kind { get; }

    public string DisplayName { get; }

    /// <summary>只有 Sekai Viewer 抽出了可读文本，Moesekai 停在 master JSON。</summary>
    public bool SekaiViewerOnly { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>当前实例筛选下这一项能不能选。</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>被禁用时的说明（空 = 没有被禁用）。既是 ToolTip 也是 HelpText 的来源。</summary>
    [ObservableProperty]
    private string _disabledReason = string.Empty;

}

/// <summary>
/// 台词页：5 种短文本（主页语音 / 我的世界推文 / 区域对话 / 我的世界对话 / 自我介绍）。
/// 与剧情页的分界是实测长度：这五类均长 14–836 字符，剧情五类均长 560–3,036。
///
/// 列表区四态与对照区失败态各由一组布尔包装属性驱动一个覆盖层
/// （WinUI 3 没有 DataTrigger，状态判断留在 VM，XAML 只绑布尔）。
/// </summary>
public partial class VoicesViewModel : ObservableObject
{
    /// <summary>列表区状态（仅 VM 内部判断，XAML 只消费布尔包装属性）。</summary>
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
    private CancellationTokenSource? _columnsCts;
    private int _offset;
    private long _total;
    private ListState _listState;
    private string _listStateDetail = string.Empty;

    /// <summary>批量改写 chip 勾选状态时抑制重查，由调用方在收尾处统一触发。</summary>
    private bool _suppressKindReload;

    /// <summary>「清除筛选」一次改多项筛选时的重查抑制。</summary>
    private bool _suppressReload;

    /// <summary>
    /// 因为切到「仅 Moesekai」而被自动取消勾选的类型（审计 V-5）。
    /// 切回来时只恢复这些，用户自己取消勾选的保持不动——
    /// 老写法「凡是 SekaiViewerOnly 一律勾上」会静默改写用户的勾选。
    /// </summary>
    private readonly HashSet<string> _autoClearedKinds = new(StringComparer.Ordinal);

    /// <summary>用户实际提交（点搜索 / 回车）的关键词；未提交的输入不参与分页。</summary>
    private string _appliedSearch = string.Empty;

    public static VoicesViewModel Shared { get; } = new();

    /// <summary>整表替换（一次属性通知，而不是 1 次 Reset + 50 次 Add，XP-6）。</summary>
    public ObservableCollection<VoiceRow> Items { get; private set; } = [];

    public ObservableCollection<VoiceKindOption> Kinds { get; } = [];
    public ObservableCollection<ParallelColumn> Columns { get; } = [];

    public IReadOnlyList<InstanceOption> InstanceOptions { get; } = SourceModel.InstanceOptions;
    public IReadOnlyList<VersionOption> VersionOptions { get; } = SourceModel.VersionOptions;

    /// <summary>搜索范围与提交方式，供工具提示和读屏使用。</summary>
    public string SearchScopeHint => "匹配标题和 ID，不含说话人与台词正文。按 Enter 或点击搜索提交；翻页沿用已提交的关键词。";

    [ObservableProperty]
    private InstanceOption _selectedInstance;

    [ObservableProperty]
    private VersionOption _selectedVersion;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>已提交的搜索词；其他筛选状态由原生控件直接显示。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>需要用户注意的事（异常、早退、复制之外的提示）。空 = 不显示。</summary>
    [ObservableProperty]
    private string _attention = string.Empty;

    [ObservableProperty]
    private string _pagerText = "—";

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private VoiceRow? _selectedItem;

    [ObservableProperty]
    private bool _showMoesekaiGap;

    /// <summary>复制动作的结果回执。</summary>
    [ObservableProperty]
    private string _copyNote = string.Empty;

    /// <summary>对照区读取失败的说明（空 = 没有失败）。</summary>
    private string _parallelNotice = string.Empty;

    public VoicesViewModel()
    {
        foreach (var kind in SourceModel.VoiceKinds)
        {
            var option = new VoiceKindOption(
                kind,
                SourceModel.KindDisplay(kind),
                SourceModel.SekaiViewerOnlyKinds.Contains(kind));
            // chip 是 TwoWay 绑到 IsSelected 的，这里补一层观察，勾选即重查。
            option.PropertyChanged += (_, e) =>
            {
                if (!_suppressKindReload && !_suppressReload && e.PropertyName == nameof(VoiceKindOption.IsSelected))
                {
                    _ = ReloadAsync();
                }
            };
            Kinds.Add(option);
        }

        _selectedInstance = InstanceOptions[0];
        _selectedVersion = VersionOptions[0];
        AppServices.StoreInvalidated += (_, _) => _ = ReloadAsync();
        UpdateKindAvailability();
    }

    public int PageSize => Math.Max(50, AppServices.Settings.PageSize);

    public bool HasAttention => Attention.Length > 0;

    public bool HasStatusText => StatusText.Length > 0;

    public bool HasCopyNote => CopyNote.Length > 0;

    /// <summary>
    /// Moesekai 缺口的类型名，由 chip 的 SekaiViewerOnly 派生（V-1）。
    /// 老文案手写「主页语音与我的世界推文」，与真正被禁的两类（我的世界对话 + 我的世界推文）
    /// 只对一个——警告本身在说谎，且以后加一类就会再错一次。
    /// </summary>
    public IReadOnlyList<string> MoesekaiMissingKindNames { get; private set; } = [];

    public string MoesekaiGapTitle => $"Moesekai 暂缺 {MoesekaiMissingKindNames.Count} 类正文";

    public string MoesekaiGapMessage =>
        $"{string.Join("、", MoesekaiMissingKindNames)}已取消勾选。切换至合并视图或 Sekai Viewer 可恢复。";

    public bool HasSelection => SelectedItem is not null;

    /// <summary>详情区的空态（台词详情直接来自列表行，没有独立的读取，所以只有空 / 有内容两态）。</summary>
    public bool ShowDetailEmpty => SelectedItem is null;

    public string DetailSpeaker => SelectedItem is null
        ? "台词详情"
        : SelectedItem.Speaker.Length > 0 ? SelectedItem.Speaker : "无说话人";

    public string DetailText => SelectedItem?.Text ?? string.Empty;

    public string DetailMeta => SelectedItem is null
        ? string.Empty
        : $"{SelectedItem.KindDisplay} · {SelectedItem.RegionLabel} · {SelectedItem.InstanceBadge}";

    public string DetailCrawledLabel => SelectedItem?.CrawledPhrase ?? string.Empty;

    /// <summary>null 安全的对齐键与稳定 id（V-9：这两个值以前在页面上根本不显示）。</summary>
    public string DetailAlignmentKey => SelectedItem?.AlignmentKey ?? string.Empty;

    public string DetailStableId => SelectedItem?.Id ?? string.Empty;

    public string ParallelScopeLabel => "五服对照";

    /// <summary>对照读取失败时的一行说明 + 重试入口（V-3：失败不得长得像「其它服都没有」）。</summary>
    public bool ShowParallelNotice => _parallelNotice.Length > 0;

    public string ParallelNoticeText => _parallelNotice;

    // ── 列表区四态（S-6 同族，V-3 的列表侧）──────────────────────────────────

    /// <summary>切换列表状态并一次性通知覆盖层用到的派生属性（与剧情页同一形状）。</summary>
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

    public bool ShowListOverlay => _listState != ListState.None;

    public bool ShowListStateFailure => _listState == ListState.Failed;

    public bool ShowListStateInfo => ShowListOverlay && !ShowListStateFailure;

    public bool ShowListClearFilter => _listState is ListState.FilteredToZero or ListState.NoKinds;

    public bool ShowListRetry => _listState is ListState.Failed or ListState.NoStore or ListState.NoData;

    public string ListStateTitle => _listState switch
    {
        ListState.NoStore => "尚未选择本地数据库",
        ListState.NoData => "本地暂无台词",
        ListState.FilteredToZero => "没有匹配的台词",
        ListState.NoKinds => "请选择台词类型",
        ListState.Failed => "台词列表读取失败",
        _ => string.Empty,
    };

    public string ListStateHint => _listState switch
    {
        ListState.NoStore => "选择本地数据库后重试。",
        ListState.NoData => "可到「同步」页抓取台词，然后重试。",
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

    /// <summary>显式提交搜索词：分页与刷新只用已提交的词（同 Story 页 S-14）。</summary>
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
        var last = _total <= 0 ? 0 : (int)((_total - 1) / PageSize) * PageSize;
        return LoadPageAsync(last);
    }

    /// <summary>空态的「可恢复」：一次点回默认档位。</summary>
    [RelayCommand]
    private Task ClearFilterAsync()
    {
        _suppressReload = true;
        try
        {
            UpdateKindAvailabilityCore(selectAllEnabled: true);
            SearchText = string.Empty;
            _appliedSearch = string.Empty;
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
    private Task RetryColumnsAsync() => RefreshColumnsAsync();

    [RelayCommand]
    private void CopyAlignmentKey() => CopyToClipboard(DetailAlignmentKey, "已复制对齐键");

    [RelayCommand]
    private void CopyStableId() => CopyToClipboard(DetailStableId, "已复制稳定 ID");

    private void CopyToClipboard(string text, string note)
    {
        if (text.Length == 0)
        {
            Attention = "请先选择一条台词。";
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
        UpdateKindAvailability();
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

    partial void OnSelectedItemChanged(VoiceRow? value)
    {
        CopyNote = string.Empty;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowDetailEmpty));
        OnPropertyChanged(nameof(DetailSpeaker));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(DetailMeta));
        OnPropertyChanged(nameof(DetailCrawledLabel));
        OnPropertyChanged(nameof(DetailAlignmentKey));
        OnPropertyChanged(nameof(DetailStableId));
        _ = RefreshColumnsAsync();
    }

    /// <summary>
    /// 切到「仅 Moesekai」时，缺正文的类型置灰并说明原因，而不是给空列表（doc §4.3）。
    /// 原因文案与 InfoBar 都从 SekaiViewerOnly 派生（V-1 / V-6）。
    /// </summary>
    private void UpdateKindAvailability() => UpdateKindAvailabilityCore(selectAllEnabled: false);

    private void UpdateKindAvailabilityCore(bool selectAllEnabled)
    {
        var onlyMoesekai = SelectedInstance.Filter == InstanceFilter.MoesekaiOnly;
        _suppressKindReload = true;
        try
        {
            foreach (var kind in Kinds)
            {
                var blocked = onlyMoesekai && kind.SekaiViewerOnly;
                kind.IsEnabled = !blocked;
                kind.DisabledReason = blocked
                    ? "Moesekai 暂无此类正文；切换至合并视图或 Sekai Viewer 可选。"
                    : string.Empty;

                if (blocked)
                {
                    if (kind.IsSelected)
                    {
                        _autoClearedKinds.Add(kind.Kind);
                        kind.IsSelected = false;
                    }
                }
                else if (selectAllEnabled)
                {
                    kind.IsSelected = true;
                    _autoClearedKinds.Remove(kind.Kind);
                }
                else if (_autoClearedKinds.Remove(kind.Kind))
                {
                    // 只恢复「因被禁而取消」的那些；用户自己取消勾选的保持不动（V-5）。
                    kind.IsSelected = true;
                }
            }
        }
        finally
        {
            _suppressKindReload = false;
        }

        ShowMoesekaiGap = onlyMoesekai;
        MoesekaiMissingKindNames = Kinds.Where(k => k.SekaiViewerOnly).Select(k => k.DisplayName).ToList();
        OnPropertyChanged(nameof(MoesekaiGapTitle));
        OnPropertyChanged(nameof(MoesekaiGapMessage));
    }

    private VoiceQuery BuildQuery() => new()
    {
        Kinds = Kinds.Where(k => k.IsSelected && k.IsEnabled).Select(k => k.Kind).ToList(),
        Instance = SelectedInstance.Filter,
        Language = SelectedVersion.Region?.Language,
        Search = _appliedSearch,
    };

    /// <summary>有没有生效中的筛选条件——决定 0 行时说「筛选排空」还是「库里没有」。</summary>
    private bool HasActiveFilter =>
        _appliedSearch.Length > 0
        || Kinds.Any(k => !k.IsSelected || !k.IsEnabled)
        || SelectedVersion.Region is not null
        || SelectedInstance.Filter != InstanceFilter.Merged;

    private async Task ReloadAsync() => await LoadPageAsync(0);

    private async Task LoadPageAsync(int offset)
    {
        var query = BuildQuery();

        if (query.Kinds.Count == 0)
        {
            // 早退分支必须先取消在飞查询：否则旧结果回包后会把「至少选择一个类型」
            // 的空态盖回一列表（V-4，与 S-5 同根）。
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
            _total = await _content.CountVoicesAsync(query, cts.Token);
            var page = await _content.QueryVoicesAsync(query, offset, PageSize, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

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
        }
        catch (Exception ex)
        {
            // 读取失败不得长得像「没有数据」：清掉旧行并给一条带原因的失败态（V-3 同族）。
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
    private void ReplaceItems(IReadOnlyList<VoiceRow> rows)
    {
        Items = [.. rows];
        OnPropertyChanged(nameof(Items));
    }

    /// <summary>按 Id + Source 恢复选中，不按引用比较。</summary>
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

    private static string DescribeFilter(VoiceQuery query) => string.IsNullOrWhiteSpace(query.Search)
        ? string.Empty
        : $"已搜索「{query.Search}」";

    /// <summary>单句级五语对照——本地化最实用的粒度。</summary>
    private async Task RefreshColumnsAsync()
    {
        _columnsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _columnsCts = cts;

        Columns.Clear();
        SetParallelNotice(string.Empty);
        var row = SelectedItem;
        if (row is null)
        {
            return;
        }

        try
        {
            // 实例筛选一路带到对照查询里：仅 Moesekai 时不得再排出 Sekai Viewer 的行（V-3 / S-4）。
            var lines = await _content.LoadParallelAsync(row.AlignmentKey, previewLength: 0, SelectedInstance.Filter, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var line in lines)
            {
                Columns.Add(new ParallelColumn
                {
                    RegionLabel = line.RegionLabel,
                    Language = line.Language,
                    Badge = line.Badge,
                    Missing = line.Missing,
                    MissingReason = line.MissingReason,
                    MissingSubject = "这一句",
                    Blocks = line.Missing ? [] : [new DialogueBlock(string.Empty, line.Text, true)],
                });
            }

            if (lines.Any(l => l.MissingReason == ParallelMissingReason.ReadFailed))
            {
                SetParallelNotice("部分对照读取失败，请重试。");
            }
            else if (lines.All(l => l.Missing))
            {
                SetParallelNotice("本地暂无对照正文，可切换实例或同步后重试。");
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的选中取代。
        }
        catch (Exception ex)
        {
            // 兜底：服务层已把失败转成 ReadFailed 占位栏，这里只兜住意外，绝不留空白区。
            App.Log($"VoicesViewModel: 对照刷新失败：{ex.Message}");
            foreach (var region in SourceModel.Regions)
            {
                Columns.Add(new ParallelColumn
                {
                    RegionLabel = region.DisplayName,
                    Language = region.Language,
                    Badge = string.Empty,
                    Missing = true,
                    MissingReason = ParallelMissingReason.ReadFailed,
                    MissingSubject = "这一句",
                    Blocks = [],
                });
            }

            SetParallelNotice($"对照读取失败：{DiagnosticText.Redact(ex.Message)}");
        }
    }

    private void SetParallelNotice(string text)
    {
        _parallelNotice = text;
        OnPropertyChanged(nameof(ShowParallelNotice));
        OnPropertyChanged(nameof(ParallelNoticeText));
    }
}
