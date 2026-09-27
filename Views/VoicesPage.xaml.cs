using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SekaiSync.Desktop.Services;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>台词页：5 类短文本，右侧给出同句的五服对照。</summary>
public sealed partial class VoicesPage : Page
{
    private readonly VoicesViewModel _vm = VoicesViewModel.Shared;
    private bool _initialized;

    public VoicesViewModel ViewModel => _vm;

    public VoicesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // F5 刷新 / Ctrl+F 聚焦搜索框（Page.KeyDown，避免 KeyboardAccelerator 的 F5 提示气泡）
        KeyDown += OnPageKeyDown;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    // 保持共享选项不变，仅本页显示简短的实例名称。
    public static string InstanceDisplayName(InstanceFilter filter) => filter switch
    {
        InstanceFilter.SekaiViewerOnly => "Sekai Viewer",
        InstanceFilter.MoesekaiOnly => "Moesekai",
        _ => "合并视图",
    };

    public static string SpeakerDisplayName(string speaker) => speaker.Length > 0 ? speaker : "无说话人";

    public static string RowMetadata(string kind, string instance, string crawled) =>
        $"{kind} · {instance} · 抓取 {crawled}";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await _vm.InitializeAsync();
    }

    /// <summary>F5 刷新 / Ctrl+F 聚焦并全选搜索框。</summary>
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
            var ctrl = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl)
            {
                if (SearchBox.Focus(FocusState.Keyboard))
                {
                    SearchBox.SelectAll();
                }
                e.Handled = true;
            }
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 输入法上屏那一发是 Win32 的 VK_PACKET(0xE7)；WinRT 的 VirtualKey 没有命名成员，按数值比较。
        // （与剧情页同一实现；能否真拦到需在中文输入法下实机确认。）
        if ((int)e.Key == 0xE7)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (_vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        // Esc 只清搜索词并重查，不动类型 / 版本 / 实例。
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _vm.SearchText = string.Empty;
            if (_vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
