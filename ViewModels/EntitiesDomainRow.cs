using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using SekaiSync.Desktop.Models;

namespace SekaiSync.Desktop.ViewModels;

/// <summary>
/// 实体页左栏的一行 = 一个资产域 + 它的**实测**行数与占比（审计 E-1、E-2、E-6）。
///
/// 为什么要有这个包装类：`EntityDomain` 在 Models 里，只有 Key/DisplayName/Types/Count/DefaultCollapsed，
/// 页面上要写的「默认不选 · 占 44%」「本地没有这个域的行」这类说明必须由实测行数算出来，
/// 而 WinUI 3 的 DataTemplate 里既不能调页面的方法，也不能写枚举比较。
///
/// `EntityDomain.Glyph`（展开/折叠箭头）故意不消费：左栏是平铺列表，
/// 画一个折叠箭头是在承诺一个不存在的交互。
/// </summary>
public sealed class EntitiesDomainRow
{
    public EntitiesDomainRow(EntityDomain domain, long grandTotal)
    {
        Key = domain.Key;
        DisplayName = domain.DisplayName;
        Types = domain.Types;
        Count = domain.Count;
        IsDefaultCollapsed = domain.DefaultCollapsed;

        Label = $"{domain.DisplayName}  {domain.Count:N0}";
        TypeListText = string.Join(" / ", domain.Types);
        ShareLabel = grandTotal > 0
            ? $"占 {Math.Round(domain.Count * 100.0 / grandTotal):0.#}%"
            : "占比未知";
        CountLabel = $"{domain.Count:N0} 行";

        NoteText = domain.DefaultCollapsed ? "任务条件与奖励" : string.Empty;
        NoteVisibility = domain.DefaultCollapsed ? Visibility.Visible : Visibility.Collapsed;

        EmptyLabel = domain.Count == 0 ? "尚未收录" : string.Empty;
        EmptyVisibility = domain.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AutomationText = domain.Count == 0
            ? $"{domain.DisplayName}，本地 0 行，未覆盖"
            : $"{domain.DisplayName}，本地 {domain.Count:N0} 行，{ShareLabel}{(domain.DefaultCollapsed ? "，默认不展开" : string.Empty)}";
    }

    public string Key { get; }

    public string DisplayName { get; }

    public IReadOnlyList<string> Types { get; }

    public long Count { get; }

    /// <summary>这个域属于「默认折叠」组（当前只有任务域）。</summary>
    public bool IsDefaultCollapsed { get; }

    public string Label { get; }

    public string CountLabel { get; }

    public string ShareLabel { get; }

    public string TypeListText { get; }

    public string NoteText { get; }

    public Visibility NoteVisibility { get; }

    public string EmptyLabel { get; }

    public Visibility EmptyVisibility { get; }

    /// <summary>给读屏的完整一行：名称 + 实测行数 + 默认折叠说明。</summary>
    public string AutomationText { get; }

    public override string ToString() => Label;
}
