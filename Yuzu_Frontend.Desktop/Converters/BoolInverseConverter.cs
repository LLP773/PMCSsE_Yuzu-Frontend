using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Yuzu_Frontend.Desktop.Converters;

/// <summary>
/// 将布尔值反转的转换器。
/// true → false，false → true。
/// 用于在 XAML 中需要反转绑定的场景（例如"连接中"时禁用按钮）。
/// </summary>
public class BoolInverseConverter : IValueConverter
{
    /// <summary>
    /// 将输入的布尔值反转后返回。
    /// </summary>
    /// <param name="value">bool? 值。</param>
    /// <param name="targetType">目标类型（应为 bool）。</param>
    /// <param name="parameter">可选参数（未使用）。</param>
    /// <param name="culture">区域设置（未使用）。</param>
    /// <returns>反转后的布尔值。空值视为 false。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return true;
    }

    /// <summary>
    /// 双向绑定时反转回原始值。
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }
}
