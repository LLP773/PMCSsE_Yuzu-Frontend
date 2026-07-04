using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;
using Yuzu_Frontend.Desktop.Models;

namespace Yuzu_Frontend.Desktop.Views;

/// <summary>
/// 仪表盘页面。用于展示后端服务网络拓扑、连接状态与管理器运行总览。
/// </summary>
[Page("仪表盘", MaterialIconKind.ServerNetwork, Order = 6, IsCollection = false)]
public partial class Dashboard : NavigatedPageBase
{
    /// <summary>当前页面绑定的仪表盘视图模型。</summary>
    public DashboardViewModel ViewModel { get; } = new();

    /// <summary>
    /// 初始化 <see cref="Dashboard"/> 的新实例并加载 XAML 组件。
    /// </summary>
    public Dashboard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 挂载到可视树时：绑定 <see cref="NavigatedPageBase.Connection"/>，
    /// 订阅连接/数据包事件并立即做一次缓存驱动的初始化刷新。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ViewModel.Attach(Connection);
    }

    /// <summary>从可视树移除时：回收 ViewModel 的事件订阅。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ViewModel.Dispose();
    }
}

/// <summary>
/// 布尔反相转换器（bool → bool）：用于 IsEnabled 等需要逻辑 NOT 的绑定场景。
/// </summary>
public class InvertBoolConverter : IValueConverter
{
    public static InvertBoolConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }
}

/// <summary>
/// 布尔 → 透明度转换器。true 时返回 1.0；false 时返回 parameter 指定的透明度（默认 0.35）。
/// </summary>
public class BoolOpacityConverter : IValueConverter
{
    public static BoolOpacityConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fallback = 0.35;
        try
        {
            if (parameter != null && double.TryParse(parameter.ToString(), out var p))
                fallback = p;
        }
        catch { /* ignore */ }

        if (value is bool b) return b ? 1.0 : fallback;
        return fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}

/// <summary>
/// 后端连接状态文本 → 状态点颜色转换器。
/// 未连接=灰、正在连接=琥珀、已缓存=靛蓝，其余（已连接）=绿。
/// </summary>
public class ConnectionStatusBrushConverter : IValueConverter
{
    public static readonly ConnectionStatusBrushConverter Instance = new();

    private static readonly IBrush Gray = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C));
    private static readonly IBrush Indigo = new SolidColorBrush(Color.FromRgb(0x61, 0x70, 0xFF));
    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x2D, 0x9D, 0x7B));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        return status switch
        {
            "未连接" => Gray,
            "正在连接..." => Amber,
            "已缓存" => Indigo,
            _ => Green,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}

/// <summary>
/// 管理器运行状态转换器（输入为 <see cref="DashboardManagerEntry"/>）：
/// <list type="bullet">
///   <item>目标类型为 <see cref="string"/>：运行中 / 已加载 / 未运行。</item>
///   <item>目标类型为画刷且 ConverterParameter="Background"：同色半透明底纹。</item>
///   <item>其余画刷目标：实心状态色（运行=绿、已加载=琥珀、未运行=灰）。</item>
/// </list>
/// </summary>
public class ManagerStateConverter : IValueConverter
{
    public static readonly ManagerStateConverter Instance = new();

    private static readonly IBrush Gray = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x2D, 0x9D, 0x7B));
    private static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C));

    private static readonly IBrush GrayBg = new SolidColorBrush(Color.FromArgb(0x20, 0x9E, 0x9E, 0x9E));
    private static readonly IBrush GreenBg = new SolidColorBrush(Color.FromArgb(0x20, 0x2D, 0x9D, 0x7B));
    private static readonly IBrush AmberBg = new SolidColorBrush(Color.FromArgb(0x20, 0xE6, 0xA2, 0x3C));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DashboardManagerEntry entry)
            return targetType == typeof(string) ? "未运行" : Gray;

        if (targetType == typeof(string))
        {
            return entry.IsRunning ? "运行中"
                : entry.IsLoaded ? "已加载"
                : "未运行";
        }

        var asBackground = string.Equals(parameter as string, "Background", StringComparison.OrdinalIgnoreCase);

        if (entry.IsRunning) return asBackground ? GreenBg : Green;
        if (entry.IsLoaded) return asBackground ? AmberBg : Amber;
        return asBackground ? GrayBg : Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}

/// <summary>
/// 布尔 → 文本转换器。ConverterParameter 格式为 "false文本|true文本"，默认 "否|是"。
/// </summary>
public class BoolToTextConverter : IValueConverter
{
    public static readonly BoolToTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (falseText, trueText) = ("否", "是");
        if (parameter is string p && p.Split('|') is { Length: 2 } parts)
        {
            falseText = parts[0];
            trueText = parts[1];
        }
        return value is bool b && b ? trueText : falseText;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}
