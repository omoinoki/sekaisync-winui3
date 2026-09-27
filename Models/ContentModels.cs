using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace SekaiSync.Desktop.Models;

/// <summary>
/// 多语字段缺格/读不出时的固定文案。放在 Models 而不是 TextRenderer，
/// 是因为「缺译怎么措辞」属于呈现契约，而且 <see cref="TermRow"/> 这类模型
/// 要按它判断单元格是「有值」还是「占位」——让 Models 反向依赖 Services 是错的方向。
/// </summary>
public static class NameCellText
{
    /// <summary>详情表里缺语言的单元格文案（手册 §5.2「缺译」/ §6）。</summary>
    public const string Missing = "暂无该语言记录";

    /// <summary>
    /// 列表里的缺译单元格。整表几百行时不能每格写整句，
    /// 图例常驻在表格上方（TermsPage 的「无 ≠ 不存在」说明行）。
    /// </summary>
    public const string MissingCell = "暂无";

    /// <summary>names_json 读不出来时列表单元格的文案。与「暂无」必须是两个词。</summary>
    public const string UnparsableCell = "无法解析";

    /// <summary>
    /// 读屏行名的一个语言段：只收「真有译文」的值。
    /// 缺格是 <see cref="MissingCell"/>、读不出是 <see cref="UnparsableCell"/>、
    /// 与词条主名重复的一律跳过——读屏是一次性听，重复与占位都只是在浪费听众的时间。
    /// </summary>
    internal static void AppendLanguage(List<string> parts, string languageLabel, string cell, string head)
    {
        var value = cell.Trim();
        if (value.Length == 0
            || value == MissingCell
            || value == UnparsableCell
            || value == head)
        {
            return;
        }

        parts.Add($"{languageLabel} {value}");
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// 通用：五语名称/事实的一行（label + value），实体与术语详情共用。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>「标签 + 值」的一行，用于多语对照表与键值表。</summary>
public sealed class LabelValue
{
    public LabelValue(string label, string value, bool isMissing = false)
    {
        Label = label;
        Value = value;
        IsMissing = isMissing;
    }

    public string Label { get; }

    public string Value { get; }

    /// <summary>缺失格（用于标红提示）。</summary>
    public bool IsMissing { get; }

    public Visibility MissingVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>缺失时让占位文案独占该格，值文本不再同位重影。</summary>
    public Visibility ValueVisibility => IsMissing ? Visibility.Collapsed : Visibility.Visible;
}

// ─────────────────────────────────────────────────────────────────────────────
// 一、资讯
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一条公告。数据来自 store/kb/news/{language}.json，与 web_pages 无关。</summary>
public sealed class NewsItem
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Language { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string Url { get; init; }
    public required string PublishedAt { get; init; }
    public required string StartAt { get; init; }
    public required string EndAt { get; init; }

    /// <summary>实例徽章 SV / MS。</summary>
    public required string InstanceBadge { get; init; }

    /// <summary>2026-09-05 这样的日期。</summary>
    public required string PublishedLabel { get; init; }

    /// <summary>「进行中 至 09-30」/「已结束」/「长期有效」。</summary>
    public required string PeriodLabel { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>爬虫已抓到原文（text 有完整可见文本）；否则详情走 WebView2 打开原链接。</summary>
    public required bool BodyAvailable { get; init; }

    /// <summary>internal = 游戏内 webview 页面；external = 官网/社交等外链。</summary>
    public required string BrowseType { get; init; }

    /// <summary>资讯分类原始值（information_tag：event/gacha/music/…）。</summary>
    public required string Category { get; init; }

    /// <summary>资讯分类的中文展示名（NewsService.CategoryDisplay 映射）。</summary>
    public required string CategoryLabel { get; init; }

    /// <summary>列表里的正文摘要。</summary>
    public required string Summary { get; init; }

    /// <summary>原链接可导航（http/https）——与有没有抓到原文无关。</summary>
    public bool HasWebUrl => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>「没抓到原文，只能看网页」——默认进网页模式的判定。</summary>
    public bool CanBrowse => !BodyAvailable && HasWebUrl;
}

/// <summary>资讯页的语言档位。当前只有 ja / zh_hans 有文件。</summary>
public sealed class NewsLanguageOption
{
    public NewsLanguageOption(string language, string label, bool available, string hint)
    {
        Language = language;
        Label = label;
        Available = available;
        Hint = hint;
    }

    public string Language { get; }

    public string Label { get; }

    public bool Available { get; }

    /// <summary>不可用时显示的说明。</summary>
    public string Hint { get; }

    public override string ToString() => Label;
}

// ─────────────────────────────────────────────────────────────────────────────
// 二、剧情 / 台词
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 列表行「抓取」列的缺失口径（§6：时间缺失写「未测量」，不留空、不填当前时间）。
/// </summary>
public static class CoverageLabels
{
    /// <summary>crawled_at 缺失时统一显示的标签。</summary>
    public const string Unmeasured = "未测量";

    /// <summary>把原始 crawled_at 转成列表用的日期标签；空值 → 「未测量」。</summary>
    public static string Crawled(string crawledAt) => crawledAt.Length switch
    {
        0 => Unmeasured,
        >= 10 => crawledAt[..10],
        _ => crawledAt,
    };
}

/// <summary>剧情列表的一行。</summary>
public sealed class StoryRow
{
    public required string Source { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Language { get; init; }
    public required string Url { get; init; }
    public required string CrawledAt { get; init; }

    public required string KindDisplay { get; init; }
    public required string RegionLabel { get; init; }
    public required string InstanceBadge { get; init; }
    public required string CrawledLabel { get; init; }
    public required long Length { get; init; }

    /// <summary>该行是未译占位。</summary>
    public required bool Untranslated { get; init; }

    /// <summary>crawled_at 有值（false = 「未测量」，与「抓取于 空」区分开）。</summary>
    public bool HasCrawledAt => CrawledAt.Length > 0;

    /// <summary>译文状态说明（trust = C 时才有内容）。</summary>
    public required string TranslationNote { get; init; }

    /// <summary>
    /// 译文列的文字：每行都有内容，「仅未译」勾选后能逐行核对筛选是否生效（S-7）。
    /// 空 TranslationNote 不再渲染成空白格——那与「没有状态标注」无法区分。
    /// </summary>
    public string TranslationLabel => Untranslated ? "未译" : TranslationNote.Length > 0 ? TranslationNote : "无标注";

    /// <summary>对齐键 {kind}:{asset}，跨实例/跨服并排靠它。</summary>
    public required string AlignmentKey { get; init; }

    /// <summary>读屏行名：裸 TextBlock 会被连读成一串（S-15）。</summary>
    public string RowAccessibleName =>
        $"{Title}，{KindDisplay}，{InstanceBadge}，{RegionLabel}，译文 {TranslationLabel}，抓取 {CrawledLabel}";
}

/// <summary>对白块：说话人 + 台词。旁白没有说话人。</summary>
public sealed class DialogueBlock
{
    public DialogueBlock(string speaker, string text, bool isNarration)
    {
        Speaker = speaker;
        Text = text;
        IsNarration = isNarration;
    }

    public string Speaker { get; }

    public string Text { get; }

    /// <summary>无冒号行按旁白处理。</summary>
    public bool IsNarration { get; }

    public Visibility SpeakerVisibility =>
        string.IsNullOrEmpty(Speaker) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 旁白（无冒号行）单独样式（doc §4.2，S-10）：斜体 + 左缩进。
    /// 两个互斥 Visibility 而不是颜色——WinUI 3 没有 DataTrigger，颜色也不能当唯一通道。
    /// </summary>
    public Visibility NarrationVisibility => IsNarration ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>有说话人的普通台词行。</summary>
    public Visibility SpeechVisibility => IsNarration ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>剧情详情。</summary>
public sealed class StoryDetail
{
    /// <summary>稳定 id（web:altsource_sv:jp:card_story:...），可复制。</summary>
    public required string Id { get; init; }

    /// <summary>原始 source 值（实例判定用）。</summary>
    public required string Source { get; init; }

    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string InstanceBadge { get; init; }
    public required string Language { get; init; }
    public required string RegionLabel { get; init; }
    public required string KindDisplay { get; init; }
    public required string CrawledAt { get; init; }
    public required string AlignmentKey { get; init; }
    public required string TranslationNote { get; init; }
    public required string QualityNote { get; init; }
    public required IReadOnlyList<DialogueBlock> Blocks { get; init; }

    /// <summary>对白块计数（详情头部由 VM 拼成一句「N 个对白块」）。</summary>
    public required int BlockCount { get; init; }

    /// <summary>详情头的抓取日期，缺失写「未测量」（S-8）。</summary>
    public string CrawledLabel => CoverageLabels.Crawled(CrawledAt);
}

/// <summary>台词列表的一行。</summary>
public sealed class VoiceRow
{
    public required string Source { get; init; }
    public required string Id { get; init; }
    public required string Speaker { get; init; }
    public required string Text { get; init; }
    public required string Kind { get; init; }
    public required string KindDisplay { get; init; }
    public required string Language { get; init; }
    public required string RegionLabel { get; init; }
    public required string InstanceBadge { get; init; }
    public required string CrawledLabel { get; init; }
    public required string AlignmentKey { get; init; }

    /// <summary>crawled_at 是否有值；false 时 CrawledLabel 已是「未测量」（V-7）。</summary>
    public required bool HasCrawledAt { get; init; }

    /// <summary>详情元信息里的抓取短语：不给一个尾随空格的「抓取于 」。</summary>
    public string CrawledPhrase => HasCrawledAt ? $"抓取于 {CrawledLabel}" : "抓取时间未测量";

    /// <summary>读屏行名（V-8）。</summary>
    public string RowAccessibleName =>
        $"{(Speaker.Length > 0 ? Speaker : "无说话人")}，{Text}，{KindDisplay}，{InstanceBadge}，{RegionLabel}，抓取 {CrawledLabel}";
}

/// <summary>对照栏「本地没有内容」的原因——机读值，界面必须按它出文字（S-1）。</summary>
public enum ParallelMissingReason
{
    /// <summary>这一服有可读正文。</summary>
    None,

    /// <summary>库里没有这个语言的行，或行在但 text 为空：本地未抓取，不代表该服没有。</summary>
    NotCrawled,

    /// <summary>缺对齐键（{kind}:{asset} 形态不成立），根本无法跨服比对。</summary>
    NoAlignmentKey,

    /// <summary>对照查询本身失败：无法判断，不得当成「没有」。</summary>
    ReadFailed,
}

/// <summary>单句在某一服的呈现。</summary>
public sealed class ParallelLine
{
    public ParallelLine(
        string regionLabel,
        string language,
        string text,
        string badge,
        bool missing,
        ParallelMissingReason missingReason = ParallelMissingReason.None)
    {
        RegionLabel = regionLabel;
        Language = language;
        Text = text;
        Badge = badge;
        Missing = missing;
        MissingReason = missing ? missingReason : ParallelMissingReason.None;
    }

    public string RegionLabel { get; }

    public string Language { get; }

    public string Text { get; }

    public string Badge { get; }

    public bool Missing { get; }

    /// <summary>机读的缺失原因（S-1）；页面把它渲染成文字而不是灰格。</summary>
    public ParallelMissingReason MissingReason { get; }

    public Visibility TextVisibility => Missing ? Visibility.Collapsed : Visibility.Visible;

    public Visibility MissingVisibility => Missing ? Visibility.Visible : Visibility.Collapsed;
}

// ─────────────────────────────────────────────────────────────────────────────
// 三、用语
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>术语/用语列表的一行，五语平铺。</summary>
public sealed class TermRow
{
    public required string[] Keys { get; init; }
    public required string Table { get; init; }
    public required string Id { get; init; }
    public required string Canonical { get; init; }
    public required string Kind { get; init; }
    public required string OfficialLabel { get; init; }
    public required string Trust { get; init; }
    public required string Ja { get; init; }
    public required string En { get; init; }
    public required string ZhHans { get; init; }
    public required string ZhHant { get; init; }
    public required string Ko { get; init; }
    public required string NamesJson { get; init; }
    public required string Extra { get; init; }

    /// <summary>
    /// 读屏行名。ListViewItem 默认拿 Content.ToString()，会念成
    /// 「SekaiSync.Desktop.Models.TermRow」这种类型名（实测），必须显式给可读名。
    ///
    /// 两个坑（都是实测踩出来的，别再"简化"回去）：
    /// 1) 缺语言的单元格不是空串，而是 <see cref="TextRenderer.MissingCellText"/>（"暂无"）——
    ///    必须按常量判缺失，否则每个缺失语言都会被念一遍。
    /// 2) <c>Canonical</c> 保留库里的原始空白，而单元格值被 Trim 过；直接 Ordinal 比较会判成
    ///    「不同」而把同一条韩文念两遍。比较用 Trim 后的规范化值。
    ///
    /// 读屏是一次性听，名字太长等于没有：所以只念有值的语言，其余不念。
    /// </summary>
    public string RowAccessibleName
    {
        get
        {
            var head = Canonical.Trim();
            var parts = new List<string> { head };
            NameCellText.AppendLanguage(parts, "日", Ja, head);
            NameCellText.AppendLanguage(parts, "英", En, head);
            NameCellText.AppendLanguage(parts, "简", ZhHans, head);
            NameCellText.AppendLanguage(parts, "繁", ZhHant, head);
            NameCellText.AppendLanguage(parts, "韩", Ko, head);
            if (parts.Count == 1)
            {
                parts.Add("暂无该语言的译名记录");
            }

            parts.Add(Trust);
            parts.Add(Table);
            return string.Join("，", parts);
        }
    }
}

/// <summary>句级证据。</summary>
public sealed class EvidenceRow
{
    public required int Index { get; init; }
    public required string StoryKey { get; init; }
    public required string Language { get; init; }
    public required string Sentence { get; init; }
}

/// <summary>官方译名字典的一行（title_overlay，按 namespace 分组）。</summary>
public sealed class TranslationNameRow
{
    public required string Namespace { get; init; }
    public required string Id { get; init; }
    public required string Key { get; init; }
    public required string Ja { get; init; }
    public required string En { get; init; }
    public required string ZhHans { get; init; }
    public required string ZhHant { get; init; }
    public required string Ko { get; init; }

    /// <summary>读屏行名：key + 有值的译文（缺格是「暂无」而非空串，与 TermRow 同一套判定）。</summary>
    public string RowAccessibleName
    {
        get
        {
            var head = (Key.Length > 0 ? Key : Id).Trim();
            var parts = new List<string> { head };
            NameCellText.AppendLanguage(parts, "日", Ja, head);
            NameCellText.AppendLanguage(parts, "英", En, head);
            NameCellText.AppendLanguage(parts, "简", ZhHans, head);
            NameCellText.AppendLanguage(parts, "繁", ZhHant, head);
            NameCellText.AppendLanguage(parts, "韩", Ko, head);
            if (parts.Count == 1)
            {
                parts.Add("五语均无记录");
            }
            return string.Join("，", parts);
        }
    }
}

/// <summary>译名 namespace 分组（左栏）。</summary>
public sealed class TranslationNamespace
{
    public required string Namespace { get; init; }
    public required int Rows { get; init; }
    public required int Missing { get; init; }

    public string Label => $"{Namespace}（{Rows}）";

    /// <summary>五语缺格数量，0 时显示「完整」。</summary>
    public string MissingLabel => Missing == 0 ? "五语完整" : $"缺 {Missing} 格";

    /// <summary>读屏行名：namespace + 行数 + 缺格情况。</summary>
    public string RowAccessibleName => $"{Namespace}，{Rows} 行，{MissingLabel}";
}

/// <summary>剧情级译文覆盖层的一行。</summary>
public sealed class OverlayRow
{
    public required string Id { get; init; }
    public required string Language { get; init; }
    public required string RegionLabel { get; init; }
    public required string TranslationSource { get; init; }
    public required string StatusLabel { get; init; }
    public required string Trust { get; init; }
    public required string Title { get; init; }
    public required string Source { get; init; }
    public required string InstanceBadge { get; init; }
    public required long Length { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 四、实体
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>实体资产域（12 个）。types 是该域下的 entities.type 列表。</summary>
public sealed class EntityDomain
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> Types { get; init; }
    public required long Count { get; init; }

    /// <summary>任务域占 44% 但全是程序化占位名，默认折叠。</summary>
    public required bool DefaultCollapsed { get; init; }

    public string Label => $"{DisplayName}  {Count:N0}";

    /// <summary>展开/折叠图标。</summary>
    public string Glyph => DefaultCollapsed ? "\uE76C" : "\uE70D";
}

/// <summary>实体列表的一行。</summary>
public sealed class EntityRow
{
    public required string Id { get; init; }
    public required string PrimaryName { get; init; }
    public required string Type { get; init; }
    public required string TypeDisplay { get; init; }
    public required string Region { get; init; }
    public required string RegionLabel { get; init; }
    public required string Trust { get; init; }
    public required string Source { get; init; }
    public required string InstanceBadge { get; init; }

    public required string NamesJson { get; init; }
    public required string FactsJson { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 五、数据源 / 同步率
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>某个服在某类目上的缺口。</summary>
public sealed class CategoryGap
{
    public required string Region { get; init; }
    public required string Language { get; init; }
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public required long Matched { get; init; }
    public required long Expected { get; init; }
    public required int Pct { get; init; }

    /// <summary>
    /// 上游 P18（0.4.0）起，数据表缺失/损坏时类目的分母不完整，pct 会是 null，
    /// 只有 known_subset_pct 有值——这时的百分比只代表「已核对的子集」。
    /// 旧格式没有这个字段，默认按完整分母处理。
    /// </summary>
    public bool DenominatorComplete { get; init; } = true;

    public string Label => DenominatorComplete
        ? $"{DisplayName} {Matched:N0} / {Expected:N0} = {Pct}%"
        : $"{DisplayName} {Matched:N0} / ≥{Expected:N0} = {Pct}%（已知子集，分母不全）";
}

/// <summary>单个服的同步率。</summary>
public sealed class RegionProgress
{
    public required string Region { get; init; }
    public required string Language { get; init; }
    public required string DisplayName { get; init; }
    public required int OverallPct { get; init; }
    public required int FactPct { get; init; }
    public required int TextPct { get; init; }
    public required long Matched { get; init; }
    public required long Expected { get; init; }
    public bool DenominatorComplete { get; init; } = true;

    public string Label => DenominatorComplete
        ? $"{DisplayName} {OverallPct}%"
        : $"{DisplayName} {OverallPct}%（已知子集）";

    public string Detail => DenominatorComplete
        ? $"事实 {FactPct}% · 正文 {TextPct}% · {Matched:N0}/{Expected:N0}"
        : $"事实 {FactPct}% · 正文 {TextPct}% · {Matched:N0}/≥{Expected:N0}（分母不全）";
}

/// <summary>
/// 同步率快照。数据来自 sekaisync progress 生成的 progress.json；
/// 没有时退化为 freshness.json 的 coverage 估算。
/// </summary>
public sealed class ProgressSnapshot
{
    public static ProgressSnapshot Empty { get; } = new()
    {
        HasData = false,
        IsEstimate = false,
        OverallPct = 0,
        FactPct = 0,
        TextPct = 0,
        Matched = 0,
        Expected = 0,
        DenominatorComplete = true,
        GeneratedAt = string.Empty,
        GeneratedLabel = "未评估",
        SourcePath = string.Empty,
        Regions = [],
        Gaps = [],
        StatusText = "未找到 progress.json，点击「重新评估」生成。",
    };

    public required bool HasData { get; init; }

    /// <summary>从 freshness.json 估算而来（标签要加「估算」）。</summary>
    public required bool IsEstimate { get; init; }

    public required int OverallPct { get; init; }
    public required int FactPct { get; init; }
    public required int TextPct { get; init; }
    public required long Matched { get; init; }
    public required long Expected { get; init; }

    /// <summary>分母是否完整；false 时 OverallPct 是已知子集口径（上游 P18）。</summary>
    public bool DenominatorComplete { get; init; } = true;

    public required string GeneratedAt { get; init; }
    public required string GeneratedLabel { get; init; }
    public required string SourcePath { get; init; }
    public required IReadOnlyList<RegionProgress> Regions { get; init; }
    public required IReadOnlyList<CategoryGap> Gaps { get; init; }
    public required string StatusText { get; init; }

    /// <summary>
    /// 源站确认不提供的单元数（上游 source_unavailable，已从分母移出）。
    /// 0 表示无需排除。
    /// </summary>
    public long SourceUnavailable { get; init; }

    /// <summary>上游对每类不可获取项的说明（键为类目名）。</summary>
    public IReadOnlyDictionary<string, string> SourceUnavailableReasons { get; init; }
        = new Dictionary<string, string>();

    /// <summary>底部条上的主标签。</summary>
    public string BarLabel => !HasData
        ? "未评估"
        : $"{(IsEstimate ? "≈" : string.Empty)}同步率 {OverallPct}%{(DenominatorComplete || IsEstimate ? string.Empty : "（已知子集）")}";

    /// <summary>「确认无法获取」的独立说明，空字符串表示没有此类条目。</summary>
    public string SourceUnavailableLabel => SourceUnavailable <= 0
        ? string.Empty
        : $"另有 {SourceUnavailable:N0} 项源站不提供，已从分母移出（不计入同步率）";

    public string Tooltip => HasData
        ? $"合计 {Matched:N0} / {Expected:N0}　生成于 {GeneratedLabel}"
        : StatusText;
}

/// <summary>meta 表的一行。</summary>
public sealed class MetaEntry
{
    public required string Key { get; init; }
    public required string Value { get; init; }
}

/// <summary>一个实例的规模与新鲜度。</summary>
public sealed class SourceStats
{
    public required string Source { get; init; }
    public required string InstanceBadge { get; init; }
    public required string DisplayName { get; init; }
    public required string Upstream { get; init; }
    public required string Site { get; init; }
    public required long Rows { get; init; }
    public required long OverlayRows { get; init; }
    public required string FirstCrawl { get; init; }
    public required string LastCrawl { get; init; }
    public required int DaysAgo { get; init; }
    public required int TextKindsCovered { get; init; }
    public required int TextKindsTotal { get; init; }

    public string RowsLabel => Rows.ToString("N0");

    // 这里曾有一个 CrawlLabel => $"{LastCrawl}（{DaysAgo} 天前）"：全仓无消费者，
    // 且 LastCrawl 为空时会渲染成「—（0 天数 前）」这种把「未测量」说成「0 天前」的假句子。
    // 界面上的抓取时间一律走 SourcesViewModel.CrawlLabel，那里才判了 HasLastCrawl。

    public string CoverageLabel => $"正文类目 {TextKindsCovered}/{TextKindsTotal}";
}

/// <summary>跨实例比对的一行。</summary>
public sealed class CompareRow
{
    public required string AlignmentKey { get; init; }
    public required string Title { get; init; }
    public required string LeftBadge { get; init; }
    public required string RightBadge { get; init; }
    public required string Status { get; init; }
    public required string StatusLabel { get; init; }
    public required string Detail { get; init; }

    /// <summary>读屏行名：对齐键 + 标题 + 状态 + 说明（ListView 行不能只念类型名）。</summary>
    public string RowAccessibleName =>
        $"{AlignmentKey}，{Title}，{StatusLabel}{(Detail.Length > 0 ? "，" + Detail : string.Empty)}";
}

/// <summary>跨实例比对的统计。</summary>
public sealed class CompareSummary
{
    public required long Both { get; init; }
    public required long LeftOnly { get; init; }
    public required long RightOnly { get; init; }
    public required long Differed { get; init; }
    public required long Scanned { get; init; }

    public string Label =>
        $"扫描 {Scanned:N0} 个对齐键：两站都有 {Both:N0} · 仅 Sekai Viewer {LeftOnly:N0} · 仅 Moesekai {RightOnly:N0} · 内容不同 {Differed:N0}";
}
