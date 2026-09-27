using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 正文渲染的公共逻辑：对白拆分、names_json 主名回退链、facts_json 多语识别。
///
/// 两套对白规则来自实测：
///   overlay = 0（原始抓取）→「角色名：台词」，续行无冒号，归到上一条。
///   overlay = 1（译文覆盖层）→ 说话人单独成行、没有冒号，续行才是台词。
/// </summary>
public static class TextRenderer
{
    /// <summary>从右往左找续行时用的上限，避免把长文误判成说话人。</summary>
    private const int MaxSpeakerLength = 16;

    private static readonly char[] SpeakerSeparators = ['：', ':'];

    /// <summary>按 kind / overlay 选择规则拆对白。</summary>
    public static IReadOnlyList<DialogueBlock> ParseDialogue(string text, bool overlay) =>
        overlay ? ParseOverlayDialogue(text) : ParsePrefixedDialogue(text);

    /// <summary>overlay = 0：「角色名：台词」。续行（无冒号）追加到上一条。</summary>
    private static List<DialogueBlock> ParsePrefixedDialogue(string text)
    {
        var blocks = new List<DialogueBlock>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return blocks;
        }

        var speaker = string.Empty;
        var buffer = new StringBuilder();

        void Flush()
        {
            var body = buffer.ToString().TrimEnd();
            if (body.Length > 0 || speaker.Length > 0)
            {
                blocks.Add(new DialogueBlock(speaker, body, speaker.Length == 0));
            }
            buffer.Clear();
        }

        foreach (var raw in SplitLines(text))
        {
            if (TrySplitSpeaker(raw, out var name, out var rest))
            {
                Flush();
                speaker = name;
                buffer.Append(rest);
            }
            else
            {
                // 续行：接到上一条台词后面。
                if (buffer.Length > 0)
                {
                    buffer.Append('\n');
                }
                buffer.Append(raw);
            }
        }

        Flush();
        return blocks;
    }

    /// <summary>overlay = 1：说话人单独成行。整行没有任何标点、且足够短，才算说话人。</summary>
    private static List<DialogueBlock> ParseOverlayDialogue(string text)
    {
        var blocks = new List<DialogueBlock>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return blocks;
        }

        var speaker = string.Empty;
        var buffer = new StringBuilder();

        void Flush()
        {
            var body = buffer.ToString().TrimEnd();
            if (body.Length > 0)
            {
                blocks.Add(new DialogueBlock(speaker, body, speaker.Length == 0));
            }
            buffer.Clear();
        }

        foreach (var raw in SplitLines(text))
        {
            if (IsStandaloneSpeaker(raw))
            {
                Flush();
                speaker = raw.Trim();
                continue;
            }

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }
            buffer.Append(raw);
        }

        Flush();
        return blocks;
    }

    /// <summary>整行无冒号、无括号、长度短且不以句读结尾 → 视作说话人标记行。</summary>
    private static bool IsStandaloneSpeaker(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is 0 or > MaxSpeakerLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (c is '：' or ':' or '（' or '）' or '(' or ')' or '「' or '」' or '，' or ',' or '。' or '、' or '…' or '！' or '？' or '!' or '?')
            {
                return false;
            }
        }

        // 纯符号（如 ………）不算说话人。
        return trimmed.Any(char.IsLetter);
    }

    /// <summary>把一行拆成「说话人 + 台词」。不是「名字：」形态时返回 false。</summary>
    private static bool TrySplitSpeaker(string line, out string speaker, out string rest)
    {
        speaker = string.Empty;
        rest = string.Empty;

        var trimmed = line.TrimStart();
        var index = trimmed.IndexOfAny(SpeakerSeparators);
        if (index <= 0 || index > MaxSpeakerLength)
        {
            return false;
        }

        var name = trimmed[..index].Trim();
        if (name.Length == 0 || !IsPlausibleSpeaker(name))
        {
            return false;
        }

        speaker = name;
        rest = trimmed[(index + 1)..];
        return true;
    }

    private static bool IsPlausibleSpeaker(string name)
    {
        foreach (var c in name)
        {
            // http:// 这类会被误判，排除掉典型的 URL 字符。
            if (c is '/' or '\\' or '.' or '@' or '(' or ')' or '（' or '）' or '「' or '」')
            {
                return false;
            }
        }
        return name.Any(char.IsLetter);
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ─────────────────────────────────────────────────────────────────────────
    // names_json
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>五语顺序与标签。</summary>
    private static readonly (string Key, string Label)[] LanguageKeys =
    [
        ("ja", "日本語"),
        ("en", "English"),
        ("zh_hans", "简体中文"),
        ("zh_hant", "繁體中文"),
        ("ko", "한국어"),
    ];

    /// <summary>详情表里缺语言的单元格文案（手册 §5.2「缺译」/ §6）。唯一来源见 <see cref="NameCellText"/>。</summary>
    public const string MissingNameText = NameCellText.Missing;

    /// <summary>
    /// 列表里的缺译单元格。整表几百行时不能每格写整句，
    /// 图例常驻在表格上方（TermsPage 的「无 ≠ 不存在」说明行）。
    /// </summary>
    public const string MissingCellText = NameCellText.MissingCell;

    /// <summary>names_json 读不出来时列表单元格的文案。与「暂无」必须是两个词。</summary>
    public const string UnparsableCellText = NameCellText.UnparsableCell;

    /// <summary>详情表里解析失败那一行的文案：说清原因与下一步，不下「该资料不存在」的结论。</summary>
    public const string UnparsableNameText =
        "names_json 无法解析为多语对象，五语对照未呈现。原始值可在「数据源 → 原始表」按这条的 id 查看。";

    /// <summary>详情表里字段为空那一行的文案。</summary>
    public const string EmptyNameText =
        "本地未记录这条的多语名称；这不代表它没有其它语言的叫法。";

    /// <summary>
    /// names_json 的三种形态。把「没有值」与「读不出」分开，
    /// 界面才不会把一次读取失败说成「库里没有」（手册 §6 unknown 行）。
    /// </summary>
    public enum NameJsonKind
    {
        /// <summary>是一个 JSON 对象（值可能缺）。</summary>
        Object,

        /// <summary>字段为空、NULL，或对象里一个非空值都没有。</summary>
        Empty,

        /// <summary>不是 JSON 对象，或根本不是合法 JSON。</summary>
        Unparsable,
    }

    /// <summary>
    /// 一条 names_json 的五语单元格。列表取 <see cref="Cell"/>、详情取 <see cref="ToRows"/>，
    /// 两边共用同一次解析判定，不再各写一套「吞掉异常返回空」的逻辑。
    /// </summary>
    public sealed class NameCells
    {
        internal NameCells(NameJsonKind kind, Dictionary<string, string> values)
        {
            Kind = kind;
            Values = values;
        }

        /// <summary>解析形态。</summary>
        public NameJsonKind Kind { get; }

        /// <summary>摊平后的原始键值（非对象时为空字典）。</summary>
        public IReadOnlyDictionary<string, string> Values { get; }

        /// <summary>该语言有值时给值；缺语言给「暂无」；整个字段读不出给「无法解析」。</summary>
        public string Cell(string languageKey) => Kind switch
        {
            NameJsonKind.Object => RawValue(languageKey) is { Length: > 0 } value ? value : MissingCellText,
            NameJsonKind.Empty => MissingCellText,
            _ => UnparsableCellText,
        };

        private string RawValue(string languageKey) =>
            Values.TryGetValue(languageKey, out var value) ? value.Trim() : string.Empty;

        /// <summary>
        /// 五语对照表：缺失的语言照样出一行并标「暂无该语言记录」（界面按 IsMissing 上状态色）；
        /// 字段读不出时只出一行说明，而不是五格空白。
        /// </summary>
        public IReadOnlyList<LabelValue> ToRows()
        {
            if (Kind != NameJsonKind.Object)
            {
                return [new LabelValue("五语对照", Kind == NameJsonKind.Empty ? EmptyNameText : UnparsableNameText, true)];
            }

            var rows = new List<LabelValue>();
            var consumed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (key, label) in LanguageKeys)
            {
                consumed.Add(key);
                var value = RawValue(key);
                rows.Add(value.Length == 0
                    ? new LabelValue(label, MissingNameText, true)
                    : new LabelValue(label, value));
            }

            // names_json 里可能有语言之外的字段（firstName / givenName 之类），一并列出来。
            foreach (var pair in Values)
            {
                if (consumed.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }
                rows.Add(new LabelValue(pair.Key, pair.Value.Trim()));
            }

            return rows;
        }
    }

    /// <summary>
    /// 解析 names_json。这是五语对照与主名回退链共用的唯一入口：
    /// 空 / 非对象 / 非法 JSON 三种情况在这里分开，调用方不再自行「catch 掉当空值」。
    /// </summary>
    public static NameCells ParseNames(string? namesJson)
    {
        if (string.IsNullOrWhiteSpace(namesJson))
        {
            return new NameCells(NameJsonKind.Empty, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        try
        {
            using var document = JsonDocument.Parse(namesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Unparsable();
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = FormatValue(property.Value);
            }

            // 一个非空值都没有：这不是「五语全缺」，而是这条压根没记名称。
            return values.Values.Any(v => !string.IsNullOrWhiteSpace(v))
                ? new NameCells(NameJsonKind.Object, values)
                : new NameCells(NameJsonKind.Empty, values);
        }
        catch (Exception)
        {
            return Unparsable();
        }

        static NameCells Unparsable() =>
            new(NameJsonKind.Unparsable, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// names_json 的 key 不统一（name / title / full / unitName 都是主名），
    /// 回退链：name → title → full → unitName → zh_hans → ja → id。
    /// </summary>
    public static string PrimaryName(string namesJson, string fallback)
    {
        var values = ParseObject(namesJson);
        if (values is null)
        {
            return fallback;
        }

        foreach (var key in new[] { "name", "title", "full", "unitName", "zh_hans", "ja" })
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        // 兜底：拿第一个非空字符串值。
        foreach (var value in values.Values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallback;
    }

    /// <summary>
    /// 把 names_json 摊成五语对照表。签名不变（Story / Entities 侧照旧调用），
    /// 行为改成：缺失语言给「暂无该语言记录」并带 IsMissing，字段读不出只出一行说明。
    /// </summary>
    public static IReadOnlyList<LabelValue> NameTable(string namesJson) => ParseNames(namesJson).ToRows();

    // ─────────────────────────────────────────────────────────────────────────
    // facts_json
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// facts_json 的多语字段一律 {key}_{lang}。扁平化后按键分组，
    /// 同一 base key 有多个语言时渲染成「base · 语言」多行。
    /// </summary>
    public static IReadOnlyList<LabelValue> FactTable(string factsJson)
    {
        var values = ParseObject(factsJson) ?? [];
        if (values.Count == 0)
        {
            return [];
        }

        var groups = new Dictionary<string, List<(string Language, string Value)>>(StringComparer.Ordinal);
        var order = new List<string>();
        var plain = new List<LabelValue>();

        foreach (var pair in values)
        {
            if (TrySplitLanguageSuffix(pair.Key, out var baseKey, out var language))
            {
                if (!groups.TryGetValue(baseKey, out var bucket))
                {
                    bucket = [];
                    groups[baseKey] = bucket;
                    order.Add(baseKey);
                }
                bucket.Add((language, pair.Value));
            }
            else
            {
                plain.Add(new LabelValue(pair.Key, pair.Value, pair.Value.Length == 0));
            }
        }

        var rows = new List<LabelValue>();
        foreach (var baseKey in order)
        {
            var bucket = groups[baseKey];
            if (bucket.Count == 1)
            {
                rows.Add(new LabelValue(baseKey, bucket[0].Value, bucket[0].Value.Length == 0));
                continue;
            }

            foreach (var (language, value) in bucket.OrderBy(b => LanguageOrder(b.Language)))
            {
                rows.Add(new LabelValue($"{baseKey} · {LanguageLabel(language)}", value, value.Length == 0));
            }
        }

        rows.AddRange(plain);
        return rows;
    }

    private static bool TrySplitLanguageSuffix(string key, out string baseKey, out string language)
    {
        baseKey = key;
        language = string.Empty;

        var index = key.LastIndexOf('_');
        while (index > 0)
        {
            var suffix = key[(index + 1)..];
            if (LanguageLabel(suffix).Length > 0)
            {
                baseKey = key[..index];
                language = suffix;
                return true;
            }
            index = key.LastIndexOf('_', index - 1);
        }

        return false;
    }

    private static int LanguageOrder(string language) => language switch
    {
        "ja" => 0,
        "en" => 1,
        "zh_hans" => 2,
        "zh_hant" => 3,
        "ko" => 4,
        _ => 9,
    };

    private static string LanguageLabel(string language) => language switch
    {
        "ja" => "日本語",
        "en" => "English",
        "zh_hans" => "简体中文",
        "zh_hant" => "繁體中文",
        "ko" => "한국어",
        _ => string.Empty,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // JSON 辅助
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>把 JSON 对象摊平成 string→string；非对象返回 null。</summary>
    private static Dictionary<string, string>? ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = FormatValue(property.Value);
            }
            return map;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Number => element.ToString(),
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        _ => element.ToString(),
    };

    /// <summary>把「角色名：台词」正文里出现的说话人收集出来（台词页的下钻要按角色分组）。</summary>
    public static string FirstSpeaker(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (var raw in SplitLines(text).Take(4))
        {
            if (TrySplitSpeaker(raw, out var name, out _))
            {
                return name;
            }
            if (IsStandaloneSpeaker(raw))
            {
                return raw.Trim();
            }
        }

        return string.Empty;
    }
}
