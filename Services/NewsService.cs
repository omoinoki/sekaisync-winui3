using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 公告数据源的**实测**状态（审计 N-1）。
///
/// 关键是别把「读取失败」说成「已就绪」：generation 指针存在而 manifest 校验或读取失败时，
/// 读取会退回旧版单文件布局，而旧布局按 doc §4.1 的定义**必然**是过期数据。
/// 所以这里把三态分开，由界面按状态选文案与严重级别。
/// </summary>
public enum NewsSourceState
{
    /// <summary>没有 store / 没有 kb/news 目录：连读什么都谈不上，不是「库里没有公告」。</summary>
    NoStore,

    /// <summary>没有任何一次 generation 发布，只有旧版单文件布局可读（首次发布后该布局不再更新）。</summary>
    LegacyOnly,

    /// <summary>活跃 generation 指针存在、manifest 校验通过、全部文件读取成功。</summary>
    GenerationReady,

    /// <summary>generation 可用，但有文件的 sha256 与 manifest 不符或读取异常，已被跳过（数据不完整）。</summary>
    GenerationPartial,

    /// <summary>meta 里的 active_news_generation 指针形态异常（不是 uuid4hex），无法据此读取。</summary>
    PointerMalformed,

    /// <summary>指针存在，但 generation 的 manifest / 文件校验或读取失败，本次退回旧布局。</summary>
    GenerationFailed,
}

/// <summary>一次 generation 探测的结果；界面文案的唯一事实来源。</summary>
public sealed class NewsGenerationStatus
{
    public required NewsSourceState State { get; init; }

    /// <summary>活跃 generation 的 id；没有或不可信时为空。</summary>
    public string GenerationId { get; init; } = string.Empty;

    /// <summary>被跳过的文件数（GenerationPartial 时 &gt; 0）。</summary>
    public int SkippedFileCount { get; init; }

    /// <summary>本次实际读到的是不是旧版单文件布局（旧布局按定义不再更新）。</summary>
    public bool UsesLegacyLayout { get; init; }

    /// <summary>失败原因 / 补充说明，供界面展示。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>校验或指针异常：界面必须出 Warning，不能出「已就绪」。</summary>
    public bool IsIntegrityFailure =>
        State is NewsSourceState.GenerationFailed or NewsSourceState.GenerationPartial or NewsSourceState.PointerMalformed;

    /// <summary>generation id 的短形式，只用于展示。</summary>
    public string ShortId => GenerationId.Length >= 8 ? GenerationId[..8] + "…" : GenerationId;
}

/// <summary>一次探测的完整结果：状态 + 语言档位。两者必须同源，否则档位会停留在旧快照（N-3）。</summary>
public sealed class NewsSnapshot
{
    public required NewsGenerationStatus Status { get; init; }

    public required IReadOnlyList<NewsLanguageOption> Languages { get; init; }
}

/// <summary>某个语言公告的读取结果；`Error` 非空 = 读取失败，不等于「该语言没有公告」。</summary>
public sealed class NewsLoadResult
{
    public required IReadOnlyList<NewsItem> Items { get; init; }

    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 公告读取。上游 0.4.0（2026-09-17 起）把公告改为「不可变 generation 发布」：
/// <c>save_news</c> 写 <c>store/kb/news/generations/&lt;uuid&gt;/NNNN.json</c>（每文件一个语言，
/// 顶层 language + news 数组，条目字段与旧布局一致）加 <c>manifest.json</c>（逐文件 sha256），
/// 然后把 <c>active_news_generation</c> 指针写进 sekaisync.db 的 meta 表，与 revision 同事务翻转。
/// 旧的 <c>kb/news/{language}.json</c> 只在「还没有任何一次显式发布」前兼容读取——
/// 也就是说一旦 generation 出现，旧文件就不再更新，只读它必然拿到过期数据。
///
/// 因此本服务的读取顺序：meta 指针 → manifest → NNNN.json（逐文件 sha256 校验，
/// 单文件失败只跳过该文件不拖垮整页）；没有指针时退回旧 {language}.json 布局。
/// id 前缀是 news: 而不是 web:，别和 web_pages 混着用。
/// </summary>
public sealed class NewsService
{
    /// <summary>generation 目录名是 uuid4().hex；只认这个形态，别的东西一律不进路径。</summary>
    private static readonly System.Text.RegularExpressions.Regex GenerationIdPattern =
        new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex GenerationFilePattern =
        new("^[0-9]{4}\\.json$", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>界面支持的语种全集；顺序即下拉顺序。</summary>
    private static readonly (string Language, string Label)[] KnownLanguages =
    [
        ("zh_hans", "简体中文"),
        ("ja", "日本語"),
        ("zh_hant", "繁體中文"),
        ("en", "English"),
        ("ko", "한국어"),
    ];

    /// <summary>资讯分类（information_tag）的中文展示名与固定排序。</summary>
    public static string CategoryDisplay(string? tag) => tag switch
    {
        "event" => "活动",
        "gacha" => "招募",
        "music" => "乐曲",
        "campaign" => "企划",
        "update" => "版本更新",
        "information" => "公告",
        "bug" => "故障报告",
        "" or null => "未分类",
        _ => tag,
    };

    public static readonly string[] CategoryOrder =
    [
        "活动", "招募", "乐曲", "公告", "企划", "版本更新", "故障报告", "未分类",
    ];

    private readonly AppEnvironment _environment;
    private readonly Dictionary<string, List<NewsItem>> _legacyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _legacyErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>当前活跃 generation 按语言分组的条目；null = 本次没走 generation（无指针或校验失败）。</summary>
    private Dictionary<string, List<NewsItem>>? _generation;

    /// <summary>generation 是否已探测过（含「指针不存在」的否定结论），避免每次 Load 都查库。</summary>
    private bool _generationProbed;

    /// <summary>最近一次探测的状态；探测前是一个「未探测」的占位值。</summary>
    private NewsGenerationStatus _status = new()
    {
        State = NewsSourceState.NoStore,
        UsesLegacyLayout = true,
        Detail = "尚未探测。",
    };

    public NewsService(AppEnvironment environment) => _environment = environment;

    public string NewsDirectory =>
        string.IsNullOrEmpty(_environment.StorePath) ? string.Empty : Path.Combine(_environment.StorePath, "kb", "news");

    public string PathFor(string language) =>
        NewsDirectory.Length == 0 ? string.Empty : Path.Combine(NewsDirectory, language + ".json");

    /// <summary>语言档位；没有数据的标为不可用，界面给提示而不是空列表。</summary>
    public IReadOnlyList<NewsLanguageOption> Languages()
    {
        lock (_gate)
        {
            EnsureGeneration();
            return BuildLanguages(_status);
        }
    }

    /// <summary>
    /// 一次性给出「状态 + 档位」。界面必须从这里同时取两者（N-3）：
    /// 档位可用性只快照一次的话，同步完公告回到本页就会挂着过期的「暂无公告数据」。
    /// </summary>
    public NewsSnapshot Probe()
    {
        lock (_gate)
        {
            EnsureGeneration();
            return new NewsSnapshot { Status = _status, Languages = BuildLanguages(_status) };
        }
    }

    /// <summary>某个语言的公告读取结果（含读取失败原因）。generation 优先，其次旧布局。</summary>
    public NewsLoadResult LoadResult(string language)
    {
        lock (_gate)
        {
            var generation = EnsureGeneration();
            if (generation is not null)
            {
                return new NewsLoadResult
                {
                    Items = generation.TryGetValue(language, out var items) ? items : [],
                };
            }

            if (_legacyCache.TryGetValue(language, out var cached))
            {
                return new NewsLoadResult
                {
                    Items = cached,
                    Error = _legacyErrors.TryGetValue(language, out var error) ? error : string.Empty,
                };
            }

            var items2 = LoadLegacyCore(language);
            _legacyCache[language] = items2.List;
            if (items2.Error.Length > 0)
            {
                _legacyErrors[language] = items2.Error;
            }
            return new NewsLoadResult { Items = items2.List, Error = items2.Error };
        }
    }

    /// <summary>读某个语言的公告；没有数据返回空列表。要区分「读取失败」用 <see cref="LoadResult"/>。</summary>
    public IReadOnlyList<NewsItem> Load(string language) => LoadResult(language).Items;

    /// <summary>按状态与「该语言有没有条目」生成提示。一律「事实 + 影响 + 下一步」（手册 §6）。</summary>
    private IReadOnlyList<NewsLanguageOption> BuildLanguages(NewsGenerationStatus status)
    {
        var options = new List<NewsLanguageOption>();
        foreach (var (language, label) in KnownLanguages)
        {
            var exists = false;
            lock (_gate)
            {
                if (_generation is not null)
                {
                    exists = _generation.TryGetValue(language, out var items) && items.Count > 0;
                }
                else
                {
                    var path = PathFor(language);
                    exists = path.Length > 0 && File.Exists(path);
                }
            }
            options.Add(new NewsLanguageOption(language, label, exists, HintFor(status, language, label)));
        }
        return options;
    }

    /// <summary>
    /// 语言档位提示。注意两件事：
    /// 1. 没选库/没建库时不能让人去「同步」页白跑一趟（N-6）；
    /// 2. 旧布局永远不自称「已就绪」，它是按定义不再更新的兼容路径（N-1）。
    /// </summary>
    private string HintFor(NewsGenerationStatus status, string language, string label)
    {
        switch (status.State)
        {
            case NewsSourceState.NoStore:
                return "尚未选择本地数据库。请选择已有库，或查看初始化步骤。公告数据与本地库同在 store 目录下，没有库就没有可读的公告。";

            case NewsSourceState.GenerationReady:
            case NewsSourceState.GenerationPartial:
                var missing = $"本地 generation（{status.ShortId}）没有收录{label}公告；这不代表该语言没有公告。可在「同步」页运行「同步官方公告」后重试。";
                if (status.State == NewsSourceState.GenerationPartial)
                {
                    missing += $" 另外该 generation 有 {status.SkippedFileCount} 个文件未通过 manifest 校验，已跳过。";
                }
                return missing;

            case NewsSourceState.PointerMalformed:
                return $"本地库里的公告 generation 指针形态异常（{status.Detail}），本次读取的是旧版单文件公告。旧布局在第一次发布 generation 后就不再更新，下方条目可能已过期。可在「同步」页重新运行「同步官方公告」。";

            case NewsSourceState.GenerationFailed:
                return $"活跃 generation（{status.ShortId}）校验或读取失败（{status.Detail}），本次退回旧版单文件公告；旧布局不再更新，可能已过期。请先确认 store 未被改动，再在「同步」页重新发布公告。";

            default:
                // LegacyOnly：还没有任何一次显式发布，旧文件是当前唯一的来源。
                return $"本地尚未发布任何公告 generation，当前读取的是旧版单文件布局（{language}.json）；第一次发布后它就不再更新。需要新数据时在「同步」页运行「同步官方公告」。";
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _legacyCache.Clear();
            _legacyErrors.Clear();
            _generation = null;
            _generationProbed = false;
            _status = new NewsGenerationStatus
            {
                State = NewsSourceState.NoStore,
                UsesLegacyLayout = true,
                Detail = "尚未探测。",
            };
        }
    }

    /// <summary>
    /// 探测并加载活跃 generation（结果缓存，Invalidate 后重探）。
    /// 返回值只说明「有没有拿到 generation 条目」，为什么没有一律看 <c>_status</c>：
    /// 无指针 / 指针异常 / 校验失败是三件不同的事，界面文案不能合并成一句「已就绪」（N-1）。
    /// </summary>
    private Dictionary<string, List<NewsItem>>? EnsureGeneration()
    {
        if (_generationProbed)
        {
            return _generation;
        }

        _generationProbed = true;
        _generation = null;

        if (NewsDirectory.Length == 0 || !Directory.Exists(NewsDirectory))
        {
            _status = new NewsGenerationStatus
            {
                State = NewsSourceState.NoStore,
                UsesLegacyLayout = true,
                Detail = NewsDirectory.Length == 0 ? "还没有 store 目录。" : $"找不到公告目录 {NewsDirectory}。",
            };
            return null;
        }

        GenerationPointer pointer;
        try
        {
            pointer = ReadActiveGenerationPointer();
        }
        catch (Exception ex)
        {
            App.Log($"NewsService: 读取公告 generation 指针失败，退回旧布局：{ex.Message}");
            _status = new NewsGenerationStatus
            {
                State = NewsSourceState.GenerationFailed,
                UsesLegacyLayout = true,
                Detail = $"读取本地库的 active_news_generation 指针失败：{ex.Message}",
            };
            return null;
        }

        if (pointer.Malformed)
        {
            _status = new NewsGenerationStatus
            {
                State = NewsSourceState.PointerMalformed,
                UsesLegacyLayout = true,
                Detail = pointer.Reason,
            };
            return null;
        }

        if (pointer.Generation is null)
        {
            _status = new NewsGenerationStatus
            {
                State = NewsSourceState.LegacyOnly,
                UsesLegacyLayout = true,
                Detail = "本地库里没有 active_news_generation 指针。",
            };
            return null;
        }

        var generationId = pointer.Generation;
        try
        {
            var grouped = LoadGeneration(generationId, pointer.ManifestSha256, out var skipped, out var skipNote);
            _generation = grouped;
            _status = new NewsGenerationStatus
            {
                State = skipped > 0 ? NewsSourceState.GenerationPartial : NewsSourceState.GenerationReady,
                GenerationId = generationId,
                SkippedFileCount = skipped,
                UsesLegacyLayout = false,
                Detail = skipNote,
            };
            return _generation;
        }
        catch (Exception ex)
        {
            App.Log($"NewsService: 读取公告 generation 失败，退回旧布局：{ex.Message}");
            _status = new NewsGenerationStatus
            {
                State = NewsSourceState.GenerationFailed,
                GenerationId = generationId,
                UsesLegacyLayout = true,
                Detail = ex.Message,
            };
            return null;
        }
    }

    /// <summary>指针读取结果。`Generation == null` 且 `Malformed == false` = 确实还没有发布过。</summary>
    private sealed class GenerationPointer
    {
        public string? Generation { get; init; }

        public string ManifestSha256 { get; init; } = string.Empty;

        public bool Malformed { get; init; }

        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>从 sekaisync.db 的 meta 表读 active_news_generation 指针。</summary>
    private GenerationPointer ReadActiveGenerationPointer()
    {
        var databasePath = _environment.DatabasePath;
        if (string.IsNullOrEmpty(databasePath) || !File.Exists(databasePath))
        {
            return new GenerationPointer { Generation = null, Reason = "本地库文件不存在。" };
        }

        using var connection = SqliteAccess.Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'active_news_generation'";
        var raw = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new GenerationPointer { Generation = null, Reason = "meta 表里没有 active_news_generation。" };
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var generation = root.TryGetProperty("generation", out var gen) && gen.ValueKind == JsonValueKind.String
            ? gen.GetString() ?? string.Empty
            : string.Empty;
        var manifestSha256 = root.TryGetProperty("manifest_sha256", out var sha) && sha.ValueKind == JsonValueKind.String
            ? sha.GetString() ?? string.Empty
            : string.Empty;
        if (!GenerationIdPattern.IsMatch(generation))
        {
            App.Log($"NewsService: active_news_generation 指针形态异常（{generation}），忽略。");
            return new GenerationPointer
            {
                Generation = null,
                Malformed = true,
                Reason = $"generation 值不是 32 位十六进制（{generation}）",
            };
        }
        return new GenerationPointer { Generation = generation, ManifestSha256 = manifestSha256 };
    }

    /// <summary>
    /// 按 manifest 读 generation 的全部语言文件。manifest 声明的 sha256 与实际不符时
    /// 跳过该文件（上游 load_news 对篡改 fail-closed；UI 是只读展示，跳过的数量必须上报给界面，
    /// 不能再只写日志——「跳过了两个文件」和「数据完整」是两种状态）。
    /// </summary>
    private Dictionary<string, List<NewsItem>> LoadGeneration(
        string generation, string manifestSha256, out int skipped, out string skipNote)
    {
        var skipNotes = new List<string>();
        skipped = 0;
        var root = Path.Combine(NewsDirectory, "generations", generation);
        var manifestPath = Path.Combine(root, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifestSha256.Length > 0)
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
            if (!string.Equals(actual, manifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"manifest sha256 不符（期望 {manifestSha256[..12]}…，实际 {actual[..12]}…）");
            }
        }

        var files = manifest.RootElement.TryGetProperty("files", out var filesElement) &&
            filesElement.ValueKind == JsonValueKind.Object
                ? filesElement
                : throw new IOException("manifest 缺少 files 段");

        var now = DateTimeOffset.Now;
        var grouped = new Dictionary<string, List<NewsItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileProperty in files.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!GenerationFilePattern.IsMatch(fileProperty.Name))
            {
                continue;
            }

            try
            {
                var path = Path.Combine(root, fileProperty.Name);
                if (fileProperty.Value.TryGetProperty("sha256", out var expectedSha) &&
                    expectedSha.ValueKind == JsonValueKind.String)
                {
                    var actualSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                    if (!string.Equals(actualSha, expectedSha.GetString(), StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        skipNotes.Add($"{fileProperty.Name} sha256 不符");
                        App.Log($"NewsService: {fileProperty.Name} sha256 不符，跳过该文件。");
                        continue;
                    }
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("news", out var news) ||
                    news.ValueKind != JsonValueKind.Array)
                {
                    skipped++;
                    skipNotes.Add($"{fileProperty.Name} 缺少 news 数组");
                    continue;
                }

                var language = document.RootElement.TryGetProperty("language", out var lang) &&
                    lang.ValueKind == JsonValueKind.String
                        ? lang.GetString() ?? string.Empty
                        : string.Empty;
                if (language.Length == 0)
                {
                    skipped++;
                    skipNotes.Add($"{fileProperty.Name} 缺少 language 字段");
                    continue;
                }

                if (!grouped.TryGetValue(language, out var list))
                {
                    list = [];
                    grouped[language] = list;
                }
                foreach (var element in news.EnumerateArray())
                {
                    list.Add(ParseItem(element, language, now));
                }
            }
            catch (Exception ex)
            {
                skipped++;
                skipNotes.Add($"{fileProperty.Name}：{ex.Message}");
                App.Log($"NewsService: 读取 {fileProperty.Name} 失败：{ex.Message}");
            }
        }

        skipNote = skipNotes.Count == 0 ? string.Empty : string.Join("；", skipNotes);

        foreach (var list in grouped.Values)
        {
            // 新到旧，与旧布局的排序一致。
            list.Sort((a, b) => string.CompareOrdinal(b.PublishedAt, a.PublishedAt));
        }
        return grouped;
    }

    /// <summary>旧布局：kb/news/{language}.json，一层独立于数据库的 JSON。读取失败要能上报，不能伪装成空文件。</summary>
    private (List<NewsItem> List, string Error) LoadLegacyCore(string language)
    {
        var path = PathFor(language);
        if (path.Length == 0)
        {
            return ([], "还没有选择本地 store 目录，无法定位公告文件。");
        }

        if (!File.Exists(path))
        {
            return ([], string.Empty);
        }

        var list = new List<NewsItem>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("news", out var news) ||
                news.ValueKind != JsonValueKind.Array)
            {
                return (list, string.Empty);
            }

            var now = DateTimeOffset.Now;
            foreach (var element in news.EnumerateArray())
            {
                list.Add(ParseItem(element, language, now));
            }

            // 新到旧。
            list.Sort((a, b) => string.CompareOrdinal(b.PublishedAt, a.PublishedAt));
        }
        catch (Exception ex)
        {
            App.Log($"NewsService: 读取 {path} 失败：{ex.Message}");
            return (list, $"读取 {language}.json 失败：{ex.Message}");
        }
        return (list, string.Empty);
    }

    private static NewsItem ParseItem(JsonElement element, string language, DateTimeOffset now)
    {
        string Get(string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        var id = Get("id");
        var source = Get("source");
        var title = Get("title");
        var text = Get("text");
        var url = Get("url");
        var published = Get("published_at");
        var start = Get("start_at");
        var end = Get("end_at");
        var browseType = Get("browse_type");
        var categoryTag = Get("information_tag");
        var informationType = Get("information_type");

        // 日服会把故障公报同时打上 tag=update；type=bug 时以故障报告为准，
        // 否则用户会看到一条「紧急修复」公告被归进「版本更新」。
        if (informationType == "bug" && categoryTag != "bug")
        {
            categoryTag = "bug";
        }

        // 爬虫侧是 Python bool；历史数据里也见过字符串 "True"/"False"，两种都认。
        var bodyAvailable = element.TryGetProperty("body_available", out var bodyProp) &&
            bodyProp.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => bodyProp.GetString() is "True" or "true" or "1",
                _ => false,
            };

        var badge = SourceModel.Resolve(source) is { } resolved
            ? SourceModel.GetInstance(resolved.Instance).ShortName
            : "—";

        return new NewsItem
        {
            Id = id,
            Source = source,
            Language = language,
            Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title,
            Text = text,
            Url = url,
            PublishedAt = published,
            StartAt = start,
            EndAt = end,
            InstanceBadge = badge,
            PublishedLabel = FormatDate(published),
            PeriodLabel = DescribePeriod(start, end, now, out var active),
            IsActive = active,
            BodyAvailable = bodyAvailable,
            BrowseType = browseType,
            Category = categoryTag,
            CategoryLabel = CategoryDisplay(categoryTag),
            Summary = Summarize(text),
        };
    }

    /// <summary>end_at 为空视为长期有效，不算已结束。</summary>
    private static string DescribePeriod(string start, string end, DateTimeOffset now, out bool active)
    {
        var hasStart = DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startAt);
        var hasEnd = DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endAt);

        if (!hasStart && !hasEnd)
        {
            active = true;
            return "长期有效";
        }

        if (hasStart && startAt > now)
        {
            active = false;
            return $"未开始（{startAt.ToLocalTime():MM-dd} 起）";
        }

        if (!hasEnd)
        {
            active = true;
            return hasStart ? $"进行中（{startAt.ToLocalTime():MM-dd} 起，无截止）" : "长期有效";
        }

        if (endAt < now)
        {
            active = false;
            return $"已结束（{endAt.ToLocalTime():MM-dd}）";
        }

        active = true;
        return $"进行中 至 {endAt.ToLocalTime():MM-dd}";
    }

    private static string FormatDate(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd")
            : (string.IsNullOrWhiteSpace(raw) ? "—" : raw);

    private static string Summarize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }
        return flat.Length > 140 ? flat[..140] + "…" : flat;
    }
}
