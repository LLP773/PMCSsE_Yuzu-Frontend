using System.Collections.ObjectModel;
using Avalonia.Controls;
using Material.Icons;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 描述一个页面集合（分组）的元数据模型。
/// 用于在侧栏中表现为可展开的分组，其中可以包含多个 <see cref="PageItem"/> 子项。
/// </summary>
public class ServerCollectionItem
{
    /// <summary>
    /// 获取或设置该集合在侧栏中显示的名称。
    /// </summary>
    public string PageName { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置该集合对应的 Material 图标种类。
    /// </summary>
    public MaterialIconKind Icon { get; set; }

    /// <summary>
    /// 获取或设置该集合在同级别的排序优先级，数字越小越靠前，默认值为 100。
    /// </summary>
    public int Order { get; set; } = 100;

    /// <summary>
    /// 获取或设置与该集合关联的页面内容（可为 <c>null</c>，用于展示集合默认页面）。
    /// </summary>
    public UserControl? Content { get; set; }

    /// <summary>
    /// 获取该集合下的页面子项列表。
    /// </summary>
    public ObservableCollection<PageItem> Pages { get; } = new();
}
