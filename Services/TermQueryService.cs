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

/// <summary>
/// 用语三张表（glossary_terms / terms / term_evidence）+ 官方译名字典（title_overlay）
/// + 剧情级译文覆盖层（overlay = 1）的查询。
/// </summary>
public sealed class TermQueryService
{
    public const string GlossaryTable = "glossary_terms";
    public const string TermsTable = "terms";

    private readonly AppEnvironment _environment;

    public TermQueryService(AppEnvironment environment) => _environment = environment;

    private string DatabasePath => _environment.DatabasePath;

    /// <summary>
    /// 浏览页的收窄条件。实例（web_pages.source）与语言/服两维，
    /// 各查询按自己表里**真实存在**的列去用它们：术语两表没有来源列，
    /// 所以那里只吃语言，不吃实例——由界面说明禁用原因，而不是假装过滤生效。
    /// </summary>
    public sealed class TermFilter
    {
        /// <summary>不限（与旧调用等价的默认值）。</summary>
        public static TermFilter None { get; } = new();

        /// <summary>允许的 source 值集合；空 = 不限。</summary>
        public IReadOnlyList<string> Sources { get; init; } = [];

        /// <summary>语言码 ja / en / zh_hans / zh_hant / ko；空 = 不限。</summary>
        public string Language { get; init; } = string.Empty;

        public bool HasSources => Sources.Count > 0;

        public bool HasLanguage => Language.Length > 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 术语 / 提取用语
    // ─────────────────────────────────────────────────────────────────────────

    public Task<PagedResult<TermRow>> QueryTermsAsync(string table, string search, int offset, int limit, CancellationToken ct = default)
        => QueryTermsAsync(table, search, offset, limit, TermFilter.None, ct);

    public Task<PagedResult<TermRow>> QueryTermsAsync(string table, string search, int offset, int limit, TermFilter filter, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            var where = BuildTermWhere(command, table, search, filter);
            command.CommandText = $"SELECT {TermColumns(table)} FROM {SqliteAccess.Quote(table)} WHERE {where} " +
                                  "ORDER BY canonical LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);

            var rows = new List<TermRow>(limit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(ReadTermRow(reader, table));
                    if (rows.Count >= limit)
                    {
                        break;
                    }
                }
            }

            stopwatch.Stop();
            return new PagedResult<TermRow>
            {
                Items = rows,
                Offset = offset,
                HasMore = rows.Count >= limit,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }, ct);

    public Task<long> CountTermsAsync(string table, string search, CancellationToken ct = default)
        => CountTermsAsync(table, search, TermFilter.None, ct);

    public Task<long> CountTermsAsync(string table, string search, TermFilter filter, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var where = BuildTermWhere(command, table, search, filter);
            command.CommandText = $"SELECT COUNT(*) FROM {SqliteAccess.Quote(table)} WHERE {where}";
            return command.ExecuteScalar().ToLong();
        }, ct);

    private static string TermColumns(string table) => table == TermsTable
        ? "id, canonical, kind, names_json, official, trust, confidence, occurrences, tags_json, evidence_count"
        : "id, canonical, kind, names_json, official, trust, 0, 0, '', 0";

    private static string BuildTermWhere(SqliteCommand command, string table, string search, TermFilter filter)
    {
        var clauses = new List<string> { "1 = 1" };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{SqliteAccess.EscapeLike(search.Trim())}%";
            command.Parameters.AddWithValue("@q", pattern);
            clauses.Add("(canonical LIKE @q ESCAPE '\\' OR names_json LIKE @q ESCAPE '\\' OR id LIKE @q ESCAPE '\\')");
        }

        if (filter.HasLanguage)
        {
            // 只保留该语言真有名目的行。库里有非法 JSON 的 names_json，
            // 所以先过 json_valid，避免一行坏数据让整页查询抛错。
            command.Parameters.AddWithValue("@lang", filter.Language);
            clauses.Add("json_valid(names_json) = 1 " +
                        "AND coalesce(json_extract(names_json, '$.' || @lang), '') <> ''");
        }

        return string.Join(" AND ", clauses);
    }

    private static TermRow ReadTermRow(SqliteDataReader reader, string table)
    {
        var namesJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
        var cells = TextRenderer.ParseNames(namesJson);
        var official = !reader.IsDBNull(4) && reader.GetInt64(4) != 0;

        string extra;
        if (table == TermsTable)
        {
            var confidence = reader.IsDBNull(6) ? 0d : reader.GetDouble(6);
            var occurrences = reader.IsDBNull(7) ? 0L : reader.GetInt64(7);
            var evidence = reader.IsDBNull(9) ? 0L : reader.GetInt64(9);
            extra = $"置信 {confidence:0.00} · 词频 {occurrences:N0} · 证据 {evidence:N0}";
        }
        else
        {
            extra = string.Empty;
        }

        var trust = reader.IsDBNull(5) ? string.Empty : reader.GetString(5).Trim();

        return new TermRow
        {
            Keys = [table, reader.GetString(0)],
            Table = table,
            Id = reader.GetString(0),
            Canonical = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Kind = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            OfficialLabel = official ? "官方" : string.Empty,
            // 可信列原本是裸字母 / 空白：空白要有文字表达，未识别的值保留原码（手册 §6）。
            Trust = trust.Length == 0 ? "未标注" : trust,
            Ja = cells.Cell("ja"),
            En = cells.Cell("en"),
            ZhHans = cells.Cell("zh_hans"),
            ZhHant = cells.Cell("zh_hant"),
            Ko = cells.Cell("ko"),
            NamesJson = namesJson,
            Extra = extra,
        };
    }

    /// <summary>取某条用语的完整 names_json（列表里只有预览）。</summary>
    public Task<string> LoadNamesJsonAsync(string table, string id, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT names_json FROM {SqliteAccess.Quote(table)} WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            var value = command.ExecuteScalar();
            return value is string s ? s : "{}";
        }, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // 句级证据
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 句级证据。**不再吞异常返回空列表**：读取失败与「确认没有记录」是两件事，
    /// 之前两者都变成空列表，界面就把它写成了「term_evidence 表里没有对应记录」这句假话。
    /// 失败一律抛出，由 ViewModel 渲染「读取失败 + 重试」。
    /// </summary>
    public Task<IReadOnlyList<EvidenceRow>> LoadEvidenceAsync(string termId, int limit = 60, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<EvidenceRow>>(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT idx, story_key, language, sentence FROM term_evidence " +
                "WHERE term_id = @id ORDER BY idx LIMIT @limit";
            command.Parameters.AddWithValue("@id", termId);
            command.Parameters.AddWithValue("@limit", limit);
            var list = new List<EvidenceRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new EvidenceRow
                {
                    Index = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    StoryKey = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Language = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Sentence = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                });
            }

            ct.ThrowIfCancellationRequested();
            return list;
        }, ct);

    /// <summary>
    /// 某条用语的证据总条数（实测值）。列表有上限，「有证据 N 条」里的 N 必须是这个数，
    /// 不能拿已加载的条数当规模（同剧情页「300 条」那个错）。
    /// </summary>
    public Task<long> CountEvidenceAsync(string termId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM term_evidence WHERE term_id = @id";
            command.Parameters.AddWithValue("@id", termId);
            return command.ExecuteScalar().ToLong();
        }, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // 官方译名字典（title_overlay）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 107 行 = 22 个 namespace × 最多 5 语言。每行的 text 是 `key\t译名` 的 TSV 块，
    /// key 有时是数字 id、有时是日文原文。这里按 namespace 拼成五语对照表。
    /// </summary>
    public Task<IReadOnlyList<TranslationNameRow>> LoadTranslationNamesAsync(CancellationToken ct = default)
        => LoadTranslationNamesAsync(TermFilter.None, ct);

    /// <summary>按实例（source）与语言收窄的同一份字典。失败一律抛出，不返回空集合。</summary>
    public Task<IReadOnlyList<TranslationNameRow>> LoadTranslationNamesAsync(TermFilter filter, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<TranslationNameRow>>(() =>
        {
            // namespace → language → (key → value)
            var buckets = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            var keyOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            using (var connection = SqliteAccess.Open(DatabasePath))
            {
                ct.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT namespace, language, text, source FROM web_pages WHERE kind = 'title_overlay'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var ns = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var language = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var text = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var source = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    if (ns.Length == 0 || language.Length == 0)
                    {
                        continue;
                    }

                    if (filter.HasSources && !filter.Sources.Contains(source, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (!buckets.TryGetValue(ns, out var byLanguage))
                    {
                        byLanguage = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                        buckets[ns] = byLanguage;
                        keyOrder[ns] = [];
                    }
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    byLanguage[language] = map;

                    foreach (var line in text.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (trimmed.Length == 0)
                        {
                            continue;
                        }
                        var tab = trimmed.IndexOf('\t');
                        var key = tab > 0 ? trimmed[..tab] : trimmed;
                        var value = tab > 0 ? trimmed[(tab + 1)..] : string.Empty;
                        map[key] = value;
                        if (!keyOrder[ns].Contains(key, StringComparer.Ordinal))
                        {
                            keyOrder[ns].Add(key);
                        }
                    }
                }
            }

            var rows = new List<TranslationNameRow>();
            foreach (var ns in buckets.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var byLanguage = buckets[ns];
                foreach (var key in keyOrder[ns])
                {
                    string Get(string language) =>
                        byLanguage.TryGetValue(language, out var map) && map.TryGetValue(key, out var value)
                            ? value
                            : string.Empty;

                    var row = new TranslationNameRow
                    {
                        Namespace = ns,
                        Id = key,
                        Key = key,
                        Ja = Get("ja"),
                        En = Get("en"),
                        ZhHans = Get("zh_hans"),
                        ZhHant = Get("zh_hant"),
                        Ko = Get("ko"),
                    };

                    // 「版本」在字典里的含义：只看在这一服有译名的键。
                    if (filter.HasLanguage && LanguageValue(row, filter.Language).Length == 0)
                    {
                        continue;
                    }

                    rows.Add(row);
                }
            }

            ct.ThrowIfCancellationRequested();
            return rows;
        }, ct);

    /// <summary>语言码 → 字典行的对应单元格（搜索与版本收窄共用，别再漏繁中/韩）。</summary>
    public static string LanguageValue(TranslationNameRow row, string language) => language switch
    {
        "ja" => row.Ja,
        "en" => row.En,
        "zh_hans" => row.ZhHans,
        "zh_hant" => row.ZhHant,
        "ko" => row.Ko,
        _ => string.Empty,
    };

    /// <summary>字典行的可搜索文本：key + 五语全部。T-3 漏繁中/韩就是漏在这里的字段上。</summary>
    public static bool TranslationRowMatches(TranslationNameRow row, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        return row.Key.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               row.Ja.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               row.En.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               row.ZhHans.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               row.ZhHant.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               row.Ko.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 剧情级译文覆盖层
    // ─────────────────────────────────────────────────────────────────────────

    public Task<PagedResult<OverlayRow>> QueryOverlaysAsync(int offset, int limit, CancellationToken ct = default)
        => QueryOverlaysAsync(offset, limit, TermFilter.None, ct);

    public Task<PagedResult<OverlayRow>> QueryOverlaysAsync(int offset, int limit, TermFilter filter, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT source, id, title, language, translation_source, trust, length(text) " +
                "FROM web_pages WHERE overlay = 1" + OverlayWhere(command, filter) +
                " ORDER BY source, id LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);

            var rows = new List<OverlayRow>(limit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var language = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var translationSource = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var trust = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                    var region = SourceModel.RegionByLanguage(language);

                    rows.Add(new OverlayRow
                    {
                        Id = reader.GetString(1),
                        Source = reader.GetString(0),
                        Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        Language = language,
                        RegionLabel = region?.DisplayName ?? language,
                        TranslationSource = translationSource,
                        StatusLabel = ContentQueryService.DescribeTranslation(trust, translationSource, false),
                        Trust = trust,
                        InstanceBadge = SourceModel.ShortBadge(reader.GetString(0)),
                        Length = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                    });

                    if (rows.Count >= limit)
                    {
                        break;
                    }
                }
            }

            stopwatch.Stop();
            return new PagedResult<OverlayRow>
            {
                Items = rows,
                Offset = offset,
                HasMore = rows.Count >= limit,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }, ct);

    public Task<long> CountOverlaysAsync(CancellationToken ct = default)
        => CountOverlaysAsync(TermFilter.None, ct);

    public Task<long> CountOverlaysAsync(TermFilter filter, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM web_pages WHERE overlay = 1" + OverlayWhere(command, filter);
            return command.ExecuteScalar().ToLong();
        }, ct);

    /// <summary>覆盖层按 translation_source 分组的状态统计。</summary>
    public Task<IReadOnlyList<LabelValue>> LoadOverlayStatusAsync(CancellationToken ct = default)
        => LoadOverlayStatusAsync(TermFilter.None, ct);

    /// <summary>带实例/语言收窄的同一统计。失败一律抛出（界面据此出「读取失败 + 重试」）。</summary>
    public Task<IReadOnlyList<LabelValue>> LoadOverlayStatusAsync(TermFilter filter, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<LabelValue>>(() =>
        {
            var rows = new List<LabelValue>();
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT translation_source, trust, COUNT(*) FROM web_pages WHERE overlay = 1" +
                OverlayWhere(command, filter) +
                " GROUP BY translation_source, trust ORDER BY COUNT(*) DESC";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var source = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var trust = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var count = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                var status = ContentQueryService.DescribeTranslation(trust, source, false);
                rows.Add(new LabelValue(
                    string.IsNullOrEmpty(status) ? source : status,
                    $"{count:N0} 条 · trust {trust}"));
            }

            ct.ThrowIfCancellationRequested();
            return rows;
        }, ct);

    /// <summary>覆盖层查询共用的收窄片段（web_pages 有 source 与 language 两列，都能真吃）。</summary>
    private static string OverlayWhere(SqliteCommand command, TermFilter filter)
    {
        if (!filter.HasSources && !filter.HasLanguage)
        {
            return string.Empty;
        }

        var clauses = new List<string>();
        if (filter.HasSources)
        {
            var names = new List<string>();
            for (var i = 0; i < filter.Sources.Count; i++)
            {
                var name = "@src" + i;
                command.Parameters.AddWithValue(name, filter.Sources[i]);
                names.Add(name);
            }
            clauses.Add($"source IN ({string.Join(", ", names)})");
        }

        if (filter.HasLanguage)
        {
            command.Parameters.AddWithValue("@olang", filter.Language);
            clauses.Add("language = @olang");
        }

        return " AND " + string.Join(" AND ", clauses);
    }

    /// <summary>把覆盖层的正文取出来（详情用）。</summary>
    public Task<string> LoadOverlayTextAsync(string id, CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = SqliteAccess.Open(DatabasePath);
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT text FROM web_pages WHERE id = @id AND overlay = 1 LIMIT 1";
            command.Parameters.AddWithValue("@id", id);
            return command.ExecuteScalar() as string ?? string.Empty;
        }, ct);
}
