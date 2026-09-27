using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 列表区状态（审计 N-5）。手册 §5.1：空库、无数据、筛选排空、读取失败是**四种**不同状态，
/// 不能都渲染成同一片空白。WinUI 3 没有 DataTrigger，所以界面只消费本 VM 的布尔包装属性。
/// </summary>
public enum NewsListState
{
    /// <summary>正在读取。</summary>
    Loading,

    /// <summary>没有 store / 没有公告目录：连读什么都谈不上。</summary>
    NoStore,

    /// <summary>当前语言本地未覆盖（没有可读文件，或 generation 不含该语言）。</summary>
    NoFile,

    /// <summary>有数据，但当前分类/时段/搜索把它筛空了。</summary>
    NoResults,

    /// <summary>读取抛异常或校验失败：条目已清空，但不能说成「没有公告」。</summary>
    Failed,

    /// <summary>正常有结果。</summary>
    Ready,
}

/// <summary>
/// 资讯页（打开应用的第一屏）。数据源是 store/kb/news/*.json，不查库。
///
/// 这里有个天然的重合：日文公告全部来自 Sekai Viewer，简中公告全部来自 Moesekai。
/// 所以语言档位上直接把实例徽章一起写出来，免得用户以为换语言换错了东西。
/// </summary>
public partial class NewsViewModel : ObservableObject
{
    private readonly NewsService _news = AppServices.News;
    private List<NewsItem> _all = [];

    /// <summary>重建档位集合时压掉一次语言切换重载，否则换列表会把 ReloadAsync 递归触发。</summary>
    private bool _suppressLanguageReload;

    /// <summary>批量改筛选（清除筛选）时只应用一次过滤。</summary>
    private bool _suppressFilterReload;

    /// <summary>读取失败的原因；非空 = 列表区进入 Failed 态（N-4）。</summary>
    private string _readFailure = string.Empty;

    /// <summary>本次读取实际用的数据源状态（N-1）。</summary>
    private NewsSourceState _sourceState = NewsSourceState.NoStore;

    public static NewsViewModel Shared { get; } = new();

    public ObservableCollection<NewsItem> Items { get; } = [];

    /// <summary>语言档位；每次重载都重建（N-3），所以是[可观察]属性而不是构造期快照。</summary>
    [ObservableProperty]
    private IReadOnlyList<NewsLanguageOption> _languages = [];

    [ObservableProperty]
    private NewsLanguageOption _selectedLanguage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// 真正送进过滤器的关键词：只有回车 / 「搜索」按钮 / 清除筛选会改它。
    ///
    /// 搜索框是 LostFocus 提交，所以「打字 + 点分类」不能把没提交的半截关键词静默用进去
    /// （Story 页 S-14 同族的坑）：`SearchText` 是输入框内容，`AppliedSearch` 才是结果口径。
    /// </summary>
    [ObservableProperty]
    private string _appliedSearch = string.Empty;

    [ObservableProperty]
    private bool _onlyActive;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>当前语言没有数据文件时的提示。</summary>
    [ObservableProperty]
    private bool _showEmptyLanguageHint;

    [ObservableProperty]
    private string _emptyLanguageHint = string.Empty;

    /// <summary>提示条标题按状态给（未建库 ≠ 该语言没数据，N-6）。</summary>
    [ObservableProperty]
    private string _emptyLanguageTitle = "该语言本地未覆盖";

    /// <summary>generation 校验/指针异常：必须出 Warning，绝不显示「已就绪」（N-1）。</summary>
    [ObservableProperty]
    private bool _showGenerationWarning;

    [ObservableProperty]
    private string _generationWarningTitle = "公告 generation 校验未通过";

    [ObservableProperty]
    private string _generationWarningText = string.Empty;

    /// <summary>没有 generation 指针、只有旧版单文件布局：不是失败，但必须说明它不再更新。</summary>
    [ObservableProperty]
    private bool _showLegacyNotice;

    [ObservableProperty]
    private string _legacyNoticeText = string.Empty;

    /// <summary>复制回执（一次性，换条目即消失）。文案说清后果，不写「已成功」。</summary>
    [ObservableProperty]
    private string _copyResultText = string.Empty;

    [ObservableProperty]
    private bool _showCopyResult;

    /// <summary>当前语言里实际出现的分类（「全部」打头）；随语言切换重建。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _categories = ["全部"];

    [ObservableProperty]
    private string _selectedCategory = "全部";

    [ObservableProperty]
    private NewsItem? _selectedItem;

    [ObservableProperty]
    private NewsListState _listState = NewsListState.Loading;

    public NewsViewModel()
    {
        Languages = _news.Languages();
        _selectedLanguage = Languages.FirstOrDefault(l => l.Available) ?? Languages[0];
        AppServices.StoreInvalidated += (_, _) => _ = ReloadAsync();
    }

    /// <summary>列表区四态的布尔包装（XAML 只做布尔→可见性，不写枚举比较）。</summary>
    public bool IsListLoading => ListState == NewsListState.Loading;

    public bool IsListNoStore => ListState == NewsListState.NoStore;

    public bool IsListNoFile => ListState == NewsListState.NoFile;

    public bool IsListNoResults => ListState == NewsListState.NoResults;

    public bool IsListFailed => ListState == NewsListState.Failed;

    /// <summary>覆盖层：只要不是 Ready 就盖住列表，避免「空白 + 计数 —」的读法分歧。</summary>
    public bool ShowListStateOverlay => ListState != NewsListState.Ready;

    /// <summary>未建库态的文案（壳层已有常驻提示，这里只服务列表区自身）。</summary>
    public string NoStoreText => "尚未选择本地数据库。请选择已有库，或查看初始化步骤。";

    /// <summary>本地未覆盖态的文案；影响与下一步直接取当前语言档位的实测提示（N-5/N-6）。</summary>
    public string NoFileText
    {
        get
        {
            var hint = SelectedLanguage?.Hint ?? string.Empty;
            return hint.Length > 0
                ? hint
                : "本地没有可读的公告数据；这不代表上游没有公告。可在「同步」页运行「同步官方公告」。";
        }
    }

    /// <summary>读取失败的可读原因（不含堆栈）。</summary>
    public string FailedText => _readFailure;

    /// <summary>搜索框真实命中范围，不能只写在悬停提示里（§4.2）。</summary>
    public string SearchScopeText =>
        "搜索范围：当前语言的标题与缓存正文全文。未缓存原文的公告只能按标题命中。";

    /// <summary>当前是否有筛选在生效（决定「清除筛选」能不能按：包含已输入但还没提交关键词的情况）。</summary>
    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(AppliedSearch)
        || SelectedCategory != "全部" || OnlyActive;

    /// <summary>计数器只报条数；时段态由开关自己的 On/OffContent 表达，不重复（第五批批示）。</summary>
    public string ListCountText => ListState switch
    {
        NewsListState.Loading => "正在读取…",
        NewsListState.NoStore => "未选择本地库",
        NewsListState.NoFile => "本地未覆盖",
        NewsListState.Failed => "读取失败",
        NewsListState.NoResults => "筛选后 0 条",
        _ => $"共 {Items.Count:N0} 条",
    };

    public async Task InitializeAsync() => await ReloadAsync();

    [RelayCommand]
    private Task RefreshAsync() => InitializeAsync();

    /// <summary>
    /// 显式提交入口（回车与「搜索」按钮）：把输入框内容落成查询口径再过滤。
    /// 文本框走 LostFocus 提交，所以这里不逐击键扫全文（N-7 / §5.1）。
    /// </summary>
    [RelayCommand]
    private void Search()
    {
        AppliedSearch = SearchText.Trim();
        ApplyFilter();
    }

    /// <summary>清除筛选只清筛选条件（分类/时段/搜索），不动语言档位与选中项以外的用户状态。</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        _suppressFilterReload = true;
        SearchText = string.Empty;
        AppliedSearch = string.Empty;
        SelectedCategory = "全部";
        OnlyActive = false;
        _suppressFilterReload = false;
        ApplyFilter();
    }

    /// <summary>复制标题 + 稳定 ID + 原文链接（§5.2 允许复制稳定 ID，N-10）。</summary>
    [RelayCommand]
    private void CopyDetail()
    {
        if (SelectedItem is not { } item)
        {
            CopyResultText = "没有可复制的条目：请先在列表里选中一条公告。";
            ShowCopyResult = true;
            return;
        }

        var payload = string.Join(Environment.NewLine,
            item.Title,
            $"ID：{item.Id}",
            $"链接：{(item.Url.Length > 0 ? item.Url : "本地没有记录")}",
            $"发布：{item.PublishedLabel}　语言：{item.Language}　来源：{item.Source}（{item.InstanceBadge}）　分类：{item.CategoryLabel}");

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetText(payload);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            CopyResultText = "已复制标题、稳定 ID 与原文链接。请粘贴到目标位置并核对内容。";
        }
        catch (Exception ex)
        {
            CopyResultText = $"复制失败：{ex.Message}。可以用「浏览器打开」直接访问原链接。";
        }
        ShowCopyResult = true;
    }

    /// <summary>语言档位切换 = 换文件重载；ApplyFilter 只动内存，读不到新文件。</summary>
    partial void OnSelectedLanguageChanged(NewsLanguageOption value)
    {
        if (_suppressLanguageReload || value is null)
        {
            // 重建档位集合时的中间态（含集合替换导致的 null 回写）不触发第二次重载。
            return;
        }
        _ = ReloadAsync();
    }

    partial void OnOnlyActiveChanged(bool value)
    {
        if (_suppressFilterReload)
        {
            return;
        }
        OnPropertyChanged(nameof(ListCountText));
        ApplyFilter();
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        if (_suppressFilterReload)
        {
            return;
        }
        ApplyFilter();
    }

    partial void OnListStateChanged(NewsListState value)
    {
        OnPropertyChanged(nameof(IsListLoading));
        OnPropertyChanged(nameof(IsListNoStore));
        OnPropertyChanged(nameof(IsListNoFile));
        OnPropertyChanged(nameof(IsListNoResults));
        OnPropertyChanged(nameof(IsListFailed));
        OnPropertyChanged(nameof(ShowListStateOverlay));
        OnPropertyChanged(nameof(ListCountText));
        OnPropertyChanged(nameof(NoFileText));
    }

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(HasActiveFilter));

    partial void OnAppliedSearchChanged(string value) => OnPropertyChanged(nameof(HasActiveFilter));

    partial void OnSelectedItemChanged(NewsItem? value)
    {
        // 缓存优先：换条目先落回缓存原文；只有没抓到原文的才停在网页侧并锁死开关。
        ShowWeb = value is { } item && item.CanBrowse;

        ShowCopyResult = false;

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsBodyCached));
        OnPropertyChanged(nameof(IsBodyMissing));
        OnPropertyChanged(nameof(HasWebUrl));
        OnPropertyChanged(nameof(CanToggleSource));
        OnPropertyChanged(nameof(ShowWebView));
        OnPropertyChanged(nameof(ShowDetailText));
        OnPropertyChanged(nameof(ShowZoom));
        OnPropertyChanged(nameof(WebViewSource));
        OnPropertyChanged(nameof(ShowDetailEmpty));
        // x:Bind 对 null 源会跳过子路径更新（TextBlock 残留旧值），详情一律绑这些 null 安全属性。
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(DetailPeriod));
        OnPropertyChanged(nameof(DetailUrl));
        OnPropertyChanged(nameof(DetailCategory));
        OnPropertyChanged(nameof(DetailId));
    }

    /// <summary>
    /// 详情正文来源：true = 内嵌浏览器看原网页，false = 看缓存原文。
    ///
    /// 缓存优先——换条目时一律先落回缓存原文（见 OnSelectedItemChanged），
    /// 只有确实没抓到原文的条目才默认停在网页侧。这是一个「每条独立」的默认值：
    /// 用户在 A 条切到网页，点进 B 条仍然从 B 的缓存原文开始看。
    /// </summary>
    [ObservableProperty]
    private bool _showWeb;

    partial void OnShowWebChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWebView));
        OnPropertyChanged(nameof(ShowDetailText));
        OnPropertyChanged(nameof(ShowZoom));
        OnPropertyChanged(nameof(WebViewSource));
    }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>详情卡自己的空态：没有选中项时不给残留标题/残留来源标签。</summary>
    public bool ShowDetailEmpty => SelectedItem is null;

    public string DetailTitle => SelectedItem?.Title ?? "未在列表中选中公告";
    public string DetailText => SelectedItem?.Text ?? string.Empty;
    public string DetailPeriod => SelectedItem?.PeriodLabel ?? string.Empty;
    public string DetailUrl => SelectedItem?.Url ?? string.Empty;
    public string DetailCategory => SelectedItem?.CategoryLabel ?? string.Empty;

    /// <summary>稳定 ID 现在真的显示出来了（N-10）；没选中时留空而不是残留。</summary>
    public string DetailId => SelectedItem?.Id ?? string.Empty;

    public bool IsBodyCached => SelectedItem is { BodyAvailable: true };

    /// <summary>未覆盖 ≠ 不存在（§6）：详情页没有正文的标记走开关锁死态。</summary>
    public bool IsBodyMissing => SelectedItem is not null && !IsBodyCached;

    /// <summary>当前条目有可导航的原链接。</summary>
    public bool HasWebUrl => SelectedItem is { } item && item.HasWebUrl;

    /// <summary>原文已缓存、且原链接可导航 → 两种来源都能看，才允许切换。</summary>
    public bool CanToggleSource => IsBodyCached && HasWebUrl;

    /// <summary>显示内嵌网页：没原文只能看网页；有原文时取决于用户切没切。</summary>
    public bool ShowWebView => SelectedItem is not null && ShowWeb && HasWebUrl;

    public bool ShowDetailText => SelectedItem is not null && !ShowWebView;

    /// <summary>
    /// 缩放调节器在所有网页模式都出现：自动字号归一化只对游戏公告页生效，
    /// external 页不注入，但手动缩放/重置对它们同样可用（第四批批示）。
    /// 看缓存原文时没有可缩放的网页，仍然不显示。
    /// </summary>
    public bool ShowZoom => ShowWebView;

    private static readonly Uri BlankUri = new("about:blank");

    /// <summary>
    /// 非网页模式一律给 about:blank。
    /// 否则每选中一条「有缓存」的公告，WebView2 都会在后台去原站拉一次页面——
    /// 用户没要求看，爬虫约束下也不该这么打。
    /// </summary>
    public Uri WebViewSource =>
        ShowWebView && SelectedItem is { } item && Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
            ? uri
            : BlankUri;

    private int _loadSequence;

    private async Task ReloadAsync()
    {
        // 快速连切语言时，慢的那次读取可能后返回；序号对不上就丢弃，防止旧数据盖新语言。
        var sequence = ++_loadSequence;
        var languageKey = SelectedLanguage?.Language ?? string.Empty;
        IsLoading = true;
        ListState = NewsListState.Loading;
        try
        {
            // 状态与语言档位同源重建（N-3）：先作废服务缓存，再重探 generation 与每个语言的可用性。
            _news.Invalidate();
            var snapshot = await Task.Run(() => _news.Probe());
            if (sequence != _loadSequence)
            {
                return;
            }

            _suppressLanguageReload = true;
            Languages = snapshot.Languages;
            var resolved = snapshot.Languages.FirstOrDefault(l => l.Language == languageKey)
                ?? snapshot.Languages.FirstOrDefault(l => l.Available)
                ?? snapshot.Languages.FirstOrDefault();
            if (resolved is not null)
            {
                SelectedLanguage = resolved;
            }
            _suppressLanguageReload = false;

            var language = SelectedLanguage;
            if (language is null)
            {
                // 档位为空（例如 store 还没建立）：不是「没有公告」，而是本地读不到档位。
                _all = [];
                ListState = NewsListState.NoStore;
                return;
            }

            var result = await Task.Run(() => _news.LoadResult(language.Language));
            if (sequence != _loadSequence)
            {
                return;
            }

            ApplySourceStatus(snapshot.Status, language);

            if (result.Error.Length > 0)
            {
                // 旧布局文件读失败：清条目 + Failed 态，不能保留上一个语言的行（N-4）。
                _all = [];
                _readFailure = result.Error;
                ApplyFilter();
                StatusText = $"读取公告失败：{result.Error}";
                return;
            }

            _readFailure = string.Empty;
            _all = [.. result.Items];
            RebuildCategories();
            SelectedCategory = Categories.Contains(SelectedCategory) ? SelectedCategory : Categories[0];
            ApplyFilter();
            StatusText = DescribeLoad(snapshot.Status, language, _all.Count);
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return;
            }

            // 关键：读失败时清空 _all，否则列表仍是上一个语言的条目，而语言标签已经切走了（N-4）。
            _all = [];
            _readFailure = ex.Message;
            ApplyFilter();
            StatusText = $"读取公告失败：{ex.Message}";
        }
        finally
        {
            if (sequence == _loadSequence)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>把服务层状态翻成界面三件事：校验警告、旧布局说明、当前语言覆盖情况。</summary>
    private void ApplySourceStatus(NewsGenerationStatus status, NewsLanguageOption language)
    {
        _sourceState = status.State;

        switch (status.State)
        {
            case NewsSourceState.GenerationPartial:
                ShowGenerationWarning = true;
                GenerationWarningTitle = $"generation 有 {status.SkippedFileCount} 个文件未通过校验，已跳过";
                GenerationWarningText =
                    $"活跃 generation（{status.ShortId}）里未通过 manifest 校验的文件被跳过了（{status.Detail}）。" +
                    "影响：下方列表不完整，缺的是那些文件里的条目。" +
                    "下一步：确认 store 未被改动，再在「同步」页重新运行「同步官方公告」。";
                break;

            case NewsSourceState.GenerationFailed:
            case NewsSourceState.PointerMalformed:
                ShowGenerationWarning = true;
                GenerationWarningTitle = status.State == NewsSourceState.PointerMalformed
                    ? "公告 generation 指针形态异常，下方是旧版单文件数据"
                    : "公告 generation 校验或读取失败，下方是旧版单文件数据";
                GenerationWarningText =
                    $"事实：{status.Detail}。影响：本次退回读取旧版单文件公告，而旧布局在第一次发布 generation 后就不再更新，" +
                    "下方条目可能已过期。下一步：先在「同步」页运行「同步官方公告」重新发布；仍然失败就查看诊断日志里的同一句原因。";
                break;

            default:
                ShowGenerationWarning = false;
                GenerationWarningText = string.Empty;
                break;
        }

        ShowLegacyNotice = status.State == NewsSourceState.LegacyOnly && language.Available;
        LegacyNoticeText = ShowLegacyNotice
            ? "本地还没有任何一次公告 generation 发布，当前读取的是旧版单文件布局。" +
              "影响：这个文件在第一次发布 generation 之后就不再更新。下一步：需要新数据时在「同步」页运行「同步官方公告」。"
            : string.Empty;

        ShowEmptyLanguageHint = !language.Available;
        EmptyLanguageTitle = status.State switch
        {
            NewsSourceState.NoStore => "尚未选择本地数据库",
            NewsSourceState.GenerationFailed or NewsSourceState.PointerMalformed => "公告数据校验未通过",
            _ => "该语言本地未覆盖",
        };
        EmptyLanguageHint = language.Hint;
    }

    private static string DescribeLoad(NewsGenerationStatus status, NewsLanguageOption language, int count) =>
        status.State switch
        {
            NewsSourceState.NoStore => "还没有可读取的本地 store，列表为空。",
            NewsSourceState.GenerationReady =>
                $"已载入 {count:N0} 条公告（{language.Label} · generation {status.ShortId}，manifest 校验通过）。",
            NewsSourceState.GenerationPartial =>
                $"已载入 {count:N0} 条公告（{language.Label}），但该 generation 有 {status.SkippedFileCount} 个文件未通过校验。",
            NewsSourceState.GenerationFailed =>
                $"已载入 {count:N0} 条旧版单文件公告（{language.Label}）；generation 校验失败，数据可能已过期。",
            NewsSourceState.PointerMalformed =>
                $"已载入 {count:N0} 条旧版单文件公告（{language.Label}）；指针异常，数据可能已过期。",
            _ => count == 0
                ? $"{language.Label}没有可读的公告文件。"
                : $"已载入 {count:N0} 条公告（{language.Label} · 旧版单文件布局，不再更新）。",
        };

    private void RebuildCategories()
    {
        var present = _all.Select(i => i.CategoryLabel).ToHashSet();
        var list = new List<string> { "全部" };
        list.AddRange(NewsService.CategoryOrder.Where(present.Contains));
        list.AddRange(present.Except(list).OrderBy(x => x, StringComparer.Ordinal));
        Categories = list;
    }

    private void ApplyFilter()
    {
        IEnumerable<NewsItem> query = _all;

        if (SelectedCategory is { } category && category != "全部")
        {
            query = query.Where(i => i.CategoryLabel == category);
        }

        if (OnlyActive)
        {
            query = query.Where(i => i.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(AppliedSearch))
        {
            var needle = AppliedSearch;
            query = query.Where(i =>
                i.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                i.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var matched = query.ToList();

        // 返回列表 / 重读文件时 _all 会换成新对象，引用比较必然落空 → 一律按 Id 恢复（N-8）。
        var wantedId = SelectedItem?.Id ?? string.Empty;

        // 先清掉已不在结果里的选中项，再动 Items——否则 ListView 会在 ItemsSource
        // 变化后把旧选中项异步推回 TwoWay 绑定，详情面板就会残留上一语言的条目。
        if (SelectedItem is not null && !matched.Any(i => i.Id == wantedId))
        {
            SelectedItem = null;
        }

        ReplaceItems(matched);

        if (SelectedItem is null || !ReferenceEquals(SelectedItem, FindById(wantedId)))
        {
            SelectedItem = FindById(wantedId) ?? Items.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ListCountText));
        ListState = ComputeState(matched.Count);
    }

    /// <summary>
    /// 按 Id 找当前列表里的条目。命中即保留选中，不命中才退到第一条（N-7 的「不重置选中」）。
    /// </summary>
    private NewsItem? FindById(string id) =>
        id.Length == 0 ? null : Items.FirstOrDefault(i => i.Id == id);

    /// <summary>
    /// 差分更新而不是 Clear + 逐条 Add：整表 Reset 会让 ListView 回到顶部、
    /// 选中被 TwoWay 回写成 null、无障碍树整片重建（N-7）。
    /// </summary>
    private void ReplaceItems(IReadOnlyList<NewsItem> matched)
    {
        var keep = new HashSet<NewsItem>(matched);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Items[i]))
            {
                Items.RemoveAt(i);
            }
        }

        for (var target = 0; target < matched.Count; target++)
        {
            if (target < Items.Count && ReferenceEquals(Items[target], matched[target]))
            {
                continue;
            }

            var existing = Items.IndexOf(matched[target]);
            if (existing < 0)
            {
                Items.Insert(target, matched[target]);
            }
            else if (existing != target)
            {
                Items.Move(existing, target);
            }
        }

        while (Items.Count > matched.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    private NewsListState ComputeState(int resultCount)
    {
        if (_readFailure.Length > 0)
        {
            return NewsListState.Failed;
        }
        if (_sourceState == NewsSourceState.NoStore)
        {
            return NewsListState.NoStore;
        }
        if (_all.Count == 0)
        {
            return NewsListState.NoFile;
        }
        return resultCount == 0 ? NewsListState.NoResults : NewsListState.Ready;
    }
}
