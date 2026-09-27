using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>
/// 数据源页：知识库概览 / 跨实例比对 / 原始表。
/// 「原始表」不再单独占一个导航项，而是托管控件里的老数据库阅览页；
/// 托管时由 <see cref="DatabasePage"/> 提供「返回概览」出口（审计：钻进来必须有能出去的地方）。
/// </summary>
public sealed partial class SourcesPage : Page
{
    private readonly SourcesViewModel _vm = SourcesViewModel.Shared;
    private bool _initialized;
    private bool _backWired;

    public SourcesViewModel ViewModel => _vm;

    public SourcesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>状态行（成功一侧）：读取中不显示任何一版措辞，避免「就绪」抢跑。</summary>
    public Visibility StateLineVisibility(bool loading, bool ok)
        => !loading && ok ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>状态行（问题一侧）：未建库 / 读取失败。</summary>
    public Visibility ProblemLineVisibility(bool loading, bool ok)
        => !loading && !ok ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.NavigateRequested += OnNavigateRequested;
        EnsureTablesHosted();

        _ = LoadOnEntryAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.NavigateRequested -= OnNavigateRequested;
    }

    /// <summary>
    /// 进入本页的初始化。<c>InitializeAsync</c> 自带 try/catch，这里的兜底只负责
    /// 「连 VM 都没机会写状态」这种极端情况（对比审计 D-3 的 async void 无错误路径）。
    /// </summary>
    private async Task LoadOnEntryAsync()
    {
        try
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _vm.ReportEntryFailure(ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourcesViewModel.SelectedTab))
        {
            EnsureTablesHosted();
        }
    }

    /// <summary>老数据库页只在第一次切到「原始表」时才实例化，省掉一次启动开销。</summary>
    private void EnsureTablesHosted()
    {
        if (!_vm.IsTablesTab)
        {
            return;
        }

        if (TablesFrame.Content is null)
        {
            TablesFrame.Navigate(typeof(DatabasePage));
        }

        if (TablesFrame.Content is DatabasePage page)
        {
            // 托管标记：本页顶部已有「刷新」，子页那只留一个，且补上返回出口（审计 SR-3）。
            page.EmbeddedInSources = true;
            if (!_backWired)
            {
                _backWired = true;
                page.BackRequested += ReturnToOverview;
            }
        }
    }

    private void ReturnToOverview() => _vm.SelectedTab = "overview";

    /// <summary>
    /// 「原始表」标签下的刷新要同时转发给托管页：
    /// 顶部按钮原先只重读概览数据，表格纹丝不动（审计 SR-3）。
    /// </summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsTablesTab || TablesFrame.Content is not DatabasePage page)
        {
            return;
        }
        try
        {
            await page.RefreshAsync();
        }
        catch (Exception ex)
        {
            App.Log($"SourcesPage: 刷新原始表失败：{ex}");
        }
    }

    /// <summary>
    /// 壳层的导航只认 NavigationView 的选中项（<c>Navigate(tag)</c> 是 private 且不带参数），
    /// 所以这里走「选中对应导航项」这条既有路径；预选状态由各页 VM 的单例承载。
    /// </summary>
    private void OnNavigateRequested(string tag)
    {
        var nav = FindAncestor<NavigationView>(this);
        var item = nav?.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag is string value && value == tag);

        if (nav is not null && item is not null)
        {
            nav.SelectedItem = item;
            return;
        }

        // 导航失败仍需明确反馈。
        _vm.ActionHintText = $"无法打开「{tag}」页，请从侧栏进入。";
    }

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(current);
        while (parent is not null)
        {
            if (parent is T match)
            {
                return match;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
