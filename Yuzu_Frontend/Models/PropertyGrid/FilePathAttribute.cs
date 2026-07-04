using System;

namespace Yuzu_Frontend.Models.PropertyGrid;

/// <summary>
/// 标记属性为路径字段，PropertyGrid 将渲染为带浏览按钮的 TextBox。
/// 由 YuzuInstanceViewModel 在反射时识别，创建 FilePathViewModel 而非 StringViewModel。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class FilePathAttribute : Attribute
{
    /// <summary>
    /// 浏览模式：Folder 选择文件夹，File 选择文件。
    /// </summary>
    public FilePickerMode Mode { get; }

    public FilePathAttribute(FilePickerMode mode)
    {
        Mode = mode;
    }
}

/// <summary>
/// 路径浏览模式。
/// </summary>
public enum FilePickerMode
{
    /// <summary>选择文件夹</summary>
    Folder,

    /// <summary>选择文件</summary>
    File
}
