using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.Services;

/// <summary>剧情页的查询条件。</summary>
public sealed class StoryQuery
{
    public IReadOnlyList<string> Kinds { get; init; } = SourceModel.StoryKinds;
    public InstanceFilter Instance { get; init; } = InstanceFilter.Merged;

    /// <summary>null = 全部版本。</summary>
    public string? Language { get; init; }

    public string Search { get; init; } = string.Empty;

    public bool OnlyUntranslated { get; init; }
}

/// <summary>台词页的查询条件。</summary>
public sealed class VoiceQuery
{
    public IReadOnlyList<string> Kinds { get; init; } = SourceModel.VoiceKinds;
    public InstanceFilter Instance { get; init; } = InstanceFilter.Merged;
    public string? Language { get; init; }
    public string Search { get; init; } = string.Empty;
}

/// <summary>
/// 剧情与台词的查询。
///
/// 几条实测出来的规矩：
///   1. 用 `source IN (...)` 代替 `overlay = 0` —— 覆盖层行全都来自另两个 source，
///      而 source 是 idx_pages_kind 的第一列，能走索引；overlay 没有任何索引。
///   2. `ORDER BY source, id` 正好是 web_pages 的主键顺序，排序免费。
///   3. 列表绝不 SELECT text 列。它是行内长文本，读一列会把整行拖出来。
///
/// 搜索口径写死在这里，页面的占位符必须引用 <see cref="StorySearchScopeHint"/> /
/// <see cref="VoiceSearchScopeHint"/>（审计 V-2 / XP-11）：
/// `BuildWhere` 只对 `title` 与 `id` 做 LIKE，**不搜说话人、不搜台词正文**。
/// 台词页原先的占位符承诺「搜索说话人 / 台词」，用户输入角色名得到 0 行，
/// 会把「本地搜不到」读成「库里没有这句」——那是数据诚实问题，不是搜索没做好。
/// 正文 LIKE 会全表扫描 `text`（台词 126,373 行、均长 14–836 字符），
/// 而 `COUNT(*)` 与列表共用同一个 WHERE，代价直接落在首屏；
/// 说话人根本不是列（`TextRenderer.FirstSpeaker(text)` 的派生值），SQL 无从匹配。
/// 所以这一轮选择「让承诺与 SQL 一致」，把「搜正文」留成需要 FTS 索引的独立改动。
/// </summary>
public sealed class ContentQueryService
{
    /// <summary>剧情页搜索框的诚实描述：命中字段与 BuildWhere 逐字对应。</summary>
    public const string StorySearchScopeHint = "搜索标题或 id（回车执行）";

    /// <summary>台词页搜索框的诚实描述：说话人与台词正文不参与匹配。</summary>
    public const string VoiceSearchScopeHint = "按 id 或标题搜索（回车执行）；说话人与台词正文不参与匹配";

    private readonly AppEnvironment _environment;

    public ContentQueryService(AppEnvironment environment) => _environment = environment;

    private string DatabasePath => _environment.DatabasePath;

    /// <summary>本地库是否可用（列表状态要区分「没库」与「查不到」，S-6 / V-3）。</summary>
    public bool HasLocalDatabase => _environment.DatabaseExists;

    // ─────────────────────────────────────────────────────────────────────────
    // 剧情
    // ─────────────────────────────────────────────────────────────────────────

    private const string StoryColumns =
        "t.source, t.id, t.title, t.kind, t.language, t.url, t.crawled_at, " +
        "t.untranslated, t.translation_source, t.trust, t.canonical_key";

    public Task<PagedResult<StoryRow>> QueryStoriesAsync(StoryQuery query, int offset, int limit, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            SqliteAccess.ValidatePage(offset, limit);
            var stopwatch = Stopwatch.StartNew();
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            var where = BuildWhere(command, query.Kinds, query.Instance, query.Language, query.Search, query.OnlyUntranslated, "t");
            command.CommandText =
                $"SELECT {StoryColumns} FROM web_pages t WHERE {where} " +
                "ORDER BY t.source, t.id LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", limit + 1);
            command.Parameters.AddWithValue("@offset", offset);

            var rows = new List<StoryRow>(limit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    rows.Add(ReadStoryRow(reader));
                    if (rows.Count > limit)
                    {
                        break;
                    }
                }
            }

            var hasMore = rows.Count > limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            stopwatch.Stop();
            return new PagedResult<StoryRow>
            {
                Items = rows,
                Offset = offset,
                HasMore = hasMore,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }, ct);

    /// <summary>总数。只带 source + kind 时能走覆盖索引，毫秒级；带语言/搜索会慢一些。</summary>
    public Task<long> CountStoriesAsync(StoryQuery query, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var where = BuildWhere(command, query.Kinds, query.Instance, query.Language, query.Search, query.OnlyUntranslated, "t");
            command.CommandText = $"SELECT COUNT(*) FROM web_pages t WHERE {where}";
            return Convert.ToInt64(command.ExecuteScalar());
        }, ct);

    private static StoryRow ReadStoryRow(SqliteDataReader reader)
    {
        var source = reader.GetString(0);
        var id = reader.GetString(1);
        var kind = reader.GetString(3);
        var language = reader.GetString(4);
        var untranslated = !reader.IsDBNull(7) && reader.GetInt64(7) != 0;
        var translationSource = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
        var trust = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
        var crawledAt = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
        var region = SourceModel.RegionByLanguage(language);

        return new StoryRow
        {
            Source = source,
            Id = id,
            Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Kind = kind,
            Language = language,
            Url = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            CrawledAt = crawledAt,
            KindDisplay = SourceModel.KindDisplay(kind),
            RegionLabel = region?.DisplayName ?? language,
            InstanceBadge = SourceModel.ShortBadge(source),
            CrawledLabel = CoverageLabels.Crawled(crawledAt),
            Length = 0,
            Untranslated = untranslated,
            TranslationNote = DescribeTranslation(trust, translationSource, untranslated),
            AlignmentKey = SourceModel.AlignmentKey(id),
        };
    }

    /// <summary>trust = C 才显示译文状态；B 是默认态，不占位置。</summary>
    internal static string DescribeTranslation(string trust, string translationSource, bool untranslated)
    {
        if (untranslated)
        {
            return "未翻译";
        }
        if (!string.Equals(trust, "C", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return translationSource switch
        {
            "official_cn" => "官方",
            "llm" => "机翻待校对",
            "human" => "人工待确认",
            "jp_pending" => "待补日文",
            "i18n" => "社区",
            _ => translationSource.Length > 0 ? translationSource : "非官方",
        };
    }

    /// <summary>取一整行的正文并拆成对白。</summary>
    public Task<StoryDetail?> LoadStoryAsync(string source, string id, CancellationToken ct = default)
        => SqliteAccess.Run<StoryDetail?>(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT source, id, title, kind, language, url, crawled_at, text, " +
                "untranslated, translation_source, trust, overlay, asset_mismatch, content_language_mismatch " +
                "FROM web_pages WHERE source = @source AND id = @id";
            command.Parameters.AddWithValue("@source", source);
            command.Parameters.AddWithValue("@id", id);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var kind = reader.GetString(3);
            var language = reader.GetString(4);
            var text = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var untranslated = !reader.IsDBNull(8) && reader.GetInt64(8) != 0;
            var translationSource = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
            var trust = reader.IsDBNull(10) ? string.Empty : reader.GetString(10);
            var overlay = !reader.IsDBNull(11) && reader.GetInt64(11) != 0;
            var assetMismatch = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);
            var languageMismatch = !reader.IsDBNull(13) && reader.GetInt64(13) != 0;
            var region = SourceModel.RegionByLanguage(language);

            var blocks = TextRenderer.ParseDialogue(text, overlay, ct);

            var notes = new List<string>();
            if (assetMismatch.Length > 0)
            {
                notes.Add($"资源不匹配：{assetMismatch}");
            }
            if (languageMismatch)
            {
                notes.Add("内容语言与 language 列不一致");
            }

            return new StoryDetail
            {
                // 稳定 id 与 source 一起带回详情：审计 N-10/V-9 的「无法复制稳定 ID」在剧情页同样成立。
                Id = reader.GetString(1),
                Source = reader.GetString(0),
                Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Url = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                InstanceBadge = SourceModel.ShortBadge(reader.GetString(0)),
                Language = language,
                RegionLabel = region?.DisplayName ?? language,
                KindDisplay = SourceModel.KindDisplay(kind),
                CrawledAt = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                AlignmentKey = SourceModel.AlignmentKey(reader.GetString(1)),
                TranslationNote = DescribeTranslation(trust, translationSource, untranslated),
                QualityNote = string.Join("；", notes),
                Blocks = blocks,
                BlockCount = blocks.Count,
            };
        }, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // 台词
    // ─────────────────────────────────────────────────────────────────────────

    public Task<PagedResult<VoiceRow>> QueryVoicesAsync(VoiceQuery query, int offset, int limit, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            SqliteAccess.ValidatePage(offset, limit);
            var stopwatch = Stopwatch.StartNew();
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            var where = BuildWhere(command, query.Kinds, query.Instance, query.Language, query.Search, false, "t");

            // 台词短，直接用 substr 截出来，省一次往返。
            command.CommandText =
                "SELECT t.source, t.id, t.kind, t.language, t.crawled_at, substr(t.text, 1, 400), t.canonical_key " +
                $"FROM web_pages t WHERE {where} ORDER BY t.source, t.id LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", limit + 1);
            command.Parameters.AddWithValue("@offset", offset);

            var rows = new List<VoiceRow>(limit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var kind = reader.GetString(2);
                    var language = reader.GetString(3);
                    var crawledAt = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var text = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                    var id = reader.GetString(1);
                    var region = SourceModel.RegionByLanguage(language);

                    rows.Add(new VoiceRow
                    {
                        Source = reader.GetString(0),
                        Id = id,
                        Speaker = TextRenderer.FirstSpeaker(text),
                        Text = Collapse(text),
                        Kind = kind,
                        KindDisplay = SourceModel.KindDisplay(kind),
                        Language = language,
                        RegionLabel = region?.DisplayName ?? language,
                        InstanceBadge = SourceModel.ShortBadge(reader.GetString(0)),
                        CrawledLabel = CoverageLabels.Crawled(crawledAt),
                        HasCrawledAt = crawledAt.Length > 0,
                        AlignmentKey = SourceModel.AlignmentKey(id),
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
            return new PagedResult<VoiceRow>
            {
                Items = rows,
                Offset = offset,
                HasMore = hasMore,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }, ct);

    public Task<long> CountVoicesAsync(VoiceQuery query, CancellationToken ct = default)
        => SqliteAccess.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath, ct);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var where = BuildWhere(command, query.Kinds, query.Instance, query.Language, query.Search, false, "t");
            command.CommandText = $"SELECT COUNT(*) FROM web_pages t WHERE {where}";
            return command.ExecuteScalar().ToLong();
        }, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // 五语并排
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 取同一条资产在五服的正文。对齐键是 {kind}:{asset}，
    /// 而 canonical_key 是 {kind}:{language}:{asset} —— 正好能拼出五个精确键，
    /// 走 idx_pages_canonical 做 IN 查询，比 LIKE 快得多。
    /// 同一键两实例都有时取 Sekai Viewer。
    ///
    /// 两条诚实性约束（审计 S-1 / S-4）：
    ///   1. 实例筛选必须一路带进对照查询。写死 Merged 会让「仅 Moesekai」下
    ///      仍然排出 Sekai Viewer 的行与 SV 徽章 —— 用户看不见的地方筛选失效。
    ///   2. 本地查不到 **不等于** 该服没有（正文层实测覆盖率只有 62–83%）。
    ///      缺行、有行但正文为空、无对齐键、查询抛异常，四种情况各带一个机读
    ///      <see cref="ParallelMissingReason"/>，由页面渲染成文字；对照区任何情况下都不留空白。
    /// </summary>
    public Task<IReadOnlyList<ParallelLine>> LoadParallelAsync(
        string alignmentKey,
        int previewLength,
        InstanceFilter instance,
        CancellationToken ct = default)
        => SqliteAccess.Run<IReadOnlyList<ParallelLine>>(() =>
        {
            var split = alignmentKey.IndexOf(':');
            if (split <= 0)
            {
                // 没有对齐键就无从跨服比对；五栏照样出现并写明原因，不能整块消失。
                return MissingLines(ParallelMissingReason.NoAlignmentKey);
            }

            var kind = alignmentKey[..split];
            var asset = alignmentKey[(split + 1)..];
            var keys = SourceModel.Regions
                .Select(r => $"{kind}:{r.Language}:{asset}")
                .ToList();

            Dictionary<string, (string Text, string Source)> found;
            try
            {
                using var connection = SqliteAccess.Open(DatabasePath, ct);
                ct.ThrowIfCancellationRequested();

                var sources = SourceModel.PrimarySourcesFor(instance);

                using var command = connection.CreateCommand();
                var keyPlaceholders = SqliteAccess.Placeholders(keys, "@k");
                var sourcePlaceholders = SqliteAccess.Placeholders(sources, "@s");
                var textProjection = previewLength > 0 ? "substr(CAST(text AS BLOB), 1, @preview)" : "text";
                if (previewLength > 0)
                    command.Parameters.AddWithValue("@preview", 4L * (previewLength + 1L));
                command.CommandText =
                    $"SELECT language, {textProjection}, source FROM web_pages WHERE canonical_key IN ({keyPlaceholders}) " +
                    $"AND source IN ({sourcePlaceholders})";
                SqliteAccess.BindList(command, keys, "@k");
                SqliteAccess.BindList(command, sources, "@s");

                found = new Dictionary<string, (string Text, string Source)>(StringComparer.OrdinalIgnoreCase);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var language = reader.GetString(0);
                        // Bound UTF-8 bytes before crossing into managed memory; SQLite
                        // substr(TEXT) would silently lose content after an embedded NUL.
                        var text = reader.IsDBNull(1) ? string.Empty : previewLength > 0
                            ? Encoding.UTF8.GetString((byte[])reader.GetValue(1)) : reader.GetString(1);
                        var source = reader.GetString(2);
                        if (found.TryGetValue(language, out var existing))
                        {
                            // 已有则按实例优先级保留。
                            if (SourceModel.MergePriority(source) < SourceModel.MergePriority(existing.Source))
                            {
                                found[language] = (text, source);
                            }
                        }
                        else
                        {
                            found[language] = (text, source);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 被新查询取代不是「读取失败」，原样上抛，由调用方丢弃结果。
                throw;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (Exception ex)
            {
                App.Log($"ContentQueryService: 对照查询失败：{ex.Message}");
                return MissingLines(ParallelMissingReason.ReadFailed);
            }

            // 单实例档位下每栏都来自同一个实例，徽章是噪音；只有合并视图需要它区分来源。
            var showBadge = instance == InstanceFilter.Merged;
            var lines = new List<ParallelLine>();
            foreach (var region in SourceModel.Regions)
            {
                if (found.TryGetValue(region.Language, out var hit) && hit.Text.Length > 0)
                {
                    lines.Add(new ParallelLine(
                        region.DisplayName,
                        region.Language,
                        previewLength > 0 && hit.Text.Length > previewLength ? hit.Text[..previewLength] + "…" : hit.Text,
                        showBadge ? SourceModel.ShortBadge(hit.Source) : string.Empty,
                        false));
                }
                else
                {
                    // 有行但 text 为空 = 抓到条目、正文未抽取；没有行 = 本地没这一语言。
                    // 两者都只能说「本地未覆盖」，不能说「该版本没有这一话」。
                    lines.Add(new ParallelLine(region.DisplayName, region.Language, string.Empty, string.Empty, true, ParallelMissingReason.NotCrawled));
                }
            }

            return lines;
        }, ct);

    /// <summary>五服全部缺失的占位栏（无对齐键 / 读取失败两种情况共用）。</summary>
    private static IReadOnlyList<ParallelLine> MissingLines(ParallelMissingReason reason) =>
        SourceModel.Regions
            .Select(r => new ParallelLine(r.DisplayName, r.Language, string.Empty, string.Empty, true, reason))
            .ToList();

    // ─────────────────────────────────────────────────────────────────────────
    // WHERE 构造
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 公共 WHERE。注意这里用 source 白名单来代替 overlay = 0：
    /// 覆盖层行只可能来自 altsource_ms_translation / altsource_sv_i18n，
    /// 把 source 限死在两个主数据源上，既等价又能吃到 idx_pages_kind。
    ///
    /// 搜索只命中 `title` 与 `id`（见类型注释与 StorySearchScopeHint / VoiceSearchScopeHint）：
    /// 不碰 `text`，也不碰由 `TextRenderer.FirstSpeaker` 派生的说话人。
    /// 改这里的命中字段时，必须同步改两个 hint —— 页面占位符绑的就是它们。
    /// </summary>
    private static string BuildWhere(
        SqliteCommand command,
        IReadOnlyList<string> kinds,
        InstanceFilter instance,
        string? language,
        string search,
        bool onlyUntranslated,
        string alias)
    {
        var clauses = new List<string>();
        var sources = SourceModel.PrimarySourcesFor(instance);
        var prefix = alias.Length > 0 ? alias + "." : string.Empty;

        var sourcePlaceholders = SqliteAccess.Placeholders(sources, "@src");
        clauses.Add($"{prefix}source IN ({sourcePlaceholders})");
        SqliteAccess.BindList(command, sources, "@src");

        if (kinds.Count > 0)
        {
            var kindPlaceholders = SqliteAccess.Placeholders(kinds, "@kd");
            clauses.Add($"{prefix}kind IN ({kindPlaceholders})");
            SqliteAccess.BindList(command, kinds, "@kd");
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            clauses.Add($"{prefix}language = @lang");
            command.Parameters.AddWithValue("@lang", language);
        }

        if (onlyUntranslated)
        {
            clauses.Add($"{prefix}untranslated = 1");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{SqliteAccess.EscapeLike(search.Trim())}%";
            clauses.Add($"({prefix}title LIKE @q ESCAPE '\\' OR {prefix}id LIKE @q ESCAPE '\\')");
            command.Parameters.AddWithValue("@q", pattern);
        }

        // 合并视图：同一对齐键两实例都有时取 Sekai Viewer，只保留 Moesekai 独有的行。
        if (instance == InstanceFilter.Merged)
        {
            clauses.Add(
                $"({prefix}source = @svSource OR {prefix}canonical_key = '' OR NOT EXISTS (" +
                $"SELECT 1 FROM web_pages s WHERE s.source = @svSource AND s.canonical_key = {prefix}canonical_key))");
            command.Parameters.AddWithValue("@svSource", SourceModel.SekaiViewerPrimary);
        }

        return string.Join(" AND ", clauses);
    }

    private static string Collapse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var flat = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        var builder = new StringBuilder(flat.Length);
        var previousSpace = false;
        foreach (var c in flat)
        {
            var isSpace = c == ' ' || c == '\t';
            if (isSpace && previousSpace)
            {
                continue;
            }
            builder.Append(c);
            previousSpace = isSpace;
        }
        return builder.ToString().Trim();
    }
}

/// <summary>Sqlite 的 Convert 简写。</summary>
internal static class SqliteScalarExtensions
{
    public static long ToLong(this object? value) =>
        value is null || value is DBNull ? 0 : Convert.ToInt64(value);
}
