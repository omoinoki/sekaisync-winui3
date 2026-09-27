using System;
using System.IO;

namespace SekaiSync.Desktop.Services;

/// <summary><see cref="AppEnvironment.ProbeCrawlLock"/> 的四态结果。</summary>
public enum CrawlLockState
{
    /// <summary>没有锁文件：从未跑过爬虫。</summary>
    Absent,

    /// <summary>锁文件在，但没有进程持有：可以正常操作。</summary>
    Free,

    /// <summary>锁文件被某进程持有：另一个会话很可能正在写。</summary>
    Held,

    /// <summary>无法判断（权限或未知错误）：界面必须说「无法判断」，不得当成占用或空闲。</summary>
    Indeterminate,
}

/// <summary>
/// 解析仓库根目录 / store 目录 / 数据库文件 / Python 解释器等本机环境路径。
/// store 默认从可执行文件位置向上自动探测，可在设置中显式覆盖。
/// </summary>
public sealed class AppEnvironment
{
    private readonly AppSettings _settings;

    public AppEnvironment(AppSettings settings)
    {
        _settings = settings;
        RepoRoot = DetectRepoRoot() ?? RepoRootFromConfiguredStore() ?? string.Empty;
    }

    /// <summary>包含 sekaisync Python 包的仓库根目录（无法确定时为空字符串）。</summary>
    public string RepoRoot { get; }

    /// <summary>设置中显式指定的 store 覆盖路径（空 = 自动）。</summary>
    public string? ConfiguredStorePath => string.IsNullOrWhiteSpace(_settings.StorePath) ? null : _settings.StorePath;

    /// <summary>
    /// 桌面端可以装在仓库之外、只靠设置里显式的 store 路径指过去。这时按可执行文件
    /// 向上探测必然落空，`python -m sekaisync` 的工作目录里没有那个包，于是报
    /// 「No module named sekaisync」——看起来就像「必须先把 sekaisync 装进 PATH」。
    /// store 约定放在仓库根下，所以从 store 的父目录反推出同一个根。
    /// </summary>
    private string? RepoRootFromConfiguredStore()
    {
        var store = ConfiguredStorePath;
        if (store is null) return null;

        var parent = Path.GetDirectoryName(store.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parent)) return null;

        return File.Exists(Path.Combine(parent, "sekaisync", "cli.py")) ? parent : null;
    }

    public string StorePath => ConfiguredStorePath ?? (RepoRoot.Length > 0 ? Path.Combine(RepoRoot, "store") : string.Empty);

    public string DatabasePath => string.IsNullOrEmpty(StorePath) ? string.Empty : Path.Combine(StorePath, "kb", "sekaisync.db");

    public bool DatabaseExists => DatabasePath.Length > 0 && File.Exists(DatabasePath);

    /// <summary>爬虫单实例锁文件路径（store 未确定时为空串）。</summary>
    public string CrawlLockPath => string.IsNullOrEmpty(StorePath) ? string.Empty : Path.Combine(StorePath, "crawl.lock");

    /// <summary>
    /// 锁文件是否存在。**界面不要用这个判断「有没有另一个会话在写」**——上游释放 OS 锁时
    /// 不删文件，跑过一次之后就永远为 true。要判活会话请用 <see cref="ProbeCrawlLock"/>。
    /// 这里保留给冒烟工具打印原始事实。
    /// </summary>
    public bool CrawlLockExists => CrawlLockPath.Length > 0 && File.Exists(CrawlLockPath);

    /// <summary>
    /// 锁的真实状态。上游用 `msvcrt.locking` 加 OS 区域锁，释放时只解锁不删文件
    /// （`crawler.py` 的 `release_crawl_lock` 无 unlink），所以**文件存在 ≠ 有会话在写**。
    /// 之前界面把「文件存在」直接说成「另一个会话可能正在写入」，是常驻误报。
    /// 探测方式：以 `FileShare.None` 尝试打开——爬虫持锁期间句柄是开着的，独占请求会失败。
    /// 探不出来时如实返回 Indeterminate，不猜。
    /// </summary>
    public CrawlLockState ProbeCrawlLock()
    {
        if (CrawlLockPath.Length == 0 || !File.Exists(CrawlLockPath))
        {
            return CrawlLockState.Absent;
        }

        try
        {
            using var probe = new FileStream(CrawlLockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return CrawlLockState.Free;
        }
        catch (IOException)
        {
            // 独占失败：有进程正持有该文件（爬虫在跑）。
            return CrawlLockState.Held;
        }
        catch (UnauthorizedAccessException)
        {
            return CrawlLockState.Held;
        }
        catch (Exception)
        {
            return CrawlLockState.Indeterminate;
        }
    }

    public string PythonExecutable => string.IsNullOrWhiteSpace(_settings.PythonExecutable) ? "python" : _settings.PythonExecutable;

    public string WorkingDirectory => RepoRoot.Length > 0 ? RepoRoot : Environment.CurrentDirectory;

    /// <summary>
    /// 产品版本（csproj 的 &lt;Version&gt;）。界面各处显示版本号一律取这里，
    /// 不再写字面量——上一轮就同时存在「0.4.0-alpha」硬编码与运行时取值两份互相打架的文案。
    /// 取不到时如实返回「未测量」，不猜。
    /// </summary>
    public static string ProductVersion { get; } = ResolveProductVersion();

    /// <summary>
    /// 必须读 InformationalVersion 而不是 GetName().Version：后者是 System.Version，
    /// 结构上装不下「-alpha」这类预发布后缀，csproj 写了 0.4.0-alpha 它也只会报 0.4.0。
    /// .NET 8 的 SDK 还会在该值后面追加 +&lt;git sha&gt;，所以取 + 之前的部分。
    /// </summary>
    private static string ResolveProductVersion()
    {
        try
        {
            var assembly = typeof(AppEnvironment).Assembly;
            var informational = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var version = assembly.GetName().Version;
            return version is null ? "未测量" : version.ToString(3);
        }
        catch (Exception)
        {
            return "未测量";
        }
    }

    /// <summary>从可执行文件所在目录向上寻找仓库根（以 sekaisync/cli.py 或 store/kb/sekaisync.db 为标志）。</summary>
    public static string? DetectRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "sekaisync", "cli.py")) ||
                    File.Exists(Path.Combine(dir.FullName, "store", "kb", "sekaisync.db")))
                {
                    return dir.FullName;
                }
            }
        }
        catch (Exception)
        {
            // 探测失败按未找到处理。
        }
        return null;
    }
}
