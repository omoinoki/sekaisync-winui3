using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SekaiSync.Desktop.Services;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 智能体接入页面：MCP stdio / MCP HTTP / REST 接入配置与本地服务控制。
///
/// 进程生命周期不再由本页私有（手册 §5.3 / docs/ui-architecture.md §5.2）：
/// 「服务在不在跑」= 总线在跑、而且跑的是本页发起的那条命令（A-4），
/// 免得用户在别的页跑同步时本页写「运行中」、而「停止」杀掉的是别人的进程。
/// </summary>
public partial class AgentViewModel : ObservableObject
{
    public const string OriginName = "智能体接入页";
    public const string ServiceLabel = "本地 HTTP 服务（serve-http）";

    private readonly AppEnvironment _environment = AppServices.Environment;
    private readonly AppSettings _settings = AppServices.Settings;
    private readonly TaskBus _bus = TaskBus.Shared;

    /// <summary>当前这个 serve-http 进程是不是本页拉起来的（A-4 的归属判定）。</summary>
    private bool _serviceStartedByThisPage;

    /// <summary>一次性回执的序号：迟到的清理不能抹掉更新的提示。</summary>
    private int _hintSerial;

    public static AgentViewModel Shared { get; } = new();

    /// <summary>页面直接绑它：OutputText / StatusText / IsRunning。</summary>
    public TaskBus Bus => _bus;

    [ObservableProperty]
    private string _stdioConfigJson = string.Empty;

    [ObservableProperty]
    private string _httpBaseUrl = string.Empty;

    /// <summary>健康检查第一行：只说服务可达性，不替数据下结论（A-1 / §5.4）。</summary>
    [ObservableProperty]
    private string _serviceText = "尚未检测。";

    /// <summary>健康检查第二行：数据状态单独列，缺失时写未测量。</summary>
    [ObservableProperty]
    private string _dataText = "未测量";

    [ObservableProperty]
    private string _healthDetailText = string.Empty;

    [ObservableProperty]
    private bool _hasHealthDetail;

    [ObservableProperty]
    private bool _isCheckingHealth;

    /// <summary>一次性回执（复制 / 保存结果），会自动退回空白。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    public AgentViewModel()
    {
        RebuildSnippets();
        _bus.PropertyChanged += OnBusPropertyChanged;
        AppServices.Settings.Saved += (_, _) => AppServices.EnqueueUi(() =>
        {
            RebuildSnippets();
            NotifyDerived();
        });
    }

    private void OnBusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskBus.IsRunning)
            or nameof(TaskBus.CurrentLabel)
            or nameof(TaskBus.CurrentOrigin))
        {
            AppServices.EnqueueUi(NotifyDerived);
        }
    }

    // ── 配置片段（A-5：所有字符串走 JSON 编码器，不再手拼转义） ─────────────

    public void RebuildSnippets()
    {
        var payload = new
        {
            mcpServers = new
            {
                sekaisync = new
                {
                    command = _settings.PythonExecutable,
                    args = new[] { "-m", "sekaisync", "--store", _environment.StorePath, "serve-mcp" },
                },
            },
        };
        StdioConfigJson = JsonSerializer.Serialize(payload, SnippetOptions);
        HttpBaseUrl = $"http://{_settings.HttpHost}:{_settings.HttpPort}";
        OnPropertyChanged(nameof(IsLoopbackHost));
        OnPropertyChanged(nameof(CanCopyBaseUrl));
        OnPropertyChanged(nameof(ShowNonLoopbackWarning));
        OnPropertyChanged(nameof(HostNoticeTitle));
        OnPropertyChanged(nameof(HostNoticeMessage));
        OnPropertyChanged(nameof(NoAuthNotice));
        OnPropertyChanged(nameof(EndpointsRow));
        OnPropertyChanged(nameof(StdioCommandEcho));
    }

    private static readonly JsonSerializerOptions SnippetOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>stdio 里实际会跑的那条命令（预览用，与上面 JSON 同源）。</summary>
    public string StdioCommandEcho =>
        $"{_settings.PythonExecutable} -m sekaisync --store \"{_environment.StorePath}\" serve-mcp";

    // ── 无认证 / 主机边界（A-6） ──────────────────────────────────────────

    public bool IsLoopbackHost => _settings.HttpHost.Trim().ToLowerInvariant() is
        "127.0.0.1" or "localhost" or "::1" or "";

    public bool ShowNonLoopbackWarning => !IsLoopbackHost;

    /// <summary>非 loopback 时不允许把基址复制出去（等于把无认证端点递给别人）。</summary>
    public bool CanCopyBaseUrl => IsLoopbackHost;

    public string HostNoticeTitle => ShowNonLoopbackWarning
        ? $"HttpHost 当前不是 loopback（{_settings.HttpHost}）"
        : "本服务没有访问认证";

    public string HostNoticeMessage => ShowNonLoopbackWarning
        ? "当前地址不是本机回环地址，服务无认证且含联网写库接口。复制地址已禁用；请将 HttpHost 恢复为 127.0.0.1。"
        : NoAuthNotice;

    public string NoAuthNotice =>
        "默认 127.0.0.1 · 无认证，本机进程可访问。";

    public string EndpointsRow =>
        "只读端点：/health · /api/v1/query · /api/v1/lookup · /api/v1/web_lookup · /api/v1/term_lookup · /openapi.json。" +
        "另有 POST /api/v1/refresh 与 GET /api/v1/events/check：它们会联网取源站数据并写本地库，不属于只读面。";

    // ── 协议选择指引（A-9） ───────────────────────────────────────────────

    public string ProtocolGuideRow1 =>
        "选哪个协议只看一件事：目标客户端能不能在本机启动进程。";

    public string ProtocolGuideRow2 =>
        "stdio：把 JSON 合并到客户端的 MCP 配置，由客户端启动 sekaisync；无需 HTTP 端口。复制不会修改客户端文件。";

    public string ProtocolGuideRow3 =>
        "HTTP / REST：启动本地服务后连接上方基址，使用明文 HTTP、无认证。跨机器或 HTTPS 需要另行配置安全部署。";

    public string StartupEventCheckNotice =>
        "启动行为：serve-http 与 serve-mcp 未带 --no-event-check；接受请求前会检测新事件，可能联网拉取基础数据并写入本地库。";

    // ── 服务状态（A-3 / A-4 / A-10） ──────────────────────────────────────

    public bool ServiceRunning => _serviceStartedByThisPage && _bus.IsRunning;

    /// <summary>子进程槽位是否被占用（本页的任务或别页的任务都算）。</summary>
    public bool IsBusBusy => _bus.IsRunning;

    /// <summary>
    /// 启停合一按钮是否可点：本页服务在跑就一定能停；槽位空着就一定能起；
    /// 被别的页占着时两头都不给点（要停得到那一页停）。
    /// </summary>
    public bool CanToggleService => ServiceRunning || !_bus.IsRunning;

    public string ServiceToggleLabel => ServiceRunning ? "停止服务" : "启动服务";

    public string ServiceToggleName => ServiceRunning ? "停止本页启动的 HTTP 服务" : "启动本地 HTTP 服务";

    public string ServiceToggleHelpText => ServiceRunning
        ? "再点一次即停止：只结束本页启动的服务进程，别的页发起的任务要到那一页停。"
        : CanToggleService
            ? "启动 python -m sekaisync serve-http，占住应用唯一的子进程槽位；运行中再点一次即停止。"
            : "暂不可用：已有任务在跑。同一时间只能运行一个 Python 子进程。";

    /// <summary>健康检查不与子进程互斥：服务在跑时恰恰最需要检查（A-3）。</summary>
    public bool CanCheckHealth => !IsCheckingHealth;

    public string ServiceSlotText
    {
        get
        {
            if (!_bus.IsRunning)
            {
                return string.Empty;
            }
            return ServiceRunning
                ? "服务进程运行中。"
                : $"{_bus.CurrentOrigin}正在运行{_bus.CurrentLabel}，结束后可启动服务。";
        }
    }

    partial void OnIsCheckingHealthChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckHealth));
    }

    /// <summary>页面进入时重算一次：设置可能在别处改过，进程归属也可能已变。</summary>
    public void RefreshState()
    {
        RebuildSnippets();
        NotifyDerived();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(ServiceRunning));
        OnPropertyChanged(nameof(IsBusBusy));
        OnPropertyChanged(nameof(CanToggleService));
        OnPropertyChanged(nameof(ServiceToggleLabel));
        OnPropertyChanged(nameof(ServiceToggleName));
        OnPropertyChanged(nameof(ServiceToggleHelpText));
        OnPropertyChanged(nameof(ServiceSlotText));
        OnPropertyChanged(nameof(IsLoopbackHost));
        OnPropertyChanged(nameof(CanCopyBaseUrl));
        OnPropertyChanged(nameof(ShowNonLoopbackWarning));
        OnPropertyChanged(nameof(HostNoticeTitle));
        OnPropertyChanged(nameof(HostNoticeMessage));
        OnPropertyChanged(nameof(NoAuthNotice));
        OnPropertyChanged(nameof(EndpointsRow));
        OnPropertyChanged(nameof(StdioCommandEcho));
    }

    // ── 命令 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动与停止合成一个命令：本页服务在跑时这个按钮就是「停止服务」，否则是「启动服务」。
    /// 归属判断只有一处（<see cref="ServiceRunning"/>），不再出现「停止」误杀别人进程的路径。
    /// </summary>
    [RelayCommand]
    private Task ToggleServiceAsync()
    {
        if (ServiceRunning)
        {
            _bus.Cancel();
            return Task.CompletedTask;
        }
        return StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        if (_bus.IsRunning)
        {
            _bus.Note($"> [未执行] 启动本地服务：{_bus.CurrentLabel}（{_bus.CurrentOrigin}）正占着唯一的子进程槽位。");
            return;
        }

        _bus.Note($"> {StartupEventCheckNotice}");
        _serviceStartedByThisPage = true;
        NotifyDerived();
        try
        {
            var arguments = $"serve-http --host {_settings.HttpHost} --port {_settings.HttpPort}";
            await _bus.RunAsync(ServiceLabel, arguments, OriginName);
        }
        finally
        {
            _serviceStartedByThisPage = false;
            DataText = "未测量：本页服务已停止。";
            NotifyDerived();
        }
    }

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        if (IsCheckingHealth)
        {
            return;
        }
        IsCheckingHealth = true;
        ServiceText = "正在检测…";
        DataText = "未测量：检测还没返回。";
        HealthDetailText = string.Empty;
        HasHealthDetail = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync($"{HttpBaseUrl}/health");
            var body = await response.Content.ReadAsStringAsync();
            var status = (int)response.StatusCode;

            // 200 只等于「服务可达」；端点自己报的 ready 才是数据状态。两行分开列（§5.4）。
            ServiceText = response.IsSuccessStatusCode
                ? $"可达（HTTP {status}）"
                : $"未就绪（HTTP {status}），见接入详情。";

            DataText = TryReadReady(body) switch
            {
                true => "端点自报 ready: true，可供查询。",
                false => "端点自报 ready: false，暂无可查询内容。",
                _ => "未测量：响应未提供有效 ready 字段。",
            };

            var detail = body.Trim();
            if (detail.Length > 400)
            {
                detail = detail[..400] + "…";
            }
            HealthDetailText = detail;
            HasHealthDetail = detail.Length > 0;
        }
        catch (Exception ex)
        {
            ServiceText = $"无法连接：{DiagnosticText.Redact(ex.Message)}";
            DataText = "未测量：服务不可达时无法判断数据状态。";
            HealthDetailText = "提示：服务需要先在本页启动；端口被设置里的 HTTP 服务端口决定。";
            HasHealthDetail = true;
        }
        finally
        {
            IsCheckingHealth = false;
        }
    }

    /// <summary>只在字段确实是布尔值时下结论；解析不了就返回 null（未测量）。</summary>
    private static bool? TryReadReady(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("ready", out var ready))
            {
                return null;
            }
            return ready.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 复制的是预览里那份配置原文：store 与解释器路径必须是本机原值，否则粘过去跑不起来
    /// （§5.4 要求隐去的是凭证，这份配置里没有凭证）。回执带下一步，不写「接入成功」。
    /// </summary>
    [RelayCommand]
    private void CopyStdioConfig() => ShowHint(LogExport.CopyToClipboard(
        StdioConfigJson,
        "配置已复制。请粘贴到本机客户端并检查连接。"));

    [RelayCommand]
    private void CopyHttpBase()
    {
        if (!CanCopyBaseUrl)
        {
            ShowHint("未复制：当前 HttpHost 不是本机地址。把无认证端点复制、粘贴给别处是独立的高影响操作，本页不代你确认。");
            return;
        }
        ShowHint(LogExport.CopyToClipboard(
            HttpBaseUrl,
            "服务地址已复制。请在本机客户端检查连接。"));
    }

    /// <summary>一次性回执：10 秒后退回空白，不永久霸占状态行（A-2 / T-7）。</summary>
    private void ShowHint(string text)
    {
        StatusText = text;
        var serial = ++_hintSerial;
        _ = ClearHintAsync(serial);
    }

    private async Task ClearHintAsync(int serial)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (serial == _hintSerial)
        {
            StatusText = string.Empty;
        }
    }
}
