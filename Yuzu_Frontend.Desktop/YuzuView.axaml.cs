using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using SukiUI.Controls;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Services;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop;

/// <summary>
/// 应用主窗体。持有应用全局单例的 ViewModel，然后在页面间共享。
/// 负责装配侧栏菜单、处理页面切换事件，并提供属性变更通知以驱动 UI 响应。
/// </summary>
public partial class YuzuView : SukiWindow, INotifyPropertyChanged
{
    /// <summary>
    /// 全局 Toast 提示管理器。也同时绑定到 SukiWindow 的宿主，以便在页面中调用以显示提示。
    /// </summary>
    public ISukiToastManager ToastManager { get; } = new SukiToastManager();

    /// <summary>
    /// 全局对话框管理器。用于在桌面窗口中弹出模态对话框、确认框等。
    /// </summary>
    public ISukiDialogManager DialogManager { get; } = new SukiDialogManager();

    /// <summary>
    /// 全局共享的连接视图模型。负责与后端的 WebSocket/HTTP 通信、连接状态管理以及数据包分发。
    /// </summary>
    public ConnectionViewModel ConnectionViewModel { get; } = new ConnectionViewModel();

    public ReactiveCommand<string, Unit> OpenUrlCommand { get; }

    private object? _activeContent;

    /// <summary>
    /// 当前处于激活状态的页面内容（即显示在主内容区域的 <see cref="Avalonia.Controls.UserControl"/>）。
    /// 设置此属性会触发 <see cref="INotifyPropertyChanged.PropertyChanged"/> 通知。
    /// </summary>
    public object? ActiveContent
    {
        get => _activeContent;
        set => SetProperty(ref _activeContent, value);
    }

    /// <summary>
    /// 侧栏菜单项到页面实例的映射。由 <see cref="Services.PageDiscoveryService"/> 填充，
    /// 并在选中菜单项时用于切换 <see cref="ActiveContent"/>。
    /// </summary>
    private readonly Dictionary<SukiSideMenuItem, object?> _menuItemToPageMap = new();

    /// <summary>
    /// 初始化 <see cref="YuzuView"/> 的新实例：
    /// 创建共享 ViewModel、将 Toast/Dialog 管理器注入到 <see cref="ConnectionViewModel"/>，
    /// 加载 XAML 并绑定数据上下文。侧栏菜单与页面装配延迟到 <see cref="Loaded"/> 事件中进行。
    /// </summary>
    public YuzuView()
    {
        OpenUrlCommand = ReactiveCommand.Create<string>(url =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        });

        // 把窗体宿主注入到 ConnectionViewModel，以便连接日志显示 Toast
        ConnectionViewModel.ToastManager = ToastManager;
        ConnectionViewModel.DialogManager = DialogManager;

        InitializeComponent();
        DataContext = this;

        // 侧栏菜单初始化延迟到 Loaded 事件
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 加载与本窗体关联的 XAML 资源。
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 窗体首次加载完成后的处理：取消事件订阅并触发页面发现与装配。
    /// </summary>
    /// <param name="sender">事件发送者（本窗体）。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        InitializePages();
    }

    /// <summary>
    /// 调用 <see cref="PageDiscoveryService.DiscoverPages"/> 扫描程序集中的页面，
    /// 装配为侧栏菜单项并添加到窗体；随后默认选中第一项（或其子项）并显示对应页面内容。
    /// </summary>
    private void InitializePages()
    {
        var sideMenu = this.FindControl<SukiSideMenu>("SideMenu");
        if (sideMenu == null) return;

        // 发现所有 Page 并注入共享 ViewModel
        var pages = PageDiscoveryService.DiscoverPages(
            ToastManager,
            DialogManager,
            ConnectionViewModel,
            _menuItemToPageMap);

        foreach (var item in pages)
        {
            sideMenu.Items.Add(item);
            // 为每一个子项递归添加事件处理
            AddSelectionHandler(item);
        }

        // 默认选中第一个可用子项
        if (sideMenu.Items.Count > 0 && sideMenu.Items[0] is SukiSideMenuItem first)
        {
            if (first.Items.Count > 0 && first.Items[0] is SukiSideMenuItem firstChild)
            {
                first.IsExpanded = true;
                firstChild.IsSelected = true;
                if (_menuItemToPageMap.TryGetValue(firstChild, out var content1))
                    ActiveContent = content1;
            }
            else
            {
                first.IsSelected = true;
                if (_menuItemToPageMap.TryGetValue(first, out var content2))
                    ActiveContent = content2;
            }
        }
    }

    /// <summary>
    /// 递归地为侧栏菜单项及其子项添加选择事件处理：
    /// 当某个项被选中时，展开容器或切换 <see cref="ActiveContent"/>，
    /// 从而在 UI 线程中实现页面的切换。
    /// </summary>
    /// <param name="menuItem">要附加事件处理的侧栏菜单项。</param>
    private void AddSelectionHandler(SukiSideMenuItem menuItem)
    {
        menuItem.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == nameof(SukiSideMenuItem.IsSelected) && menuItem.IsSelected)
            {
                // 容器项：展开自身并触发第一个子项
                if (menuItem.Items.Count > 0 && menuItem.Items[0] is SukiSideMenuItem firstChild)
                {
                    menuItem.IsExpanded = true;
                    // 避免递归触发自身选中逻辑
                    if (!firstChild.IsSelected)
                        firstChild.IsSelected = true;
                    return;
                }

                if (_menuItemToPageMap.TryGetValue(menuItem, out var content))
                {
                    Dispatcher.UIThread.Post(() => ActiveContent = content);
                }
            }
        };

        foreach (var child in menuItem.Items)
        {
            if (child is SukiSideMenuItem childItem)
                AddSelectionHandler(childItem);
        }
    }

    /// <summary>
    /// 属性变更通知事件。由视图绑定使用，当 <see cref="ActiveContent"/> 等属性变化时触发。
    /// </summary>
    public new event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 属性变更通知辅助方法：比较字段旧值与新值，若不同则更新字段并触发 <see cref="PropertyChanged"/>。
    /// </summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">要更新的字段引用。</param>
    /// <param name="value">要设置的新值。</param>
    /// <param name="propertyName">属性名称，默认由 <see cref="CallerMemberNameAttribute"/> 自动提供。</param>
    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
