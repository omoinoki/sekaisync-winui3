using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SekaiSync.Desktop.Services;
using SekaiSync.Desktop.ViewModels;
using SekaiSync.Desktop.Views;
using Windows.Graphics;

namespace SekaiSync.Desktop;

/// <summary>
/// 导航壳：标题栏 + NavigationView（资讯 / 剧情 / 台词 / 用语 / 实体 / 数据源 / 同步 / 智能体接入 / 设置）
/// + 常驻底部状态条（同步率、进度、重新评估、同步入口）。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _confirmingClear;

    /// <summary>
    /// 解析后的当前主题（Auto 已经落到具体的 Light / Dark）。
    ///
    /// ContentDialog 与 Popup 挂在 XamlRoot 的 island 上，不是 RootGrid 的后代，
    /// 吃不到 RootGrid 的 RequestedTheme 继承——暗色应用里弹出的仍是跟随系统的白窗。
    /// XamlRoot.ContentIsland 不暴露 RequestedTheme，所以每个弹窗创建时要显式取这一份。
    /// </summary>
    public static ElementTheme ActiveTheme { get; private set; } = ElementTheme.Default;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "SekaiSync Desktop for Windows";
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(1360 * scale), workArea.Width - (int)(32 * scale));
        var height = Math.Min((int)(860 * scale), workArea.Height - (int)(32 * scale));
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width, height));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyWindowMinimumSize();
        AppWindow.Changed += OnAppWindowChanged;
        // Mica 在不支持、用户关闭透明度、省电、高对比等场景自行退回实底色，
        // 不需要（也不应该）自绘模糊——手册 §3.2 / §4 Windows 行。
        SystemBackdrop = new MicaBackdrop();

        AppServices.UiDispatcher = action => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
        AppServices.Settings.Saved += (_, _) =>
        {
            ApplyTheme();
            ApplyMotionDensity();
        };
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
        ShellViewModel.Shared.Bus.OutputAppended += OnOutputAppended;
        Closed += (_, _) =>
        {
            _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
            ShellViewModel.Shared.Bus.OutputAppended -= OnOutputAppended;
            AppWindow.Changed -= OnAppWindowChanged;
            _tray?.Dispose();
        };
        ApplyTheme();
        ApplyMotionDensity();
        RootGrid.ActualThemeChanged += (_, _) => ApplyTheme();

        NavView.Loaded += (sender, args) =>
        {
            if (NavView.MenuItems.Count > 0 && NavView.SelectedItem is null)
            {
                // 打开应用先看「有什么新的」——资讯排在第一项。
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
            }
        };

        AttachTray();
    }

    // ── 托盘驻留 ─────────────────────────────────────────────────────────

    private TrayIconService? _tray;

    /// <summary>托盘「退出」置位后关闭才真的退出；否则点 X 只是收进托盘。</summary>
    private bool _exiting;

    private void AttachTray()
    {
        _tray = TrayIconService.TryCreate(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            // 悬浮文本直接用窗口标题：两处名字以后不可能对不上。
            Title,
            ShowFromTray,
            ExitFromTray);
        if (!_tray!.IsAvailable)
        {
            // 托盘没建起来就绝不能拦截关闭，否则应用既收不进托盘也关不掉。
            _tray = null;
            return;
        }
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }
        args.Cancel = true;
        AppWindow.Hide();
    }

    /// <summary>托盘「打开面板」与第二实例重定向都走这里。</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }
        Activate();
    }

    private void ExitFromTray()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    private void ApplyMotionDensity() => BackgroundDrift.SetDensity(AppServices.Settings.MotionDensity);

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            ApplyWindowMinimumSize();
        }
    }

    private void ApplyWindowMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        // 原生窗口边界使用物理像素，尺寸令牌使用 DIP。
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var minWidth = Math.Min((int)Math.Ceiling((double)Application.Current.Resources["SkWindowMinWidth"] * scale), workArea.Width);
        var minHeight = Math.Min((int)Math.Ceiling((double)Application.Current.Resources["SkWindowMinHeight"] * scale), workArea.Height);
        if (presenter.PreferredMinimumWidth != minWidth) presenter.PreferredMinimumWidth = minWidth;
        if (presenter.PreferredMinimumHeight != minHeight) presenter.PreferredMinimumHeight = minHeight;

        var size = AppWindow.Size;
        if (presenter.State == OverlappedPresenterState.Restored && (size.Width < minWidth || size.Height < minHeight))
        {
            AppWindow.Resize(new SizeInt32(Math.Max(size.Width, minWidth), Math.Max(size.Height, minHeight)));
        }
    }

    /// <summary>底部状态条与明细面板的数据源。</summary>
    public ShellViewModel Shell => ShellViewModel.Shared;

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        var pageType = tag switch
        {
            "news" => typeof(NewsPage),
            "story" => typeof(StoryPage),
            "voices" => typeof(VoicesPage),
            "terms" => typeof(TermsPage),
            "entities" => typeof(EntitiesPage),
            "sources" => typeof(SourcesPage),
            "sync" => typeof(SyncPage),
            "agent" => typeof(AgentPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };

        // 只有这两页有「跑一个东西、顺手看输出」的动作，停靠区就只在这两页钉着。
        Shell.IsConsoleVisible = tag is "sync" or "agent";
        if (Shell.IsConsoleVisible)
        {
            ApplyDockHeight();
            ScrollConsoleToBottom(force: true);
        }

        if (pageType is not null && (ContentFrame.Content is null || ContentFrame.Content.GetType() != pageType))
        {
            ContentFrame.Navigate(pageType);
        }
    }

    // ── 任务输出停靠区的高度 ──────────────────────────────────────────────

    private const double DockMinHeight = 120;
    private const double DockDefaultHeight = 200;

    /// <summary>用户拉出来的期望高度；窗体变矮时只临时压下去，放大后按这个值还原。</summary>
    private double _dockWantedHeight = DockDefaultHeight;
    private bool _dockDragging;
    private double _dockDragStartY;
    private double _dockDragStartHeight;

    /// <summary>
    /// 窗体尺寸变化时重算停靠区高度：用户想要的值优先，但不超过窗高的一半，
    /// 也不许把上方内容压没。
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDockHeight();

    private void ApplyDockHeight()
    {
        if (!Shell.IsConsoleVisible)
        {
            return;
        }
        // 高度写在元素上而不是行上：行是 Auto，停靠区一隐藏就归零，不会把空白摊给别的页。
        var available = RootGrid.ActualHeight;
        var cap = available > 0 ? Math.Max(DockMinHeight, available * 0.45) : double.MaxValue;
        ConsoleDock.Height = Math.Clamp(_dockWantedHeight, DockMinHeight, cap);
    }

    private void SetDockHeight(double desired)
    {
        _dockWantedHeight = desired;
        ApplyDockHeight();
    }

    private void DockResizer_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dockDragging = true;
        // 位移必须相对 RootGrid 取，不能相对拉拽条自己取：条本身会随停靠区变高而下移，
        // 以自己为参照等于把位移反馈进位移里，拖动时会来回抽搐。
        _dockDragStartY = e.GetCurrentPoint(RootGrid).Position.Y;
        _dockDragStartHeight = ConsoleDock.ActualHeight;
        DockResizer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DockResizer_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dockDragging)
        {
            return;
        }
        // 边界往下拖 = 停靠区变矮，所以位移取反。
        SetDockHeight(_dockDragStartHeight - (e.GetCurrentPoint(RootGrid).Position.Y - _dockDragStartY));
        e.Handled = true;
    }

    private void DockResizer_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dockDragging = false;
        DockResizer.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void DockResizer_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => _dockDragging = false;

    private void DockResizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetDockHeight(DockDefaultHeight);
        e.Handled = true;
    }

    /// <summary>键盘等价路径：上下方向键各调 16 dip，Home 回默认。拖拽不能只给鼠标留口。</summary>
    private void DockResizer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            Windows.System.VirtualKey.Up => 16,
            Windows.System.VirtualKey.Down => -16,
            Windows.System.VirtualKey.Home => double.NaN,
            _ => 0d,
        };
        if (step == 0d)
        {
            return;
        }
        SetDockHeight(double.IsNaN(step) ? DockDefaultHeight : ConsoleDock.ActualHeight + step);
        e.Handled = true;
    }

    private void OnOutputAppended(object? sender, EventArgs e) => ScrollConsoleToBottom(force: false);

    /// <summary>
    /// 只有用户本来就在底部附近时才跟随滚动，否则会把人正在读的历史拽走。
    /// 与控制台 TextBox 内部的 ScrollViewer 对话（模板尚未可用时这次就不滚，下一行输出会再试）。
    /// </summary>
    private void ScrollConsoleToBottom(bool force)
    {
        // 低优先级 + 先布局一次：OutputText 刚赋值时内部 ScrollViewer 的 ExtentHeight
        // 还是上一批的旧值，照旧值判断「在不在底部附近」会每批都落后一截，
        // 落后超过 64 像素后就彻底不再跟随，新输出全在视口下面。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            ConsoleBox.UpdateLayout();
            var viewer = FindScrollViewer(ConsoleBox, 0);
            if (viewer is null)
            {
                return;
            }
            var max = Math.Max(0, viewer.ExtentHeight - viewer.ViewportHeight);
            if (!force && max - viewer.VerticalOffset > 64)
            {
                return;
            }
            viewer.ChangeView(null, max, null, true);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root, int depth)
    {
        if (depth > 8)
        {
            return null;
        }
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child, depth + 1) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>清空前的二次确认。对话框起不来时**不**清空——那唯一的失败证据不能悄悄没掉。</summary>
    private async void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_confirmingClear)
        {
            return;
        }
        _confirmingClear = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "清空当前视图？",
                Content = Shell.ClearOutputConfirmText,
                PrimaryButtonText = "清空",
                CloseButtonText = "保留输出",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = ActiveTheme,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Shell.ClearOutput();
            }
        }
        catch (Exception ex)
        {
            App.Log($"MainWindow: 清空确认对话框未能显示：{ex}");
        }
        finally
        {
            _confirmingClear = false;
        }
    }

    /// <summary>状态条与明细面板上的「同步」入口：只负责把用户送到同步页，
    /// 前置清单与正文抓取的条款确认都在那一页完成（手册 §5.3）。</summary>
    private void GoToSyncPage_Click(object sender, RoutedEventArgs e) => SelectNavItem("sync");

    private void GoToSettingsPage_Click(object sender, RoutedEventArgs e) => SelectNavItem("settings");

    private void SelectNavItem(string tag)
    {
        var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag)
            ?? NavView.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }
    }

    private void CloseProgressDetail_Click(object sender, RoutedEventArgs e) => Shell.IsDetailOpen = false;

    /// <summary>
    /// Esc 收起明细面板。用 KeyDown 而不是 KeyboardAccelerator：挂快捷键时 WinUI 会在
    /// 悬停处自动弹一个按键提示气泡（本仓 NewsPage 已踩过同一个坑，见那里的注释）。
    /// 面板没开时不吃掉这次按键，让 Esc 继续交给页面/对话处理。
    /// </summary>
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F &&
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) &&
            ContentFrame.Content is Page page && page.FindName("SearchBox") is TextBox search &&
            search.Focus(FocusState.Keyboard))
        {
            search.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key != Windows.System.VirtualKey.Escape || !Shell.IsDetailOpen)
        {
            return;
        }

        Shell.IsDetailOpen = false;
        e.Handled = true;
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sekaisync_desktop.log");
        try
        {
            if (!System.IO.File.Exists(path))
            {
                CrashNoteText.Text = $"诊断日志尚未生成：{path}";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashNoteText.Text = $"无法打开日志文件：{DiagnosticText.Redact(ex.Message)}";
            App.Log($"OpenLogButton 失败：{ex}");
        }
    }

    private void OnSystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args)
        => DispatcherQueue.TryEnqueue(ApplyTheme);

    private void ApplyTheme()
    {
        var systemBackground = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        var theme = AppServices.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => systemBackground.R + systemBackground.G + systemBackground.B < 384
                ? ElementTheme.Dark : ElementTheme.Light,
        };
        RootGrid.RequestedTheme = theme;
        ActiveTheme = theme;

        AppWindow.TitleBar.PreferredTheme = RootGrid.ActualTheme == ElementTheme.Dark
            ? TitleBarTheme.Dark : TitleBarTheme.Light;

        // 对比主题：logo 图形退回纯文字（手册 §3.4）。只读 HighContrast 状态，
        // 不订阅 HighContrastChanged——该事件在 WinUI 3 桌面订阅即抛 COMException（实测），
        // 切换对比主题时用户会重启或重开页面，这里不做常驻监听。
        var highContrast = false;
        try
        {
            highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        }
        catch (Exception ex)
        {
            App.Log($"AccessibilitySettings 不可用：{DiagnosticText.Redact(ex.Message)}");
        }

        TitleBarLogo.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        BackgroundDrift.SetDecorationEnabled(!highContrast);
    }

    public void ShowCrashInfo(string message)
    {
        var redacted = DiagnosticText.Redact(message);
        CrashMessageText.Text = redacted.Length > 600 ? redacted[..600] + "…" : redacted;

        // InfoBar 打开后改内容不会重新播报，先关再开（WinUI 官方 InfoBar 说明）。
        if (CrashInfoBar.IsOpen)
        {
            CrashInfoBar.IsOpen = false;
        }

        CrashInfoBar.IsOpen = true;
    }
}
