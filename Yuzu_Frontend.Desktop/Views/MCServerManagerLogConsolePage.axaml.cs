using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CodeWF.LogViewer.Avalonia;
using Material.Icons;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;
using ToastType = Yuzu_Frontend.ViewModels.ToastsViewModel.ToastType;

namespace Yuzu_Frontend.Desktop.Views;

/// <summary>
/// 日志控制台页面。
/// 使用 CodeWF.LogViewer.Avalonia.LogView 控件显示日志，
/// 从 CodeWF.Log.Core.Logger 全局 feed 自动读取，内置级别颜色与虚拟化滚动。
/// </summary>
[Page("日志控制台", MaterialIconKind.FileText, Order = 3, IsCollection = false)]
public partial class MCServerManagerLogConsolePage : NavigatedPageBase
{
    public MCServerManagerLogConsoleViewModel ViewModel { get; } = new();

    private DisposableManager? _disposableManager;

    public MCServerManagerLogConsolePage()
    {
        InitializeComponent();
        DataContext = this;

        // 监听 ViewModel.SelectedManager 变化，同步更新下拉按钮显示摘要
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedManager)
                || e.PropertyName == nameof(ViewModel.HasSelectedManager))
            {
                SyncDropButtonDisplay();
            }
        };

        // 延迟到 Loaded 后再查找 TreeView（此时视觉树已构建完成）
        Loaded += (s, e) =>
        {
            if (this.FindControl<TreeView>("ManagerTreeView") is { } tv)
            {
                tv.SelectionChanged -= ManagerTreeView_SelectionChanged;
                tv.SelectionChanged += ManagerTreeView_SelectionChanged;
            }
            SyncDropButtonDisplay();
        };
    }

    /// <summary>
    /// 当下拉按钮点击时，切换 Popup 显示状态。
    /// </summary>
    private void ManagerTreeDropButton_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Popup>("ManagerTreePopup") is { } popup)
        {
            popup.IsOpen = !popup.IsOpen;
        }
    }

    /// <summary>
    /// TreeView 选择变化：若选中了可选择的 Manager 子节点，则关闭下拉 Popup。
    /// BackendGroupNode（父节点）在 ViewModel 内部已被视为不可选择。
    /// </summary>
    private void ManagerTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedTreeNode is MCServerManagerLogConsoleViewModel.ManagerItemNode)
        {
            if (this.FindControl<Popup>("ManagerTreePopup") is { } popup)
            {
                popup.IsOpen = false;
            }
        }
    }

    /// <summary>
    /// 把当前选中管理器的摘要（名称 + 状态）同步到下拉按钮的 Tag，供 AXAML 中 ContentPresenter 显示。
    /// </summary>
    private void SyncDropButtonDisplay()
    {
        if (this.FindControl<Button>("ManagerTreeDropButton") is { } btn)
        {
            if (ViewModel.SelectedManager == null)
            {
                btn.Tag = null;
            }
            else
            {
                var mgr = ViewModel.SelectedManager;
                btn.Tag = $"{mgr.MCServerName}  [{mgr.MCServerStatus}]";
            }
        }
    }

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

    // ============================================================
    //  工具栏按钮事件
    // ============================================================
    private void ClearLogsButton_Click(object? sender, RoutedEventArgs e)
        => ViewModel.ClearLogsCommand.Execute().Subscribe();

    private void GetOlderLogsButton_Click(object? sender, RoutedEventArgs e)
        => ViewModel.GetOlderLogsCommand.Execute().Subscribe();

    private async void RefreshManagersButton_Click(object? sender, RoutedEventArgs e)
        => await ViewModel.RefreshManagerList();

    // ============================================================
    //  导出
    // ============================================================
    private async void ExportTxtButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志为 TXT",
            SuggestedFileName = $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("文本文档 (*.txt)") { Patterns = new[] { "*.txt" } },
                FilePickerFileTypes.All,
            },
            DefaultExtension = ".txt",
        });
        if (file == null) return;
        try
        {
            await ViewModel.ExportTxtAsync(file.Path.LocalPath);
            ViewModel.ShowToast("导出成功", $"TXT 文件已保存：{file.Name}", ToastType.Success);
        }
        catch (Exception ex)
        {
            ViewModel.ShowToast("导出失败", ex.Message, ToastType.Error);
        }
    }

    private async void ExportCsvButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志为 CSV",
            SuggestedFileName = $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV 表格 (*.csv)") { Patterns = new[] { "*.csv" } },
                FilePickerFileTypes.All,
            },
            DefaultExtension = ".csv",
        });
        if (file == null) return;
        try
        {
            await ViewModel.ExportCsvAsync(file.Path.LocalPath);
            ViewModel.ShowToast("导出成功", $"CSV 文件已保存：{file.Name}", ToastType.Success);
        }
        catch (Exception ex)
        {
            ViewModel.ShowToast("导出失败", ex.Message, ToastType.Error);
        }
    }

    // ============================================================
    //  命令发送
    // ============================================================
    private void CommandTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !string.IsNullOrWhiteSpace(ViewModel.CommandText):
                DoSendCommand();
                e.Handled = true;
                break;
            case Key.Up:
                ViewModel.NavigateCommandHistory(up: true);
                e.Handled = true;
                break;
            case Key.Down:
                ViewModel.NavigateCommandHistory(up: false);
                e.Handled = true;
                break;
        }
    }

    private void SendCommandButton_Click(object? sender, RoutedEventArgs e) => DoSendCommand();

    private void DoSendCommand()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.CommandText)) return;
        ViewModel.SendCommand.Execute(ViewModel.CommandText).Subscribe();
        // 发送后焦点回到输入框，方便连续输入
        Dispatcher.UIThread.Post(() => CommandTextBox?.Focus(), DispatcherPriority.Background);
    }

    // ============================================================
    //  视觉树生命周期
    // ============================================================

    /// <summary>
    /// 附加到可视化树时：创建资源管理器、重新初始化 ViewModel 订阅，
    /// 并根据当前连接状态通知 ViewModel。
    /// 页面缓存机制导致 InitializePage 仅在首次创建时调用，重新挂载时需在此补全订阅。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _disposableManager = new DisposableManager();

        // 设置清空 LogView 的回调，供 ViewModel 在切换管理器或清空日志时调用
        ViewModel.RequestClearLogView = ClearLogView;

        // 页面从缓存重新挂载时，ViewModel 已在 OnDetachedFromVisualTree 中 Dispose 取消订阅，
        // 需要重新 Initialize 以恢复数据包接收等事件订阅。
        if (Connection != null)
        {
            ViewModel.Initialize(Connection);
        }

        var connected = Connection?.IsConnected == true;
        var backendId = Connection?.ConnectedBackends.Count == 1
            ? Connection.ConnectedBackends[0].BackendId
            : null;
        ViewModel.OnViewAttached(connected, backendId);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // 清除回调引用，避免 ViewModel 在 Dispose 后仍尝试访问 UI 控件
        ViewModel.RequestClearLogView = null;

        _disposableManager?.Dispose();
        _disposableManager = null;
        ViewModel.Dispose();
    }

    // ============================================================
    //  LogView 清空
    // ============================================================

    /// <summary>
    /// 清空 LogView 控件的日志显示。
    ///
    /// CodeWF.LogViewer.Avalonia 12.1.0.16 的 LogView 没有公开的 Clear 方法，
    /// 但其上下文菜单中有"清空"项（Header="清空"），绑定到内部的 Clear_OnClick。
    /// Clear_OnClick 通过设置 _clearSequence 过滤旧日志并清空显示，
    /// 是清除 LogView 显示的正确方式。
    ///
    /// 此方法通过 FindControl 找到 LogView 内部的 SelectableTextBlock，
    /// 访问其 ContextMenu 属性，找到"清空"菜单项并触发 Click 事件，
    /// 从而间接调用 LogView 的私有 Clear_OnClick 方法。
    /// 全程使用 Avalonia 公开 API，无反射，AOT 兼容。
    /// </summary>
    private void ClearLogView()
    {
        var logView = this.FindControl<LogView>("LogView");
        if (logView == null) return;

        // LogView 内部的 SelectableTextBlock（命名为 "LogTextView"）持有 ContextMenu
        var textView = logView.FindControl<SelectableTextBlock>("LogTextView");
        if (textView?.ContextMenu is not { } contextMenu) return;

        // 查找"清空"菜单项并触发其 Click 事件
        foreach (var item in contextMenu.Items)
        {
            if (item is MenuItem menuItem &&
                menuItem.Header is string header &&
                (header == "清空" || header == "Clear"))
            {
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return;
            }
        }
    }
}

// ============================================================
//  值转换器集合（全部为强类型静态单例，无反射，AOT 友好）
// ============================================================

/// <summary>发送按钮图标转换器：发送中显示 Loading，空闲显示 Send。</summary>
public class SendButtonIconConverter : IValueConverter
{
    public static SendButtonIconConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b && b ? MaterialIconKind.Loading : MaterialIconKind.Send;
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>发送按钮文本转换器：发送中显示"发送中..."，空闲显示"发送"。</summary>
public class SendButtonTextConverter : IValueConverter
{
    public static SendButtonTextConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b && b ? "发送中..." : "发送";
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>布尔取反转换器。</summary>
public class BoolNegationConverter : IValueConverter
{
    public static BoolNegationConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>int 是否大于 0 的转换器（用于导出按钮可用性）。</summary>
public class IntGreaterThanZeroConverter : IValueConverter
{
    public static IntGreaterThanZeroConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is int n ? n > 0 : (value is long l ? l > 0L : (value is not null));
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
