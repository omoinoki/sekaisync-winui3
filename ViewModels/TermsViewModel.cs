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

/// <summary>
/// 用语页：三张表合一。
///   术语 = glossary_terms，提取用语 = terms，官方译名 = title_overlay + 剧情级译文覆盖层。
///
/// 合并的理由很直接：原来的用语表看得到证据看不到 names_json 全文，
/// 数据库页看得到表但 names_json 是截断的 JSON —— 两边各缺一半。
///
/// 状态纪律（本轮改的重点）：读取失败、筛选排空、表里真的没有、没有本地库是**四种**状态，
/// 各自一条覆盖层与文案；旧代码把「读取失败」一路降级成「表里没有记录」，是本页的 P0。
/// </summary>
public partial class TermsViewModel : ObservableObject
{
    /// <summary>译名子标签一次渲染的上限。超过必须在界面上说明，不能再静默截断。</summary>
    private const int TranslationRowCeiling = 400;

    /// <summary>剧情级译文只取一页预览；规模另走 CountOverlaysAsync 的实测值。</summary>
    private const int OverlayPreviewCeiling = 300;

    /// <summary>句级证据一屏上限；总数另走 CountEvidenceAsync。</summary>
    private const int EvidenceCeiling = 60;

    private readonly TermQueryService _terms = AppServices.Terms;
    private readonly AppEnvironment _environment = AppServices.Environment;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _evidenceCts;
    private CancellationTokenSource? _translationCts;
    private int _offset;
    private long _total;
    private bool _listFailed;
    private bool _translationFailed;
    private bool _suppressDetailLoad;
    private bool _suppressFilterReload;
    private string? _restoreSelectedId;
    private string? _detailForId;
    private bool _translationLoaded;
    private bool _overlayLoaded;
    private bool _dirty = true;
    private List<TranslationNameRow> _allTranslationRows = [];
    private readonly Dictionary<string, TabMemory> _tabs = new(StringComparer.Ordinal);

    public static TermsViewModel Shared { get; } = new();

    /// <summary>整表替换而不是 Clear+Add：Clear 会把 ListView 送回顶部、丢掉选中并重建无障碍树。</summary>
    private ObservableCollection<TermRow> _items = [];

    public ObservableCollection<TermRow> Items
    {
        get => _items;
        private set
        {
            if (ReferenceEquals(_items, value))
            {
                return;
            }

            _items = value;
            OnPropertyChanged();
        }
    }

    private ObservableCollection<TranslationNameRow> _translationRows = [];

    /// <summary>同样是整表替换：子标签 3 每次筛都会重建这块表。</summary>
    public ObservableCollection<TranslationNameRow> TranslationRows
    {
        get => _translationRows;
        private set
        {
            if (ReferenceEquals(_translationRows, value))
            {
                return;
            }

            _translationRows = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<EvidenceRow> Evidence { get; } = [];
    public ObservableCollection<LabelValue> NameRows { get; } = [];
    public ObservableCollection<TranslationNamespace> Namespaces { get; } = [];
    public ObservableCollection<LabelValue> OverlayStatus { get; } = [];
    public ObservableCollection<OverlayRow> Overlays { get; } = [];

    // 只缩短本页标签；保留共享选项的筛选值和其它页面的展示方式。
    public IReadOnlyList<InstanceOption> InstanceOptions { get; } = SourceModel.InstanceOptions
        .Select(option => new InstanceOption(option.Filter switch
        {
            InstanceFilter.Merged => "合并视图",
            InstanceFilter.SekaiViewerOnly => "Sekai Viewer",
            InstanceFilter.MoesekaiOnly => "Moesekai",
            _ => option.Label,
        }, option.Filter)).ToArray();

    public IReadOnlyList<VersionOption> VersionOptions { get; } = SourceModel.VersionOptions;

    [ObservableProperty]
    private string _selectedTab = "glossary";

    /// <summary>搜索框里正在输入的值。只有回车 / 「搜索」才落到 AppliedSearch。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>已经提交的搜索词。分页与刷新只认它，避免未提交的词被静默带进查询。</summary>
    [ObservableProperty]
    private string _appliedSearch = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>需要用户注意的一行：读取失败、跨页动作结果等。带状态色 + live 播报。</summary>
    [ObservableProperty]
    private string _attention = string.Empty;

    [ObservableProperty]
    private bool _isAttentionError;

    [ObservableProperty]
    private bool _isAttentionWarning;

    [ObservableProperty]
    private string _pagerText = "未测量";

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private TermRow? _selectedItem;

    [ObservableProperty]
    private TranslationNamespace? _selectedNamespace;

    [ObservableProperty]
    private string _detailTitle = "选择词条查看详情";

    [ObservableProperty]
    private string _detailId = string.Empty;

    [ObservableProperty]
    private InstanceOption _selectedInstance;

    [ObservableProperty]
    private VersionOption _selectedVersion;

    /// <summary>详情里 names_json 重读失败、退回列表已载值时的说明（空 = 没有发生）。</summary>
    [ObservableProperty]
    private string _namesNote = string.Empty;

    // ── 列表四态（互斥，由 RefreshListStates 统一算）────────────────────────
    [ObservableProperty]
    private bool _showNoStore;

    [ObservableProperty]
    private bool _showListFailed;

    [ObservableProperty]
    private bool _showListFilterEmpty;

    [ObservableProperty]
    private bool _showListTableEmpty;

    [ObservableProperty]
    private string _listError = string.Empty;

    // ── 句级证据三态 ────────────────────────────────────────────────────────
    // 加载中与「无证据」两态不在这里建标志位：两者都由 EvidenceSummary 直接呈现
    // （「读取中…」/「未收录」），再加一层布尔只会多一处要同步的真相。
    [ObservableProperty]
    private bool _hasEvidence;

    [ObservableProperty]
    private bool _isEvidenceFailed;

    [ObservableProperty]
    private string _evidenceSummary = string.Empty;

    [ObservableProperty]
    private string _evidenceError = string.Empty;

    // ── 子标签 3 的状态 ─────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _showTranslationFailed;

    [ObservableProperty]
    private bool _showTranslationFilterEmpty;

    [ObservableProperty]
    private bool _showNamespaceFailed;

    [ObservableProperty]
    private string _translationError = string.Empty;

    [ObservableProperty]
    private bool _isTranslationTruncated;

    /// <summary>命中数没超上限时的说明行（与「被截断」用不同强度，别混成一句）。</summary>
    [ObservableProperty]
    private bool _showTranslationNotePlain;

    [ObservableProperty]
    private string _translationLimitNote = string.Empty;

    [ObservableProperty]
    private string _dictionarySummary = string.Empty;

    [ObservableProperty]
    private string _overlayCoverage = string.Empty;

    [ObservableProperty]
    private bool _showOverlayFailed;

    [ObservableProperty]
    private string _overlayError = string.Empty;

    public TermsViewModel()
    {
        _selectedInstance = InstanceOptions[0];
        _selectedVersion = VersionOptions[0];

        AppServices.StoreInvalidated += (_, _) =>
        {
            _translationLoaded = false;
            _overlayLoaded = false;
            _dirty = true;
        };
    }

    public int PageSize => Math.Max(50, AppServices.Settings.PageSize);

    public bool IsGlossaryTab => SelectedTab == "glossary";

    public bool IsTermsTab => SelectedTab == "terms";

    public bool IsTranslationTab => SelectedTab == "translation";

    public Visibility TranslationVisibility => IsTranslationTab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>共用词条表的两个子标签合成一块可见性，省掉一层 Grid 切换。</summary>
    public Visibility TermListVisibility => IsTranslationTab ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>译名子标签不翻页（它一次性载入后本地筛选），但分页栏藏掉后规模必须另说（见 TranslationLimitNote）。</summary>
    public Visibility PagerVisibility => IsTranslationTab ? Visibility.Collapsed : Visibility.Visible;

    public string CurrentTable => IsTermsTab ? TermQueryService.TermsTable : TermQueryService.GlossaryTable;

    public string InstanceFilterNote => IsTranslationTab
        ? "实例筛选应用于译名和剧情译文；合并视图优先采用 Sekai Viewer 的冲突项。"
        : "术语与提取用语为两站合并数据，未区分来源实例。";

    public string SearchScopeHint => IsTranslationTab
        ? "搜索标识与五语译名，按回车或点击搜索执行。"
        : "搜索词条、五语名称与标识，按回车或点击搜索执行。";

    public bool HasUnsubmittedSearch => !string.Equals(SearchText, AppliedSearch, StringComparison.Ordinal);

    private bool HasActiveListFilter =>
        !string.IsNullOrWhiteSpace(AppliedSearch) || CurrentTermFilter().HasLanguage;

    private TermQueryService.TermFilter CurrentTermFilter() => new()
    {
        // 术语两表没有 source 列：那里不吃实例（见 InstanceFilterNote）。
        Sources = IsTranslationTab ? SourcesFor(SelectedInstance) : Array.Empty<string>(),
        Language = SelectedVersion.Region?.Language ?? string.Empty,
    };

    private static IReadOnlyList<string> SourcesFor(InstanceOption option)
    {
        return option.Filter switch
        {
            InstanceFilter.SekaiViewerOnly => new[] { SourceModel.SekaiViewerPrimary, SourceModel.SekaiViewerOverlay },
            InstanceFilter.MoesekaiOnly => new[] { SourceModel.MoesekaiPrimary, SourceModel.MoesekaiOverlay },
            _ => Array.Empty<string>(),
        };
    }

    partial void OnSelectedTabChanged(string value)
    {
        NotifyTabDerived();

        if (IsTranslationTab)
        {
            _ = LoadTranslationAsync();
        }
        else
        {
            _ = LoadPageAsync(MemoryFor(CurrentTable).Offset);
        }
    }

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(HasUnsubmittedSearch));

    partial void OnAppliedSearchChanged(string value) => OnPropertyChanged(nameof(HasUnsubmittedSearch));

    partial void OnSelectedItemChanged(TermRow? value)
    {
        if (value is null)
        {
            // 换整表时 ListView 会把 SelectedItem 冲成 null（有时还晚一帧）。
            // 按 Id 复位，别把用户的选中丢掉，也别因此重打一次库。
            if (_restoreSelectedId is not null)
            {
                var id = _restoreSelectedId;
                AppServices.EnqueueUi(() => RestoreSelectedById(id));
            }

            return;
        }

        MemoryFor(CurrentTable).SelectedId = value.Id;
        if (_suppressDetailLoad || string.Equals(_detailForId, value.Id, StringComparison.Ordinal))
        {
            return;
        }

        _ = LoadDetailAsync(value);
    }

    private void RestoreSelectedById(string id)
    {
        if (SelectedItem is not null)
        {
            return;
        }

        var target = Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (target is not null)
        {
            SelectedItem = target;
        }
    }

    partial void OnSelectedInstanceChanged(InstanceOption value)
    {
        if (_suppressFilterReload)
        {
            return;
        }

        OnPropertyChanged(nameof(InstanceFilterNote));
        if (!IsTranslationTab)
        {
            return;
        }

        // 字典与覆盖层都要按新实例重取。
        _translationLoaded = false;
        _overlayLoaded = false;
        _ = ReloadTranslationAsync();
    }

    partial void OnSelectedVersionChanged(VersionOption value)
    {
        if (_suppressFilterReload)
        {
            return;
        }

        if (IsTranslationTab)
        {
            // 字典与覆盖层预览都按新语言重取，不能只换字典却留着上一档的预览。
            _translationLoaded = false;
            _overlayLoaded = false;
            _ = ReloadTranslationAsync();
        }
        else
        {
            _ = LoadPageAsync(0);
        }
    }

    partial void OnSelectedNamespaceChanged(TranslationNamespace? value) => ApplyTranslationFilter();

    /// <summary>
    /// 首次进入。回到本页时**不重查**（列表、分页、选中都在这个单例 VM 里），
    /// 只有首次、库变过、或上一次读取失败时才查。
    /// </summary>
    public async Task InitializeAsync()
    {
        RefreshNoStoreState();

        if (IsTranslationTab)
        {
            await LoadTranslationAsync();
            _dirty = false;
            return;
        }

        if (_dirty || Items.Count == 0 || _listFailed)
        {
            await LoadPageAsync(_dirty ? 0 : _offset);
        }
        else
        {
            RefreshListStates();
        }

        _dirty = false;
    }

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        // 重复点当前子标签不再打一次库、不再把选中与滚动位置清掉。
        if (string.IsNullOrEmpty(tab) || string.Equals(SelectedTab, tab, StringComparison.Ordinal))
        {
            return;
        }

        SelectedTab = tab;
    }

    [RelayCommand]
    private Task RefreshAsync() => IsTranslationTab ? ReloadTranslationAsync() : LoadPageAsync(_offset);

    [RelayCommand]
    private Task SearchAsync()
    {
        AppliedSearch = SearchText.Trim();
        if (!IsTranslationTab)
        {
            return LoadPageAsync(0);
        }

        // 字典一次性载入后走内存筛选，不再为了改个词打库。
        if (_translationLoaded)
        {
            ApplyTranslationFilter();
            return Task.CompletedTask;
        }

        return ReloadTranslationAsync();
    }

    /// <summary>清除本页的收窄条件（搜索词 / 版本 / 实例 / namespace），回到无条件列表。</summary>
    [RelayCommand]
    private Task ClearFilterAsync()
    {
        _suppressFilterReload = true;
        SearchText = string.Empty;
        AppliedSearch = string.Empty;
        SelectedVersion = VersionOptions[0];
        SelectedInstance = InstanceOptions[0];
        _suppressFilterReload = false;
        return IsTranslationTab ? ReloadTranslationAsync() : LoadPageAsync(0);
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
        // 先数再算末页，不做「最多 500 跳」式盲翻。
        var last = _total <= 0 ? 0 : (int)((_total - 1) / PageSize) * PageSize;
        return LoadPageAsync(last);
    }

    [RelayCommand]
    private Task RetryListAsync() => LoadPageAsync(_offset);

    [RelayCommand]
    private Task RetryEvidenceAsync() => LoadDetailForSelectionAsync();

    [RelayCommand]
    private Task RetryTranslationAsync()
    {
        _translationLoaded = false;
        _overlayLoaded = false;
        return ReloadTranslationAsync();
    }

    private TabMemory MemoryFor(string table)
    {
        if (!_tabs.TryGetValue(table, out var memory))
        {
            memory = new TabMemory();
            _tabs[table] = memory;
        }

        return memory;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 子标签 1 / 2：术语与提取用语
    // ─────────────────────────────────────────────────────────────────────────

    private async Task LoadPageAsync(int offset)
    {
        var table = CurrentTable;
        var memory = MemoryFor(table);

        // 早退分支也要先取消在飞查询，否则旧结果会回灌进空态。
        _queryCts?.Cancel();
        _queryCts = null;

        if (!_environment.DatabaseExists)
        {
            _restoreSelectedId = null;
            _suppressDetailLoad = true;
            Items = [];
            SelectedItem = null;
            _suppressDetailLoad = false;
            ClearDetail();
            _total = 0;
            _offset = 0;
            memory.Offset = 0;
            CanGoPrevious = CanGoNext = false;
            PagerText = "未测量（没有本地库）";
            _listFailed = false;
            ListError = string.Empty;
            Attention = string.Empty;
            IsAttentionWarning = false;
            IsAttentionError = false;
            IsLoading = false;
            RefreshListStates();
            return;
        }

        var cts = new CancellationTokenSource();
        _queryCts = cts;
        IsLoading = true;

        try
        {
            var filter = CurrentTermFilter();
            var search = AppliedSearch;

            _total = await _terms.CountTermsAsync(table, search, filter, cts.Token);
            var page = await _terms.QueryTermsAsync(table, search, offset, PageSize, filter, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _offset = page.Offset;
            memory.Offset = page.Offset;

            // 按 Id 恢复选中：整表替换后旧引用必然对不上。
            var keepId = SelectedItem?.Id ?? memory.SelectedId;
            var restored = page.Items.FirstOrDefault(i => string.Equals(i.Id, keepId, StringComparison.Ordinal))
                           ?? page.Items.FirstOrDefault();
            _restoreSelectedId = restored?.Id;
            _suppressDetailLoad = true;
            Items = new ObservableCollection<TermRow>(page.Items);
            SelectedItem = restored;
            memory.SelectedId = restored?.Id;
            _suppressDetailLoad = false;

            _listFailed = false;
            ListError = string.Empty;
            var shown = Items.Count == 0 ? 0 : _offset + Items.Count;
            PagerText = _total == 0
                ? "0 条"
                : $"第 {_offset + 1:N0} – {shown:N0} 条 · 共 {_total:N0} 条";
            CanGoPrevious = _offset > 0;
            CanGoNext = shown < _total;
            Attention = string.Empty;
            IsAttentionError = IsAttentionWarning = false;

            if (restored is null)
            {
                ClearDetail();
            }
            else
            {
                // 同一 Id 的详情还在屏上时不重读，翻页回来不该闪一下。
                if (!string.Equals(_detailForId, restored.Id, StringComparison.Ordinal) || NameRows.Count == 0)
                {
                    await LoadDetailAsync(restored);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 被更新的查询取代，不改状态。
        }
        catch (Exception ex)
        {
            _listFailed = true;
            ListError = DiagnosticText.Redact(ex.Message);
            Attention = "用语读取失败，可在列表中重试。";
            IsAttentionError = true;
            IsAttentionWarning = false;
            PagerText = "条数未知（读取失败）";
            CanGoPrevious = CanGoNext = false;
            App.Log($"TermsViewModel: 查询失败：{ex.Message}");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
                RefreshListStates();
            }
        }
    }

    private void RefreshListStates()
    {
        var noStore = !_environment.DatabaseExists;
        ShowNoStore = noStore;
        ShowListFailed = !noStore && _listFailed;
        ShowListFilterEmpty = !noStore && !_listFailed && _total == 0 && HasActiveListFilter;
        ShowListTableEmpty = !noStore && !_listFailed && _total == 0 && !HasActiveListFilter;
    }

    private void RefreshNoStoreState()
    {
        ShowNoStore = !_environment.DatabaseExists;
        if (ShowNoStore)
        {
            ShowListFailed = false;
            ShowListFilterEmpty = false;
            ShowListTableEmpty = false;
            ShowTranslationFailed = false;
            ShowTranslationFilterEmpty = false;
            ShowNamespaceFailed = false;
            ShowOverlayFailed = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 详情：五语对照 + 句级证据
    // ─────────────────────────────────────────────────────────────────────────

    private void ClearDetail()
    {
        _detailForId = null;
        NameRows.Clear();
        Evidence.Clear();
        DetailTitle = "选择词条查看详情";
        DetailId = string.Empty;
        NamesNote = string.Empty;
        HasEvidence = false;
        IsEvidenceFailed = false;
        EvidenceError = string.Empty;
        EvidenceSummary = string.Empty;
    }

    private async Task LoadDetailForSelectionAsync()
    {
        var row = SelectedItem;
        if (row is null)
        {
            ClearDetail();
            return;
        }

        await LoadDetailAsync(row);
    }

    private async Task LoadDetailAsync(TermRow row)
    {
        _evidenceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _evidenceCts = cts;

        _detailForId = row.Id;
        NameRows.Clear();
        Evidence.Clear();
        DetailTitle = string.IsNullOrWhiteSpace(row.Canonical) ? "未命名词条" : row.Canonical;
        DetailId = row.Id;
        NamesNote = string.Empty;
        HasEvidence = false;
        IsEvidenceFailed = false;
        EvidenceError = string.Empty;
        EvidenceSummary = "读取中…";

        // 列表里的 names_json 已是完整值；仍重读一次拿规范顺序，失败时如实上报并退回列表值。
        var namesJson = row.NamesJson;
        try
        {
            namesJson = await _terms.LoadNamesJsonAsync(row.Table, row.Id, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            NamesNote = $"名称刷新失败，暂显示列表值：{DiagnosticText.Redact(ex.Message)}";
            App.Log($"TermsViewModel: 读取 names_json 失败：{ex.Message}");
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, row))
        {
            return;
        }

        foreach (var cell in TextRenderer.NameTable(namesJson))
        {
            // 仅替换本页缺值说明；解析失败仍保留独立状态，正常名称原样展示。
            NameRows.Add(cell.IsMissing
                ? new LabelValue(cell.Label, cell.Value == TextRenderer.UnparsableNameText
                    ? "名称格式异常；可在「数据源 → 原始表」查看原值。"
                    : "未收录", true)
                : cell);
        }

        try
        {
            var evidence = await _terms.LoadEvidenceAsync(row.Id, EvidenceCeiling, cts.Token);
            var total = await _terms.CountEvidenceAsync(row.Id, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedItem, row))
            {
                return;
            }

            foreach (var item in evidence)
            {
                Evidence.Add(item);
            }

            HasEvidence = total > 0;
            if (Attention == "句级证据读取失败，可在详情中重试。")
            {
                Attention = string.Empty;
                IsAttentionError = IsAttentionWarning = false;
            }

            EvidenceSummary = total == 0
                ? "未收录"
                : total > Evidence.Count
                    ? $"共 {total:N0} 条 · 展示前 {Evidence.Count:N0} 条（已截断）"
                    : $"{total:N0} 条";
        }
        catch (OperationCanceledException)
        {
            // 换选中项了。
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            IsEvidenceFailed = true;
            EvidenceError = DiagnosticText.Redact(ex.Message);
            EvidenceSummary = "证据读取失败";
            Attention = "句级证据读取失败，可在详情中重试。";
            IsAttentionError = true;
            IsAttentionWarning = false;
            App.Log($"TermsViewModel: 读取证据失败：{ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 子标签 3：官方译名（字典 + 剧情级译文覆盖层）
    // ─────────────────────────────────────────────────────────────────────────

    private async Task LoadTranslationAsync()
    {
        if (_translationLoaded && !_translationFailed)
        {
            RefreshTranslationStates();
            return;
        }

        await ReloadTranslationAsync();
    }

    private async Task ReloadTranslationAsync()
    {
        _translationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _translationCts = cts;
        IsLoading = true;

        if (!_environment.DatabaseExists)
        {
            _allTranslationRows = [];
            Namespaces.Clear();
            TranslationRows = [];
            Overlays.Clear();
            OverlayStatus.Clear();
            IsTranslationTruncated = false;
            ShowTranslationNotePlain = false;
            TranslationLimitNote = string.Empty;
            _translationFailed = false;
            RefreshNoStoreState();
            RefreshTranslationStates();
            DictionarySummary = string.Empty;
            OverlayCoverage = "未测量（没有本地库）";
            IsLoading = false;
            return;
        }

        var dictionaryFailed = false;
        var overlayStatusFailed = false;
        var overlayCountFailed = false;
        string? error = null;

        try
        {
            var filter = CurrentTermFilter();

            try
            {
                _allTranslationRows = [.. await _terms.LoadTranslationNamesAsync(filter, cts.Token)];
                dictionaryFailed = false;
            }
            catch (Exception ex)
            {
                dictionaryFailed = true;
                error = DiagnosticText.Redact(ex.Message);
                App.Log($"TermsViewModel: 读取官方译名字典失败：{ex.Message}");
                _allTranslationRows = [];
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _translationFailed = dictionaryFailed;
            TranslationError = dictionaryFailed ? error ?? "读取失败" : string.Empty;

            Namespaces.Clear();
            var selectedKey = SelectedNamespace?.Namespace;
            foreach (var group in _allTranslationRows
                         .GroupBy(r => r.Namespace)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Namespaces.Add(new TranslationNamespace
                {
                    Namespace = group.Key,
                    Rows = group.Count(),
                    Missing = group.Count(r =>
                        r.Ja.Length == 0 || r.En.Length == 0 || r.ZhHans.Length == 0 ||
                        r.ZhHant.Length == 0 || r.Ko.Length == 0),
                });
            }

            var restoredNamespace = Namespaces.FirstOrDefault(n => n.Namespace == selectedKey) ?? Namespaces.FirstOrDefault();
            SelectedNamespace = restoredNamespace;
            ApplyTranslationFilter();

            OverlayStatus.Clear();
            OverlayError = string.Empty;
            try
            {
                foreach (var item in await _terms.LoadOverlayStatusAsync(filter, cts.Token))
                {
                    OverlayStatus.Add(item);
                }
            }
            catch (Exception ex)
            {
                overlayStatusFailed = true;
                OverlayError = DiagnosticText.Redact(ex.Message);
                App.Log($"TermsViewModel: 读取覆盖层状态失败：{ex.Message}");
            }

            ShowOverlayFailed = overlayStatusFailed;

            // 覆盖层规模取实测 Count，不再拿已加载页数冒充总数。
            if (overlayStatusFailed)
            {
                OverlayCoverage = "译文覆盖读取失败，条数未知。";
            }
            else
            {
                try
                {
                    var overlayTotal = await _terms.CountOverlaysAsync(filter, cts.Token);
                    if (!_overlayLoaded)
                    {
                        var overlays = await _terms.QueryOverlaysAsync(0, OverlayPreviewCeiling, filter, cts.Token);
                        Overlays.Clear();
                        foreach (var row in overlays.Items)
                        {
                            Overlays.Add(row);
                        }

                        _overlayLoaded = true;
                    }

                    OverlayCoverage = overlayTotal == 0
                        ? "未收录剧情译文"
                        : overlayTotal > Overlays.Count
                            ? $"共 {overlayTotal:N0} 条 · 预览前 {Overlays.Count:N0} 条（已截断）"
                            : $"剧情译文 {overlayTotal:N0} 条";
                }
                catch (Exception ex)
                {
                    overlayCountFailed = true;
                    OverlayError = DiagnosticText.Redact(ex.Message);
                    OverlayCoverage = "剧情译文计数或预览读取失败。";
                    App.Log($"TermsViewModel: 读取覆盖层条数失败：{ex.Message}");
                }
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            ShowOverlayFailed = overlayStatusFailed || overlayCountFailed;
            if (dictionaryFailed || ShowOverlayFailed)
            {
                Attention = dictionaryFailed && ShowOverlayFailed
                    ? "译名与译文覆盖读取失败，请分别查看详情。"
                    : dictionaryFailed ? "译名读取失败，可在列表中重试。" : "译文覆盖读取失败，展开「译文覆盖」查看详情。";
                IsAttentionError = true;
                IsAttentionWarning = false;
            }
            else
            {
                Attention = string.Empty;
                IsAttentionError = IsAttentionWarning = false;
                _translationLoaded = true;
            }

            DictionarySummary = dictionaryFailed
                ? "译名字典读取失败"
                : $"译名 {_allTranslationRows.Count:N0} 条 · {Namespaces.Count} 个分类";
        }
        catch (OperationCanceledException)
        {
            // 被新条件取代。
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
                RefreshTranslationStates();
            }
        }
    }

    private void ApplyTranslationFilter()
    {
        var ns = SelectedNamespace?.Namespace;
        var needle = AppliedSearch.Trim();
        var matched = new List<TranslationNameRow>();

        foreach (var row in _allTranslationRows)
        {
            if (ns is not null && row.Namespace != ns)
            {
                continue;
            }

            if (TermQueryService.TranslationRowMatches(row, needle))
            {
                matched.Add(row);
            }
        }

        // 整表替换：Clear + 逐条 Add 会让 ListView 回顶部并重建无障碍树。
        var next = new ObservableCollection<TranslationNameRow>(matched.Take(TranslationRowCeiling));
        TranslationRows = next;

        IsTranslationTruncated = matched.Count > TranslationRowCeiling;
        TranslationLimitNote = _translationFailed ? string.Empty : matched.Count switch
        {
            > TranslationRowCeiling =>
                $"匹配 {matched.Count:N0} 条 · 展示前 {TranslationRowCeiling} 条（已截断），可按分类或搜索词缩小范围。",
            _ => $"匹配 {matched.Count:N0} 条 · 点击条目查看全文",
        };
        ShowTranslationNotePlain = !IsTranslationTruncated && TranslationLimitNote.Length > 0;

        ShowNamespaceFailed = _translationFailed && Namespaces.Count == 0;
        RefreshTranslationStates();
    }

    private void RefreshTranslationStates()
    {
        if (ShowNoStore)
        {
            return;
        }

        ShowTranslationFailed = _translationFailed;
        ShowTranslationFilterEmpty = !_translationFailed && TranslationRows.Count == 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 证据 → 剧情页定位
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把 story_key 交给剧情页当搜索词并让它重查（「跳转 + 定位」的定位那一半，
    /// 跳转由 TermsPage 通过壳层导航栏完成）。失败时返回 false，调用方保留可复制的完整键值。
    /// </summary>
    public bool TryApplyStoryLocate(string storyKey)
    {
        if (string.IsNullOrWhiteSpace(storyKey))
        {
            Attention = "出处标识未收录，暂不能定位。";
            IsAttentionWarning = true;
            IsAttentionError = false;
            return false;
        }

        try
        {
            var story = StoryViewModel.Shared;
            story.SearchText = storyKey;
            if (story.SearchCommand.CanExecute(null))
            {
                story.SearchCommand.Execute(null);
            }

            Attention = $"已在剧情页搜索「{storyKey}」。";
            IsAttentionWarning = false;
            IsAttentionError = false;
            return true;
        }
        catch (Exception ex)
        {
            Attention = $"剧情定位失败：{DiagnosticText.Redact(ex.Message)}。出处标识：{storyKey}";
            IsAttentionError = true;
            IsAttentionWarning = false;
            App.Log($"TermsViewModel: 定位剧情失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>导航栏没找到（界面结构异常）时如实说，不静默。</summary>
    public void ReportLocateBlocked(string storyKey)
    {
        Attention = $"导航栏未就绪，请手动打开剧情页。检索词已填入：{storyKey}";
        IsAttentionWarning = true;
        IsAttentionError = false;
    }

    private void NotifyTabDerived()
    {
        OnPropertyChanged(nameof(IsGlossaryTab));
        OnPropertyChanged(nameof(IsTermsTab));
        OnPropertyChanged(nameof(IsTranslationTab));
        OnPropertyChanged(nameof(TranslationVisibility));
        OnPropertyChanged(nameof(TermListVisibility));
        OnPropertyChanged(nameof(PagerVisibility));
        OnPropertyChanged(nameof(CurrentTable));
        OnPropertyChanged(nameof(InstanceFilterNote));
        OnPropertyChanged(nameof(SearchScopeHint));
    }

    /// <summary>每个子标签记住自己的页码与选中项，切回来不必从头再来。</summary>
    private sealed class TabMemory
    {
        public int Offset { get; set; }

        public string? SelectedId { get; set; }
    }
}
