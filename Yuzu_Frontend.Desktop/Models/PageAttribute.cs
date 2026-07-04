using System;
using Material.Icons;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 页面标记特性。
/// 用于将一个 <see cref="Avalonia.Controls.UserControl"/> 标记为可导航页面，
/// 由 <see cref="Services.PageDiscoveryService"/> 进行反射扫描并装配到侧栏菜单中。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class PageAttribute : Attribute
{
    /// <summary>
    /// 获取或设置在侧栏中展示的页面名称。
    /// </summary>
    public string DisplayPageName { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置当前页面作为集合（分组）的名称；
    /// 当 <see cref="IsCollection"/> 为 <c>true</c> 时，该名称用于显示分组标题。
    /// </summary>
    public string DisplayCollectionName { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置页面在侧栏中显示的 Material 图标种类。
    /// </summary>
    public MaterialIconKind Icon { get; set; }

    /// <summary>
    /// 获取或设置页面的排序优先级；数字越小越靠前，默认值为 100。
    /// </summary>
    public int Order { get; set; } = 100;

    /// <summary>
    /// 获取或设置一个值，指示当前页面是否表示一个可展开的集合/分组。
    /// 当值为 <c>true</c> 时，该项会在侧栏中表现为分组，其内容将作为默认子项。
    /// </summary>
    public bool IsCollection { get; set; } = false;

    /// <summary>
    /// 获取或设置所属分组的名称；若不为空，则表示该页面属于已存在的集合。
    /// 当值为 <c>null</c> 或空字符串时，该页面将作为顶层项或一个新的集合。
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// 初始化 <see cref="PageAttribute"/> 的新实例，并设置页面显示名称与图标。
    /// </summary>
    /// <param name="displayPageName">在侧栏中展示的页面名称。</param>
    /// <param name="icon">页面对应的 Material 图标种类。</param>
    public PageAttribute(string displayPageName, MaterialIconKind icon)
    {
        DisplayPageName = displayPageName;
        Icon = icon;
    }
}
