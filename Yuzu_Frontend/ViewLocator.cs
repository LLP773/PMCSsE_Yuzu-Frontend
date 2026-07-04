using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend;

/// <summary>
/// 视图定位器。实现 <see cref="IDataTemplate"/> 接口，根据传入的 ViewModel
/// 查找并实例化对应的视图控件。
/// <br/><b>AOT 改造：</b>不再使用 <c>Type.GetType(string)</c> +
/// <c>Activator.CreateInstance(Type)</c> 反射命名约定查找视图，
/// 改为优先使用编译期由 <c>ViewModelViewMappingGenerator</c> 生成的
/// <see cref="StaticMappings"/> 字典（AOT 100% 安全）。
/// <para>命名约定（Source Generator 自动匹配）：
///   XxxViewModel → XxxView / XxxPage （同命名空间优先，其次跨命名空间查找）。
/// </para>
/// </summary>
public partial class ViewLocator : IDataTemplate
{
    /// <summary>
    /// 根据传入的 ViewModel 实例构建对应视图。
    /// <list type="number">
    ///   <item>优先命中 <see cref="StaticMappings"/>（编译期生成，AOT 安全）；</item>
    ///   <item>未命中时：非 AOT 构建回退到命名约定字符串替换 + 反射（仅开发/调试场景使用）；
    ///         AOT 构建下直接返回友好的"未注册"占位控件。</item>
    /// </list>
    /// </summary>
    /// <param name="param">数据上下文对象，通常为 ViewModelBase 派生类。</param>
    /// <returns>对应视图控件；无法匹配时返回提示文本控件。</returns>
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var vmType = param.GetType();

        // === AOT 优先路径：编译期生成的静态映射 ===
        try
        {
            var factory = GetFactoryForViewModelType(vmType);
            if (factory != null)
            {
                return factory();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ViewLocator] 静态映射创建视图失败（{vmType.Name}）: {ex.Message}");
        }

        // === 回退路径区分构建模式 ===
#if !NATIVEAOT
        // 非 AOT 构建：保留反射回退逻辑（便于开发阶段快速新增 VM/View 而不必重走 SG 代码生成）
        var name = vmType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
#pragma warning disable IL2057, IL2072, IL2075 // 反射回退仅在非 AOT（未裁剪）构建中执行
        var type = Type.GetType(name);

        if (type != null)
        {
            try
            {
                return (Control)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                return new TextBlock { Text = $"Create view failed: {name}\n{ex.Message}" };
            }
        }
#pragma warning restore IL2057, IL2072, IL2075

        return new TextBlock { Text = "Not Found: " + name };
#else
        // AOT 构建：完全禁用反射，直接返回静态映射未命中的提示（确保 SG 覆盖全部 VM）
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = $"[AOT] View 未注册: {vmType.FullName}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "请确保 ViewModel 与 View 遵循 XxxViewModel↔XxxView/XxxPage 命名约定，",
                    Foreground = Avalonia.Media.Brushes.Gray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "以便 ViewModelViewMappingGenerator 在编译期写入静态注册表。",
                    Foreground = Avalonia.Media.Brushes.Gray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        };
#endif
    }

    /// <summary>
    /// 判断当前数据对象是否可由本定位器处理：仅接受 <see cref="ViewModelBase"/> 派生实例。
    /// </summary>
    /// <param name="data">待判断的数据对象。</param>
    /// <returns>数据为 ViewModelBase 时返回 true；否则返回 false。</returns>
    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
