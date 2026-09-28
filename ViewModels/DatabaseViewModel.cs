using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>表下拉框选项。</summary>
public sealed class DbTableOption
{
    public required DbTableInfo Info { get; init; }

    public string Label => Info.DisplayName;

    public string Details => $"{Info.Label} · {Info.Name}\n{Info.Description}";

    public override string ToString() => Label;
}

/// <summary>
/// 主界面：本地数据库阅览（只读）。表选择 / 分页 / LIKE 搜索 / 行详情。
///
/// 文案分工（审计 D-2）：
///   <see cref="StatusText"/>   只描述当前表，不再兼任错误载体；
///   <see cref="ErrorText"/>    查询/详情失败，带 <see cref="HasError"/> 的可视宿主；
///   <see cref="DatabaseWarningText"/> 库不可用（含「以只读方式打开失败」）；
///   <see cref="HintText"/>     非打断式的一次性提示（如「数据源已更新，正在重载」）。
/// </summary>
public partial class DatabaseViewModel : ObservableObject
{
    /// <summary>重载提示原文，重载结束后按它清除。</summary>
    private const string ReloadHint = "数据源已更新，正在重载…";

    private readonly DatabaseService _database = AppServices.Database;
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _detailCts;
    private int _offset;
    private bool _hasMore;
    private bool _initializing;
    private bool _rerunRequested;
    private bool _suppressAutoLoad;
    private bool _pastEndOfData;
    private string? _restoreIdentity;

    public static DatabaseViewModel Shared { get; } = new();

    public ObservableCollection<DbTableOption> Tables { get; } = [];
    public ObservableCollection<DbGridColumn> GridColumns { get; } = [];
    public ObservableCollection<DbRowItem> Rows { get; } = [];

    [ObservableProperty]
    private DbTableOption? _selectedTable;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>加载指示可见性（供 x:Bind 直接使用）。</summary>
    public Microsoft.UI.Xaml.Visibility IsLoadingVisibility => IsLoading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    [ObservableProperty]
    private bool _hasDatabase;

    /// <summary>当前表的说明文字。只用于「这张表是什么」，不放错误（审计 D-2）。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _pagerText = "未测量";

    /// <summary>查询/详情失败的可读原因，由页面上的 InfoBar 承载（审计 D-2）。</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>库不可用（未建库 / 以只读方式打开失败 / 读表结构失败）时的原因。</summary>
    [ObservableProperty]
    private string _databaseWarningText = string.Empty;

    /// <summary>非打断式提示（不占用 StatusText，也不弹错误条）。</summary>
    [ObservableProperty]
    private string _hintText = string.Empty;

    /// <summary>是否有提示文字（页面用它切换可见性；WinUI 3 没有 DataTrigger）。</summary>
    public bool HasHint => HintText.Length > 0;

    partial void OnHintTextChanged(string value) => OnPropertyChanged(nameof(HasHint));

    /// <summary>列表四态之一：有筛选词但零行。</summary>
    [ObservableProperty]
    private bool _isEmptyByFilter;

    /// <summary>列表四态之一：库可用、无筛选、这张表本身没有行。</summary>
    [ObservableProperty]
    private bool _isEmptyTable;

    /// <summary>列表四态之一：读取失败且当前没有可信行。</summary>
    [ObservableProperty]
    private bool _isReadFailed;

    /// <summary>当前生效的查询是否带筛选词（决定「末页」能否算得出）。</summary>
    [ObservableProperty]
    private bool _hasActiveFilter;

    /// <summary>顶层上下文：只读标识 + 库文件尾段 + 表数（审计 D-5，手册 §5.1）。</summary>
    [ObservableProperty]
    private string _readOnlyContextText = "本地知识库未加载";

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private DbRowItem? _selectedRow;

    public bool HasSelectedRow => SelectedRow is not null;

    public bool HasNoSelectedRow => !HasSelectedRow;

    [ObservableProperty]
    private string _detailHintText = string.Empty;

    public bool HasDetailHint => !string.IsNullOrWhiteSpace(DetailHintText);

    partial void OnDetailHintTextChanged(string value) => OnPropertyChanged(nameof(HasDetailHint));

    [ObservableProperty]
    private ObservableCollection<DbDetailField> _rowDetail = [];

    [ObservableProperty]
    private string _detailTitle = "行详情";

    [ObservableProperty]
    private int _pageSize = Math.Clamp(AppServices.Settings.PageSize, 50, 1000);

    public int[] PageSizeOptions { get; } = [50, 100, 200, 500, 1000];

    /// <summary>列结构变化（切表/翻页重算列宽后）通知页面重建表头与行模板。</summary>
    public event Action? ColumnsChanged;

    private DbTableInfo? CurrentTable => SelectedTable?.Info;

    public bool ShowDatabaseWarning => !IsLoading && !HasDatabase && !string.IsNullOrWhiteSpace(DatabaseWarningText);

    public bool ShowTableStatus => SelectedTable is null && !IsLoading;

    partial void OnDatabaseWarningTextChanged(string value) => OnPropertyChanged(nameof(ShowDatabaseWarning));

    /// <summary>「末页」只在总数已知（无筛选词）时可点：过滤模式的总数未知，
    /// 原先的 500 轮探测会把界面卡成假死（审计 D-7）。</summary>
    public bool CanGoLastPage => CanGoNext && !HasActiveFilter;

    public DatabaseViewModel()
    {
        // 审计 D-4：原先本页是八个 VM 里唯一不订阅 StoreInvalidated 的，
        // 刚同步完回到原始表看到的还是旧行且没有任何「数据可能过期」提示。
        AppServices.StoreInvalidated += (_, _) => AppServices.EnqueueUi(OnStoreInvalidated);

        // 跨页审计 T-1（SettingsPage 提出、落点在本 VM）：PageSize 原来是构造时的快照，
        // 改「每页行数」后本页要到重启才生效。
        AppServices.Settings.Saved += (_, _) => AppServices.EnqueueUi(OnSettingsSaved);
    }

    private void OnStoreInvalidated()
    {
        HintText = ReloadHint;
        _ = InitializeAsync();
    }

    private void OnSettingsSaved()
    {
        var configured = Math.Clamp(AppServices.Settings.PageSize, 50, 1000);
        if (configured != PageSize)
        {
            // 走 OnPageSizeChanged → 重新查第一页。
            PageSize = configured;
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLoadingVisibility));
        OnPropertyChanged(nameof(ShowDatabaseWarning));
        OnPropertyChanged(nameof(ShowTableStatus));
        UpdateListStates(Rows.Count);
    }

    partial void OnHasDatabaseChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDatabaseWarning));
        UpdateListStates(Rows.Count);
    }

    partial void OnHasErrorChanged(bool value) => UpdateListStates(Rows.Count);

    partial void OnCanGoNextChanged(bool value) => OnPropertyChanged(nameof(CanGoLastPage));

    partial void OnHasActiveFilterChanged(bool value) => OnPropertyChanged(nameof(CanGoLastPage));

    partial void OnSelectedTableChanged(DbTableOption? value)
    {
        OnPropertyChanged(nameof(ShowTableStatus));
        if (HasDatabase && !_suppressAutoLoad)
        {
            _ = LoadPageAsync(0);
        }
    }

    partial void OnPageSizeChanged(int value)
    {
        if (HasDatabase && !_suppressAutoLoad)
        {
            _ = LoadPageAsync(0);
        }
    }

    public async Task InitializeAsync()
    {
        if (_initializing)
        {
            // 重载期间又收到失效信号：跑完这一次再补一次，不并发写同一批集合（审计 SR-11 同型）。
            _rerunRequested = true;
            return;
        }
        _initializing = true;

        IsLoading = true;
        HasError = false;
        ErrorText = string.Empty;
        // 重载前记住选中的是哪张表、哪一行（行按主键值记，不按引用），
        // 下面重灌 Tables/Rows 后按值恢复（实施契约 §3「返回列表要保住筛选/分页/选中」）。
        var previousTableName = SelectedTable?.Info.Name;
        var restoreIdentity = SelectedRow?.IdentityKey;
        try
        {
            await _database.InitializeAsync();
            Tables.Clear();
            GridColumns.Clear();
            Rows.Clear();
            RowDetail.Clear();
            SelectedRow = null;

            if (_database.IsAvailable)
            {
                foreach (var table in _database.Tables)
                {
                    Tables.Add(new DbTableOption { Info = table });
                }
                HasDatabase = true;
                DatabaseWarningText = string.Empty;
                StatusText = _database.Tables.Count > 0
                    ? $"{_database.Tables.Count} 张表"
                    : "暂无数据表，可运行同步后刷新。";
                ReadOnlyContextText = BuildReadOnlyContext(_database.Tables.Count);

                if (Tables.Count > 0)
                {
                    // Tables 里的选项对象每次重载都是新实例，必须按表名重新命中，
                    // 否则 SelectedItem 指向一个不在 ItemsSource 里的旧对象，下拉框会显示空白。
                    var kept = previousTableName is null
                        ? null
                        : Tables.FirstOrDefault(t => t.Info.Name == previousTableName);
                    _suppressAutoLoad = true;
                    SelectedTable = kept ?? Tables[0];
                    _suppressAutoLoad = false;
                    _restoreIdentity = restoreIdentity;
                    await LoadPageAsync(kept is null ? 0 : _offset);
                    ColumnsChanged?.Invoke();
                }
                else
                {
                    SelectedTable = null;
                    _offset = 0;
                    _hasMore = false;
                    CanGoPrevious = false;
                    CanGoNext = false;
                    HasActiveFilter = false;
                    PagerText = "暂无数据表";
                    ColumnsChanged?.Invoke();
                }
                UpdateListStates(Rows.Count);
            }
            else
            {
                HasDatabase = false;
                SelectedTable = null;
                _offset = 0;
                _hasMore = false;
                CanGoPrevious = false;
                CanGoNext = false;
                DatabaseWarningText = _database.UnavailableReason;
                ReadOnlyContextText = BuildReadOnlyContext(0);
                StatusText = "表目录未加载";
                PagerText = "未测量";
                HasActiveFilter = false;
                ColumnsChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // 审计 D-3：原先 InitializeAsync 的异常没人接（页面侧是 async void）。
            SurfaceError(ex, "读取数据库失败");
        }
        finally
        {
            IsLoading = false;
            if (HintText == ReloadHint)
            {
                HintText = string.Empty;
            }
            _initializing = false;
            if (_rerunRequested)
            {
                _rerunRequested = false;
                _ = InitializeAsync();
            }
        }
    }

    /// <summary>只读上下文文案。路径只留尾段，用户名不出现在这句话里。</summary>
    private string BuildReadOnlyContext(int tableCount)
    {
        var tail = DescribePathTail(_database.DatabasePath);
        var countPart = tableCount > 0 ? $"{tableCount} 张表" : "未读到表";
        return $"{tail} · {countPart}";
    }

    private static string DescribePathTail(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "未选择本地数据库";
        }
        try
        {
            var name = Path.GetFileName(path);
            var parent = Path.GetFileName(Path.GetDirectoryName(path));
            return string.IsNullOrEmpty(parent) ? name : $"…{Path.DirectorySeparatorChar}{parent}{Path.DirectorySeparatorChar}{name}";
        }
        catch (Exception)
        {
            return "库路径无法解析";
        }
    }

    /// <summary>列表四态：空表 / 筛选排空 / 读取失败 / 正常（互斥由这里统一算）。</summary>
    private void UpdateListStates(int rowCount)
    {
        var settled = !IsLoading;
        // 末页越界时这一页确实是 0 行，但表不是空的——不能套「空表」措辞。
        IsEmptyByFilter = settled && !HasError && !_pastEndOfData && HasDatabase && CurrentTable is not null && rowCount == 0 && HasActiveFilter;
        IsEmptyTable = settled && !HasError && !_pastEndOfData && HasDatabase && CurrentTable is not null && rowCount == 0 && !HasActiveFilter;
        IsReadFailed = settled && HasError && rowCount == 0;
    }

    /// <summary>
    /// 把异常写成用户能照着做的一句话。
    /// 只读打开失败必须照实说「以只读方式打开失败：…」（审计 R-1 的文案半边），
    /// 不能混进「查询失败」，否则读者会以为是语句写坏了。
    /// </summary>
    private static string BuildErrorMessage(Exception ex, string context)
        => ex is DatabaseUnreachableException ? ex.Message : $"{context}：{DiagnosticText.Redact(ex.Message)}";

    private void SurfaceError(Exception ex, string context)
    {
        ErrorText = BuildErrorMessage(ex, context);
        HasError = true;
    }

    /// <summary>
    /// 页面侧初始化兜底（审计 D-3：`async void OnLoaded` 的异常原先无人接）。
    /// 只写错误与其来源，不猜原因。
    /// </summary>
    public void ReportEntryFailure(Exception ex)
    {
        Rows.Clear();
        SelectedRow = null;
        PagerText = "未取得行，请刷新重试。";
        SurfaceError(ex, "打开原始表视图失败");
        UpdateListStates(0);
    }

    [RelayCommand]
    private Task RefreshAsync() => InitializeAsync();

    [RelayCommand]
    private Task SearchAsync() => HasDatabase ? LoadPageAsync(0) : Task.CompletedTask;

    /// <summary>清除筛选词并重查（筛选排空态的「下一步」，实施契约 §3）。</summary>
    [RelayCommand]
    private Task ClearFilterAsync()
    {
        SearchText = string.Empty;
        return HasDatabase ? LoadPageAsync(0) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task FirstPageAsync() => HasDatabase ? LoadPageAsync(0) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousPageAsync() => HasDatabase && _offset > 0
        ? LoadPageAsync(Math.Max(0, _offset - PageSize))
        : Task.CompletedTask;

    [RelayCommand]
    private Task NextPageAsync() => HasDatabase && _hasMore
        ? LoadPageAsync(_offset + PageSize)
        : Task.CompletedTask;

    [RelayCommand]
    private Task LastPageAsync()
    {
        if (!HasDatabase || !CanGoLastPage)
        {
            return Task.CompletedTask;
        }
        var info = CurrentTable;
        var total = info?.RowCount ?? 0;
        var lastOffset = total == 0 ? 0 : (int)((total - 1) / PageSize) * PageSize;
        return LoadPageAsync(lastOffset);
    }

    private async Task LoadPageAsync(int offset)
    {
        var info = CurrentTable;
        if (info is null)
        {
            Rows.Clear();
            PagerText = "未测量";
            UpdateListStates(0);
            return;
        }

        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;

        IsLoading = true;
        HasError = false;
        ErrorText = string.Empty;
        _pastEndOfData = false;
        var restoreIdentity = _restoreIdentity;
        _restoreIdentity = null;
        SelectedRow = null;
        RowDetail = [];
        try
        {
            var result = await _database.QueryPageAsync(info, SearchText, offset, PageSize, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _offset = result.Offset;
            _hasMore = result.HasMore;
            HasActiveFilter = result.IsFiltered;

            GridColumns.Clear();
            foreach (var column in result.Columns)
            {
                GridColumns.Add(new DbGridColumn(column, ComputeColumnWidth(column)));
            }

            Rows.Clear();
            for (var i = 0; i < result.Cells.Count; i++)
            {
                Rows.Add(new DbRowItem { Cells = result.Cells[i], Keys = result.Keys[i] });
            }
            ColumnsChanged?.Invoke();

            if (restoreIdentity is not null)
            {
                var restored = Rows.FirstOrDefault(r => r.IdentityKey == restoreIdentity);
                if (restored is not null)
                {
                    SelectedRow = restored;
                }
            }

            if (result.Cells.Count == 0 && _offset > 0)
            {
                // 审计 D-8：以前这里显示「空表」，一张其实有数据的表被说成空的。
                // 现在能走到这一步只剩「RowCount 是旧快照、末页越界」这一种情况。
                _pastEndOfData = true;
                PagerText = "这一页没有行——已到末尾，上一页仍有数据。";
                CanGoPrevious = true;
                CanGoNext = false;
            }
            else
            {
                _pastEndOfData = false;
                PagerText = result.Cells.Count == 0
                    ? (result.IsFiltered ? "没有匹配的行。" : "这张表目前没有行。")
                    : $"第 {_offset + 1:N0} – {_offset + result.Cells.Count:N0} 行{(result.IsFiltered ? "（已过滤）" : string.Empty)} · {result.ElapsedMilliseconds} ms";
                CanGoPrevious = _offset > 0;
                // 零行就不该再给「下一页」的希望（审计 D-8）。
                CanGoNext = result.Cells.Count > 0 && result.HasMore;
            }

            // StatusText 只描述表，不再兼任错误载体（审计 D-2）。
            StatusText = $"{info.DisplayName} — {info.Description}";
            UpdateListStates(Rows.Count);
        }
        catch (OperationCanceledException)
        {
            // 被新的查询取代，属正常流程。
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Rows.Clear();
            SelectedRow = null;
            PagerText = "未取得行，请刷新重试。";
            SurfaceError(ex, "查询失败");
            UpdateListStates(0);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnSelectedRowChanged(DbRowItem? value)
    {
        _detailCts?.Cancel();
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(HasNoSelectedRow));
        RowDetail = [];
        DetailTitle = "行详情";
        DetailHintText = value is null ? string.Empty : "正在读取字段…";
        if (value is not null)
        {
            _ = LoadDetailAsync(value);
        }
    }

    private async Task LoadDetailAsync(DbRowItem row)
    {
        var info = CurrentTable;
        if (info is null)
        {
            return;
        }
        using var cts = new CancellationTokenSource();
        _detailCts = cts;
        try
        {
            var fields = await _database.GetRowDetailAsync(info, row.Keys, cts.Token);
            if (!ReferenceEquals(SelectedRow, row) || !ReferenceEquals(CurrentTable, info))
            {
                return;
            }
            HasError = false;
            ErrorText = string.Empty;
            RowDetail = new ObservableCollection<DbDetailField>(fields);
            DetailTitle = $"行详情 — {info.DisplayName}";
            DetailHintText = fields.Length == 0 ? "未读到字段，可刷新后重新选择。" : string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 已切换行或表时，旧请求的错误也不能覆盖新选择。
            if (!ReferenceEquals(SelectedRow, row) || !ReferenceEquals(CurrentTable, info))
            {
                return;
            }
            RowDetail = [];
            DetailTitle = "行详情";
            DetailHintText = "字段读取失败，查看上方原因后重试。";
            SurfaceError(ex, "读取详情失败");
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts)) _detailCts = null;
        }
    }

    /// <summary>按列名给出展示宽度：长文本列加宽，其余按名称长度。</summary>
    public static double ComputeColumnWidth(string column) => column switch
    {
        "text" or "url" or "sentence" or "names_json" or "facts_json" or "extra_json" or "positions_json" or "tags_json" or "untranslated_placeholder" or "value" => 320,
        "id" or "canonical" or "source_hash" or "text_hash" or "original_text_hash" or "source_etag" or "source_last_modified" or "created_at" or "crawled_at" or "source" => 180,
        _ => Math.Clamp(column.Length * 11 + 48, 88, 220),
    };
}
