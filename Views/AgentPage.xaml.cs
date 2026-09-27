using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>
/// 智能体接入：MCP stdio / HTTP 配置与本地服务控制。
///
/// 输出控制台不在本页：它已经收进 MainWindow 的跨页停靠区，只在这两页可见。
/// 本页只负责连接方式的选择与服务生命周期。
/// </summary>
public sealed partial class AgentPage : Page
{
    private readonly AgentViewModel _vm = AgentViewModel.Shared;

    public AgentViewModel ViewModel => _vm;

    public AgentPage()
    {
        InitializeComponent();
        _vm.RebuildSnippets();
        Loaded += OnLoaded;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TextVisibility(string value) => BoolToVisibility(!string.IsNullOrWhiteSpace(value));

    public Visibility ShowOtherTask(bool busy, bool serviceRunning) =>
        BoolToVisibility(busy && !serviceRunning);

    public string ServiceStateLabel(bool running, bool cancelRequested) => running
        ? cancelRequested ? "正在请求停止…" : "本页进程运行中"
        : "本页未运行服务";

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 760;
        HttpColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(HttpCard, wide ? 1 : 0);
        Grid.SetRow(HttpCard, wide ? 0 : 1);
        ConnectionCards.ColumnSpacing = wide ? 12 : 0;
        ConnectionCards.RowSpacing = wide ? 0 : 12;
        JsonPreview.MaxHeight = wide ? 160 : 120;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _vm.RefreshState();

    /// <summary>
    /// 帮助：原来的「接入详情」折叠面板搬进二级窗口，与同步页同一套做法——
    /// 外层说人话，端点清单、stdio 命令行与 /health 原始响应收进内层技术细节。
    /// </summary>
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 680 };
        foreach (var row in new[]
        {
            _vm.ProtocolGuideRow1, _vm.ProtocolGuideRow2, _vm.ProtocolGuideRow3, _vm.StartupEventCheckNotice,
        })
        {
            content.Children.Add(HelpLine(row));
        }

        var technical = new StackPanel { Spacing = 8, Margin = new Thickness(16, 16, 16, 12) };
        technical.Children.Add(HelpLine(_vm.EndpointsRow));
        technical.Children.Add(HelpLine("stdio 启动命令", strong: true));
        technical.Children.Add(HelpLine(_vm.StdioCommandEcho, mono: true));
        if (_vm.HasHealthDetail)
        {
            technical.Children.Add(HelpLine("/health 原始响应（已裁剪长度）", strong: true));
            var health = new TextBox
            {
                Text = _vm.HealthDetailText,
                Style = Sk("SkWorkspaceTextBoxStyle"),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Consolas"),
            };
            health.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(health, "健康检查原始响应");
            technical.Children.Add(health);
        }

        content.Children.Add(new Expander
        {
            Header = "技术细节：端点、命令行与健康检查",
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
            var dialog = new ContentDialog
            {
                Title = "接入说明",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = MainWindow.ActiveTheme,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"AgentPage: 接入说明对话框未能显示：{ex}");
        }
    }

    /// <summary>
    /// 说明行。FontFamily 只在需要等宽时赋值：赋 null 会被类型转换器变成
    /// Source="Unknown"，点击当场抛异常、弹窗根本不出现。
    /// </summary>
    private static TextBlock HelpLine(string text, bool mono = false, bool strong = false)
    {
        var line = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = Sk(strong ? "SkStrongTextStyle" : "SkBodyTextStyle"),
            IsTextSelectionEnabled = !strong,
        };
        if (mono)
        {
            line.FontFamily = new FontFamily("Consolas");
        }
        return line;
    }

    /// <summary>样式键在合并字典里，页面级 Resources 查不到，必须走 Application 资源。</summary>
    private static Style Sk(string key) => (Style)Application.Current.Resources[key];
}
