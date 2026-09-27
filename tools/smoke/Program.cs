using System.Diagnostics;
using SekaiSync.Desktop.Models;
using SekaiSync.Desktop.Services;

// 冒烟验证：SekaiSync Desktop 数据层 × 真实本地库。
// store 路径交给产品自己那套探测逻辑（AppEnvironment 从 exe 位置向上找仓库根），
// 不在脚本里写死任何机器路径；要指定别的库就把它作为第一个参数传进来。
var storePath = args.Length > 0 ? args[0] : string.Empty;

var settings = new AppSettings { StorePath = storePath };
var env = new AppEnvironment(settings);
var content = new ContentQueryService(env);
var terms = new TermQueryService(env);
var news = new NewsService(env);
var progress = new ProgressService(env);

Console.WriteLine($"=== 环境 ===");
Console.WriteLine($"store      = {env.StorePath}");
Console.WriteLine($"db exists  = {env.DatabaseExists} ({env.DatabasePath})");
Console.WriteLine($"crawl.lock = {env.CrawlLockExists}");
Console.WriteLine($"news dir   = {news.NewsDirectory}");

// 探测是从本工具的 exe 位置向上找仓库根，而主仓通常是兄弟目录而不是祖先目录，
// 所以无参数运行完全可能什么都找不到。这时要给明确出口，别继续打一堆 0 冒充结果。
if (!env.DatabaseExists)
{
    Console.WriteLine();
    Console.WriteLine("没有可核对的本地库。把 store 目录作为第一个参数传进来：");
    Console.WriteLine("  dotnet run --project tools/smoke/Smoke.csproj -- <repo>\\store");
    return 1;
}

Console.WriteLine();
Console.WriteLine("=== 资讯（NewsService）===");
foreach (var lang in news.Languages())
{
    var items = news.Load(lang.Language);
    Console.WriteLine($"{lang.Language}: {items.Count} 条，最新: {items.FirstOrDefault()?.Title}");
}

Console.WriteLine();
Console.WriteLine("=== 同步率（ProgressService）===");
Console.WriteLine($"progress.json 存在 = {File.Exists(progress.PrimaryPath)}，freshness.json 存在 = {File.Exists(progress.FreshnessPath)}");
var snapshot = progress.Load();
Console.WriteLine($"总体 {snapshot.OverallPct:0}%，地区 {string.Join(" ", snapshot.Regions.Select(r => $"{r.Label}={r.OverallPct:0}%"))}");
Console.WriteLine($"合计 {snapshot.Matched:N0} / {snapshot.Expected:N0}（fact {snapshot.FactPct}% / text {snapshot.TextPct}%）");
Console.WriteLine(snapshot.SourceUnavailableLabel.Length > 0
    ? $"不可获取说明：{snapshot.SourceUnavailableLabel}"
    : "不可获取说明：（无）");
Console.WriteLine($"状态文本：{snapshot.StatusText}");

Console.WriteLine();
Console.WriteLine("=== 剧情（ContentQueryService，story kinds）===");
var storyQuery = new StoryQuery { Kinds = SourceModel.StoryKinds, Instance = InstanceFilter.Merged };
var sw = Stopwatch.StartNew();
var storyTotal = await content.CountStoriesAsync(storyQuery);
Console.WriteLine($"count(merged) = {storyTotal}，耗时 {sw.ElapsedMilliseconds}ms");

sw.Restart();
var page1 = await content.QueryStoriesAsync(storyQuery, 0, 50);
Console.WriteLine($"第 1 页 50 行：{page1.ElapsedMilliseconds}ms，首行 [{page1.Items[0].InstanceBadge}/{page1.Items[0].RegionLabel}] {page1.Items[0].Title}");

var searchQuery = new StoryQuery { Kinds = SourceModel.StoryKinds, Instance = InstanceFilter.Merged, Search = "第1话" };
var searchTotal = await content.CountStoriesAsync(searchQuery);
var lastOffset = Math.Max(0, (int)((searchTotal - 1) / 50) * 50);
sw.Restart();
var lastPage = await content.QueryStoriesAsync(searchQuery, lastOffset, 50);
Console.WriteLine($"搜索「第1话」count={searchTotal}，末页 offset={lastOffset} 耗时 {lastPage.ElapsedMilliseconds}ms");

Console.WriteLine();
Console.WriteLine("=== 正文与对白解析（TextRenderer）===");
var normalRow = page1.Items[0];
var detail = await content.LoadStoryAsync(normalRow.Source, normalRow.Id);
if (detail is not null)
{
    Console.WriteLine($"原文行 {normalRow.Source}/{normalRow.Id} kind={normalRow.Kind}：{detail.Blocks.Count} 个对白块");
    var first = detail.Blocks.FirstOrDefault();
    if (first is not null)
    {
        Console.WriteLine($"  首块 speaker=[{first.Speaker}] text={Trunc(first.Text)}");
    }
}

// 覆盖层行：格式不同（台词在上、说话人单独成行），验证第二套解析规则。
var (overlaySource, overlayId) = await FirstOverlayStoryRowAsync(env.DatabasePath);
if (overlaySource is not null)
{
    var oDetail = await content.LoadStoryAsync(overlaySource, overlayId);
    if (oDetail is not null)
    {
        Console.WriteLine($"覆盖行 {overlaySource}/{overlayId}：{oDetail.Blocks.Count} 个对白块");
        var of = oDetail.Blocks.FirstOrDefault();
        if (of is not null)
        {
            Console.WriteLine($"  首块 speaker=[{of.Speaker}] text={Trunc(of.Text)}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== 合并视图去重核查（canonical_key 缺口的量化）===");
await QuantifyDedupGapAsync(env.DatabasePath, SourceModel.StoryKinds);

Console.WriteLine();
Console.WriteLine("=== 用语（TermQueryService）===");
sw.Restart();
var glossaryTotal = await terms.CountTermsAsync(TermQueryService.GlossaryTable, "");
Console.WriteLine($"glossary_terms count={glossaryTotal}，耗时 {sw.ElapsedMilliseconds}ms");
var termPage = await terms.QueryTermsAsync(TermQueryService.GlossaryTable, "", 0, 10);
Console.WriteLine($"第 1 页 10 行：{termPage.ElapsedMilliseconds}ms，首行 {termPage.Items.FirstOrDefault()?.Canonical}");
var translations = await terms.LoadTranslationNamesAsync();
Console.WriteLine($"官方译名字典条目 = {translations.Count}");
var overlayTotal = await terms.CountOverlaysAsync();
Console.WriteLine($"译文覆盖层行数 = {overlayTotal}");

Console.WriteLine();
Console.WriteLine("=== 完成 ===");

static string Trunc(string text) => text.Length > 60 ? text[..60] + "…" : text;

static async Task<(string? Source, string Id)> FirstOverlayStoryRowAsync(string dbPath)
{
    return await Task.Run(() =>
    {
        using var conn = SqliteAccess.Open(dbPath);
        var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT source, id FROM web_pages WHERE source = 'altsource_ms_translation' AND kind = 'event_story' LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, "");
        return ((string?)reader.GetString(0), reader.GetString(1));
    });
}

/// 模拟 BuildWhere 的合并去重，再按正确对齐键数一遍，量化「canonical_key 为空放行」的缺口。
static async Task QuantifyDedupGapAsync(string dbPath, IReadOnlyList<string> kinds)
{
    await Task.Run(() =>
    {
        using var conn = SqliteAccess.Open(dbPath);
        var cmd = conn.CreateCommand();
        var kindParams = SqliteAccess.Placeholders(kinds, "@kd");
        cmd.CommandText =
            "SELECT source, id, canonical_key FROM web_pages " +
            $"WHERE source IN ('altsource_ms', 'altsource_sv') AND kind IN ({kindParams})";
        SqliteAccess.BindList(cmd, kinds, "@kd");
        var rows = new List<(string Source, string Id, string Canonical)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var canonical = reader.IsDBNull(2) ? "" : reader.GetString(2);
                rows.Add((reader.GetString(0), reader.GetString(1), canonical));
            }
        }

        // BuildWhere 的现行 SQL：SV 全保留；MS 行仅当 canonical_key 非空且 SV 无同 key 行时保留。
        var svKeys = rows.Where(r => r.Source == SourceModel.SekaiViewerPrimary && r.Canonical.Length > 0)
            .Select(r => r.Canonical).ToHashSet();
        var kept = rows.Where(r =>
            r.Source == SourceModel.SekaiViewerPrimary ||
            r.Canonical.Length == 0 ||
            !svKeys.Contains(r.Canonical)).ToList();

        // 正确做法：按 SourceModel.AlignmentKey 分组去重。
        var distinctByKey = kept.GroupBy(r => SourceModel.AlignmentKey(r.Id)).Count();

        var emptyCanonical = rows.Count(r => r.Canonical.Length == 0);
        Console.WriteLine($"主数据源 story 行数   = {rows.Count}");
        Console.WriteLine($"canonical_key 为空   = {emptyCanonical} ({100.0 * emptyCanonical / rows.Count:0.0}%)");
        Console.WriteLine($"现行 SQL 去重后保留  = {kept.Count}");
        Console.WriteLine($"按对齐键去重后应为   = {distinctByKey}");
        Console.WriteLine($"多余重复行（缺口）   = {kept.Count - distinctByKey}");
    });
}

return 0;

