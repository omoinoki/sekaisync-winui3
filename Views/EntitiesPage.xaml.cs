using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>实体页：按 12 个资产域浏览 entities 表。</summary>
public sealed partial class EntitiesPage : Page
{
    private readonly EntitiesViewModel _vm = EntitiesViewModel.Shared;
    private bool _initialized;
    private bool _showDetails;

    public EntitiesViewModel ViewModel => _vm;

    public EntitiesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static string DisplaySource(string source) => source.StartsWith("master_db", System.StringComparison.Ordinal)
        ? "主数据" : source;

    public static string DisplayRegion(string region) => string.IsNullOrEmpty(region) ? "共用" : region;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyContentLayout(ContentGrid.ActualWidth);
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await _vm.InitializeAsync();
    }

    /// <summary>
    /// 分类说明：原先挂在标题行右侧的 Flyout 换成二级窗口，与用语页同一套做法。
    /// 这里只有分布与未归类两行，没有需要收进折叠区的技术细节，所以不再套内层展开。
    /// </summary>
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 680 };
        content.Children.Add(HelpLine(_vm.DomainSummary));
        if (_vm.ShowUnmappedNotice)
        {
            content.Children.Add(HelpLine(_vm.UnmappedText, muted: true));
        }

        try
        {
            await new ContentDialog
            {
                Title = "分类与数量",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.ActiveTheme,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"EntitiesPage: 分类说明对话框未能显示：{ex}");
        }
    }

    /// <summary>样式键在合并字典里，页面级 Resources 查不到，必须走 Application 资源。</summary>
    private static TextBlock HelpLine(string text, bool muted = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources[muted ? "SkMutedTextStyle" : "SkBodyTextStyle"],
        IsTextSelectionEnabled = true,
    };

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

        var showDomains = width >= 1000;
        var singlePane = width < 760;
        // 从宽布局进入单面板时默认列表；选择记录本身不触发切换。
        _showDetails = singlePane && _showDetails;
        var showList = !singlePane || !_showDetails;
        var showDetails = !singlePane || _showDetails;

        DomainPanel.Visibility = BoolToVisibility(showDomains);
        CompactDomainSelector.Visibility = BoolToVisibility(!showDomains);
        ResultsPanel.Visibility = BoolToVisibility(showList);
        DetailsPanel.Visibility = BoolToVisibility(showDetails);
        DomainColumn.Width = new GridLength(showDomains ? 176 : 0);
        // 分栏比例与 ColumnSplitHandle 共用同一份落盘值，否则每次 resize 都会把
        // 用户拖出来的比例冲回 1.25:1。越界的旧值由手柄的限界夹回来。
        var ratio = SplitHandle.StoredRatioClamped(1.25 / 2.25);
        ResultsColumn.Width = showList ? new GridLength(ratio, GridUnitType.Star) : new GridLength(0);
        DetailsColumn.Width = showDetails ? new GridLength(1 - ratio, GridUnitType.Star) : new GridLength(0);
        SplitHandle.Visibility = BoolToVisibility(showList && showDetails);
        // 列间距固定为 0：双面板的间隔由手柄自身的 14+2+2 承担，域列与列表的间隔走左边距。
        ResultsPanel.Margin = new Thickness(showDomains ? 12 : 0, 0, !showDomains && !singlePane ? 12 : 0, 0);
        ShowDetailsButton.Visibility = BoolToVisibility(singlePane && !_showDetails);
        ReturnToListButton.Visibility = BoolToVisibility(singlePane && _showDetails);
    }

    private void ShowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelection)
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
        SearchBox.SelectAll();
        args.Handled = true;
    }

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
}
