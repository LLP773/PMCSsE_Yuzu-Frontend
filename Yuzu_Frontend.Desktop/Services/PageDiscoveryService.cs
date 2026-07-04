using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Material.Icons;
using Material.Icons.Avalonia;
using SukiUI.Controls;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Desktop.Views;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Services;

/// <summary>
/// 发现所有实现 <see cref="INavigatedPage"/> 并携带 <see cref="PageAttribute"/> 的页面，
/// 将它们装配为 <see cref="SukiSideMenuItem"/> 侧栏菜单项。
/// <br/><b>AOT 改造：</b>页面类型发现由 <c>PageAttributeGenerator</c> 在编译期完成，
/// 本类不再使用 <c>Assembly.GetTypes()</c>、<c>t.GetCustomAttribute()</c> 或
/// <c>Activator.CreateInstance()</c> 等运行时反射；页面元信息与构造工厂均来自
/// 编译期生成的 <see cref="PageRegistry.AllPages"/> / <see cref="PageRegistry.Factories"/>。
/// <br/><b>性能优化（Lazy 加载）</b>：启动阶段仅做菜单装配，不实例化页面、
/// 不调用 <see cref="INavigatedPage.InitializePage"/>。页面实例改为用户首次点击
/// 对应菜单项时由 <see cref="LazyCreatePage"/> 创建并初始化，显著降低启动时间。
/// </summary>
public static partial class PageDiscoveryService
{
    /// <summary>
    /// 编译期生成的页面类型元信息与构造器的容器（half class）。
    /// 另一半由 Source Generator 输出的 PageRegistry.g.cs 填充：
    /// <c>AllPages { get; }</c> — 已按 Order 升序的页面元信息数组
    /// <c>Factories { get; }</c> — 页面类型 → 强类型构造函数字典
    /// </summary>
    internal static partial class PageRegistry { }

    /// <summary>
    /// 页面类型元信息：记录页面的运行时类型与其 PageAttribute，供后续 Lazy 实例化使用。
    /// </summary>
    internal sealed class PageTypeInfo
    {
        public Type PageType { get; }
        public PageAttribute Attribute { get; }
        public PageTypeInfo(Type t, PageAttribute a) { PageType = t; Attribute = a; }
    }

    /// <summary>
    /// 菜单项到页面类型信息的映射：Lazy 加载用（首次点击才创建实例）。
    /// Key = 侧栏子菜单项，Value = 对应页面的类型元信息。
    /// </summary>
    internal static System.Collections.Generic.Dictionary<SukiSideMenuItem, PageTypeInfo> MenuToPageTypeMap { get; } = new();

    /// <summary>
    /// 缓存已经创建过的页面实例（避免每次切换都重建）。
    /// Key = 侧栏子菜单项，Value = 已经调用过 InitializePage 的页面实例。
    /// </summary>
    internal static System.Collections.Generic.Dictionary<SukiSideMenuItem, object> MenuToPageInstanceMap { get; } = new();

    /// <summary>
    /// 启动时注入的全局共享服务（ToastManager / DialogManager / ConnectionViewModel）。
    /// Lazy 创建页面时需要用到这些引用调用 InitializePage。
    /// </summary>
    internal static ISukiToastManager? SharedToastManager { get; private set; }
    internal static ISukiDialogManager? SharedDialogManager { get; private set; }
    internal static ConnectionViewModel? SharedConnection { get; private set; }

    /// <summary>
    /// 读取编译期生成的 <see cref="PageRegistry.AllPages"/> 并按 <see cref="PageAttribute.Order"/>
    /// 装配为 <see cref="SukiSideMenuItem"/> 列表。<b>不做运行时反射。</b>
    /// 随后默认选中第一项（或其子项）并按需创建对应页面内容（Lazy 创建）。
    /// </summary>
    /// <param name="toastManager">全局的 Toast 提示管理器，Lazy 创建时注入到页面。</param>
    /// <param name="dialogManager">全局的对话框管理器，Lazy 创建时注入到页面。</param>
    /// <param name="connectionViewModel">全局连接视图模型，Lazy 创建时注入到页面。</param>
    /// <param name="menuItemToPageMap">兼容旧接口，保持参数签名不变（内部不再写入，切换时走 Lazy 创建）。</param>
    /// <returns>装配后的顶层侧栏菜单项集合。</returns>
    public static ObservableCollection<SukiSideMenuItem> DiscoverPages(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel connectionViewModel,
        System.Collections.Generic.Dictionary<SukiSideMenuItem, object?> menuItemToPageMap)
    {
        // 保存全局服务引用，供 Lazy 创建页面时调用 InitializePage
        SharedToastManager = toastManager;
        SharedDialogManager = dialogManager;
        SharedConnection = connectionViewModel;

        // 清空上次的缓存（避免热重载/多次调用残留）
        MenuToPageTypeMap.Clear();
        MenuToPageInstanceMap.Clear();

        var menuItems = new ObservableCollection<SukiSideMenuItem>();
        // 用于按名称查找已存在的集合分组（避免重复创建同名分组）
        var collectionDict = new System.Collections.Generic.Dictionary<string, SukiSideMenuItem>();

        // === AOT 改造：不再调用 Assembly.GetTypes()，直接遍历编译期生成的数组 ===
        // 注意：PageRegistry.AllPages 已在生成阶段按 Order 升序排序好
        var pageInfos = PageRegistry.AllPages ?? System.Array.Empty<PageTypeInfo>();

        foreach (var pageInfo in pageInfos)
        {
            try
            {
                var attr = pageInfo.Attribute;
                if (attr == null) continue;

                // 按照 PageAttribute 的分类装配为不同的侧栏菜单结构
                if (attr.IsCollection)
                {
                    var collectionName = string.IsNullOrEmpty(attr.DisplayCollectionName)
                        ? attr.DisplayPageName
                        : attr.DisplayCollectionName;

                    // 若同名集合已存在：把当前页面作为子项追加到其中
                    if (collectionDict.TryGetValue(collectionName, out var existing))
                    {
                        var child = new SukiSideMenuItem
                        {
                            Header = attr.DisplayPageName,
                            Icon = new MaterialIcon { Kind = attr.Icon, Width = 16, Height = 16 }
                        };
                        existing.Items.Add(child);
                        MenuToPageTypeMap[child] = pageInfo;
                        continue;
                    }

                    // 创建一个新的集合分组项
                    var collectionItem = new SukiSideMenuItem
                    {
                        Header = collectionName,
                        IsContentMovable = true,
                        Icon = new MaterialIcon { Kind = attr.Icon, Width = 16, Height = 16 }
                    };
                    collectionDict[collectionName] = collectionItem;
                    menuItems.Add(collectionItem);

                    // 把当前页面作为默认子项（集合下默认打开的页面）
                    var defaultChild = new SukiSideMenuItem
                    {
                        Header = attr.DisplayPageName,
                        IsContentMovable = false,
                        Icon = new MaterialIcon { Kind = attr.Icon, Width = 16, Height = 16 }
                    };
                    collectionItem.Items.Add(defaultChild);
                    MenuToPageTypeMap[defaultChild] = pageInfo;
                }
                else if (!string.IsNullOrEmpty(attr.CollectionName)
                         && collectionDict.TryGetValue(attr.CollectionName, out var parent))
                {
                    // 属于已有集合：将其作为子项添加
                    var child = new SukiSideMenuItem
                    {
                        Header = attr.DisplayPageName,
                        IsContentMovable = false,
                        Icon = new MaterialIcon { Kind = attr.Icon, Width = 16, Height = 16 }
                    };
                    parent.Items.Add(child);
                    MenuToPageTypeMap[child] = pageInfo;
                }
                else
                {
                    // 顶层独立菜单项
                    var topItem = new SukiSideMenuItem
                    {
                        Header = attr.DisplayPageName,
                        IsContentMovable = false,
                        Icon = new MaterialIcon { Kind = attr.Icon, Width = 16, Height = 16 }
                    };
                    menuItems.Add(topItem);
                    MenuToPageTypeMap[topItem] = pageInfo;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PageDiscovery] 装配页面 {pageInfo.PageType?.Name} 失败: {ex.Message}");
            }
        }

        return menuItems;
    }

    /// <summary>
    /// 按需创建或从缓存取回指定菜单项对应的页面实例。
    /// <list type="bullet">
    ///   <item>首次调用：编译期生成的 <see cref="PageRegistry.Factories"/> →
    ///         调用 InitializePage 注入共享服务 → 缓存实例。</item>
    ///   <item>后续调用：直接返回缓存实例（单例模型），避免重复构造和重复事件订阅。</item>
    /// </list>
    /// <b>AOT 改造：</b>不再使用 <c>Activator.CreateInstance(Type)</c> 动态构造，
    /// 改为使用编译期注册的强类型工厂（<c>new TPage()</c>）。
    /// </summary>
    /// <param name="menuItem">用户当前点击选中的侧栏菜单项（子级）。</param>
    /// <returns>已初始化的页面 UserControl 实例；找不到对应元数据则返回 null。</returns>
    public static object? LazyCreatePage(SukiSideMenuItem menuItem)
    {
        // 命中缓存：直接返回已创建过的页面实例（避免重复初始化事件订阅）
        if (MenuToPageInstanceMap.TryGetValue(menuItem, out var cached))
            return cached;

        // 未命中：需要类型元信息来构造
        if (!MenuToPageTypeMap.TryGetValue(menuItem, out var info))
            return null;

        try
        {
            // === AOT 改造：用编译期生成的工厂字典替换 Activator.CreateInstance ===
            UserControl? pageInstance;
            if (PageRegistry.Factories != null
                && PageRegistry.Factories.TryGetValue(info.PageType, out var factory))
            {
                pageInstance = factory();
            }
            else
            {
                // 极端兜底：工厂未注册时（理论上不会发生，因为生成器会为所有 [Page] 项写入字典）
                Console.WriteLine($"[PageDiscovery] 未在 PageRegistry.Factories 中找到类型 {info.PageType.FullName}，回退跳过。");
                return null;
            }

            if (pageInstance == null)
                return null;

            // 若页面实现了 INavigatedPage，则注入共享服务（只在首次创建时执行一次）
            if (pageInstance is INavigatedPage navPage)
            {
                // Toast/Dialog 管理器由宿主在启动时通过 InitializeSharedServices 注入；
                // 若尚未注入则不创建页面，避免向页面传递 null 依赖。
                if (SharedToastManager == null || SharedDialogManager == null)
                {
                    Console.WriteLine("[PageDiscovery] Toast/Dialog 共享管理器尚未初始化，跳过页面创建。");
                    return null;
                }
                navPage.InitializePage(SharedToastManager, SharedDialogManager, SharedConnection);
            }

            // 写入实例缓存
            MenuToPageInstanceMap[menuItem] = pageInstance;
            return pageInstance;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PageDiscovery] 创建页面 {info.PageType.Name} 失败: {ex.Message}");
            return null;
        }
    }
}
