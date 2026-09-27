using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>用语页：术语 / 提取用语 / 官方译名，三张表合一。</summary>
public sealed partial class TermsPage : Page
{
    private readonly TermsViewModel _vm = TermsViewModel.Shared;
    private bool _initialized;

    public TermsViewModel ViewModel => _vm;

    public TermsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>列表状态覆盖层用（WinUI 3 没有 DataTrigger）。模板内的判定见本文件的三个转换器。</summary>
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 单例 VM：先摘再挂，避免同一页多次 Loaded 时重复订阅。
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _vm.InitializeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _vm.PropertyChanged -= OnViewModelPropertyChanged;

    /// <summary>
    /// 返回本页或按 Id 恢复选中后，把选中行滚回可视区。
    /// 只滚列表自己的项，不重建任何查询。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TermsViewModel.SelectedItem) && e.PropertyName != nameof(TermsViewModel.Items))
        {
            return;
        }

        var row = _vm.SelectedItem;
        if (row is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => TermList?.ScrollIntoView(row));
    }

    /// <summary>
    /// 数据说明：原来是挂在筛选行里的 Flyout，现在按同步 / 智能体接入那一套改成二级窗口——
    /// 外层只说筛选与搜索怎么生效，标记语义和字典规模收进内层技术细节，随当前子标签取舍。
    /// </summary>
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 680 };
        foreach (var row in new[]
        {
            _vm.InstanceFilterNote,
            "版本筛选保留含所选语言名称的条目。",
            _vm.SearchScopeHint,
            "未收录：本地尚无该语言记录。名称格式异常会单独提示。",
        })
        {
            content.Children.Add(HelpLine(row));
        }

        var technical = new StackPanel { Spacing = 8, Margin = new Thickness(16, 16, 16, 12) };
        if (!_vm.IsTranslationTab)
        {
            technical.Children.Add(HelpLine(
                "可信标记沿用来源数据：B 为官方或原文，C 为非官方译文；其它标记保留原值。置信、词频及证据指标见提取用语。"));
        }
        else
        {
            technical.Children.Add(HelpLine(_vm.DictionarySummary));
        }

        content.Children.Add(new Expander
        {
            Header = "技术细节：标记语义与字典规模",
            Style = Sk("SkDetailExpanderStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = technical,
            },
        });

        try
        {
            await new ContentDialog
            {
                Title = "用语数据说明",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.ActiveTheme,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"TermsPage: 数据说明对话框未能显示：{ex}");
        }
    }

    /// <summary>
    /// 说明行。FontFamily 只在需要等宽时赋值：赋 null 会被类型转换器变成
    /// Source="Unknown"，点击当场抛异常、弹窗根本不出现。
    /// </summary>
    private static TextBlock HelpLine(string text, bool mono = false)
    {
        var line = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = Sk("SkBodyTextStyle"),
            IsTextSelectionEnabled = true,
        };
        if (mono)
        {
            line.FontFamily = new FontFamily("Consolas");
        }
        return line;
    }

    /// <summary>样式键在合并字典里，页面级 Resources 查不到，必须走 Application 资源。</summary>
    private static Style Sk(string key) => (Style)Application.Current.Resources[key];

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

    // Missing 是有缺译的条目数，而不是缺失单元格数。
    public static string NamespaceSummary(int rows, int missing) => missing > 0
        ? $"{rows:N0} 条 · {missing:N0} 条缺译"
        : $"{rows:N0} 条";

    private void OnTranslationItemClick(object sender, ItemClickEventArgs e)
    {
        if (sender is not ListView list || e.ClickedItem is not TranslationNameRow row)
        {
            return;
        }

        var content = new StackPanel { Spacing = 12, Width = Math.Clamp(ActualWidth - 64, 240, 520) };
        content.Children.Add(DetailText(row.Key, "SkPageTitleTextStyle"));
        content.Children.Add(DetailText($"{row.Namespace}\nID · {row.Id}", "SkMonoTextStyle"));
        foreach (var (label, value) in new[]
        {
            ("日本語", row.Ja), ("English", row.En), ("简体中文", row.ZhHans),
            ("繁體中文", row.ZhHant), ("한국어", row.Ko),
        })
        {
            var name = new Grid { ColumnSpacing = 12 };
            name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            name.Children.Add(DetailText(label, "SkSectionTextStyle"));
            var text = DetailText(string.IsNullOrWhiteSpace(value) ? "未收录" : value, "SkBodyTextStyle");
            Grid.SetColumn(text, 1);
            name.Children.Add(text);
            content.Children.Add(name);
        }

        var flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = Math.Clamp((XamlRoot?.Size.Height ?? 600) - 120, 160, 480),
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters = { new Setter(FrameworkElement.MaxWidthProperty, 568d) },
            },
        };
        AutomationProperties.SetName(content, "完整五语译名");
        flyout.ShowAt(list.ContainerFromItem(row) as FrameworkElement ?? list);
    }

    private static TextBlock DetailText(string text, string styleKey) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources[styleKey],
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.None,
        IsTextSelectionEnabled = true,
    };

    /// <summary>VK_PACKET：输入法组合结束后交给应用的按键码。组合期间的回车会以它上报，而不是 Enter。</summary>
    private const int ImeProcessedKeyCode = 0xE7;

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 输入法组合中的回车不能当「执行搜索」，否则会提交半截查询（§4.2）。
        // Windows.System.VirtualKey 的引用元数据里没有 ImeProcessed / Packet 成员名，
        // 所以按 VK_PACKET 的数值判定。这条分支能否命中取决于运行时 IME 行为，静态无法确认。
        if ((int)e.Key == ImeProcessedKeyCode)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (_vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// 证据行的「到剧情页定位」：doc §4.4 要求 story_key 能跳到剧情页并定位。
    /// 定位键交给 StoryViewModel（单例）填进它的搜索框并重查；
    /// 跳页只做一件事——把壳层导航栏选中「剧情」，导航本身仍由 MainWindow 的既有逻辑完成。
    /// 失败（找不到导航栏 / 交不出去）时如实写进 Attention，并留下可选中复制的完整键值。
    /// </summary>
    private void OnLocateEvidenceClick(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string ?? string.Empty;
        if (!_vm.TryApplyStoryLocate(key))
        {
            return;
        }

        if (!TrySelectStoryNavItem())
        {
            _vm.ReportLocateBlocked(key);
        }
    }

    private bool TrySelectStoryNavItem()
    {
        try
        {
            var nav = FindAncestor<NavigationView>();
            if (nav is null)
            {
                return false;
            }

            var item = nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == "story");
            if (item is null)
            {
                return false;
            }

            nav.SelectedItem = item;
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"TermsPage: 切到剧情页失败：{ex.Message}");
            return false;
        }
    }

    private T? FindAncestor<T>() where T : DependencyObject
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            // VisualTreeHelper.GetParent 接受 DependencyObject 并在根节点返回 null，
            // 不需要（也没有）WinUI 3 里并不存在的 `Visual` 类型判断。
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

/// <summary>
/// 模板内的 bool → Visibility。页面级绑定用 <c>BoolToVisibility()</c>，
/// DataTemplate 里拿不到页面方法，所以要有这个转换器。
/// 命名带 Terms 前缀，避免与其它页各自新增的同型转换器撞名。
/// </summary>
public sealed class TermsBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>空字符串 / null → 隐藏该行内说明（词类、官方标记、指标行、局部提示）。</summary>
public sealed class TermsHasTextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>本页空值用短标签；仅术语单元格转换服务层的缺值占位，保留解析错误。</summary>
public sealed class TermsEmptyCellConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)
            || parameter is "term" && text == NameCellText.MissingCell)
        {
            return "未收录";
        }

        return text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
