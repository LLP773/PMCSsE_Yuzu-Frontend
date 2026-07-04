using Material.Icons;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 描述一个可导航页面的元数据模型。
/// 由 <see cref="Services.PageDiscoveryService"/> 在反射扫描过程中使用，
/// 用于将带有 <see cref="PageAttribute"/> 的 <see cref="Avalonia.Controls.UserControl"/> 装配为侧栏菜单项。
/// </summary>
public class PageItem
{
    /// <summary>
    /// 获取或设置在侧栏/菜单中展示的页面名称。
    /// </summary>
    public string DisplayPageName { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置页面对应的 Material 图标种类。
    /// </summary>
    public MaterialIconKind Icon { get; set; }

    /// <summary>
    /// 获取或设置实际承载页面内容的对象（通常为 <see cref="Avalonia.Controls.UserControl"/> 实例）。
    /// </summary>
    public object Content { get; set; } = null!;

    /// <summary>
    /// 获取或设置页面在同级别中的排序优先级，数字越小越靠前，默认值为 100。
    /// </summary>
    public int Order { get; set; } = 100;

    /// <summary>
    /// 返回页面的显示名称，便于在调试器与 UI 模板中直接呈现。
    /// </summary>
    /// <returns>页面的 <see cref="DisplayPageName"/>。</returns>
    public override string ToString()
    {
        return DisplayPageName;
    }
}
