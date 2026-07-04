using Avalonia.Collections;
using SukiUI.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace Yuzu_Frontend.Models.PropertyGrid;

/// <summary>
/// PropertyGrid 自定义实例视图模型。
/// <br/><b>AOT 改造：</b>优先使用 <see cref="PropertyGridStaticRegistry"/>
/// 编译期生成的静态分类列表（100% AOT 安全，不依赖反射）；
/// 仅当目标类型未在注册表中注册时，回退到旧的反射扫描逻辑（调试/开发场景，仅非 AOT 构建保留）。
/// <br/>静态生成：Source Generator 扫描目标类带 <c>[Category]</c> 标记的属性，
/// 生成 <c>BuildCategories_X()</c> 方法并注册到 <see cref="PropertyGridStaticRegistry"/>。
/// </summary>
public class YuzuInstanceViewModel : InstanceViewModel
{
    /// <summary>
    /// 初始化 <see cref="YuzuInstanceViewModel"/> 的新实例。
    /// </summary>
    /// <param name="viewModel">被编辑的目标对象，需实现 INotifyPropertyChanged。</param>
    public YuzuInstanceViewModel(INotifyPropertyChanged? viewModel) : base(viewModel!) { }

    /// <summary>
    /// 生成 PropertyGrid 的分类列表。
    /// <para>AOT 路径（推荐）：</para>
    /// <list type="number">
    ///   <item>在注册表中查询编译期生成的静态构造器，命中则直接返回。</item>
    ///   <item>未命中时：非 AOT 构建回退到反射扫描；AOT 构建返回空列表（提示开发者补全 SG 标注）。</item>
    /// </list>
    /// </summary>
    /// <param name="viewModel">被编辑的目标对象。</param>
    /// <returns>分类列表，每个分类包含若干属性编辑器。</returns>
    public override IAvaloniaReadOnlyList<CategoryViewModel> GenerateCategories(INotifyPropertyChanged viewModel)
    {
        if (viewModel == null)
        {
            return new AvaloniaList<CategoryViewModel>();
        }

        // === AOT 优先路径：静态注册表（编译期生成，零反射）===
        try
        {
            if (PropertyGridStaticRegistry.TryGetFactory(viewModel.GetType(), out var factory))
            {
                return factory(viewModel);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PropertyGrid] 静态注册表构造失败（{viewModel.GetType().Name}）: {ex.Message}");
        }

        // === 回退路径区分构建模式 ===
#if !NATIVEAOT
        // 非 AOT 构建：保留反射扫描（便于开发阶段新增配置类后立即调试）
        return GenerateCategoriesViaReflection(viewModel);
#else
        // AOT 构建：不允许反射，返回空列表并输出诊断提示
        Console.WriteLine(
            $"[PropertyGrid][AOT] 类型 {viewModel.GetType().FullName} 未在 PropertyGridStaticRegistry 中注册。" +
            "请为该类的属性添加 [Category] 特性，以便 PropertyGridCategoryGenerator 在编译期生成静态构造器。");
        return new AvaloniaList<CategoryViewModel>();
#endif
    }

#if !NATIVEAOT
    /// <summary>
    /// 纯反射版的分类生成逻辑（旧实现，仅作回退；AOT 构建完全剔除）。
    /// 扫描目标对象所有带 [Category] 标记的公共属性，按分类分组，
    /// 并根据属性类型选择对应的编辑器 ViewModel。
    /// </summary>
    private static IAvaloniaReadOnlyList<CategoryViewModel> GenerateCategoriesViaReflection(INotifyPropertyChanged viewModel)
    {
        // 反射获取所有公共实例属性，仅保留带 [Category] 特性的属性
#pragma warning disable IL2072, IL2075 // 仅非 AOT 构建执行，此处忽略裁剪警告
        var properties = viewModel.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty);
#pragma warning restore IL2072, IL2075
        var propertiesByCategory = properties
            .Where(p => p.GetCustomAttribute<CategoryAttribute>() != null)
            .GroupBy(p => p.GetCustomAttribute<CategoryAttribute>()!.Category);

        var categories = new List<CategoryViewModel>();

        foreach (var group in propertiesByCategory)
        {
            var propertyViewModels = new AvaloniaList<IPropertyViewModel>();

            foreach (var property in group)
            {
                // 优先使用 [DisplayName] 特性值，未标记时使用属性名
                var displayname = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? property.Name;

                // 带 [FilePath] 特性的 string 属性使用 FilePathViewModel（路径浏览框）
                var filePathAttr = property.GetCustomAttributes<FilePathAttribute>(false).FirstOrDefault();
                if (filePathAttr is not null && property.PropertyType == typeof(string))
                {
                    propertyViewModels.Add(new FilePathViewModel(viewModel, displayname, property, filePathAttr.Mode));
                }
                else
                {
                    // 根据属性类型选择对应的编辑器
                    if (property.PropertyType.IsClass && property.PropertyType != typeof(string))
                    {
                        propertyViewModels.Add(new ComplexTypeViewModel(viewModel, displayname, property));
                    }
                    else if (property.PropertyType.IsEnum)
                    {
                        propertyViewModels.Add(new EnumViewModel(viewModel, displayname, property));
                    }
                    else if (property.PropertyType == typeof(bool))
                    {
                        propertyViewModels.Add(new BoolViewModel(viewModel, displayname, property));
                    }
                    else if (property.PropertyType == typeof(int) || property.PropertyType == typeof(double))
                    {
                        propertyViewModels.Add(new StringViewModel(viewModel, displayname, property));
                    }
                    else
                    {
                        // 默认使用字符串编辑器
                        propertyViewModels.Add(new StringViewModel(viewModel, displayname, property));
                    }
                }
            }

            categories.Add(new CategoryViewModel(group.Key, propertyViewModels));
        }

        return new AvaloniaList<CategoryViewModel>(categories);
    }
#endif
}
