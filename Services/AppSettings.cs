using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SekaiSync.Desktop.Services;

/// <summary>桌面端自身设置，保存在 %LOCALAPPDATA%\SekaiSyncDesktop\settings.json。</summary>
public sealed class AppSettings
{
    public string StorePath { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = "python";
    public string HttpHost { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 8787;
    public int PageSize { get; set; } = 200;

    /// <summary>资讯详情内嵌网页的默认缩放（用于没有独立记录的站点）。</summary>
    public double NewsWebZoom { get; set; } = 1.25;

    /// <summary>按站点域记忆的缩放：五服公告页的字号体系完全不同
    /// （sekai-web 固定 2.34px rem 基准 vs 字节 CDN 动态 clientWidth/29.87 rem），
    /// 单一全局系数无法同时归一，因此每个域各自校准、各自记忆。</summary>
    public Dictionary<string, double> NewsHostZooms { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>资讯页列表/预览的分栏比例（0 = 未设置，用默认 2:1）。</summary>
    public double NewsSplitRatio { get; set; }

    /// <summary>其余各页的分栏比例，按页键名存（未写入的页沿用 XAML 默认比例）。</summary>
    public Dictionary<string, double> ColumnSplitRatios { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>Auto / Light / Dark。</summary>
    public string Theme { get; set; } = "Auto";

    /// <summary>装饰性动效密度：Off / Quiet（24 枚）/ Full（42 枚）。</summary>
    public string MotionDensity { get; set; } = "Quiet";

    /// <summary>剧情页的阅读模式标签；认不出的值回落到单语。</summary>
    public string StoryReadingMode { get; set; } = "单语";

    [JsonIgnore]
    public string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SekaiSyncDesktop");

    [JsonIgnore]
    public string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            var path = new AppSettings().SettingsFilePath;
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // 配置损坏时回退默认值，不阻塞启动。
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, JsonOptions));
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置保存后触发（用于刷新服务与主题）。</summary>
    public event EventHandler? Saved;
}
