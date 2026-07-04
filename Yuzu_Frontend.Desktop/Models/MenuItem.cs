using System.Collections.ObjectModel;
using Avalonia.Controls;
using Material.Icons;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 表示菜单项的类型。
/// </summary>
public enum MenuItemType
{
    /// <summary>
    /// 普通页面类型：点击后直接切换到对应页面。
    /// </summary>
    Page,

    /// <summary>
    /// 集合（分组）类型：包含一个或多个 <see cref="PageItem"/>，可在 UI 中展开。
    /// </summary>
    Collection
}

/// <summary>
/// 应用菜单的描述性模型。用于描述侧栏中的顶层菜单项（页面或分组），
/// 包含显示名称、图标、排序顺序以及可选的子项集合。
/// </summary>
public class AppMenuItem
{
    /// <summary>
    /// 获取或设置菜单项的类型（普通页面或集合分组）。
    /// </summary>
    public MenuItemType Type { get; set; }

    /// <summary>
    /// 获取或设置在侧栏中显示的菜单名称。
    /// </summary>
    public string PageDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置菜单项的 Material 图标种类。
    /// </summary>
    public MaterialIconKind PageIcon { get; set; }

    /// <summary>
    /// 获取或设置菜单项的排序优先级；数字越小越靠前，默认值为 100。
    /// </summary>
    public int PageOrder { get; set; } = 100;

    /// <summary>
    /// 获取或设置与该菜单项关联的页面内容（可为 <c>null</c>）。
    /// </summary>
    public UserControl? PageContent { get; set; }

    /// <summary>
    /// 获取该菜单项下属的页面列表；集合类型的菜单会包含一项或多项。
    /// </summary>
    public ObservableCollection<PageItem> Pages { get; } = new();
}
