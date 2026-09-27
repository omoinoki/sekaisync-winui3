using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SekaiSync.Desktop.Services;

/// <summary>一次任务结语的语气（文字永远同时出现，颜色不是唯一通道，手册 §6 / A-03）。</summary>
public enum TaskTone
{
    /// <summary>没有任务、空闲。</summary>
    Neutral,

    /// <summary>正在跑 / 正在请求停止。</summary>
    Working,

    /// <summary>退出码 0 且未被请求停止。</summary>
    Success,

    /// <summary>非 0 退出码或启动失败。</summary>
    Problem,

    /// <summary>进程确认结束后的取消。</summary>
    Cancelled,
}

/// <summary>
/// 全局任务状态总线：进程生命周期、输出缓冲与结语文案的唯一来源。
///
/// 底部状态条与「同步」「智能体接入」两页的控制台都是它的视图
/// （docs/ui-architecture.md §5.2 推荐的「干净」做法）。之前两页各自持有
/// OutputText / StatusText / IsBusy，互相看不见：底栏在跑任务时同步页仍写「空闲。」。
///
/// 它只管「现在在跑什么、跑的是哪页发起的、结果如何」，具体命令由调用方给。
/// </summary>
public sealed partial class TaskBus : ObservableObject
{
    /// <summary>输出缓冲上限：超过后截掉最旧的一段，保留尾部（与旧版一致，避免无界增长）。</summary>
    private const int OutputSoftCap = 400_000;
    private const int OutputKeepChars = 200_000;

    private readonly ProcessLauncherService _launcher = AppServices.Launcher;
    private readonly AppEnvironment _environment = AppServices.Environment;

    public static TaskBus Shared { get; } = new();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _outputText = string.Empty;

    /// <summary>当前正在跑的命令的人类可读标签，如「同步五服 Master Data」。</summary>
    [ObservableProperty]
    private string _currentLabel = string.Empty;

    /// <summary>发起当前任务的界面（「同步页」/「智能体接入页」/「底部状态条」/「设置页」）。</summary>
    [ObservableProperty]
    private string _currentOrigin = string.Empty;

    /// <summary>最近一次任务的结论文本。</summary>
    [ObservableProperty]
    private string _statusText = "空闲。";

    /// <summary>结语气色；页面用 <c>ToneIsXxx</c> 布尔包装切换文字样式。</summary>
    [ObservableProperty]
    private TaskTone _statusTone = TaskTone.Neutral;

    /// <summary>已请求停止、但还没确认进程结束（手册 §5.3 的中间态）。</summary>
    [ObservableProperty]
    private bool _cancelRequested;

    /// <summary>输出缓冲新增了一行：页面据此滚动到底部。</summary>
    public event EventHandler? OutputAppended;

    /// <summary>一次任务结束（无论成败）。状态栏据此重读 progress.json。</summary>
    public event EventHandler<TaskCompletion>? Completed;

    public TaskBus()
    {
        _launcher.RunningChanged += OnRunningChanged;
    }

    /// <summary>有没有可停的进程（取消按钮的开关）。</summary>
    public bool CanCancel => IsRunning;

    /// <summary>
    /// 跑一条 sekaisync 子命令。已有任务在跑时不排队、直接如实说明未执行并返回 -1。
    /// </summary>
    /// <param name="origin">发起方名字，用于在两个控制台与底栏上说清「是谁在跑」。</param>
    public async Task<int> RunAsync(string label, string arguments, string? origin = null)
    {
        if (IsRunning)
        {
            AppendLine($"[未执行] {label}：已有任务在运行（{CurrentLabel}，来自 {CurrentOrigin}）。同一时间只能运行一个 Python 子进程。");
            StatusText = "未启动新任务：已有任务在运行。";
            StatusTone = TaskTone.Working;
            return -1;
        }

        var fullArguments = $"--store {QuotePath(_environment.StorePath)} {arguments}";
        CancelRequested = false;
        CurrentLabel = label;
        CurrentOrigin = string.IsNullOrEmpty(origin) ? "本程序" : origin!;
        StatusTone = TaskTone.Working;
        StatusText = $"正在运行：{label}";
        AppendLine("────────────────────────────");
        AppendLine($"> [{DateTime.Now:HH:mm:ss}] {label}（{CurrentOrigin}）");
        AppendLine($"> python -m sekaisync {fullArguments}");
        // 任务标题要立刻可见，不能等攒够一批子进程输出。
        FlushOutput();

        var code = -1;
        try
        {
            var result = await _launcher.RunWithResultAsync(fullArguments, AppendLine, CancellationToken.None);
            code = result.ExitCode;
            StatusText = DescribeOutcome(label, result);
            StatusTone = ToneOf(result);
        }
        catch (Exception ex)
        {
            StatusText = $"{label} 未成功：{ex.Message}";
            StatusTone = TaskTone.Problem;
            AppendLine($"[错误] {ex.Message}");
            App.Log($"TaskBus: {label} 失败：{ex}");
        }
        finally
        {
            IsRunning = _launcher.IsRunning;
            CurrentLabel = string.Empty;
            CurrentOrigin = string.Empty;
            CancelRequested = false;

            // 排在同样走 EnqueueUi 的输出行之后，保证不满一批的尾部输出先上屏再收尾。
            FlushOutput();
            AppServices.RaiseStoreInvalidated();
            Completed?.Invoke(this, new TaskCompletion(label, code, StatusText));
        }

        return code;
    }

    /// <summary>等价于 `python -m sekaisync &lt;args&gt;`，但按「评估同步率」这种语义命名。</summary>
    public Task<int> RunProgressAsync() => RunAsync("重新评估同步率", "--no-event-check progress", "底部状态条");

    /// <summary>
    /// 追加一行不属于子进程的说明（页面自己写的提示）。
    /// 这类提示往往只有一两行、且后面没有子进程输出来触发批量发布，所以立刻刷出去。
    /// </summary>
    public void Note(string line)
    {
        AppendLine(line);
        FlushOutput();
    }

    /// <summary>请求停止当前任务：先写「正在请求停止」，进程确认结束后才由结语文案改成「已取消」。</summary>
    public void Cancel()
    {
        if (!IsRunning)
        {
            StatusText = "当前没有正在运行的任务可停止。";
            StatusTone = TaskTone.Neutral;
            return;
        }

        CancelRequested = true;
        StatusTone = TaskTone.Working;
        StatusText = "正在请求停止…";
        AppendLine($"> 已发出停止请求：终止 {CurrentLabel} 的子进程（直接终止进程树，不是优雅停止）。等进程确认结束后才报「已取消」。");
        if (!_launcher.Cancel())
        {
            AppendLine("> 停止请求未生效：进程可能已经自行结束。");
        }
    }

    /// <summary>清空共享输出（页面必须先二次确认——这里是唯一的失败证据存放处）。</summary>
    public void ClearOutput()
    {
        AppServices.EnqueueUi(() =>
        {
            _buffer.Clear();
            _pendingLines = 0;
            OutputText = string.Empty;
        });
    }

    /// <summary>当前缓冲的原文（本机控制台用；离开本机前先走 <see cref="RedactedLogText"/>）。</summary>
    public string LogText => _buffer.ToString();

    /// <summary>脱敏后的日志：复制 / 另存走这一份（手册 §5.4）。读缓冲本体，不能漏掉还没发布的一批。</summary>
    public string RedactedLogText => DiagnosticText.Redact(LogText);

    /// <summary>
    /// 结论措辞。硬规则：非 0 退出码不得写成「完成」；没跑完就是取消/未成功，
    /// 也不虚构「部分完成」——CLI 没有给出条目级结论，界面就不编（§6）。
    /// </summary>
    private static string DescribeOutcome(string label, SekaiRunResult result) => result switch
    {
        { CancelRequested: true, ExitedConfirmed: false } =>
            $"{label}：已请求停止，但进程在 {ProcessLauncherService.CancelGraceSeconds} 秒内没有确认退出，可能仍在收尾。本程序不会重复强杀，请等它结束。",
        { CancelRequested: true, ExitedConfirmed: true, ExitCode: 0 } =>
            $"{label}：停止请求到达时进程已正常结束（退出码 0），本次未被打断。",
        { CancelRequested: true, ExitedConfirmed: true } =>
            $"{label} 已取消（进程已结束，退出码 {result.ExitCode}）。本次未完成。",
        { ExitCode: 0 } =>
            $"{label} 完成（退出码 0）。",
        _ =>
            $"{label} 未成功：退出码 {result.ExitCode}，见下方输出。",
    };

    private static TaskTone ToneOf(SekaiRunResult result) => result switch
    {
        { CancelRequested: true, ExitedConfirmed: false } => TaskTone.Working,
        { CancelRequested: true } => TaskTone.Cancelled,
        { ExitCode: 0 } => TaskTone.Success,
        _ => TaskTone.Problem,
    };

    private void OnRunningChanged(object? sender, bool running)
        => AppServices.EnqueueUi(() => IsRunning = running);

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
    }

    /// <summary>
    /// 写入缓冲并广播。子进程的输出回调可能来自非 UI 线程，这里统一排队。
    ///
    /// 每来一行就 `OutputText + 新行` 重建整个字符串，是 O(n²) 的拼接，而且每次赋值都会
    /// 让控制台全文重排一次、再走一遍 ScrollViewer 查找。`status` 这类几百行的命令
    /// 能把 UI 线程压住几十秒（表现为前端卡死、跑完才恢复）。
    /// 现在用 StringBuilder 累积，攒够一批才发布一次；任务收尾会强制发布一次，
    /// 保证最后不满一批的输出不会迟迟不上屏。
    /// </summary>
    private const int FlushLines = 32;

    private readonly StringBuilder _buffer = new();
    private int _pendingLines;

    private void AppendLine(string line)
        => AppServices.EnqueueUi(() =>
        {
            if (_buffer.Length > 0)
            {
                _buffer.Append(Environment.NewLine);
            }
            _buffer.Append(line);
            _pendingLines++;

            if (_buffer.Length > OutputSoftCap)
            {
                _buffer.Remove(0, _buffer.Length - OutputKeepChars);
            }
            if (_pendingLines >= FlushLines)
            {
                Flush();
            }
        });

    /// <summary>把尚未发布的输出刷到界面。任务结束时必须调一次。</summary>
    public void FlushOutput() => AppServices.EnqueueUi(Flush);

    private void Flush()
    {
        if (_pendingLines == 0)
        {
            return;
        }
        _pendingLines = 0;
        OutputText = _buffer.ToString();
        OutputAppended?.Invoke(this, EventArgs.Empty);
    }

    public static string QuotePath(string path)
        => string.IsNullOrWhiteSpace(path) || path.Contains(' ') ? $"\"{path}\"" : path;
}

/// <summary>一次任务的结论。</summary>
public sealed class TaskCompletion
{
    public TaskCompletion(string label, int exitCode, string statusText)
    {
        Label = label;
        ExitCode = exitCode;
        StatusText = statusText;
    }

    public string Label { get; }

    public int ExitCode { get; }

    public string StatusText { get; }
}

/// <summary>
/// 输出的复制 / 另存（两页共用一份，避免 SyncPage 与 AgentPage 各写一套）。
/// 手册 §5.4：分享与落盘的内容默认移除用户名路径与凭证形态；本机控制台仍显示原始路径。
/// </summary>
public static class LogExport
{
    /// <summary>本机原始日志（由 App 写的崩溃/诊断日志）；页面只显示路径，不代为打开。</summary>
    public static string LocalDiagnosticsPath => Path.Combine(Path.GetTempPath(), "sekaisync_desktop.log");

    public static bool HasLocalDiagnostics => File.Exists(LocalDiagnosticsPath);

    /// <summary>复制到剪贴板。调用方给了回执就用它的（配置原文需要说明来源），否则按脱敏文本回执。</summary>
    public static string CopyToClipboard(string text, string? successMessage = null)
    {
        if (text.Length == 0)
        {
            return "没有可复制的内容。";
        }
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return successMessage
                ?? "输出已复制（已移除用户名路径与凭证形态）。粘贴前请再检查一遍。";
        }
        catch (Exception ex)
        {
            return $"复制失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 另存为用户选定的文件。返回回执文案；取消选择时不写任何文件。
    /// 内容按调用方给的文本写，页面侧应传脱敏后的缓冲（手册 §5.4）。
    /// </summary>
    public static async Task<string> SaveAsync(string text, string suggestedName)
    {
        if (text.Length == 0)
        {
            return "没有可保存的输出。";
        }
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.SuggestedFileName = SanitizeFileName(suggestedName);
            // WinRT 的 FileSavePicker 用 FileTypeChoices（键是显示名，值是扩展名列表），
            // 不是 UWP 早期示例里的 FileTypeFilter。
            picker.FileTypeChoices.Add("日志文件", new List<string> { ".log" });
            picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return "未保存：已取消选择，没有写入任何文件。";
            }
            await Windows.Storage.FileIO.WriteTextAsync(file, text);
            return $"日志已保存到 {file.Path}（已脱敏）。";
        }
        catch (Exception ex)
        {
            return $"保存失败：{ex.Message}";
        }
    }

    private static string SanitizeFileName(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }
        var name = builder.ToString().Trim('-');
        return name.Length == 0 ? "sekaisync-log" : name;
    }
}
