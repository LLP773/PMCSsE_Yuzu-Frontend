using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace Yuzu_Frontend.Desktop.Converters;

/// <summary>
/// 将配置分区名称（如"基本设置"、"备份排除"）转换为对应的 Material 图标枚举。
/// 用于侧栏 <c>SectionList</c> 的 <c>MaterialIcon.Kind</c> 绑定，
/// 避免依赖"构造函数时遍历可视化树找容器"的时序脆弱写法，
/// 保证 ListBoxItem 虚拟化或延迟生成时图标仍能正确显示。
/// </summary>
public class SectionNameToIconKindConverter : IValueConverter
{
    /// <summary>单例实例。</summary>
    public static readonly SectionNameToIconKindConverter Instance = new();

    /// <summary>分区名 → Material 图标枚举 的静态映射表。</summary>
    private static readonly IReadOnlyDictionary<string, MaterialIconKind> IconMap =
        new Dictionary<string, MaterialIconKind>(StringComparer.Ordinal)
        {
            { "基本设置", MaterialIconKind.Settings },
            { "备份设置", MaterialIconKind.BackupRestore },
            { "备份排除", MaterialIconKind.FilterList },
            { "远程备份", MaterialIconKind.CloudUpload },
            { "在线聊天", MaterialIconKind.ChatBubble }
        };

    /// <summary>未命中映射时使用的兜底图标（设置齿轮）。</summary>
    private const MaterialIconKind FallbackKind = MaterialIconKind.Settings;

    /// <summary>
    /// 将分区名字符串转换为 <see cref="MaterialIconKind"/> 枚举值。
    /// </summary>
    /// <param name="value">分区名（string）。</param>
    /// <param name="targetType">目标类型，应为 <see cref="MaterialIconKind"/>。</param>
    /// <param name="parameter">可选参数（未使用）。</param>
    /// <param name="culture">区域设置（未使用）。</param>
    /// <returns>匹配的图标枚举；未命中返回 <see cref="FallbackKind"/>。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string name && !string.IsNullOrEmpty(name))
        {
            if (IconMap.TryGetValue(name, out var kind))
                return kind;
        }
        return FallbackKind;
    }

    /// <summary>反向转换未实现（绑定为单向）。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
