using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Material.Icons;
using Material.Icons.Avalonia;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;

namespace Yuzu_Frontend.Desktop.Views;

/// <summary>
/// 添加 MC 服务端管理器页面。提供表单用于填写新管理器的基本信息（名称、
/// 服务端目录、Java 路径等），并通过 ViewModel 向后端发起创建请求。
/// </summary>
[Page("添加管理器", MaterialIconKind.Plus, Order = 2, IsCollection = false)]
public partial class MCServerManagerAddPage : NavigatedPageBase
{
    /// <summary>当前页面对应的视图模型，负责表单状态管理与后端通信。</summary>
    public MCServerManagerAddViewModel ViewModel { get; } = new();

    // 一次性资源管理器，集中托管数据包订阅的订阅/取消订阅生命周期
    private DisposableManager? _disposableManager;

    /// <summary>
    /// 初始化 <see cref="MCServerManagerAddPage"/> 的新实例：
    /// 加载 XAML，并订阅 ViewModel 的属性变更以联动提交按钮状态。
    /// </summary>
    public MCServerManagerAddPage()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    /// <summary>
    /// 监听 ViewModel 属性变化，当创建状态（IsCreating）发生变化时刷新提交按钮的图标与文本。
    /// </summary>
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsCreating))
        {
            UpdateSubmitButtonState();
        }
    }

    /// <summary>
    /// 根据当前是否处于创建中状态切换提交按钮的图标（Loading/Plus）与文字（创建中.../创建）。
    /// </summary>
    private void UpdateSubmitButtonState()
    {
        if (SubmitIcon != null && SubmitText != null)
        {
            SubmitIcon.Kind = ViewModel.IsCreating ? MaterialIconKind.Loading : MaterialIconKind.Plus;
            SubmitText.Text = ViewModel.IsCreating ? "创建中..." : "创建";
        }
    }

    /// <summary>
    /// 页面初始化入口：注入 Toast/Dialog 管理器，并在存在连接时初始化 ViewModel。
    /// </summary>
    /// <param name="toastManager">全局 Toast 提示管理器。</param>
    /// <param name="dialogManager">全局对话框管理器。</param>
    /// <param name="connectionViewModel">当前活动连接的视图模型；为 null 表示尚未连接。</param>
    public override void InitializePage(ISukiToastManager toastManager, ISukiDialogManager dialogManager, ConnectionViewModel? connectionViewModel = null)
    {
        base.InitializePage(toastManager, dialogManager, connectionViewModel);

        ViewModel.ToastManager = toastManager;
        ViewModel.DialogManager = dialogManager;

        if (connectionViewModel != null)
        {
            ViewModel.Initialize(connectionViewModel);
        }
    }

    /// <summary>
    /// 表单输入变化时调用 ViewModel 重新计算提交按钮可用状态。
    /// </summary>
    private void OnFormInputChanged(object? sender, TextChangedEventArgs e)
    {
        ViewModel.UpdateSubmitState();
    }

    /// <summary>
    /// "浏览服务端目录" 按钮点击事件：弹出文件夹选择器并将路径回写到 ViewModel。
    /// </summary>
    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择服务端目录"
        });

        if (result.Count >= 1)
        {
            ViewModel.MCServerDirectory = result[0].Path.LocalPath;
            ViewModel.UpdateSubmitState();
        }
    }

    /// <summary>
    /// "浏览 Java 路径" 按钮点击事件：弹出文件选择器（过滤 .exe/.bin）并将路径回写到 ViewModel。
    /// </summary>
    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择Java可执行文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java可执行文件") { Patterns = new[] { "*.exe", "*.bin" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        if (result.Count >= 1)
        {
            ViewModel.JavaPath = result[0].Path.LocalPath;
            ViewModel.UpdateSubmitState();
        }
    }

    /// <summary>提交按钮点击事件：触发 ViewModel 创建新管理器。</summary>
    private void SubmitButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.CreateNewManager();
    }

    /// <summary>重置按钮点击事件：清空表单并恢复初始状态。</summary>
    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ResetForm();
    }

    /// <summary>
    /// 附加到可视化树时：创建资源管理器、重新初始化 ViewModel 订阅、并同步后端数据。
    /// 页面缓存机制导致 InitializePage 仅在首次创建时调用，重新挂载时需在此补全订阅和同步。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _disposableManager = new DisposableManager();

        // 注册数据包订阅的创建/销毁回调，Detach 时自动取消订阅
        _disposableManager.RegisterDataPackSubscription(
            ViewModel.SubscribeToDataPacks,
            ViewModel.UnsubscribeFromDataPacks
        );

        // 页面从缓存重新挂载时，ViewModel 已在 OnDetachedFromVisualTree 中 Dispose 取消订阅，
        // 需要重新 Initialize 以恢复 ConnectedBackends.CollectionChanged 等事件订阅，
        // 确保后端连接变化能实时反映到 AvailableBackends 列表。
        if (Connection != null)
        {
            ViewModel.Initialize(Connection);
        }

        // 若已连接，需重新向后端请求支持的服务端类型列表
        if (Connection?.IsConnected == true)
        {
            ViewModel.LoadSupportedTypes();
        }

        UpdateSubmitButtonState();
    }

    /// <summary>
    /// 从可视化树分离时：释放资源管理器（自动取消数据包订阅）、解绑 ViewModel 属性变更事件、
    /// 释放 ViewModel 对 Connection 的事件订阅（防内存泄漏）。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _disposableManager?.Dispose();
        _disposableManager = null;

        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        ViewModel.Dispose();
    }
}