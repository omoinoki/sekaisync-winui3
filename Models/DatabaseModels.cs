using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace SekaiSync.Desktop.Models;

/// <summary>数据库中的一列（来自 PRAGMA table_info）。</summary>
public sealed record DbColumn(string Name, string DeclaredType, bool IsPrimaryKey);

/// <summary>一张可浏览的数据表的静态描述。</summary>
public sealed class DbTableInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<DbColumn> Columns { get; init; } = [];
    public IReadOnlyList<string> SortColumns { get; init; } = [];
    public IReadOnlyList<string> SearchColumns { get; init; } = [];
    public long RowCount { get; set; }

    /// <summary>导航/下拉框中显示的标签。</summary>
    public string Label => string.IsNullOrEmpty(RowCountLabel) ? DisplayName : $"{DisplayName}（{RowCountLabel}）";

    public string RowCountLabel => RowCount.ToString("N0");
}

/// <summary>一页查询结果：列名、每行单元格文本、每行主键值（用于打开详情）。</summary>
public sealed class DbQueryResult
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<string[]> Cells { get; init; }
    public required IReadOnlyList<string[]> Keys { get; init; }
    public required int Offset { get; init; }
    public required bool HasMore { get; init; }
    public required bool IsFiltered { get; init; }
    public required long ElapsedMilliseconds { get; init; }
}

/// <summary>详情面板中的一个字段。</summary>
public sealed record DbDetailField(string Name, string Value, bool IsJson)
{
    /// <summary>JSON 徽标可见性（供 x:Bind 直接使用）。</summary>
    public Visibility JsonBadge => IsJson ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>数据网格的列规格（标题 + 像素宽度），用于生成表头与行模板。</summary>
public sealed record DbGridColumn(string Title, double Width);

/// <summary>行数据项，Cells 与列一一对应。</summary>
public sealed class DbRowItem
{
    public required string[] Cells { get; init; }
    public required string[] Keys { get; init; }

    /// <summary>
    /// 行的稳定标识（主键值拼接）。重载后按它恢复选中，
    /// 而不是按对象引用比较——引用每次查询都会重建（实施契约 §3「返回列表要保住选中」）。
    /// </summary>
    public string IdentityKey => Keys.Length == 0
        ? string.Empty
        : string.Join("\u241F", Keys);
}

/// <summary>术语证据（term_evidence）条目。</summary>
public sealed record GlossaryEvidence(int Index, string StoryKey, string Language, string Sentence);
