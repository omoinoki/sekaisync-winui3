using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// store/kb/sekaisync.db 的只读访问层：表目录、分页浏览、LIKE 搜索与行详情。
/// 每次操作独立打开连接，避免与其他会话（爬虫/CLI）产生锁交互。
/// </summary>
public sealed class DatabaseService
{
    private const int CellPreviewLength = 240;

    // 0.4 起上游新增的表排在相邻位置：region facts 属于实体层，term_slots 属于用语层，
    // review_* 是 agent_review（W4）搬进 SQLite 后的权威存储。
    private static readonly string[] TableOrder =
    [
        "entities", "entity_region_facts", "glossary_terms", "terms", "term_slots",
        "term_evidence", "review_queue", "review_rules", "review_decisions", "web_pages", "meta",
    ];

    private static readonly Dictionary<string, (string Display, string Description, string[] Sort, string[] Search)> Catalog = new()
    {
        ["entities"] = ("实体事实层", "角色 / 卡牌 / 活动 / 区域物品等 master 实体与多语名称", ["id"], ["id", "names_json", "facts_json"]),
        ["entity_region_facts"] = ("实体区域事实", "0.4 起按服拆分的实体事实与出处（per-region facts + provenance，v3 schema）", ["entity_id", "region"], ["entity_id", "region", "facts_json", "retrieval_json"]),
        ["glossary_terms"] = ("术语总表", "官方 + 社区术语的跨语对照（registry / glossary 导入）", ["id"], ["canonical", "names_json"]),
        ["terms"] = ("用语提取", "从正文提取的用语、词频与翻译记忆", ["id"], ["canonical", "names_json"]),
        ["term_slots"] = ("用语槽位", "跨语译名的权威槽位（accepted / pending / conflict / rejected，slot-authoritative 写入的落点）", ["term_id", "language"], ["term_id", "language", "value", "reason"]),
        ["term_evidence"] = ("术语证据", "用语的句级出处（术语 → 原文句子）", ["term_id", "idx"], ["term", "sentence", "story_key"]),
        ["review_queue"] = ("复核队列", "agent 复核任务的待办与状态（W4 起权威存 SQLite）", ["item_id"], ["term_id", "language", "status"]),
        ["review_rules"] = ("复核规则", "复核方法论的规则积累（family + key + scope，带 revision）", ["rule_id"], ["rule_id", "family", "key"]),
        ["review_decisions"] = ("复核决定", "agent 对复核项的一次性决定及其证据快照", ["decision_id"], ["item_id", "term_id", "language", "action"]),
        ["web_pages"] = ("正文索引", "五服剧情 / 官设 / 辅助页等网页正文全文", ["source", "id"], ["title", "url", "text"]),
        ["meta"] = ("元数据", "知识库导入状态等内部标记（含 active_news_generation 指针）", ["key"], ["key", "value"]),
    };

    private readonly AppEnvironment _environment;
    private readonly List<DbTableInfo> _tables = [];

    public DatabaseService(AppEnvironment environment) => _environment = environment;

    public string DatabasePath => _environment.DatabasePath;

    public bool IsAvailable { get; private set; }

    public string UnavailableReason { get; private set; } = "尚未加载。";

    public IReadOnlyList<DbTableInfo> Tables => _tables;

    /// <summary>读取表结构并统计行数（不含数据行）。</summary>
    public Task InitializeAsync() => Task.Run(() =>
    {
        _tables.Clear();
        IsAvailable = false;

        if (string.IsNullOrEmpty(DatabasePath))
        {
            UnavailableReason = "未找到 store 目录：请先初始化或同步数据，或在“设置”中指定 store 路径。";
            return;
        }

        if (!System.IO.File.Exists(DatabasePath))
        {
            UnavailableReason = $"数据库不存在：{DatabasePath}";
            return;
        }

        try
        {
            using var conn = OpenConnection();
            ReadTableCatalog(conn);
            IsAvailable = true;
            UnavailableReason = string.Empty;
        }
        catch (DatabaseUnreachableException ex)
        {
            // 只读打开失败：审计 R-1 的界面半边——不得退回读写把失败伪装成成功。
            IsAvailable = false;
            UnavailableReason = ex.Message;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"读取表结构失败（{DiagnosticText.Redact(ex.Message)}）。若爬虫正在写入，请稍后重试。";
        }
    });

    /// <summary>枚举表、列与行数（连接已由调用方以只读方式打开）。</summary>
    private void ReadTableCatalog(SqliteConnection conn)
    {
        var names = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            var columns = ReadColumns(conn, name);
            var (display, description, sort, search) = Catalog.TryGetValue(name, out var meta)
                ? meta
                : (name, "数据表", Array.Empty<string>(), columns.Select(c => c.Name).ToArray());
            var sorts = sort.Length > 0 ? sort : columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToArray();
            var counts = search.Length > 0 ? search : columns.Select(c => c.Name).ToArray();

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {Quote(name)}";
            var count = Convert.ToInt64(countCmd.ExecuteScalar());

            _tables.Add(new DbTableInfo
            {
                Name = name,
                DisplayName = display,
                Description = description,
                Columns = columns,
                SortColumns = sorts,
                SearchColumns = counts,
                RowCount = count,
            });
        }

        _tables.Sort((a, b) =>
        {
            var ia = Array.IndexOf(TableOrder, a.Name);
            var ib = Array.IndexOf(TableOrder, b.Name);
            return (ia, ib) switch
            {
                (>= 0, >= 0) => ia.CompareTo(ib),
                (>= 0, _) => -1,
                (_, >= 0) => 1,
                _ => string.CompareOrdinal(a.Name, b.Name),
            };
        });
    }

    /// <summary>分页读取一个表；search 非空时在预设列上做 LIKE 过滤。</summary>
    public Task<DbQueryResult> QueryPageAsync(DbTableInfo table, string search, int offset, int limit, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var conn = OpenConnection();
            cancellationToken.ThrowIfCancellationRequested();

            var columnList = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));
            var sql = new StringBuilder($"SELECT {columnList} FROM {Quote(table.Name)}");

            var parameters = new List<(string Name, object Value)>();
            var isFiltered = !string.IsNullOrWhiteSpace(search);
            if (isFiltered)
            {
                var pattern = $"%{EscapeLike(search.Trim())}%";
                var predicates = table.SearchColumns
                    .Select((c, i) => $"{Quote(c)} LIKE @q{i} ESCAPE '\\'")
                    .ToList();
                sql.Append(" WHERE ").Append(string.Join(" OR ", predicates));
                for (var i = 0; i < table.SearchColumns.Count; i++)
                {
                    parameters.Add(($"@q{i}", pattern));
                }
            }

            var orderColumns = table.SortColumns.Count > 0
                ? table.SortColumns
                : table.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToArray();
            var orderBy = orderColumns.Count > 0
                ? string.Join(", ", orderColumns.Select(Quote))
                : "rowid";
            sql.Append($" ORDER BY {orderBy} LIMIT @limit OFFSET @offset");
            // 多取一行来判断「还有下一页」：原先 limit 行取满就算 HasMore，
            // 行数正好是每页整数倍时「下一页」可点、点下去返回空（审计 D-8）。
            parameters.Add(("@limit", (object)(limit + 1)));
            parameters.Add(("@offset", (object)offset));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var stopwatch = Stopwatch.StartNew();
            var cells = new List<string[]>();
            var keys = new List<string[]>();
            var pkIndexes = table.Columns
                .Select((c, i) => (c, i))
                .Where(pair => pair.c.IsPrimaryKey)
                .Select(pair => pair.i)
                .ToArray();

            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var row = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[i] = FormatPreview(reader.IsDBNull(i) ? null : reader.GetValue(i));
                    }
                    cells.Add(row);
                    keys.Add(pkIndexes.Select(i => row[i]).ToArray());
                    if (cells.Count > limit)
                    {
                        break;
                    }
                }
            }

            stopwatch.Stop();
            var hasMore = cells.Count > limit;
            if (hasMore)
            {
                // 丢掉那行只为探测「还有下一页」而多取的哨兵行。
                cells.RemoveAt(cells.Count - 1);
                keys.RemoveAt(keys.Count - 1);
            }

            return new DbQueryResult
            {
                Columns = table.Columns.Select(c => c.Name).ToArray(),
                Cells = cells,
                Keys = keys,
                Offset = offset,
                HasMore = hasMore,
                IsFiltered = isFiltered,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }, cancellationToken);

    /// <summary>按主键取一行完整数据，用于详情面板。</summary>
    public Task<DbDetailField[]> GetRowDetailAsync(DbTableInfo table, string[] keys)
        => Task.Run(() =>
        {
            using var conn = OpenConnection();

            var pkColumns = table.Columns.Where(c => c.IsPrimaryKey).ToArray();
            var where = string.Join(" AND ", pkColumns.Select((c, i) => $"{Quote(c.Name)} = @k{i}"));
            if (pkColumns.Length == 0 || pkColumns.Length != keys.Length)
            {
                // 无主键表退化为顺序定位：按排序键 OFFSET 取。
                return DetailByPosition(conn, table, keys);
            }

            var columnList = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {columnList} FROM {Quote(table.Name)} WHERE {where}";
            for (var i = 0; i < pkColumns.Length; i++)
            {
                cmd.Parameters.AddWithValue($"@k{i}", keys[i]);
            }

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return DetailByPosition(conn, table, keys);
            }

            return EnumerateFields(table, reader);
        });

    /// <summary>取某条用语的句级证据（term_evidence），最多 50 条。</summary>
    public Task<List<GlossaryEvidence>> GetEvidenceAsync(string termId)
        => Task.Run(() =>
        {
            var list = new List<GlossaryEvidence>();
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT idx, story_key, language, sentence FROM term_evidence WHERE term_id = @id ORDER BY idx LIMIT 50";
                cmd.Parameters.AddWithValue("@id", termId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new GlossaryEvidence(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)));
                }
            }
            catch (Exception)
            {
                // 证据查询失败不阻塞术语浏览。
            }
            return list;
        });

    private static DbDetailField[] DetailByPosition(SqliteConnection conn, DbTableInfo table, string[] keys)
    {
        var orderColumns = table.SortColumns.Count > 0 ? table.SortColumns : ["rowid"];
        var orderBy = string.Join(", ", orderColumns.Select(Quote));
        var columnList = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {columnList} FROM {Quote(table.Name)} ORDER BY {orderBy} LIMIT 1 OFFSET @offset";
        cmd.Parameters.AddWithValue("@offset", ParseOffset(keys));

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? EnumerateFields(table, reader) : [new DbDetailField("(提示)", "未能定位该行。", false)];
    }

    private static int ParseOffset(string[] keys)
        => keys.Length > 0 && int.TryParse(keys[^1], out var value) ? Math.Max(0, value) : 0;

    private static DbDetailField[] EnumerateFields(DbTableInfo table, SqliteDataReader reader)
    {
        var fields = new DbDetailField[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
            var text = raw is null
                ? string.Empty
                : (raw as string) ?? (raw.ToString() ?? string.Empty);
            var isJson = name.EndsWith("_json", StringComparison.Ordinal);
            if (isJson)
            {
                text = PrettyJson(text) ?? text;
            }
            fields[i] = new DbDetailField(name, text, isJson);
        }
        return fields;
    }

    /// <summary>
    /// 连接口径与全站一致：只读，失败不退回读写（见 <see cref="SqliteAccess.Open"/>）。
    /// 失败统一包装成 <see cref="DatabaseUnreachableException"/>，让界面能说「以只读方式打开失败：<原因>」
    /// 而不是把它混进「查询失败」——只读是对外承诺，伪装成读得出问题是掩盖它。
    /// </summary>
    private SqliteConnection OpenConnection()
    {
        try
        {
            return SqliteAccess.Open(DatabasePath);
        }
        catch (Exception ex)
        {
            throw new DatabaseUnreachableException(
                $"以只读方式打开失败：{DiagnosticText.Redact(ex.Message)}。" +
                "本程序不会改用写模式重试；若爬虫正在写入，请等它结束后再刷新。");
        }
    }

    private static List<DbColumn> ReadColumns(SqliteConnection conn, string table)
    {
        var columns = new List<DbColumn>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var type = reader.GetString(2);
            var pk = reader.GetInt32(5) > 0;
            columns.Add(new DbColumn(name, type, pk));
        }
        return columns;
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string FormatPreview(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            string s => s,
            IFormattable f => f.ToString() ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
        if (text.Length > CellPreviewLength)
        {
            text = text[..CellPreviewLength] + "…";
        }
        return text;
    }

    internal static string? PrettyJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// 「以只读方式打开本地知识库」这一步失败。与 SQL 语法/查询错误区分开，
/// 界面才能照实说：只读承诺成立，是这次没能以只读方式读到文件（审计 R-1 的文案半边）。
/// </summary>
public sealed class DatabaseUnreachableException : Exception
{
    public DatabaseUnreachableException(string message)
        : base(message)
    {
    }
}
