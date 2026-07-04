using System.ComponentModel;
using SukiUI.Controls;
using Yuzu_Frontend.Models.PropertyGrid;

namespace Yuzu_Frontend.Desktop.Controls;

/// <summary>
/// 扩展 SukiUI PropertyGrid，使用 YuzuInstanceViewModel 替代默认的 InstanceViewModel，
/// 从而在反射生成属性编辑器时支持 [FilePath] 标记的路径字段。
/// </summary>
public class YuzuPropertyGrid : PropertyGrid
{
    protected override void SetItem(INotifyPropertyChanged? item)
    {
        if (item is null)
        {
            return;
        }

        Instance = new YuzuInstanceViewModel(item);
    }
}
