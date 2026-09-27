using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.Services;

/// <summary>
/// 同步率读取。指标本身由 `sekaisync progress` 算好写成 progress.json，
/// 应用只读结果，不在前端重算。
///
/// 路径按优先级探测：
///   1. {store}/cache/progress.json          （当前版式）
///   2. {store}/legacy/cache/progress.json   （v1 版式，实测只有这个存在）
/// 两者都没有时退化为读 freshness.json 的 coverage 估一个粗略值。
/// </summary>
public sealed class ProgressService
{
    private readonly AppEnvironment _environment;
    private ProgressSnapshot? _cache;

    public ProgressService(AppEnvironment environment) => _environment = environment;

    /// <summary>缓存失效（同步完成后调用）。</summary>
    public void Invalidate() => _cache = null;

    public string PrimaryPath => Combine("cache", "progress.json");

    public string LegacyPath => Combine("legacy", "cache", "progress.json");

    public string FreshnessPath => Combine("cache", "freshness.json");

    /// <summary>读快照；结果会缓存，直到 Invalidate。</summary>
    public ProgressSnapshot Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var snapshot = LoadCore();
        _cache = snapshot;
        return snapshot;
    }

    private ProgressSnapshot LoadCore()
    {
        foreach (var path in new[] { PrimaryPath, LegacyPath })
        {
            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    var parsed = Parse(text, path);
                    if (parsed is not null)
                    {
                        return parsed;
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"ProgressService: 读取 {path} 失败：{ex.Message}");
                }
            }
        }

        return LoadFromFreshness();
    }

    private static ProgressSnapshot? Parse(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var overall = root.TryGetProperty("overall", out var overallElement) ? overallElement : default;
        // P18（上游 0.4.0）：分母不完整时 pct 为 null，只有 known_subset_pct 可用。
        var (overallPct, denominatorComplete) = ReadScore(overall);
        var matched = ReadLong(overall, "matched_units");
        var expected = ReadLong(overall, "expected_units");

        var regions = new List<RegionProgress>();
        var gaps = new List<CategoryGap>();

        if (root.TryGetProperty("regions", out var regionsElement) &&
            regionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var regionProperty in regionsElement.EnumerateObject())
            {
                var regionKey = regionProperty.Name;
                var region = regionProperty.Value;
                var language = ReadString(region, "language");
                var info = SourceModel.RegionByRegion(regionKey) ?? SourceModel.RegionByLanguage(language);

                var regionOverall = region.TryGetProperty("overall", out var ro) ? ro : default;
                var fact = region.TryGetProperty("fact", out var f) ? f : default;
                var text = region.TryGetProperty("text", out var t) ? t : default;

                // region 根对象上有 denominator_complete（该服数据表是否齐全）；
                // 旧格式没有该字段，ReadFlag 缺省按齐全处理。
                var regionComplete = ReadFlag(region, "denominator_complete");
                var (regionPct, _) = ReadScore(regionOverall);
                var (factPct, _) = ReadScore(fact);
                var (textPct, _) = ReadScore(text);

                regions.Add(new RegionProgress
                {
                    Region = regionKey,
                    Language = language.Length > 0 ? language : info?.Language ?? string.Empty,
                    DisplayName = info?.DisplayName ?? regionKey,
                    OverallPct = regionPct,
                    FactPct = factPct,
                    TextPct = textPct,
                    Matched = ReadLong(regionOverall, "matched_units"),
                    Expected = ReadLong(regionOverall, "expected_units"),
                    DenominatorComplete = regionComplete,
                });

                // 只收集未确认 100% 的类目，并按缺口大小排序——这一步把「70%」变成可执行信息。
                // 分母不全的类目即使已知子集 100% 也不能算达标，一并保留。
                if (text.ValueKind == JsonValueKind.Object &&
                    text.TryGetProperty("categories", out var categories) &&
                    categories.ValueKind == JsonValueKind.Object)
                {
                    foreach (var category in categories.EnumerateObject())
                    {
                        // 分母为 0 的类目（该服上游没有这类内容，例如外语服的
                        // mysekai 对白）既不是「已完成」也不是「缺口」——它的
                        // pct 为 null，ReadScore 会退化成 0，于是被误报成 0% 的
                        // 最大缺口。按「无期望单元」直接跳过。
                        var categoryExpected = ReadLong(category.Value, "expected");
                        if (categoryExpected <= 0)
                        {
                            continue;
                        }
                        var (pct, categoryComplete) = ReadScore(category.Value);
                        if (categoryComplete && pct >= 100)
                        {
                            continue;
                        }
                        gaps.Add(new CategoryGap
                        {
                            Region = regionKey,
                            Language = language,
                            Kind = category.Name,
                            DisplayName = SourceModel.KindDisplay(category.Name),
                            Matched = ReadLong(category.Value, "matched"),
                            Expected = categoryExpected,
                            Pct = pct,
                            DenominatorComplete = categoryComplete,
                        });
                    }
                }
            }
        }

        var orderedRegions = regions
            .OrderBy(r => Array.FindIndex(SourceModel.Regions.ToArray(), x => x.Region == r.Region) is var i && i >= 0 ? i : 99)
            .ToList();

        var orderedGaps = gaps
            .OrderBy(g => g.Pct)
            .ThenByDescending(g => g.Expected - g.Matched)
            .ToList();

        var (overallFactPct, _) = ReadScore(overall.TryGetProperty("fact", out var of) ? of : default);
        var (overallTextPct, _) = ReadScore(overall.TryGetProperty("text", out var otx) ? otx : default);
        var statusText = orderedGaps.Count == 0
            ? "所有类目均已达 100%。"
            : $"有 {orderedGaps.Count} 个类目低于 100%，最大缺口 {orderedGaps[0].DisplayName}（{orderedGaps[0].Pct}%）。";
        if (!denominatorComplete)
        {
            statusText += "部分上游数据表缺失或损坏，百分比为已知子集口径。";
        }

        // 上游 source_unavailable：源站确认不提供的单元（如 displayPhrase 为空的
        // 角色语音行、外语服缺失的 mysekai 对白）。它们已从分母移出，这里只读出
        // 规模与原因，作为独立说明展示，不参与百分比计算。
        long sourceUnavailable = 0;
        var unavailableReasons = new Dictionary<string, string>();
        if (root.TryGetProperty("source_unavailable", out var unavailable) &&
            unavailable.ValueKind == JsonValueKind.Object)
        {
            if (unavailable.TryGetProperty("total_units", out var totalElement) &&
                totalElement.ValueKind == JsonValueKind.Number)
            {
                sourceUnavailable = totalElement.GetInt64();
            }
            if (unavailable.TryGetProperty("reasons", out var reasons) &&
                reasons.ValueKind == JsonValueKind.Object)
            {
                foreach (var reason in reasons.EnumerateObject())
                {
                    if (reason.Value.ValueKind == JsonValueKind.String)
                    {
                        unavailableReasons[reason.Name] = reason.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }

        return new ProgressSnapshot
        {
            HasData = true,
            IsEstimate = false,
            OverallPct = overallPct,
            FactPct = overallFactPct,
            TextPct = overallTextPct,
            Matched = matched,
            Expected = expected,
            DenominatorComplete = denominatorComplete,
            GeneratedAt = ReadString(root, "generated_at"),
            GeneratedLabel = FormatTimestamp(ReadString(root, "generated_at")),
            SourcePath = path,
            Regions = orderedRegions,
            Gaps = orderedGaps,
            StatusText = statusText,
            SourceUnavailable = sourceUnavailable,
            SourceUnavailableReasons = unavailableReasons,
        };
    }

    /// <summary>退化路径：用 freshness.json 的 coverage 粗估。标签要加「估算」前缀。</summary>
    private ProgressSnapshot LoadFromFreshness()
    {
        var path = FreshnessPath;
        if (path.Length == 0 || !File.Exists(path))
        {
            return ProgressSnapshot.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("coverage", out var coverage) ||
                coverage.ValueKind != JsonValueKind.Object)
            {
                return ProgressSnapshot.Empty;
            }

            var total = 0;
            var available = 0;
            var regions = new List<RegionProgress>();

            foreach (var property in coverage.EnumerateObject())
            {
                var info = SourceModel.RegionByRegion(property.Name) ?? SourceModel.RegionByLanguage(property.Name);
                var availableCount = 0;
                var all = 0;

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var capability in property.Value.EnumerateObject())
                    {
                        all++;
                        var value = capability.Value.ValueKind == JsonValueKind.String
                            ? capability.Value.GetString() ?? string.Empty
                            : capability.Value.ToString();
                        if (string.Equals(value, "available", StringComparison.OrdinalIgnoreCase))
                        {
                            availableCount++;
                        }
                    }
                }

                var pct = all == 0 ? 0 : (int)Math.Round(100.0 * availableCount / all);
                total += all;
                available += availableCount;

                regions.Add(new RegionProgress
                {
                    Region = info?.Region ?? property.Name,
                    Language = info?.Language ?? string.Empty,
                    DisplayName = info?.DisplayName ?? property.Name,
                    OverallPct = pct,
                    FactPct = 0,
                    TextPct = pct,
                    Matched = availableCount,
                    Expected = all,
                });
            }

            var overallPct = total == 0 ? 0 : (int)Math.Round(100.0 * available / total);

            return new ProgressSnapshot
            {
                HasData = true,
                IsEstimate = true,
                OverallPct = overallPct,
                FactPct = 0,
                TextPct = overallPct,
                Matched = available,
                Expected = total,
                GeneratedAt = string.Empty,
                GeneratedLabel = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"),
                SourcePath = path,
                Regions = regions,
                Gaps = [],
                StatusText = "由 freshness.json 的覆盖能力估算，精度低于 progress.json。",
            };
        }
        catch (Exception ex)
        {
            App.Log($"ProgressService: 读取 freshness.json 失败：{ex.Message}");
            return ProgressSnapshot.Empty;
        }
    }

    private string Combine(params string[] parts)
    {
        var store = _environment.StorePath;
        return string.IsNullOrEmpty(store) ? string.Empty : Path.Combine([store, .. parts]);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// 读 P18 的分数对象（pct / known_subset_pct / denominator_complete）。
    /// 分母不完整时 pct 为 null，退到 known_subset_pct；旧格式只有数值 pct，
    /// 没有 denominator_complete 字段，视为分母完整。
    /// </summary>
    private static (int Pct, bool Complete) ReadScore(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (0, true);
        }

        var complete = ReadFlag(element, "denominator_complete");
        var pct = ReadNullableInt(element, "pct");
        if (pct is not null)
        {
            return (pct.Value, complete);
        }

        var known = ReadNullableInt(element, "known_subset_pct");
        return (known ?? 0, complete);
    }

    /// <summary>读布尔字段；字段缺失（旧格式）按 true 处理，保持历史行为。</summary>
    private static bool ReadFlag(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return true;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString() is not "false" and not "False" and not "0",
            _ => true,
        };
    }

    private static int? ReadNullableInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : (int)Math.Round(value.GetDouble()),
            JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : null,
            _ => null,
        };
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : (int)Math.Round(value.GetDouble()),
            JsonValueKind.String => int.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    private static long ReadLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : (long)Math.Round(value.GetDouble()),
            JsonValueKind.String => long.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    internal static string FormatTimestamp(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "未知";
        }
        return DateTimeOffset.TryParse(raw, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : raw;
    }
}
