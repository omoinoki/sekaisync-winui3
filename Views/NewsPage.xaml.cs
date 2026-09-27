using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using SekaiSync.Desktop.Services;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>资讯页：两个实例的官方公告，按语言分档（切语言等于切实例）。</summary>
public sealed partial class NewsPage : Page
{
    private readonly NewsViewModel _vm = NewsViewModel.Shared;
    private bool _initialized;

    /// <summary>分栏比例的保守限界：列表最小占 1/4，最大占 3/5。</summary>
    private const double MinListRatio = 0.25;
    private const double MaxListRatio = 0.60;

    /// <summary>归一化后可注入的缩放限界。日/英（sekai-web 模板）正文字号极小，
    /// 需要放大到 4 倍；外链与常规模板可能偏大，下探到 0.25。</summary>
    private const double MinWebZoom = 0.25;
    private const double MaxWebZoom = 4.0;

    private const double ReferenceFontPx = 16.0;

    /// <summary>尺寸变化后重归一化的防抖窗口：连发只取最后一次。</summary>
    private const double NormalizeDebounceMs = 250;

    /// <summary>
    /// 测量脚本：先归零 zoom，再找「直接文本最多的元素」，读它的 computed font-size。
    ///
    /// **不能测 body**：sekai-web 模板的 html rem 基准固定 2.34375px，body 继承到 2.3px，
    /// 而真正的正文是子元素上的大 rem 倍数——第一版测 body 得 2.3px，顶格 4.0 也只有
    /// 目标的一半。「直接文本最多的元素」的 computed font-size 才是真实正文字号。
    /// 任何异常或取不到都返回 0，由调用方退回纯倍率。
    /// </summary>
    private const string MeasureFontScript = """
        (function () {
          try {
            var root = document.documentElement;
            if (!root) { return 0; }
            root.style.zoom = '1';
            var best = null;
            var bestLen = 0;
            var scan = function (el) {
              if (!el || !el.tagName) { return; }
              var tag = el.tagName;
              if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEMPLATE') { return; }
              var len = 0;
              for (var n = el.firstChild; n; n = n.nextSibling) {
                if (n.nodeType === 3) { len += (n.nodeValue || '').trim().length; }
              }
              if (len > bestLen) { bestLen = len; best = el; }
            };
            scan(document.body);
            var all = document.body ? document.body.querySelectorAll('*') : [];
            for (var i = 0; i < all.length; i++) { scan(all[i]); }
            if (!best) { return 0; }
            var size = parseFloat(window.getComputedStyle(best).fontSize);
            return isFinite(size) && size > 0 ? size : 0;
          } catch (e) {
            return 0;
          }
        })()
        """;

    private double _splitRatio = 0.55;
    private bool _splitterDragging;
    private bool _showCompactPreview;

    /// <summary>尺寸变化后的重归一化定时器（非重复；防抖用）。</summary>
    private DispatcherQueueTimer? _normalizeTimer;

    public NewsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 尺寸变化只订阅一次：不反订阅也没关系（WebView2 是本页的子元素，不成环）。
        DetailWeb.SizeChanged += OnDetailWebSizeChanged;
        Splitter.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSplitterPointerPressed), true);
        Splitter.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSplitterPointerMoved), true);
        Splitter.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSplitterPointerReleased), true);
        Splitter.PointerCaptureLost += (_, _) =>
        {
            if (!_splitterDragging) return;
            _splitterDragging = false;
            AppServices.Settings.NewsSplitRatio = _splitRatio;
            AppServices.Settings.Save();
        };
    }

    public NewsViewModel ViewModel => _vm;

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private static string HostOf(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    /// <summary>
    /// 当前域的用户倍率。归一化之后这里存的是「用户想要多大」而不是最终注入值：
    /// 每个页面按自己的实测字号反算最终缩放，倍率只表达用户偏好。
    /// </summary>
    private double ZoomForHost(string host)
    {
        if (AppServices.Settings.NewsHostZooms.TryGetValue(host, out var stored) &&
            stored > 0)
        {
            return Math.Clamp(stored, MinWebZoom, MaxWebZoom);
        }
        var fallback = AppServices.Settings.NewsWebZoom;
        return double.IsNaN(fallback) || fallback <= 0 ? 1.0 : Math.Clamp(fallback, MinWebZoom, MaxWebZoom);
    }

    /// <summary>当前详情页所属站点的缩放倍率（按域读写，切换条目/语言时各自恢复）。</summary>
    private double WebZoom
    {
        get => ZoomForHost(HostOf(DetailWeb.Source?.ToString()));
        set
        {
            var host = HostOf(DetailWeb.Source?.ToString());
            if (host.Length == 0)
            {
                return;
            }
            AppServices.Settings.NewsHostZooms[host] = Math.Clamp(value, MinWebZoom, MaxWebZoom);
            AppServices.Settings.Save();
            UpdateZoomLabel();
            ApplyZoomToCurrentPage();
        }
    }

    // 不同站点的 rem 基准不同，须按实测正文字号计算缩放。
    private async void ApplyZoomToCurrentPage()
    {
        // 外链（社媒 / 官网首页）是桌面排版：自动字号归一化跳过，但用户倍率仍要生效
        // （第四批批示）。WinUI 的 WebView2 控件没有 ZoomFactor 属性，走同一个 JS
        // 注入通道直接缩放 body，不做字号测量。
        if (_vm.SelectedItem is { BrowseType: "external" })
        {
            var web0 = DetailWeb.CoreWebView2;
            if (web0 is null)
            {
                return;
            }

            var host0 = HostOf(DetailWeb.Source?.ToString());
            var factor = Math.Clamp(ZoomForHost(host0), MinWebZoom, MaxWebZoom);
            try
            {
                var zoom0 = factor.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await web0.ExecuteScriptAsync($"document.body.style.zoom='{zoom0}';");
                App.Log($"NewsPage: 外链（external）原生缩放 body zoom={factor:0.##}");
            }
            catch (Exception ex)
            {
                App.Log($"NewsPage: 外链缩放注入失败：{ex.Message}");
            }
            return;
        }

        var web = DetailWeb.CoreWebView2;
        if (web is null)
        {
            return;
        }

        var host = HostOf(DetailWeb.Source?.ToString());
        var multiplier = ZoomForHost(host);

        try
        {
            var measured = ParseMeasured(await web.ExecuteScriptAsync(MeasureFontScript));

            // 测量失败（0）时退回纯倍率——不比不做更差，至少尊重用户按过的档位。
            var applied = measured > 0
                ? Math.Clamp(multiplier * ReferenceFontPx / measured, MinWebZoom, MaxWebZoom)
                : multiplier;

            var zoom = applied.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await web.ExecuteScriptAsync($"document.documentElement.style.zoom='{zoom}';");
            App.Log($"NewsPage: 缩放归一 host={host} 实测={measured:0.##}px 倍率={multiplier:0.##} 应用={applied:0.###}");
        }
        catch (Exception ex)
        {
            App.Log($"NewsPage: 注入缩放失败：{ex.Message}");
        }
    }

    /// <summary>测量脚本返回 JSON 数字（异常路径返回 0 或 null）；取不到一律当 0。</summary>
    private static double ParseMeasured(string? raw)
    {
        var text = raw is null ? string.Empty : raw.Trim().Trim('"');
        return double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : 0;
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{CurrentZoom * 100:0}%";

    /// <summary>
    /// 缩放一套控件、两种语义：网页模式调 WebView 缩放（有按站点记忆），
    /// 文本模式调正文字号（会话内有效，不落盘——没有「站点基准字号」可复位，
    /// 所以重置按钮在两种模式下语义不同：网页模式退回站点 100%，文本模式退回字号 100%。
    /// 用户要求两套逻辑分开但控件只有一套。
    /// </summary>
    private double CurrentZoom => _vm.ShowWeb ? WebZoom : _textZoom;

    private double _textZoom = 1.0;

    private TextBlock? _detailTextBlock;

    private void OnZoomInClick(object sender, RoutedEventArgs e) => NudgeZoom(+0.25);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => NudgeZoom(-0.25);

    private void OnZoomResetClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ShowWeb)
        {
            WebZoom = 1.0;
            return;
        }

        _textZoom = 1.0;
        ApplyTextZoom();
        UpdateZoomLabel();
    }

    private void NudgeZoom(double delta)
    {
        if (_vm.ShowWeb)
        {
            WebZoom += delta;
            return;
        }

        _textZoom = Math.Clamp(_textZoom + delta, 0.5, 3.0);
        ApplyTextZoom();
        UpdateZoomLabel();
    }

    private void ApplyTextZoom()
    {
        if (_detailTextBlock is not null)
        {
            _detailTextBlock.FontSize = 16 * _textZoom;
            _detailTextBlock.LineHeight = 28 * _textZoom;
        }
    }

    /// <summary>
    /// 缩放一套控件、两种语义：网页模式调 WebView 缩放（按站点记忆），
    /// 文本模式调正文字号（会话内有效）。切回文本模式时字号复位，
    /// 避免上一条的放大比例悄悄带到下一条。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewsViewModel.HasSelection) && !_vm.HasSelection)
        {
            _showCompactPreview = false;
            UpdateLayoutMode();
        }
        if (e.PropertyName == nameof(NewsViewModel.ShowWeb) && !_vm.ShowWeb)
        {
            _textZoom = 1.0;
            ApplyTextZoom();
            UpdateZoomLabel();
        }
    }

    // ── 分栏拖拽 ──

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).CapturePointer(e.Pointer);
        _splitterDragging = true;
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            return;
        }

        var position = e.GetCurrentPoint(DetailColumns).Position;
        SetSplitRatio(position.X / Math.Max(1, DetailColumns.ActualWidth));
        e.Handled = true;
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            return;
        }

        _splitterDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        AppServices.Settings.NewsSplitRatio = _splitRatio;
        AppServices.Settings.Save();
        e.Handled = true;
    }

    private void SetSplitRatio(double ratio)
    {
        _splitRatio = Math.Clamp(ratio, MinListRatio, MaxListRatio);
        UpdateLayoutMode();
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();

    private void OnPreviewToggleClick(object sender, RoutedEventArgs e)
    {
        _showCompactPreview = !_showCompactPreview;
        UpdateLayoutMode();
        if (!_showCompactPreview)
        {
            NewsList.Focus(FocusState.Keyboard);
        }
    }

    private void UpdateLayoutMode()
    {
        var compact = LayoutRoot.ActualWidth < 1000;
        var stackedToolbar = LayoutRoot.ActualWidth < 760;
        ToolbarGrid.RowSpacing = stackedToolbar ? 8 : 0;
        ToolbarActionsRow.Height = stackedToolbar ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(ToolbarActions, stackedToolbar ? 1 : 0);
        Grid.SetColumn(ToolbarActions, stackedToolbar ? 0 : 1);
        Grid.SetColumnSpan(ToolbarActions, stackedToolbar ? 2 : 1);
        PreviewToggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggle.Content = _showCompactPreview ? "返回列表" : "查看详情";
        ListPanel.Visibility = !compact || !_showCompactPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewPanel.Visibility = !compact || _showCompactPreview ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ListColumn.MinWidth = compact ? 0 : 400;
        PreviewColumn.MinWidth = compact ? 0 : 400;
        ListColumn.Width = compact
            ? new GridLength(_showCompactPreview ? 0 : 1, GridUnitType.Star)
            : new GridLength(_splitRatio, GridUnitType.Star);
        PreviewColumn.Width = compact
            ? new GridLength(_showCompactPreview ? 1 : 0, GridUnitType.Star)
            : new GridLength(1 - _splitRatio, GridUnitType.Star);
    }

    private void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ratio = e.Key switch
        {
            Windows.System.VirtualKey.Left => _splitRatio - 0.05,
            Windows.System.VirtualKey.Right => _splitRatio + 0.05,
            Windows.System.VirtualKey.Home => MinListRatio,
            Windows.System.VirtualKey.End => MaxListRatio,
            _ => double.NaN,
        };
        if (double.IsNaN(ratio)) return;
        SetSplitRatio(ratio);
        AppServices.Settings.NewsSplitRatio = _splitRatio;
        AppServices.Settings.Save();
        e.Handled = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateLayoutMode();
        var storedRatio = AppServices.Settings.NewsSplitRatio;
        if (storedRatio is >= MinListRatio and <= MaxListRatio)
        {
            SetSplitRatio(storedRatio);
        }

        _detailTextBlock ??= FindName("DetailBodyText") as TextBlock;
        ApplyTextZoom();
        UpdateZoomLabel();

        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await _vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        // 定时器由 DispatcherQueue 持有（应用级），不摘钩子会把本页实例一直挂住；
        // 顺手也掐掉待触发的那一次，免得它在 WebView2 拆除后跑。
        if (_normalizeTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnNormalizeTick;
            _normalizeTimer = null;
        }
    }

    /// <summary>
    /// 尺寸变化 → 250ms 防抖 → 重归一化。
    ///
    /// 字节 CDN 的资讯模板是视口自适应排版（html font-size = clientWidth/29.87，
    /// 页面 JS 监听 resize 重设字号），最大化窗口时 webview 变宽、页面自己把字放大，
    /// 而归一化系数只在导航时算过一次——不重测就会被等比拉大。
    /// </summary>
    private void OnDetailWebSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var timer = EnsureNormalizeTimer();
        if (timer is null)
        {
            return;
        }

        // 非重复计时器：Stop + Start 即把窗口重置，连发只有最后一次会真的走到 Tick。
        timer.Stop();
        timer.Start();
    }

    private void OnNormalizeTick(DispatcherQueueTimer sender, object args) => ApplyZoomToCurrentPage();

    /// <summary>惰性建定时器；只建一次，Tick 也只挂一次。</summary>
    private DispatcherQueueTimer? EnsureNormalizeTimer()
    {
        if (_normalizeTimer is not null)
        {
            return _normalizeTimer;
        }

        var timer = DispatcherQueue?.CreateTimer();
        if (timer is null)
        {
            return null;
        }

        timer.Interval = TimeSpan.FromMilliseconds(NormalizeDebounceMs);
        timer.IsRepeating = false;
        timer.Tick += OnNormalizeTick;
        _normalizeTimer = timer;
        return _normalizeTimer;
    }

    /// <summary>
    /// F5 刷新 / Ctrl+F 聚焦搜索框。
    ///
    /// 不用 KeyboardAccelerator：WinUI 会在悬停时自动给挂了加速键的区域弹按键提示气泡
    /// （webview 上方会莫名出现一个「F5」），用户明确要求去掉。
    /// </summary>
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F5)
        {
            if (_vm.RefreshCommand.CanExecute(null))
            {
                _vm.RefreshCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F)
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            if ((state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
            {
                SearchBox.Focus(FocusState.Keyboard);
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// 回车才执行搜索。
    ///
    /// 搜索框绑定是 `UpdateSourceTrigger=LostFocus`（避免逐击键扫全文正文 + 重置列表选中，N-7），
    /// 所以回车时必须自己把当前文本写进 VM —— 否则失焦还没发生，执行的是上一次提交的旧关键词。
    /// 输入法组合期间的 Enter 保护在 WinUI 3 没有可用 API（UWP 的 TextComposition* 事件未投影），
    /// 无法静态确认，已写进交付报告而不是假装修好。
    /// </summary>
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (sender is TextBox box && _vm.SearchText != box.Text)
            {
                _vm.SearchText = box.Text;
            }
            if (_vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    /// <summary>WebView2 初始化与导航全程落日志——它 fail-fast 时事件日志里拿不到栈。</summary>
    private void OnDetailWebNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // 新导航可能落在另一个站点域上：切到该域记忆的倍率再渲染。
        UpdateZoomLabel();
        ApplyZoomToCurrentPage();
    }

    private void OnDetailWebInitializationCompleted(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception is not null)
        {
            App.Log($"WebView2 初始化失败：{args.Exception}");
            return;
        }

        App.Log("WebView2 初始化完成");
        sender.CoreWebView2.ProcessFailed += (_, e) =>
            App.Log($"WebView2 进程失败：{e.ProcessFailedKind}");
        sender.CoreWebView2.NavigationStarting += (_, e) =>
            App.Log($"WebView2 导航开始：{e.Uri}");
        sender.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            App.Log($"WebView2 导航完成：成功={e.IsSuccess} 错误={e.WebErrorStatus}");
            // 新页面 = 新的字号体系，重新测一次再归一。
            ApplyZoomToCurrentPage();
        };
    }
}
