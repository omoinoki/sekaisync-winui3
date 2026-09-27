using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>
/// 设置：store / Python / 端口 / 分页 / 主题。
/// 本页唯一需要 code-behind 的是「重置 store 覆盖路径」的二次确认：
/// 它会立刻落盘并改变整个应用的数据源，所以默认焦点按钮是「取消」（手册 §4.1）。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = SettingsViewModel.Shared;
    private bool _confirmingReset;

    public SettingsViewModel ViewModel => _vm;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void ResetStorePath_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmingReset)
        {
            return;
        }
        _confirmingReset = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "重置 store 覆盖路径？",
                Content = $"当前生效的 store：{_vm.CurrentEffectiveStore}\n\n" +
                    "确认后会清空设置里的手填覆盖路径、立刻保存并广播变更——整个应用的数据源当场切到自动探测结果。" +
                    "如果自动探测不到仓库根，各页会显示「读不到本地库」。",
                PrimaryButtonText = "确定重置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = MainWindow.ActiveTheme,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await _vm.ResetStorePathAndSaveAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log($"SettingsPage: 重置确认对话框未能显示：{ex}");
        }
        finally
        {
            _confirmingReset = false;
        }
    }

    /// <summary>
    /// 帮助：从「关于」面板挪出来的行为规则与技术细节。
    /// 与同步页 / 智能体接入页同一套做法——外层说人话并按卡片分组，
    /// 命令原文、动效枚数与数据库访问边界收进内层技术细节。
    /// </summary>
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 10, MaxWidth = 680 };
        AddHelpGroup(content, "数据源", _vm.StoreOverrideNote, _vm.StoreChangeNote);
        AddHelpGroup(content, "后端进程", _vm.PythonNote, _vm.PortNote);
        AddHelpGroup(content, "界面", _vm.ThemeNote, _vm.PageSizeNote);

        var technical = new StackPanel { Spacing = 8, Margin = new Thickness(16, 16, 16, 12) };
        technical.Children.Add(HelpLine(_vm.PythonCheckHelpText));
        technical.Children.Add(HelpLine(_vm.MotionDensityNote));
        technical.Children.Add(HelpLine(_vm.ReadOnlyAccessNote));

        content.Children.Add(new Expander
        {
            Header = "技术细节：检测命令、动效枚数与读写边界",
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
                Title = "设置项说明",
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
            App.Log($"SettingsPage: 设置说明对话框未能显示：{ex}");
        }
    }

    private static void AddHelpGroup(StackPanel host, string title, params string[] lines)
    {
        host.Children.Add(HelpLine(title, strong: true));
        foreach (var line in lines)
        {
            host.Children.Add(HelpLine(line));
        }
    }

    /// <summary>
    /// 说明行。FontFamily 只在需要时赋值：赋 null 会被类型转换器变成 Source="Unknown"，
    /// 点击当场抛异常、弹窗根本不出现（同步页的帮助按钮就是这么坏过一次）。
    /// </summary>
    private static TextBlock HelpLine(string text, bool strong = false)
    {
        var line = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = Sk(strong ? "SkStrongTextStyle" : "SkBodyTextStyle"),
            IsTextSelectionEnabled = !strong,
        };
        return line;
    }

    /// <summary>
    /// 署名链接：WinUI 3 的 Hyperlink 没有 UWP 那套 RequestNavigate，只有 Click，
    /// 而且不会自己开浏览器，必须在这里接住。
    /// </summary>
    private async void Credit_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as Microsoft.UI.Xaml.Documents.Hyperlink)?.NavigateUri?.AbsoluteUri ?? string.Empty;
        if (url.Length == 0)
        {
            return;
        }
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            App.Log($"SettingsPage: 打开署名链接失败 {url}：{ex.Message}");
        }
    }

    /// <summary>样式键在合并字典里，页面级 Resources 查不到，必须走 Application 资源。</summary>
    private static Style Sk(string key) => (Style)Application.Current.Resources[key];
}
