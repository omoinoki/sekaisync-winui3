using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>
/// 同步：Master / 公告 / 正文命令的图形化入口。
///
/// 页面负责日志布局与跟随滚动、取消 / 清空确认，以及可见期间的锁状态复测。
/// 状态与输出本身都在 <see cref="SekaiSync.Desktop.Services.TaskBus"/>。
/// </summary>
public sealed partial class SyncPage : Page
{
    private readonly SyncViewModel _vm = SyncViewModel.Shared;
    private DispatcherTimer? _lockProbeTimer;
    private bool _confirmingCancel;
    private bool _confirmingCache;

    public SyncViewModel ViewModel => _vm;

    public SyncPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyCommandGroupLayout(e.NewSize.Width < CompactBreakpointDip);
    }

    /// <summary>页面可用宽度低于此值时，两组命令改为上下堆叠。</summary>
    private const double CompactBreakpointDip = 900;

    /// <summary>
    /// 命令组的双列 / 单列换向。
    ///
    /// 堆叠行高必须用 Auto 而不是星型：操作区整体在一个 ScrollViewer 里，
    /// 星型行拿到的可用高度是无穷大，量出来会是 0 高，第二组直接看不见。
    /// 间距也全部走子元素外边距而不是 Grid 的列/行间距——宽度为 0 的列照样会
    /// 产生一份列间距，换向后右侧就多出一条 12 dip 的空白。
    ///
    /// 单列时整页宽度足够，标题与命令栏同行；双列时每栏只有一半宽度，
    /// 标题挤进同一行会把命令栏压到溢出（按钮折进「…」里），所以标题回到上一行。
    /// </summary>
    private void ApplyCommandGroupLayout(bool compact)
    {
        CommandGroupsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        CommandGroupsGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
        CommandGroupsGrid.RowDefinitions[0].Height = GridLength.Auto;
        CommandGroupsGrid.RowDefinitions[1].Height = GridLength.Auto;
        Grid.SetRow(InspectionGroup, compact ? 1 : 0);
        Grid.SetColumn(InspectionGroup, compact ? 0 : 1);
        DataSyncGroup.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        InspectionGroup.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(6, 0, 0, 0);

        SetGroupTitleInline(DataSyncTitle, DataSyncBar, compact);
        SetGroupTitleInline(InspectionTitle, InspectionBar, compact);
    }

    private static void SetGroupTitleInline(TextBlock title, CommandBar bar, bool inline)
    {
        Grid.SetRow(title, 0);
        Grid.SetColumn(title, 0);
        title.Margin = inline ? new Thickness(4, 0, 0, 0) : new Thickness(4, 0, 0, 2);
        Grid.SetRow(bar, inline ? 0 : 1);
        Grid.SetColumn(bar, inline ? 1 : 0);
        Grid.SetColumnSpan(bar, inline ? 1 : 2);
    }

    /// <summary>
    /// 文字缓存入口：二级窗口选语言与深度，三级窗口读合规声明并二次确认。
    /// 只有三级确认通过才会把 --accept-tos 传下去，取消则回滚本页的勾选与深度。
    /// </summary>
    private async void ConfigureCache_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmingCache || !_vm.CanRun)
        {
            return;
        }

        _confirmingCache = true;
        var originalServers = _vm.CrawlServerOptions.ToDictionary(o => o.Region, o => o.IsSelected, StringComparer.Ordinal);
        var originalDepth = _vm.SelectedCrawlDepth;
        _vm.TosAccepted = false;

        void RestoreSelection()
        {
            foreach (var option in _vm.CrawlServerOptions)
            {
                option.IsSelected = originalServers[option.Region];
            }
            _vm.SelectedCrawlDepth = originalDepth;
            _vm.TosAccepted = false;
        }

        try
        {
            if (!await ShowCacheConfigDialogAsync())
            {
                RestoreSelection();
                return;
            }

            if (!await ShowCacheConsentDialogAsync())
            {
                RestoreSelection();
                return;
            }

            _vm.TosAccepted = true;
            _vm.CrawlCommand.Execute(null);
        }
        catch (Exception ex)
        {
            App.Log($"SyncPage: 文字缓存设置流程失败：{ex}");
        }
        finally
        {
            _confirmingCache = false;
        }
    }

    /// <summary>二级：只选范围。返回 true = 用户点了「下一步」。</summary>
    private async Task<bool> ShowCacheConfigDialogAsync()
    {
        var serverPanel = new StackPanel { Spacing = 2 };
        var serverChecks = new List<CheckBox>();
        foreach (var option in _vm.CrawlServerOptions)
        {
            var check = new CheckBox
            {
                Content = option.Label,
                IsChecked = option.IsSelected,
                MinHeight = 32,
            };
            check.Checked += (_, _) => option.IsSelected = true;
            check.Unchecked += (_, _) => option.IsSelected = false;
            serverChecks.Add(check);
            serverPanel.Children.Add(check);
        }

        var depthLabel = new TextBlock { Text = "缓存深度", Style = Sk("SkInlineLabelStyle") };
        var depth = new ComboBox
        {
            ItemsSource = _vm.CrawlDepthOptions,
            SelectedItem = _vm.SelectedCrawlDepth,
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = Sk("SkWorkspaceComboBoxStyle"),
        };
        depth.SelectionChanged += (_, _) =>
        {
            if (depth.SelectedItem is CrawlDepthOption selected)
            {
                _vm.SelectedCrawlDepth = selected;
            }
        };

        var scope = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = Sk("SkMutedTextStyle"),
        };
        var dialog = new ContentDialog
        {
            Title = "第 1 步 / 共 2 步 · 选择缓存范围",
            PrimaryButtonText = "下一步：确认声明",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = MainWindow.ActiveTheme,
        };

        void Sync()
        {
            scope.Text = $"当前选择：{_vm.CrawlScopeSummary}";
            dialog.IsPrimaryButtonEnabled = _vm.HasCrawlServers && !_vm.IsBusy && !_vm.LockHeld;
        }

        foreach (var check in serverChecks)
        {
            check.Checked += (_, _) => Sync();
            check.Unchecked += (_, _) => Sync();
        }
        Sync();

        var content = new StackPanel { Spacing = 10, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = "勾选要缓存的区服。一服一语，勾中区服就同时定了该服语言；一个都不勾时无法继续。",
            TextWrapping = TextWrapping.Wrap,
            Style = Sk("SkBodyTextStyle"),
        });
        content.Children.Add(serverPanel);
        content.Children.Add(depthLabel);
        content.Children.Add(depth);
        content.Children.Add(scope);
        dialog.Content = content;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>三级：合规与免责声明。返回 true = 用户勾选并点了「确认并开始缓存」。</summary>
    private async Task<bool> ShowCacheConsentDialogAsync()
    {
        var consent = new CheckBox
        {
            Content = _vm.CrawlTosLabel,
            IsChecked = false,
            MinHeight = 32,
        };
        var dialog = new ContentDialog
        {
            Title = "第 2 步 / 共 2 步 · 使用声明",
            PrimaryButtonText = "确认并开始缓存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = MainWindow.ActiveTheme,
            IsPrimaryButtonEnabled = false,
        };
        consent.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        consent.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;

        var content = new StackPanel { Spacing = 10, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = _vm.CrawlConsentText,
            TextWrapping = TextWrapping.Wrap,
            Style = Sk("SkBodyTextStyle"),
        });
        content.Children.Add(new TextBlock
        {
            Text = _vm.CrawlScopeSummary,
            TextWrapping = TextWrapping.Wrap,
            Style = Sk("SkMutedTextStyle"),
        });
        content.Children.Add(consent);
        dialog.Content = content;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 帮助：把原来内联的「任务详情」折叠面板搬进二级窗口。
    /// 每次打开都现读一遍 VM，拿到的是当时的真实范围，不是写死的说明。
    /// </summary>
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 680 };
        foreach (var row in new[]
        {
            _vm.PreflightWhatRow, _vm.PreflightWhereRow, _vm.PreflightNetworkRow,
            _vm.PreflightImpactRow, _vm.PreflightCancelRow, _vm.PreflightConsentRow,
        })
        {
            content.Children.Add(HelpLine(row));
        }

        var technical = new StackPanel { Spacing = 8, Margin = new Thickness(16, 16, 16, 12) };
        foreach (var row in new[]
        {
            _vm.PreflightScopeRow, _vm.PreflightLayerRow, _vm.PreflightSourceRow,
            _vm.PreflightEventCheckRow, _vm.PreflightAtomicRow,
        })
        {
            technical.Children.Add(HelpLine(row));
        }
        if (_vm.ShowLockNotice)
        {
            technical.Children.Add(HelpLine(_vm.PreflightLockRow));
        }
        technical.Children.Add(HelpLine(_vm.PreflightCommandsRow, mono: true));

        content.Children.Add(new Expander
        {
            Header = "技术细节与完整命令行",
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
                Title = "任务说明",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.ActiveTheme,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"SyncPage: 任务说明对话框未能显示：{ex}");
        }
    }

    /// <summary>
    /// 说明行。等宽字体只在需要时赋：给 FontFamily 塞 null 会被类型转换器变成
    /// Source="Unknown"，点击当场抛「'Unknown' is not a valid value for property 'FontFamily'」，
    /// 弹窗不出现、只留一条未处理异常——和之前样式键查错集合是同一类"handler 抛异常"故障。
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

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmingCancel || !_vm.CanCancel)
        {
            return;
        }
        _confirmingCancel = true;
        var label = _vm.Bus.CurrentLabel;
        var origin = _vm.Bus.CurrentOrigin;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "取消当前任务？",
                Content = $"{label}\n\n{_vm.CancelEffectShortText}",
                PrimaryButtonText = "终止任务",
                CloseButtonText = "继续运行",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = MainWindow.ActiveTheme,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && _vm.CanCancel && _vm.Bus.CurrentLabel == label && _vm.Bus.CurrentOrigin == origin)
            {
                _vm.CancelCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            App.Log($"SyncPage: 取消确认对话框未能显示：{ex}");
        }
        finally
        {
            _confirmingCancel = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.RefreshLockState();

        // 页面停留期间也要能反映锁的变化（旧版只在构造与跑完命令时读一次）。
        _lockProbeTimer?.Stop();
        _lockProbeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _lockProbeTimer.Tick += (_, _) => _vm.RefreshLockState();
        _lockProbeTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lockProbeTimer?.Stop();
        _lockProbeTimer = null;
    }
}
