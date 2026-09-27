using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 设置页面：store / Python / 端口 / 分页 / 主题。
///
/// 两个「改全局上下文」的动作（重置 store、浏览选目录）与一次性的保存回执都在这里分栏；
/// 破坏性/上下文级动作的二次确认由页面负责（ContentDialog），默认焦点按钮是「取消」。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>「检测」用的命令：kb-status 在 CLI 侧被排除在自动新事件检测之外，纯读本机 store。</summary>
    public const string KbStatusArguments = "--no-event-check kb-status";

    private int _savedNoticeSerial;

    private readonly AppSettings _settings = AppServices.Settings;
    private readonly AppEnvironment _environment = AppServices.Environment;
    private readonly DatabaseService _database = AppServices.Database;
    private readonly TaskBus _bus = TaskBus.Shared;

    public static SettingsViewModel Shared { get; } = new();

    /// <summary>页面绑它取输出与运行态（检测解释器要跑一次子进程）。</summary>
    public TaskBus Bus => _bus;

    [ObservableProperty]
    private string _storePath;

    [ObservableProperty]
    private string _pythonExecutable;

    [ObservableProperty]
    private string _httpPort;

    [ObservableProperty]
    private int _pageSize;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private string _databaseStatusText = string.Empty;

    [ObservableProperty]
    private bool _databaseOk;

    [ObservableProperty]
    private string _repoRootText = string.Empty;

    /// <summary>一次性保存回执（InfoBar，自动收起）；不再往状态行里写永不清除的文本。</summary>
    [ObservableProperty]
    private bool _hasSavedNotice;

    [ObservableProperty]
    private string _savedNoticeText = string.Empty;

    [ObservableProperty]
    private bool _savedNoticeIsWarning;

    public Microsoft.UI.Xaml.Controls.InfoBarSeverity SavedNoticeSeverity => SavedNoticeIsWarning
        ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning
        : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    [ObservableProperty]
    private bool _isCheckingPython;

    [ObservableProperty]
    private string _pythonCheckText = "尚未检测。";

    /// <summary>装饰性动效密度：0 关闭 / 1 轻盈 / 2 丰富。</summary>
    [ObservableProperty]
    private int _motionDensityIndex;

    public SettingsViewModel()
    {
        _storePath = _environment.ConfiguredStorePath ?? string.Empty;
        _pythonExecutable = _settings.PythonExecutable;
        _httpPort = _settings.HttpPort.ToString();
        _pageSize = _settings.PageSize;
        _motionDensityIndex = _settings.MotionDensity switch
        {
            "Off" => 0,
            "Full" => 2,
            _ => 1,
        };
        _themeIndex = _settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        _repoRootText = _environment.RepoRoot.Length > 0 ? _environment.RepoRoot : "（未自动识别到仓库根，请手动指定 store）";
        _ = RefreshDatabaseStatusAsync();
    }

    public string[] ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    public string[] MotionDensityOptions { get; } = ["关闭", "轻盈", "丰富"];

    public int[] PageSizeOptions { get; } = [50, 100, 200, 500, 1000];

    // ── 说明文案（T-1 / T-4 / T-5：写了就得说清有没有被读、改了会影响谁） ────

    /// <summary>当前真正生效的 store（覆盖路径为空时是自动探测结果）。</summary>
    public string CurrentEffectiveStore =>
        _environment.StorePath.Length == 0 ? "未确定：既没有覆盖路径，也没探测到仓库根下的 store" : _environment.StorePath;

    public string StoreOverrideNote =>
        string.IsNullOrWhiteSpace(StorePath)
            ? "留空时自动查找仓库根下的 store（识别 sekaisync/cli.py 或 store/kb/sekaisync.db）。"
            : "已填写覆盖目录，保存后生效；清空并保存可恢复自动探测。";

    public string PythonNote =>
        "填写可执行 sekaisync 模块的 Python 命令名或 .exe 路径，用于同步与本地服务。工作目录为探测到的仓库根。";

    public string PythonCheckHelpText =>
        $"先保存当前设置，再运行 python -m sekaisync --store \"<当前生效 store>\" {KbStatusArguments}。" +
        "仅读取本机数据，不联网；输出见同步页共享控制台。";

    public string PortNote =>
        "端口范围 1–65535。保存后用于下次服务启动；已复制的连接地址需重新复制。默认绑定 127.0.0.1，服务无认证。";

    /// <summary>
    /// 「关于」面板的三行实况：仓库根、服务基址、设置文件。
    /// 原先基址与设置文件挤在 HostNote 一句里，拆开后每一行才对得上一个变量。
    /// </summary>
    public string RepoRootNote => $"探测到的仓库根：{RepoRootText}";

    public string ServiceBaseNote => $"已保存的服务基址：http://{_settings.HttpHost}:{PortOrCurrent()}";

    public string SettingsFileNote => $"设置文件：{_settings.SettingsFilePath}";

    public string PageSizeNote =>
        "保存后原始表立即重查；其他列表在下次查询或翻页时采用新行数。";

    public string ThemeNote => "保存后立即应用主题；跟随系统时随 Windows 外观切换。";

    public string MotionDensityNote =>
        "标题栏与导航区背景的漂流图形密度：轻盈 24 枚、丰富 42 枚、关闭则不绘制。" +
        "保存后立即生效；系统「动画效果」关闭时始终静止。内容区不受影响。";

    public string StoreChangeNote =>
        "保存新目录后，各页切换数据源并重载；无效目录会显示读取失败。";

    /// <summary>版本号统一取自 <see cref="AppEnvironment.ProductVersion"/>，不再各页解析一份。</summary>
    public static string ProductVersion => AppEnvironment.ProductVersion;

    public string AboutText => $"SekaiSync Desktop {ProductVersion} · WinUI 3 / Windows App SDK 1.8";

    public string ReadOnlyAccessNote =>
        "界面只读访问 kb/sekaisync.db；同步与正文爬取由 CLI 子进程写库。";

    private string PortOrCurrent() => _settings.HttpPort.ToString();

    partial void OnStorePathChanged(string value)
    {
        OnPropertyChanged(nameof(StoreOverrideNote));
        OnPropertyChanged(nameof(StoreChangeNote));
    }

    // ── 数据库状态（T-7：构造函数里的 fire-and-forget 必须自带 try/catch） ───

    public async Task RefreshDatabaseStatusAsync()
    {
        try
        {
            await _database.InitializeAsync();
            DatabaseOk = _database.IsAvailable;
            DatabaseStatusText = _database.IsAvailable
                ? $"已连接：{_database.DatabasePath}"
                : _database.UnavailableReason;
        }
        catch (Exception ex)
        {
            DatabaseOk = false;
            DatabaseStatusText = $"读取数据库状态时出错：{ex.Message}";
            App.Log($"SettingsViewModel: 数据库状态刷新失败：{ex}");
        }
    }

    // ── 命令 ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        var problems = string.Empty;
        _settings.StorePath = StorePath.Trim();
        _settings.PythonExecutable = string.IsNullOrWhiteSpace(PythonExecutable) ? "python" : PythonExecutable.Trim();
        if (int.TryParse(HttpPort, out var port) && port is > 0 and < 65536)
        {
            _settings.HttpPort = port;
        }
        else
        {
            problems = $"端口「{HttpPort}」不是 1–65535 的整数，已保留原值 {_settings.HttpPort}；";
            HttpPort = _settings.HttpPort.ToString();
        }
        _settings.PageSize = PageSize is > 0 and <= 10000 ? PageSize : 200;
        _settings.Theme = ThemeIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "Auto",
        };
        _settings.MotionDensity = MotionDensityIndex switch
        {
            0 => "Off",
            2 => "Full",
            _ => "Quiet",
        };
        _settings.Save();

        await RefreshDatabaseStatusAsync();

        // 不区分成败地广播：换成坏路径时，各页要走自己的「读不到本地库」分支，
        // 而不是继续显示上一份库的数据、跟设置页互相矛盾（T-2）。
        AppServices.RaiseStoreInvalidated();
        AgentViewModel.Shared.RefreshState();

        ShowSavedNotice(DatabaseOk
            ? $"{problems}设置已保存。"
            : $"{problems}设置已保存，当前数据库不可用，请检查目录。",
            warning: !DatabaseOk || problems.Length > 0);

        OnPropertyChanged(nameof(CurrentEffectiveStore));
        OnPropertyChanged(nameof(StoreOverrideNote));
        OnPropertyChanged(nameof(PortNote));
        OnPropertyChanged(nameof(ServiceBaseNote));
        OnPropertyChanged(nameof(SettingsFileNote));
        OnPropertyChanged(nameof(RepoRootNote));
        OnPropertyChanged(nameof(ReadOnlyAccessNote));
        OnPropertyChanged(nameof(MotionDensityNote));
    }

    /// <summary>
    /// 重置 store 覆盖路径。页面必须先用 ContentDialog 确认（默认焦点是「取消」），
    /// 确认后才调这里——这一步会立刻落盘并改变整个应用的数据源（T-3）。
    /// </summary>
    public Task ResetStorePathAndSaveAsync()
    {
        StorePath = string.Empty;
        return SaveAsync();
    }

    /// <summary>FolderPicker 需要窗口句柄，由页面传入。</summary>
    [RelayCommand]
    private async Task BrowseStoreAsync()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                StorePath = folder.Path;
                await SaveAsync();
            }
            else
            {
                ShowSavedNotice("未选择文件夹，store 路径保持原值。", warning: false);
            }
        }
        catch (Exception ex)
        {
            ShowSavedNotice($"选择文件夹失败：{ex.Message}", warning: true);
        }
    }

    /// <summary>检测解释器能不能跑 sekaisync（T-4）。结果按退出码如实说，不写「完成」。</summary>
    [RelayCommand]
    private async Task CheckPythonAsync()
    {
        if (IsCheckingPython)
        {
            return;
        }
        if (_bus.IsRunning)
        {
            ShowSavedNotice($"未检测：{_bus.CurrentLabel}（{_bus.CurrentOrigin}）正占着唯一的子进程槽位。", warning: true);
            return;
        }

        IsCheckingPython = true;
        PythonCheckText = "正在检测…";
        try
        {
            // 先把界面上的改动落到设置里，否则检测的是上一个解释器路径。
            await SaveAsync();
            var code = await _bus.RunAsync("检测 Python 解释器", KbStatusArguments, "设置页");
            PythonCheckText = code == 0
                ? $"检测通过：{_settings.PythonExecutable}（退出码 0）。"
                : $"检测未通过：退出码 {code}，详见同步页输出。";
        }
        catch (Exception ex)
        {
            PythonCheckText = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsCheckingPython = false;
        }
    }

    partial void OnIsCheckingPythonChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckPython));
    }

    public bool CanCheckPython => !IsCheckingPython;

    /// <summary>一次性回执：显示后自动收起（旧版 StatusText 永远停在「设置已保存。」）。</summary>
    private void ShowSavedNotice(string text, bool warning)
    {
        SavedNoticeText = text;
        SavedNoticeIsWarning = warning;
        OnPropertyChanged(nameof(SavedNoticeSeverity));
        HasSavedNotice = true;
        var serial = ++_savedNoticeSerial;
        _ = AutoCloseNoticeAsync(serial, warning ? 14 : 8);
    }

    private async Task AutoCloseNoticeAsync(int serial, int seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception)
        {
            return;
        }
        if (serial == _savedNoticeSerial)
        {
            HasSavedNotice = false;
        }
    }
}
