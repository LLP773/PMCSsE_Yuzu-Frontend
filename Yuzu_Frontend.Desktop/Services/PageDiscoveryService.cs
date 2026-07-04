using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
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
/// 并将它们装配为 <see cref="SukiSideMenuItem"/> 侧栏菜单项。
/// 同时为每个页面调用 <see cref="INavigatedPage.InitializePage"/>，
/// 把全局共享的 Toast/Dialog 管理器与业务 ViewModel 注入到页面中。
/// </summary>
public static class PageDiscoveryService
{
    /// <summary>
    /// 扫描当前程序集中带 <see cref="PageAttribute"/> 的 <see cref="UserControl"/> 子类，
    /// 按 <see cref="PageAttribute.Order"/> 排序后装配为 <see cref="SukiSideMenuItem"/> 列表。
    /// 装配策略如下：
    /// <list type="bullet">
    ///   <item><description>若 <see cref="PageAttribute.IsCollection"/> 为 <c>true</c>：
    ///   创建一个可展开的侧栏分组项，将当前页面作为默认子项。</description></item>
    ///   <item><description>若 <see cref="PageAttribute.CollectionName"/> 有值：
    ///   将该页面作为对应分组的子项追加（需分组已被先发现）。</description></item>
    ///   <item><description>其他情况：作为顶层独立菜单项添加。</description></item>
    /// </list>
    /// </summary>
    /// <param name="toastManager">全局的 Toast 提示管理器，将注入到每个页面。</param>
    /// <param name="dialogManager">全局的对话框管理器，将注入到每个页面。</param>
    /// <param name="connectionViewModel">全局连接视图模型，用于后端通信与连接状态。</param>
    /// <param name="serverManagerViewModel">全局服务器管理器视图模型，用于管理器增删改查。</param>
    /// <param name="logsViewModel">全局日志视图模型，用于日志轮询与命令发送。</param>
    /// <param name="menuItemToPageMap">菜单项到页面实例的映射，供宿主在选中菜单项时切换页面。</param>
    /// <returns>装配后的顶层侧栏菜单项集合。</returns>
    public static ObservableCollection<SukiSideMenuItem> DiscoverPages(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel connectionViewModel,
        System.Collections.Generic.Dictionary<SukiSideMenuItem, object?> menuItemToPageMap)
    {
        var menuItems = new ObservableCollection<SukiSideMenuItem>();
        // 用于按名称查找已存在的集合分组（避免重复创建同名分组）
        var collectionDict = new System.Collections.Generic.Dictionary<string, SukiSideMenuItem>();

        var assembly = Assembly.GetExecutingAssembly();

        // 反射：筛选继承自 UserControl 且携带 PageAttribute 的类型，按 Order 排序
        var pageTypes = assembly.GetTypes()
            .Where(t => typeof(UserControl).IsAssignableFrom(t)
                        && t.GetCustomAttribute<PageAttribute>() != null)
            .OrderBy(t => t.GetCustomAttribute<PageAttribute>()?.Order ?? 100)
            .ToList();

        foreach (var pageType in pageTypes)
        {
            try
            {
                var attr = pageType.GetCustomAttribute<PageAttribute>();
                if (attr == null) continue;

                // 创建页面实例
                if (Activator.CreateInstance(pageType) is not UserControl pageInstance)
                    continue;

                // 若页面实现了 INavigatedPage，则注入共享服务
                if (pageInstance is INavigatedPage navPage)
                {
                    navPage.InitializePage(toastManager,dialogManager,connectionViewModel);
                }

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
                        menuItemToPageMap[child] = pageInstance;
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
                    menuItemToPageMap[defaultChild] = pageInstance;
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
                    menuItemToPageMap[child] = pageInstance;
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
                    menuItemToPageMap[topItem] = pageInstance;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PageDiscovery] 初始化页面 {pageType.Name} 失败: {ex.Message}");
            }
        }

        return menuItems;
    }
}
