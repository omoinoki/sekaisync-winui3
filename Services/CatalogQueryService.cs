using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 数据源页要的东西：实体域、meta 管线状态、两个实例的规模与新鲜度、跨实例比对。
/// </summary>
public sealed class CatalogQueryService
{
    /// <summary>
    /// 12 个资产域 ← entities.type 的归并。
    ///
    /// 这里**不写行数**：行数由 <see cref="LoadEntityDomainsAsync"/> 用
    /// `SELECT type, COUNT(*) GROUP BY type` 实测，因为库里还有没归进任何域的类型
    /// （2026-09-20 实测：37 个 type / 71,555 行，其中 9 个 type / 6,523 行不在下表里），
    /// 写死的快照每次同步都会变假（审计 E-2）。
    /// </summary>
    private static readonly (string Key, string DisplayName, string[] Types, bool Collapsed)[] DomainMap =
    [
        ("character", "角色", ["character", "character_profile", "character_rank", "character_unit"], false),
        ("card", "卡牌", ["card", "card_episode"], false),
        ("event", "活动", ["event", "event_item", "event_mission", "event_story"], false),
        ("music", "音乐", ["song", "music_difficulty", "music_vocal"], false),
        ("honor", "称号", ["honor", "honor_group", "bonds_honor", "honor_mission"], false),
        ("stamp", "表情", ["stamp"], false),
        ("gacha", "抽卡", ["gacha"], false),
        ("virtual_live", "演唱会", ["virtual_live"], false),
        ("mysekai", "我的世界", ["mysekai_fixture", "area", "area_item"], false),
        ("story", "剧情", ["special_story", "unit_story"], false),
        ("unit", "乐团", ["unit"], false),
        // 任务占 44%，但名字全是「Live mission 269 (period 3, req 25)」这种占位名，默认折叠。
        ("mission", "任务", ["live_mission", "character_mission", "normal_mission", "story_mission"], true),
    ];

    private readonly AppEnvironment _environment;
    private readonly Dictionary<string, long> _domainCountCache = new(StringComparer.Ordinal);

    public CatalogQueryService(AppEnvironment environment) => _environment = environment;

    private string DatabasePath => _environment.DatabasePath;

    // ─────────────────────────────────────────────────────────────────────────
    // 实体
    // ─────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<EntityDomain>> LoadEntityDomainsAsync(CancellationToken ct = default)
        => SqliteAccess.Run<IReadOnlyList<EntityDomain>>(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, COUNT(*) FROM entities GROUP BY type";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    counts[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            var domains = new List<EntityDomain>();
            foreach (var (key, displayName, types, collapsed) in DomainMap)
            {
                var total = types.Sum(t => counts.TryGetValue(t, out var n) ? n : 0);
                domains.Add(new EntityDomain
                {
                    Key = key,
                    DisplayName = displayName,
                    Types = types,
                    Count = total,
                    DefaultCollapsed = collapsed,
                });
            }
            return domains;
        }, ct);

    /// <summary>
    /// entities 表的实测规模与「有没有落在 12 域之外」的量。
    ///
    /// 界面原来把这些数字写死成 2026-08 的快照（30 个 type / 66,435 行 / 12 域），
    /// 同步一次就悄悄变假（审计 E-2）。这里给实时值，并且如实报告未归域的 type：
    /// DomainMap 只列了 30 个 type，真实库里还有 beginner_mission、shop_item 之类
    /// 没归进任何资产域的行——它们不出现在左栏，用户必须能知道这件事。
    /// </summary>
    public sealed class EntityScopeTotals
    {
        public required long TotalRows { get; init; }

        public required int DistinctTypes { get; init; }

        public required long UnmappedRows { get; init; }

        public required int UnmappedTypes { get; init; }
    }

    public Task<EntityScopeTotals> LoadEntityScopeTotalsAsync(CancellationToken ct = default)
        => SqliteAccess.Run<EntityScopeTotals>(() =>
        {
            var mapped = DomainMap.SelectMany(d => d.Types).ToList();
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            long totalRows;
            int distinctTypes;
            long unmappedRows;
            int unmappedTypes;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT type) FROM entities";
                using var reader = command.ExecuteReader();
                reader.Read();
                totalRows = reader.GetInt64(0);
                distinctTypes = reader.GetInt32(1);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"SELECT COUNT(*), COUNT(DISTINCT type) FROM entities WHERE type NOT IN ({SqliteAccess.Placeholders(mapped, "@m")})";
                SqliteAccess.BindList(command, mapped, "@m");
                using var reader = command.ExecuteReader();
                reader.Read();
                unmappedRows = reader.GetInt64(0);
                unmappedTypes = reader.GetInt32(1);
            }

            return new EntityScopeTotals
            {
                TotalRows = totalRows,
                DistinctTypes = distinctTypes,
                UnmappedRows = unmappedRows,
                UnmappedTypes = unmappedTypes,
            };
        }, ct);

    /// <summary>
    /// 一条实体的**上下文**：来源实例、区服覆盖、数据版本、是否合成样本。
    ///
    /// 注意 `entities` 表没有 `crawled_at` 列（实测列集合为 id/type/region/regions_json/
    /// names_json/facts_json/source/version/demo/trust/seq），所以「抓取时间」只能如实写
    /// 「未测量」——按手册 §5.1 不许拿当前时间或别的字段冒充抓取时间。
    /// </summary>
    public sealed class EntityContext
    {
        public bool Found { get; init; }

        public string Type { get; init; } = string.Empty;

        public string Region { get; init; } = string.Empty;

        public string RegionsJson { get; init; } = "[]";

        public string Source { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public bool IsDemo { get; init; }
    }

    public Task<EntityContext> ReadEntityContextAsync(string id, CancellationToken ct = default)
        => SqliteAccess.Run<EntityContext>(() =>
        {
            if (string.IsNullOrEmpty(id))
            {
                return new EntityContext();
            }

            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT type, region, regions_json, source, version, demo FROM entities WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new EntityContext();
            }

            return new EntityContext
            {
                Found = true,
                Type = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                Region = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                RegionsJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2),
                Source = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Version = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                IsDemo = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
            };
        }, ct);

    /// <summary>
    /// 查一页实体。`nameOnly` = true 时搜索只命中 names_json / id，不再扫 facts_json：
    /// 页面上的搜索框原来承诺「名称 / id」，SQL 却还匹配 facts_json，
    /// 结果里会出现「名字完全不含关键词」的行（审计 E-4）。
    /// </summary>
    public Task<PagedResult<EntityRow>> QueryEntitiesAsync(
        IReadOnlyList<string> types, string search, int offset, int limit,
        bool nameOnly = false, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            SqliteAccess.ValidatePage(offset, limit);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var where = BuildEntityWhere(command, types, search, nameOnly);
            command.CommandText =
                "SELECT id, type, region, names_json, facts_json, source, trust FROM entities " +
                $"WHERE {where} ORDER BY id LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", limit + 1);
            command.Parameters.AddWithValue("@offset", offset);

            var rows = new List<EntityRow>(limit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var id = reader.GetString(0);
                    var type = reader.GetString(1);
                    var region = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var namesJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
                    var source = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);

                    rows.Add(new EntityRow
                    {
                        Id = id,
                        PrimaryName = TextRenderer.PrimaryName(namesJson, id),
                        Type = type,
                        TypeDisplay = SourceModel.KindDisplay(type),
                        Region = region,
                        RegionLabel = SourceModel.RegionByRegion(region)?.DisplayName ?? region,
                        Trust = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        Source = source,
                        InstanceBadge = source.Length > 0 ? SourceModel.ShortBadge(source) : "—",
                        NamesJson = namesJson,
                        FactsJson = reader.IsDBNull(4) ? "{}" : reader.GetString(4),
                    });

                    if (rows.Count > limit)
                    {
                        break;
                    }
                }
            }

            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            stopwatch.Stop();
            return new PagedResult<EntityRow>
            {
                Items = rows,
                Offset = offset,
                HasMore = hasMore,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }, ct);

    public Task<long> CountEntitiesAsync(
        IReadOnlyList<string> types, string search, bool nameOnly = false, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var where = BuildEntityWhere(command, types, search, nameOnly);
            command.CommandText = $"SELECT COUNT(*) FROM entities WHERE {where}";
            return command.ExecuteScalar().ToLong();
        }, ct);

    /// <summary>
    /// 搜索命中范围。默认与页面承诺一致：名称（names_json）与 id；
    /// 只有显式勾掉「仅搜名称」才把 facts_json 加进来（审计 E-4）。
    /// </summary>
    private static string BuildEntityWhere(
        Microsoft.Data.Sqlite.SqliteCommand command, IReadOnlyList<string> types, string search, bool nameOnly)
    {
        var clauses = new List<string>();
        if (types.Count > 0)
        {
            clauses.Add($"type IN ({SqliteAccess.Placeholders(types, "@t")})");
            SqliteAccess.BindList(command, types, "@t");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{SqliteAccess.EscapeLike(search.Trim())}%";
            clauses.Add(nameOnly
                ? "(names_json LIKE @q ESCAPE '\\' OR id LIKE @q ESCAPE '\\')"
                : "(names_json LIKE @q ESCAPE '\\' OR facts_json LIKE @q ESCAPE '\\' OR id LIKE @q ESCAPE '\\')");
            command.Parameters.AddWithValue("@q", pattern);
        }
        return clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // meta
    // ─────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<MetaEntry>> LoadMetaAsync(CancellationToken ct = default)
        => SqliteAccess.Run<IReadOnlyList<MetaEntry>>(() =>
        {
            var list = new List<MetaEntry>();
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM meta ORDER BY key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MetaEntry
                {
                    Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                });
            }
            return list;
        }, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // 实例规模与新鲜度
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 规模走 count(*)（能被 idx_pages_kind 覆盖），抓取首末日期走 seq 的首末行
    /// —— seq 是写入序号，和 crawled_at 单调一致，这样避免对 75 万行做全表扫描。
    /// </summary>
    public Task<IReadOnlyList<SourceStats>> LoadSourceStatsAsync(CancellationToken ct = default)
        => SqliteAccess.Run<IReadOnlyList<SourceStats>>(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            var rows = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT source, COUNT(*) FROM web_pages GROUP BY source";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rows[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            // 正文类目的覆盖情况：source + kind 正好是 idx_pages_kind 的两列，能纯索引扫描。
            var coverage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                var kinds = SourceModel.CanonicalKinds;
                command.CommandText =
                    $"SELECT source, kind FROM web_pages WHERE kind IN ({SqliteAccess.Placeholders(kinds, "@k")}) GROUP BY source, kind";
                SqliteAccess.BindList(command, kinds, "@k");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var source = reader.GetString(0);
                    if (!coverage.TryGetValue(source, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        coverage[source] = set;
                    }
                    set.Add(reader.GetString(1));
                }
            }

            var stats = new List<SourceStats>();
            foreach (var instance in SourceModel.Instances)
            {
                var primary = instance.PrimarySource;
                var (first, last) = ReadCrawlRange(connection, primary);
                var daysAgo = 0;
                if (DateTimeOffset.TryParse(last, out var parsed))
                {
                    daysAgo = Math.Max(0, (int)(DateTimeOffset.Now - parsed).TotalDays);
                }
                else if (DateTime.TryParse(last, out var fallback))
                {
                    daysAgo = Math.Max(0, (int)(DateTimeOffset.Now - new DateTimeOffset(fallback)).TotalDays);
                }

                var covered = coverage.TryGetValue(primary, out var set) ? set.Count : 0;
                var overlayRows = rows.TryGetValue(instance.OverlaySource, out var o) ? o : 0;

                stats.Add(new SourceStats
                {
                    Source = primary,
                    InstanceBadge = instance.ShortName,
                    DisplayName = instance.DisplayName,
                    Upstream = instance.Upstream,
                    Site = instance.Site,
                    Rows = rows.TryGetValue(primary, out var n) ? n : 0,
                    OverlayRows = overlayRows,
                    FirstCrawl = first,
                    LastCrawl = last,
                    DaysAgo = daysAgo,
                    TextKindsCovered = covered,
                    TextKindsTotal = SourceModel.CanonicalKinds.Count,
                });
            }

            return stats;
        }, ct);

    private static (string First, string Last) ReadCrawlRange(Microsoft.Data.Sqlite.SqliteConnection connection, string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT substr(crawled_at,1,10) FROM web_pages WHERE source=@s ORDER BY seq ASC LIMIT 1), " +
            "       (SELECT substr(crawled_at,1,10) FROM web_pages WHERE source=@s ORDER BY seq DESC LIMIT 1)";
        command.Parameters.AddWithValue("@s", source);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return (
                reader.IsDBNull(0) ? "—" : reader.GetString(0),
                reader.IsDBNull(1) ? "—" : reader.GetString(1));
        }
        return ("—", "—");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 跨实例比对
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按 canonical_key 对齐（它自带语言，天然是「同一条内容」的键，且被 idx_pages_canonical 索引）。
    /// 这步会比较慢，只在用户点了「开始比对」时跑。
    /// </summary>
    public Task<(CompareSummary Summary, IReadOnlyList<CompareRow> Rows)> CompareAsync(
        IReadOnlyList<string> kinds, int sampleLimit, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            var kindPlaceholders = SqliteAccess.Placeholders(kinds, "@k");
            var sv = SourceModel.SekaiViewerPrimary;
            var ms = SourceModel.MoesekaiPrimary;

            long both = 0, differed = 0, svOnly = 0, msOnly = 0;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"
SELECT
  SUM(CASE WHEN b.id IS NULL THEN 1 ELSE 0 END),
  SUM(CASE WHEN b.id IS NOT NULL THEN 1 ELSE 0 END),
  SUM(CASE WHEN b.id IS NOT NULL AND IFNULL(a.text_hash,'') <> IFNULL(b.text_hash,'') THEN 1 ELSE 0 END)
FROM web_pages a
LEFT JOIN web_pages b ON b.source = @ms AND b.canonical_key = a.canonical_key AND b.canonical_key <> ''
WHERE a.source = @sv AND a.kind IN ({kindPlaceholders})";
                command.Parameters.AddWithValue("@sv", sv);
                command.Parameters.AddWithValue("@ms", ms);
                SqliteAccess.BindList(command, kinds, "@k");

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    svOnly = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    both = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    differed = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                }
            }

            ct.ThrowIfCancellationRequested();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"
SELECT COUNT(*) FROM web_pages a
WHERE a.source = @ms AND a.kind IN ({kindPlaceholders}) AND a.canonical_key <> ''
  AND NOT EXISTS (SELECT 1 FROM web_pages b WHERE b.source = @sv AND b.canonical_key = a.canonical_key)";
                command.Parameters.AddWithValue("@sv", sv);
                command.Parameters.AddWithValue("@ms", ms);
                SqliteAccess.BindList(command, kinds, "@k");
                msOnly = command.ExecuteScalar().ToLong();
            }

            ct.ThrowIfCancellationRequested();

            // 最有价值的输出：Sekai Viewer 有、Moesekai 没有的条目。
            var rows = new List<CompareRow>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"
SELECT a.canonical_key, a.title, a.language, a.crawled_at
FROM web_pages a
LEFT JOIN web_pages b ON b.source = @ms AND b.canonical_key = a.canonical_key AND b.canonical_key <> ''
WHERE a.source = @sv AND a.kind IN ({kindPlaceholders}) AND b.id IS NULL
ORDER BY a.source, a.id LIMIT @limit";
                command.Parameters.AddWithValue("@sv", sv);
                command.Parameters.AddWithValue("@ms", ms);
                command.Parameters.AddWithValue("@limit", sampleLimit);
                SqliteAccess.BindList(command, kinds, "@k");

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var crawlerAt = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    rows.Add(new CompareRow
                    {
                        AlignmentKey = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        LeftBadge = "SV",
                        RightBadge = "—",
                        Status = "sv-only",
                        StatusLabel = "仅 Sekai Viewer",
                        Detail = $"{(reader.IsDBNull(2) ? string.Empty : reader.GetString(2))} · {crawlerAt}",
                    });
                }
            }

            var summary = new CompareSummary
            {
                Both = both,
                LeftOnly = svOnly,
                RightOnly = msOnly,
                Differed = differed,
                Scanned = both + svOnly,
            };

            return (summary, (IReadOnlyList<CompareRow>)rows);
        }, ct);
}
