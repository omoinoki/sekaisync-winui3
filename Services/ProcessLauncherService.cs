using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 以子进程方式运行 python -m sekaisync &lt;命令&gt;，逐行回调输出。
/// 同一时间只允许一个子进程（同步 / MCP 服务共用，避免并发写 store）。
///
/// 取消语义（手册 §5.3「取消时先正在请求停止，确认结束后才已取消」）：
/// <see cref="Cancel"/> 触发的是**停止请求**，本类会等到进程真的结束（有宽限上限）
/// 才把 <see cref="SekaiRunResult.CancelRequested"/> / <see cref="SekaiRunResult.ExitedConfirmed"/>
/// 交回调用方。旧实现由 <c>Cancel()</c> 直接 <c>Kill</c>，而 <c>WaitForExitAsync</c> 正常返回退出码、
/// 不抛 <c>OperationCanceledException</c>，于是调用方那条 catch 分支是死代码，取消被写成「完成」。
/// </summary>
public sealed class ProcessLauncherService
{
    /// <summary>请求停止后等待进程退出的宽限时间；超时则如实报告「尚未确认结束」。</summary>
    public const int CancelGraceSeconds = 8;

    private readonly AppEnvironment _environment;
    private Process? _current;
    private CancellationTokenSource? _currentCts;
    private volatile bool _cancelRequestedForCurrent;

    public ProcessLauncherService(AppEnvironment environment) => _environment = environment;

    public bool IsRunning => _current is { HasExited: false };

    /// <summary>当前这次运行是否已被请求停止（「已请求」不等于「已取消」）。</summary>
    public bool CancelRequestedForCurrentRun => _cancelRequestedForCurrent;

    public event EventHandler<bool>? RunningChanged;

    /// <summary>运行 sekaisync 子命令并流式回调输出行，返回退出码（保留给不关心取消原因的调用方）。</summary>
    public async Task<int> RunSekaiAsync(string arguments, Action<string> onOutput, CancellationToken cancellationToken)
    {
        var result = await RunWithResultAsync(arguments, onOutput, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>
    /// 运行 sekaisync 子命令，返回退出码 + 取消真相。调用方据此写「完成 / 未成功 / 已取消」，
    /// 不得把非 0 退出码写成完成（手册 §6「未识别状态不写成功」）。
    /// </summary>
    public async Task<SekaiRunResult> RunWithResultAsync(
        string arguments,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("已有命令在运行，请先等待完成或点击取消。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _environment.PythonExecutable,
            Arguments = $"-m sekaisync {arguments}",
            WorkingDirectory = _environment.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
        startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Emit(onOutput, e.Data);
        process.ErrorDataReceived += (_, e) => Emit(onOutput, e.Data);

        // 内部 CTS：Cancel() 走 token，registration 分支才真的生效（旧代码传 CancellationToken.None 时形同虚设）。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenRegistration registration = default;
        _cancelRequestedForCurrent = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 Python 进程，请检查“设置”中的解释器路径。");
            }

            // _current 只能在 Start 成功之后赋值：Process.HasExited 在进程尚未启动时会抛
            // InvalidOperationException，而 IsRunning / Cancel() 都会读它——早赋值的窗口里
            // 用户点取消或界面读状态就会炸出一个看不懂的异常。
            _current = process;
            _currentCts = cts;
            RunningChanged?.Invoke(this, true);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            registration = cts.Token.Register(() =>
            {
                _cancelRequestedForCurrent = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 进程可能已自行退出。
                }
            });

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return new SekaiRunResult(SafeExitCode(process), false, true);
            }
            catch (OperationCanceledException)
            {
                // 停止请求已发出：只有确认进程结束，调用方才允许写「已取消」。
                var exited = await WaitForExitGraceAsync(process).ConfigureAwait(false);
                return new SekaiRunResult(exited ? SafeExitCode(process) : -1, true, exited);
            }
        }
        finally
        {
            registration.Dispose();
            cts.Dispose();
            _current = null;
            _currentCts = null;
            _cancelRequestedForCurrent = false;
            RunningChanged?.Invoke(this, false);
            process.Dispose();
        }
    }

    /// <summary>请求终止当前子进程（若有）。返回是否真的有进程可停。</summary>
    public bool Cancel()
    {
        var cts = _currentCts;
        if (cts is { IsCancellationRequested: false })
        {
            _cancelRequestedForCurrent = true;
            try
            {
                cts.Cancel();
                return true;
            }
            catch (Exception)
            {
                // CTS 已被释放（进程刚好结束）：落到下面的兜底判断。
            }
        }

        if (_current is { HasExited: false } process)
        {
            _cancelRequestedForCurrent = true;
            try
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception)
            {
                // 忽略终止失败，进程退出后自然结束。
            }
        }
        return false;
    }

    private static async Task<bool> WaitForExitGraceAsync(Process process)
    {
        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(CancelGraceSeconds)))
            .ConfigureAwait(false);
        if (completed != exitTask)
        {
            // 宽限超时：不要让未观察的异常再冒出来（进程随后会被 Dispose）。
            _ = exitTask.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return false;
        }
        return exitTask.IsCompletedSuccessfully;
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void Emit(Action<string> onOutput, string? line)
    {
        if (line is null)
        {
            return;
        }
        try
        {
            onOutput(line);
        }
        catch (Exception)
        {
            // 回调异常不应拖垮读取循环。
        }
    }
}

/// <summary>
/// 一次子进程运行的事实：退出码 + 是否被请求停止 + 请求后是否确认结束。
/// 界面用它区分「完成 / 未成功 / 已取消 / 已请求停止但未确认结束」四态（手册 §5.3）。
/// </summary>
public sealed class SekaiRunResult
{
    public SekaiRunResult(int exitCode, bool cancelRequested, bool exitedConfirmed)
    {
        ExitCode = exitCode;
        CancelRequested = cancelRequested;
        ExitedConfirmed = exitedConfirmed;
    }

    public int ExitCode { get; }

    /// <summary>用户点过取消（不等于取消已完成）。</summary>
    public bool CancelRequested { get; }

    /// <summary>进程确实结束了；<see cref="CancelRequested"/> 为真而本项为假时只能说「尚未确认结束」。</summary>
    public bool ExitedConfirmed { get; }
}
