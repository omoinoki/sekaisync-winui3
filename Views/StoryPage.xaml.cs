using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SekaiSync.Desktop.Services;
using SekaiSync.Desktop.ViewModels;

namespace SekaiSync.Desktop.Views;

/// <summary>剧情页：5 种叙事单元，正文按说话人拆块，可切 2 / 5 语对照。</summary>
public sealed partial class StoryPage : Page
{
    private readonly StoryViewModel _vm = StoryViewModel.Shared;
    private bool _initialized;

    public StoryViewModel ViewModel => _vm;

    public StoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        WireParallelPanning();
    }

    // ── 对照栏横向平移 ──────────────────────────────────────────────────────
    // 按住滚轮（中键）拖动即可调整横向滚动位置。ScrollViewer 会把指针事件标记为
    // 已处理，因此必须用 AddHandler(handledEventsToo: true) 才收得到。
    private bool _panning;
    private double _panStartX;
    private double _panStartOffset;

    private void WireParallelPanning()
    {
        ParallelScroller.AddHandler(PointerPressedEvent, new PointerEventHandler(OnParallelPointerPressed), true);
        ParallelScroller.AddHandler(PointerMovedEvent, new PointerEventHandler(OnParallelPointerMoved), true);
        ParallelScroller.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnParallelPointerEnded), true);
        ParallelScroller.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnParallelPointerEnded), true);
        ParallelScroller.PointerCaptureLost += (_, _) => _panning = false;
    }

    private void OnParallelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        _panning = true;
        _panStartX = point.Position.X;
        _panStartOffset = ParallelScroller.HorizontalOffset;
        ParallelScroller.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnParallelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var x = e.GetCurrentPoint(null).Position.X;
        ParallelScroller.ChangeView(_panStartOffset - (x - _panStartX), null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnParallelPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        ParallelScroller.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    // 仅本页缩短显示，不改变共享选项及其他页面的 ToString 行为。
    public static string InstanceDisplayName(InstanceFilter filter) => filter switch
    {
        InstanceFilter.SekaiViewerOnly => "Sekai Viewer",
        InstanceFilter.MoesekaiOnly => "Moesekai",
        _ => "合并视图",
    };

    public static string RowMetadata(string kind, string instance, string translation, string crawled) =>
        $"{kind} · {instance} · {translation} · 抓取 {crawled}";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await _vm.InitializeAsync();
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
        if (SearchBox.Focus(FocusState.Keyboard))
        {
            // Ctrl+F 要把已有关键词整段选中，改起来才是「替换搜索词」而不是追加。
            SearchBox.SelectAll();
        }
        args.Handled = true;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 中文输入法把候选词上屏时，这一发按键是 Win32 的 VK_PACKET(0xE7)「来自输入法」信号而不是真 Enter。
        // WinRT 的 VirtualKey 枚举没有给它命名成员，所以按数值比较；若框架在某些输入法下根本不派发它，
        // 这个分支就是空转，不会误吞真实回车。（是否真能拦到，需在真机上用中文输入法确认。）
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

        // Esc 只关「当前这一层」：清空搜索词并重查，不动类型 / 版本 / 实例 / 仅未译。
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
