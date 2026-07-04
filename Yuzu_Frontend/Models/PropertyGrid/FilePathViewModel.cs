using System.ComponentModel;
using System.Reflection;
using SukiUI.Controls;

namespace Yuzu_Frontend.Models.PropertyGrid;

/// <summary>
/// 路径字段 ViewModel，在 StringViewModel 基础上增加浏览模式信息。
/// 由 YuzuInstanceViewModel 在反射时遇到 [FilePath] 标记的 string 属性时创建。
/// PropertyGridTemplateSelector 通过类型名 "FilePathViewModel" 查找对应的 DataTemplate。
/// </summary>
public class FilePathViewModel : PropertyViewModelBase<string?>
{
    /// <summary>
    /// 浏览模式：Folder 或 File，决定点击浏览按钮时打开文件夹选择器还是文件选择器。
    /// </summary>
    public FilePickerMode PickerMode { get; }

    public FilePathViewModel(INotifyPropertyChanged viewmodel, string displayName, PropertyInfo propertyInfo, FilePickerMode mode)
        : base(viewmodel, displayName, propertyInfo)
    {
        PickerMode = mode;
    }
}

