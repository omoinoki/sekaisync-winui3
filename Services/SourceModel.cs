using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 抓取实例（资料站）。实例的两字母缩写、`altsource_xx` 与上游 repo 是本地口径，写死在这里；
/// 实例显示名与主站域名则跟随主项目 settings.json 的 sites[]（见 <see cref="ApplySiteProfile"/>），
/// 换域名或改名时界面自动跟上，不必改代码。
/// </summary>
public enum InstanceId
{
    SekaiViewer,
    Moesekai,
}

/// <summary>数据层。Primary 是原始抓取内容；Overlay 是译文覆盖层（web_pages.overlay = 1）。</summary>
public enum SourceLayer
{
    Primary,
    Overlay,
}

/// <summary>实例筛选档位。</summary>
public enum InstanceFilter
{
    /// <summary>两个实例合并，同一对齐键冲突时取 Sekai Viewer。</summary>
    Merged,

    SekaiViewerOnly,

    MoesekaiOnly,
}

/// <summary>一个实例的静态描述。</summary>
public sealed class InstanceInfo
{
    public InstanceInfo(
        InstanceId id,
        string displayName,
        string shortName,
        string upstream,
        string site,
        string primarySource,
        string overlaySource)
    {
        Id = id;
        DisplayName = displayName;
        ShortName = shortName;
        Upstream = upstream;
        Site = site;
        PrimarySource = primarySource;
        OverlaySource = overlaySource;
    }

    public InstanceId Id { get; }

    /// <summary>实例显示名。初值为内置口径，启动时由 settings.json 的 sites[].name 覆盖。</summary>
    public string DisplayName { get; internal set; }

    /// <summary>徽章文字「SV」/「MS」。</summary>
    public string ShortName { get; }

    /// <summary>上游仓库，如 Sekai-World/sekai-viewer。</summary>
    public string Upstream { get; }

    /// <summary>主站域名。初值为内置口径，启动时由该实例配置的主站址覆盖。</summary>
    public string Site { get; internal set; }

    /// <summary>主数据 source 值。</summary>
    public string PrimarySource { get; }

    /// <summary>译文覆盖层 source 值。</summary>
    public string OverlaySource { get; }

    public string Label => $"{DisplayName}（{ShortName}）";
}

/// <summary>
/// 一个服的描述。两站把地域写进 web_pages.id 的第 3 段，写法不同，跨实例对齐前必须归一化。
/// language 列本身倒是统一的，是唯一能直接跨实例比较的维度。
/// </summary>
public sealed class RegionInfo
{
    public RegionInfo(string region, string language, string displayName, string msSegment, string svSegment)
    {
        Region = region;
        Language = language;
        DisplayName = displayName;
        MsSegment = msSegment;
        SvSegment = svSegment;
    }

    /// <summary>归一化地域码：jp / en / cn / tc / kr。</summary>
    public string Region { get; }

    /// <summary>语言码：ja / en / zh_hans / zh_hant / ko。</summary>
    public string Language { get; }

    /// <summary>「日服」这类玩家叫法。</summary>
    public string DisplayName { get; }

    /// <summary>Moesekai 在 id 里写的地域段（ja-jp）。</summary>
    public string MsSegment { get; }

    /// <summary>Sekai Viewer 在 id 里写的地域段（jp）。</summary>
    public string SvSegment { get; }

    /// <summary>选择器里双写标签，兼顾玩家心智与语言码。</summary>
    public string Label => $"{DisplayName} {Language}";
}

/// <summary>一个 kind（正文类型）的展示名与归属分组。</summary>
public sealed class KindInfo
{
    public KindInfo(string kind, string displayName, string group)
    {
        Kind = kind;
        DisplayName = displayName;
        Group = group;
    }

    public string Kind { get; }

    public string DisplayName { get; }

    /// <summary>story = 有标题的叙事单元；voice = 台词碎片。</summary>
    public string Group { get; }
}

/// <summary>版本（服）筛选选项。Region 为 null 表示「全部」。</summary>
public sealed class VersionOption
{
    public VersionOption(string label, RegionInfo? region)
    {
        Label = label;
        Region = region;
    }

    public string Label { get; }

    public RegionInfo? Region { get; }

    public override string ToString() => Label;
}

/// <summary>实例筛选选项。</summary>
public sealed class InstanceOption
{
    public InstanceOption(string label, InstanceFilter filter)
    {
        Label = label;
        Filter = filter;
    }

    public string Label { get; }

    public InstanceFilter Filter { get; }

    public override string ToString() => Label;
}

/// <summary>
/// 实例 / 层 / 五服的统一抽象。所有跨实例的映射都收在这里，别在页面里散着写。
/// </summary>
public static class SourceModel
{
    public const string SekaiViewerPrimary = "altsource_sv";
    public const string SekaiViewerOverlay = "altsource_sv_i18n";
    public const string MoesekaiPrimary = "altsource_ms";
    public const string MoesekaiOverlay = "altsource_ms_translation";

    public static IReadOnlyList<InstanceInfo> Instances { get; } =
    [
        new InstanceInfo(
            InstanceId.SekaiViewer,
            "Sekai Viewer",
            "SV",
            "Sekai-World/sekai-viewer",
            "sekai.best",
            SekaiViewerPrimary,
            SekaiViewerOverlay),
        new InstanceInfo(
            InstanceId.Moesekai,
            "Moesekai",
            "MS",
            "StarMoe-org/Moesekai",
            "pjsk.moe",
            MoesekaiPrimary,
            MoesekaiOverlay),
    ];

    /// <summary>
    /// 用主项目 settings.json 的 sites[] 覆盖实例显示名与主站域名。
    ///
    /// 只覆盖这两项：缩写、`altsource_xx`、上游 repo 属于本地口径，保持写死。
    /// 主站址统一取 sites[].site_base——两站都以此字段声明对外主站
    /// （sekai_viewer 另有 master_base，那是抓取用的 master 数据端点，不是站点地址）；
    /// 读不到文件、没有同 id 条目或 URL 不合法时保留内置初值，界面不会因此变空。
    ///
    /// 配置文件优先取仓库根；仓库探测不到时（桌面端跑在独立目录、store 靠显式路径指过去）
    /// 退回 store 的上级目录——store 约定就放在仓库根下，两者是同一个 settings.json。
    /// </summary>
    public static void ApplySiteProfile(AppEnvironment environment)
    {
        var candidates = new List<string>();
        if (environment.RepoRoot.Length > 0) candidates.Add(Path.Combine(environment.RepoRoot, "settings.json"));
        if (environment.StorePath.Length > 0)
        {
            var parent = Path.GetDirectoryName(environment.StorePath.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent)) candidates.Add(Path.Combine(parent, "settings.json"));
        }

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("sites", out var sites) ||
                sites.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var site in sites.EnumerateArray())
            {
                if (site.ValueKind != JsonValueKind.Object) continue;
                var sourceId = ReadString(site, "id");
                var instance = Instances.FirstOrDefault(i => i.PrimarySource == sourceId);
                if (instance is null) continue;

                var name = ReadString(site, "name");
                if (name.Length > 0) instance.DisplayName = name;

                var baseurl = ReadString(site, "site_base");
                if (Uri.TryCreate(baseurl, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                {
                    instance.Site = uri.Host;
                }
            }

            App.Log($"SourceModel: 实例名与主站址取自 {path}");
        }
        catch (Exception ex)
        {
            App.Log($"SourceModel: 读取 sites 配置失败，沿用内置实例名与域名：{ex.Message}");
        }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;

    /// <summary>五服。顺序即界面呈现顺序。</summary>
    public static IReadOnlyList<RegionInfo> Regions { get; } =
    [
        new RegionInfo("jp", "ja", "日服", "ja-jp", "jp"),
        new RegionInfo("en", "en", "英文服", "en-us", "en"),
        new RegionInfo("cn", "zh_hans", "简中服", "zh-cn", "cn"),
        new RegionInfo("tc", "zh_hant", "繁中服", "zh-tw", "tc"),
        new RegionInfo("kr", "ko", "韩服", "ko-kr", "kr"),
    ];

    /// <summary>有标题的叙事单元。归到剧情页。</summary>
    public static IReadOnlyList<string> StoryKinds { get; } =
        ["event_story", "card_story", "unit_story", "special_story", "virtual_live"];

    /// <summary>碎片化台词。归到台词页。</summary>
    public static IReadOnlyList<string> VoiceKinds { get; } =
        ["home_line", "mysekai_tweet", "area_talk", "mysekai_talk", "self_intro"];

    /// <summary>webindex.py 的 CANONICAL_KINDS 白名单，共 10 类。</summary>
    public static IReadOnlyList<string> CanonicalKinds { get; } =
        [.. StoryKinds, .. VoiceKinds];

    /// <summary>只有 Sekai Viewer 抽出了可读文本的两类。</summary>
    public static IReadOnlyList<string> SekaiViewerOnlyKinds { get; } =
        ["mysekai_talk", "mysekai_tweet"];

    private static readonly Dictionary<string, KindInfo> KindMap = new(StringComparer.Ordinal)
    {
        ["event_story"] = new("event_story", "活动剧情", "story"),
        ["card_story"] = new("card_story", "卡牌剧情", "story"),
        ["unit_story"] = new("unit_story", "组合剧情", "story"),
        ["special_story"] = new("special_story", "特别剧情", "story"),
        ["virtual_live"] = new("virtual_live", "虚拟 Live", "story"),
        ["home_line"] = new("home_line", "主页语音", "voice"),
        ["mysekai_tweet"] = new("mysekai_tweet", "我的世界推文", "voice"),
        ["area_talk"] = new("area_talk", "区域对话", "voice"),
        ["mysekai_talk"] = new("mysekai_talk", "我的世界对话", "voice"),
        ["self_intro"] = new("self_intro", "角色自我介绍", "voice"),
    };

    /// <summary>source 字符串 → (实例, 层)。未收录的返回 null。</summary>
    public static (InstanceId Instance, SourceLayer Layer)? Resolve(string source) => source switch
    {
        SekaiViewerPrimary => (InstanceId.SekaiViewer, SourceLayer.Primary),
        SekaiViewerOverlay => (InstanceId.SekaiViewer, SourceLayer.Overlay),
        MoesekaiPrimary => (InstanceId.Moesekai, SourceLayer.Primary),
        MoesekaiOverlay => (InstanceId.Moesekai, SourceLayer.Overlay),
        _ => null,
    };

    public static InstanceInfo GetInstance(InstanceId id) =>
        Instances.First(i => i.Id == id);

    /// <summary>source 属于哪个实例；未收录返回 null。</summary>
    public static InstanceId? InstanceOf(string source) => Resolve(source)?.Instance;

    public static string ShortBadge(string source)
    {
        var resolved = Resolve(source);
        return resolved is null ? source : GetInstance(resolved.Value.Instance).ShortName;
    }

    /// <summary>source 的层；未收录按 Primary 处理。</summary>
    public static SourceLayer LayerOf(string source) => Resolve(source)?.Layer ?? SourceLayer.Primary;

    /// <summary>实例筛选 → 该档位包含的主数据 source 列表（覆盖层单独处理，不在这里）。</summary>
    public static IReadOnlyList<string> PrimarySourcesFor(InstanceFilter filter) => filter switch
    {
        InstanceFilter.SekaiViewerOnly => [SekaiViewerPrimary],
        InstanceFilter.MoesekaiOnly => [MoesekaiPrimary],
        _ => [SekaiViewerPrimary, MoesekaiPrimary],
    };

    /// <summary>合并视图下的优先级（越小越优先）。Sekai Viewer 更新、覆盖更全。</summary>
    public static int MergePriority(string source) => InstanceOf(source) switch
    {
        InstanceId.SekaiViewer => 0,
        InstanceId.Moesekai => 1,
        _ => 2,
    };

    /// <summary>语言码 → 服描述。</summary>
    public static RegionInfo? RegionByLanguage(string language)
    {
        foreach (var region in Regions)
        {
            if (string.Equals(region.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                return region;
            }
        }
        return null;
    }

    /// <summary>地域码 → 服描述。</summary>
    public static RegionInfo? RegionByRegion(string region)
    {
        foreach (var info in Regions)
        {
            if (string.Equals(info.Region, region, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }
        return null;
    }

    /// <summary>
    /// 把 id 里的地域段归一化成服。两站的写法（ja-jp / jp）都认。
    /// </summary>
    public static RegionInfo? RegionBySegment(string segment)
    {
        foreach (var info in Regions)
        {
            if (string.Equals(info.MsSegment, segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info.SvSegment, segment, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }
        return null;
    }

    /// <summary>
    /// 跨实例对齐键：去掉 web:{source}:{地域}: 前缀，余下即 {kind}:{asset}。
    /// 例 web:altsource_ms:zh-cn:event_story:100:1 → event_story:100:1
    /// 注意覆盖层行的地域段与 language 不是一回事，别拿它当语言用。
    /// </summary>
    public static string AlignmentKey(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        var parts = id.Split(':');
        // web : source : region : kind : ...
        return parts.Length > 3 ? string.Join(':', parts.Skip(3)) : id;
    }

    public static KindInfo? Kind(string kind) =>
        KindMap.TryGetValue(kind, out var info) ? info : null;

    public static string KindDisplay(string kind) =>
        KindMap.TryGetValue(kind, out var info) ? info.DisplayName : kind;

    /// <summary>按分组取 kind 列表。</summary>
    public static IReadOnlyList<string> KindsInGroup(string group) => group switch
    {
        "story" => StoryKinds,
        "voice" => VoiceKinds,
        _ => [],
    };

    /// <summary>「全部」+ 五服的版本选项。</summary>
    public static IReadOnlyList<VersionOption> VersionOptions { get; } =
    [
        new VersionOption("全部版本", null),
        .. Regions.Select(r => new VersionOption(r.Label, r)),
    ];

    public static IReadOnlyList<InstanceOption> InstanceOptions { get; } =
    [
        new InstanceOption("合并视图（冲突优先 Sekai Viewer）", InstanceFilter.Merged),
        new InstanceOption("仅 Sekai Viewer", InstanceFilter.SekaiViewerOnly),
        new InstanceOption("仅 Moesekai", InstanceFilter.MoesekaiOnly),
    ];
}
