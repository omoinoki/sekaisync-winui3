using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// sekaisync.db 的只读连接与 SQL 片段工具。每次操作独立开连接，
/// 避免与其他会话（爬虫 / CLI）产生锁交互。
/// </summary>
public static class SqliteAccess
{
    /// <summary>
    /// 以只读方式打开 sekaisync.db。只读失败时**不再退回 ReadWrite**：
    /// 界面承诺「前端只读访问本地知识库」，以写意图打开会参与写锁竞争
    /// （可能与正在运行的爬虫互相干扰），也让那句承诺失去代码依据（手册 §1.1）。
    /// 失败原样抛出，由调用方走「不可用 + 可行等待」的文案分支。
    /// </summary>
    public static SqliteConnection Open(string databasePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            // 只读连接默认不等待写锁，与爬虫并发时会立刻 SQLITE_BUSY，
            // 在界面上长成一句看不出所以然的读取失败。让 SQLite 自己退避等待。
            using var busy = connection.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA query_only = ON; PRAGMA trusted_schema = OFF;";
            busy.ExecuteNonQuery();
            // Cancel() is a no-op in Microsoft.Data.Sqlite. A progress callback also
            // catches cancellation that arrives before sqlite3_step starts.
            if (ct.CanBeCanceled)
            {
                SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000,
                    state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0, ct);
            }
            ct.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Double-quoted identifiers support embedded quotes and closing brackets.</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Translate SQLite interruption to the normal cancelled-query path.</summary>
    public static Task<T> Run<T>(Func<T> query, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = query();
                ct.ThrowIfCancellationRequested();
                return result;
            }
            catch (SqliteException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }, ct);

    public static void ValidatePage(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be between 1 and 1000.");
        }
    }

    public static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>生成 `@p0, @p1, ...` 形式的占位符串。</summary>
    public static string Placeholders(IReadOnlyList<string> names, string prefix)
    {
        if (names.Count == 0)
        {
            return "NULL";
        }
        var parts = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            parts[i] = prefix + i;
        }
        return string.Join(", ", parts);
    }

    /// <summary>把字符串列表按 @前缀0..n 绑到命令上。</summary>
    public static void BindList(SqliteCommand command, IReadOnlyList<string> values, string prefix)
    {
        for (var i = 0; i < values.Count; i++)
        {
            command.Parameters.AddWithValue(prefix + i, values[i]);
        }
    }
}

/// <summary>一页查询结果。</summary>
public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Offset { get; init; }
    public required bool HasMore { get; init; }
    public required long ElapsedMilliseconds { get; init; }

    public static PagedResult<T> Empty { get; } = new()
    {
        Items = [],
        Offset = 0,
        HasMore = false,
        ElapsedMilliseconds = 0,
    };
}
