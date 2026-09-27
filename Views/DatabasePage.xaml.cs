using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>
/// 本地数据库阅览（只读）。表选择 / 分页 / LIKE 搜索 / 行详情。
/// 常态是被「数据源 → 原始表」标签托管在 <see cref="SourcesPage"/> 的 Frame 里。
/// </summary>
public sealed partial class DatabasePage : Page
{
    private readonly DatabaseViewModel _vm = DatabaseViewModel.Shared;
    private ScrollViewer? _listScroller;
    private bool _initialized;
    private bool _gridWired;
    private bool _showDetails;

    // 列签名：只在列结构真的变化时重建行模板（审计 D-9：每次翻页都重解析 XAML 会整表重建容器）。
    private string? _templateSignature;

    /// <summary>
    /// 被 SourcesPage 托管时为真：显示返回入口、收起重复的「刷新」按钮。
    /// 赋值即刷新可见性，因为宿主是在导航之后、Loaded 之前/之后都可能设置。
    /// </summary>
    public bool EmbeddedInSources
    {
        get => _embeddedInSources;
        set
        {
            _embeddedInSources = value;
            ApplyEmbeddedChrome();
        }
    }

    private bool _embeddedInSources;

    /// <summary>宿主请求回到「概览」标签。仅托管模式下会被触发。</summary>
    public event Action? BackRequested;

    public DatabaseViewModel ViewModel => _vm;

    public DatabasePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        HeaderScroller.SizeChanged += OnRowsScrollerSizeChanged;
        HeaderGrid.SizeChanged += OnRowsScrollerSizeChanged;
        // 行容器复用时给它一个可读的拼接名：表头是列表外的另一组 TextBlock，
        // 读屏否则只能读到值、读不到列名（审计 D-6）。
        RowsList.ContainerContentChanging += OnRowContainerContentChanging;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            ApplyContentLayout(e.NewSize.Width);
        }
    }

    private void ApplyContentLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var singlePane = width < 800;
        _showDetails = singlePane && _showDetails;
        var showList = !singlePane || !_showDetails;
        var showDetails = !singlePane || _showDetails;

        // 只折叠现有面板，保留 RowsList、滚动源和行模板，不重新查询。
        RowsPanel.Visibility = BoolToVisibility(showList);
        DetailsPanel.Visibility = BoolToVisibility(showDetails);
        RowsColumn.Width = showList ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        DetailsColumn.Width = showDetails ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ContentGrid.ColumnSpacing = singlePane ? 0 : 12;
        ShowDetailsButton.Visibility = BoolToVisibility(singlePane && !_showDetails);
        ReturnToListButton.Visibility = BoolToVisibility(singlePane && _showDetails);
    }

    private void ShowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelectedRow)
        {
            return;
        }
        _showDetails = true;
        ApplyContentLayout(ContentGrid.ActualWidth);
        ReturnToListButton.Focus(FocusState.Keyboard);
    }

    private void ReturnToList_Click(object sender, RoutedEventArgs e)
    {
        _showDetails = false;
        ApplyContentLayout(ContentGrid.ActualWidth);
        ShowDetailsButton.Focus(FocusState.Keyboard);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyContentLayout(ContentGrid.ActualWidth);
        HookScrollSync();
        ApplyEmbeddedChrome();

        // 订阅配对：以前订阅在一次性 _initialized 门里、退订却无条件跑，
        // 第二次进入本页就再也没有 ColumnsChanged → 表头与数据错位（审计 D-3）。
        if (!_gridWired)
        {
            _vm.ColumnsChanged += RebuildGrid;
            _gridWired = true;
        }

        _ = LoadOnEntryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_gridWired)
        {
            _vm.ColumnsChanged -= RebuildGrid;
            _gridWired = false;
        }
        UnhookScrollSync();
    }

    /// <summary>
    /// 进入本页时的一次初始化。原 <c>async void OnLoaded</c> 没有错误路径（审计 D-3），
    /// 这里把异常交给 VM 写成 ErrorText，由页面上的 InfoBar 承载。
    /// </summary>
    private async Task LoadOnEntryAsync()
    {
        try
        {
            if (!_initialized)
            {
                _initialized = true;
                await _vm.InitializeAsync();
            }
            else
            {
                // 第二次以后进来不重查（VM 里数据还在），但表头必须按当前列重新对齐一次。
                _templateSignature = null;
                RebuildGrid();
            }
        }
        catch (Exception ex)
        {
            _vm.ReportEntryFailure(ex);
        }
    }

    /// <summary>供宿主（数据源页顶部「刷新」）转发一次完整刷新（审计 SR-3）。</summary>
    public Task RefreshAsync()
    {
        _templateSignature = null;
        return _vm.InitializeAsync();
    }

    /// <summary>托管时只留一个「刷新」：顶部标签栏那个会转发到这里（审计 SR-3）。</summary>
    private void ApplyEmbeddedChrome()
    {
        BackButton.Visibility = EmbeddedInSources ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = EmbeddedInSources ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BackToOverview_Click(object sender, RoutedEventArgs e)
    {
        var requested = BackRequested;
        if (requested is not null)
        {
            requested.Invoke();
            return;
        }
        // 没有宿主就不该出现这个按钮；真出现了宁可什么都不做，也不要把用户丢进未知页面。
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (_vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_vm.RefreshCommand.CanExecute(null))
        {
            _vm.RefreshCommand.Execute(null);
        }
        args.Handled = true;
    }

    private void FindAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    /// <summary>按当前列结构重建表头与行模板（列宽两侧保持一致）。</summary>
    private void RebuildGrid()
    {
        var signature = BuildColumnsSignature();
        if (signature == _templateSignature && HeaderGrid.Children.Count == _vm.GridColumns.Count)
        {
            return;
        }
        _templateSignature = signature;

        RebuildHeader();
        RebuildRowTemplate();
        SyncHeaderScroll();
    }

    private string BuildColumnsSignature()
    {
        if (_vm.GridColumns.Count == 0)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        foreach (var column in _vm.GridColumns)
        {
            builder.Append(column.Title).Append(':')
                .Append(column.Width.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('|');
        }
        return builder.ToString();
    }

    private void RebuildHeader()
    {
        HeaderGrid.Children.Clear();
        HeaderGrid.ColumnDefinitions.Clear();
        HeaderGrid.Width = _vm.GridColumns.Sum(column => column.Width);

        foreach (var column in _vm.GridColumns)
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        }

        for (var i = 0; i < _vm.GridColumns.Count; i++)
        {
            var text = new TextBlock
            {
                Text = _vm.GridColumns[i].Title,
                Style = (Style)Application.Current.Resources["SkTableHeaderTextStyle"],
                Margin = new Thickness(12, 0, 8, 0),
            };
            Grid.SetColumn(text, i);
            HeaderGrid.Children.Add(text);
        }
    }

    private void RebuildRowTemplate()
    {
        if (_vm.GridColumns.Count == 0)
        {
            RowsList.ItemTemplate = null;
            return;
        }

        var builder = new StringBuilder();
        builder.Append("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">");
        builder.Append("<Grid MinHeight=\"32\" Width=\"")
            .Append(_vm.GridColumns.Sum(column => column.Width).ToString("0.###", CultureInfo.InvariantCulture))
            .Append("\" HorizontalAlignment=\"Left\" Background=\"Transparent\">");
        builder.Append("<Grid.ColumnDefinitions>");
        foreach (var column in _vm.GridColumns)
        {
            builder.Append("<ColumnDefinition Width=\"")
                .Append(column.Width.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("\" />");
        }
        builder.Append("</Grid.ColumnDefinitions>");

        for (var i = 0; i < _vm.GridColumns.Count; i++)
        {
            builder.Append("<TextBlock Grid.Column=\"").Append(i)
                .Append("\" Text=\"{Binding Cells[").Append(i).Append("]}\"")
                .Append(" Style=\"{StaticResource DbCellTextStyle}\"")
                .Append(" Margin=\"12,0,8,0\" VerticalAlignment=\"Center\" />");
        }

        builder.Append("</Grid></DataTemplate>");
        RowsList.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(builder.ToString());
    }

    /// <summary>
    /// 给每个行容器一个「列名 值 · 列名 值」的可访问名（审计 D-6）。
    /// 单元格文本本身仍留在树里，这里补的是列与值的对应关系。
    /// </summary>
    private void OnRowContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // ContainerContentChangingEventArgs 上没有 Container 属性；容器要从列表按索引取。
        if (args.Item is not DbRowItem item
            || sender.ContainerFromIndex(args.ItemIndex) is not ListViewItem container)
        {
            return;
        }

        var builder = new StringBuilder();
        var count = Math.Min(item.Cells.Length, _vm.GridColumns.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(" · ");
            }
            builder.Append(_vm.GridColumns[i].Title).Append(' ');

            var cell = item.Cells[i];
            builder.Append(cell.Length > 40 ? cell[..40] + "…" : cell);
        }
        if (count == 0)
        {
            builder.Append("空行");
        }
        AutomationProperties.SetName(container, builder.ToString());
    }

    /// <summary>列表是唯一滚动源；表头使用同一视口宽度，末列也能保持对齐。</summary>
    private void HookScrollSync()
    {
        RowsList.ApplyTemplate();
        var scroller = FindScrollViewer(RowsList);
        if (scroller is null)
        {
            return;
        }
        if (!ReferenceEquals(scroller, _listScroller))
        {
            UnhookScrollSync();
            _listScroller = scroller;
            scroller.ViewChanged += OnRowsViewChanged;
            scroller.SizeChanged += OnRowsScrollerSizeChanged;
        }
        SyncHeaderScroll();
    }

    private void UnhookScrollSync()
    {
        if (_listScroller is null)
        {
            return;
        }
        _listScroller.ViewChanged -= OnRowsViewChanged;
        _listScroller.SizeChanged -= OnRowsScrollerSizeChanged;
        _listScroller = null;
    }

    private void OnRowsViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => SyncHeaderScroll();

    private void OnRowsScrollerSizeChanged(object sender, SizeChangedEventArgs e) => SyncHeaderScroll();

    private void SyncHeaderScroll()
    {
        if (_listScroller is null)
        {
            return;
        }
        var viewportWidth = _listScroller.ViewportWidth;
        if (viewportWidth > 0 && double.IsFinite(viewportWidth))
        {
            HeaderScroller.Width = viewportWidth;
        }
        HeaderScroller.ChangeView(_listScroller.HorizontalOffset, 0, null, true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }
            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
