using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Linq;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Views;

/// <summary>
/// 管理器总览页面。以 DataGrid 形式列出当前连接的后端下所有 MC 服务端管理器，
/// 支持选中、启动、停止、查看启动参数等操作，并提供分页控件与列宽持久化能力。
/// </summary>
[Page("管理器总览", MaterialIconKind.Server, Order = 1, IsCollection = false)]
public partial class MCServerManagerOverviewPage : NavigatedPageBase
{
    /// <summary>页面视图模型，承载管理器列表、分页状态及后端交互逻辑。</summary>
    public MCServerManagerOverviewViewModel ViewModel { get; } = new();

    // 一次性资源管理器，托管数据包订阅与 DataGrid 事件的生命周期
    private DisposableManager? _disposableManager;

    // 列宽持久化使用的页面唯一键，对应 ColumnWidthStorage 中的配置节
    private const string PageKey = "MCServerManagerOverview";

    // 表头字符串 → DataGridColumn 的映射，用于列宽持久化时按表头读写宽度
    private Dictionary<string, DataGridColumn> _columns = new();

    // 标记当前正在加载已保存的列宽，避免加载过程触发反向保存
    private bool _isLoadingColumnWidths = false;

    // 最近一次指针按下位置（相对于 DataGrid），用于右键菜单选中行
    private Avalonia.Point _lastPointerPos;

    /// <summary>初始化 <see cref="MCServerManagerOverviewPage"/> 的新实例并加载 XAML。</summary>
    public MCServerManagerOverviewPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 页面初始化入口：注入 Toast/Dialog 管理器，并在存在已连接后端时加载管理器列表。
    /// </summary>
    /// <param name="toastManager">全局 Toast 提示管理器。</param>
    /// <param name="dialogManager">全局对话框管理器。</param>
    /// <param name="connectionViewModel">当前活动连接的视图模型。</param>
    public override void InitializePage(ISukiToastManager toastManager, ISukiDialogManager dialogManager, ConnectionViewModel? connectionViewModel = null)
    {
        base.InitializePage(toastManager, dialogManager, connectionViewModel);

        ViewModel.ToastManager = toastManager;
        ViewModel.DialogManager = dialogManager;

        if (connectionViewModel != null)
        {
            ViewModel.Initialize(connectionViewModel);
            if (connectionViewModel.HasConnectedBackends)
            {
                ViewModel.LoadManagers();
            }
        }
    }

    /// <summary>"刷新" 按钮点击事件：重新拉取管理器列表。</summary>
    private void RefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Refresh();
    }

    /// <summary>"加载" 按钮点击事件：加载当前选中的管理器到后端运行时。</summary>
    private void LoadButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.LoadSelectedManager();
    }

    /// <summary>"启动" 按钮点击事件：启动当前选中的 MC 服务端进程。</summary>
    private void StartButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.StartMCServer();
    }

    /// <summary>"停止" 按钮点击事件：停止当前选中的 MC 服务端进程。</summary>
    private void StopButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.StopMCServer();
    }

    /// <summary>"删除" 按钮点击事件：弹出确认对话框，确认后删除选中的管理器。</summary>
    private void DeleteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var manager = ViewModel.SelectedManager;
        if (manager == null) return;

        ViewModel.ShowDialog(
            "确认删除管理器",
            $"确定要删除管理器 \"{manager.MCServerName}\" 吗？\n此操作不可撤销，管理器配置将被永久移除。",
            new ViewModelBase.DialogButton("删除", () => ViewModel.DeleteSelectedManager(), true),
            new ViewModelBase.DialogButton("取消"));
    }

    /// <summary>
    /// 右键菜单打开前：将鼠标所在行设为当前选中项，
    /// 确保 ContextMenu 的操作作用于正确的管理器。
    /// 使用 _lastPointerPos（由 PointerPressed 事件记录）定位行。
    /// Avalonia 无 DataGrid.ContextMenuOpening 事件，改用 ContextMenu.Opened。
    /// </summary>
    private void ManagerContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.PlacementTarget is not DataGrid grid) return;

        var hit = grid.InputHitTest(_lastPointerPos);
        var row = FindAncestor<DataGridRow>(hit as Control);
        if (row?.DataContext is MCServerManagerItemModel model)
        {
            grid.SelectedItem = model;
        }
    }

    /// <summary>沿可视化树向上查找指定类型的祖先控件。</summary>
    private static T? FindAncestor<T>(Control? element) where T : Control
    {
        while (element != null)
        {
            if (element is T result) return result;
            element = element.Parent as Control;
        }
        return null;
    }

    /// <summary>右键菜单：加载管理器。</summary>
    private void LoadMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.LoadSelectedManager();
    }

    /// <summary>右键菜单：启动服务端。</summary>
    private void StartMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.StartMCServer();
    }

    /// <summary>右键菜单：停止服务端。</summary>
    private void StopMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.StopMCServer();
    }

    /// <summary>右键菜单：删除管理器（含确认对话框）。</summary>
    private void DeleteMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var manager = ViewModel.SelectedManager;
        if (manager == null) return;

        ViewModel.ShowDialog(
            "确认删除管理器",
            $"确定要删除管理器 \"{manager.MCServerName}\" 吗？\n此操作不可撤销，管理器配置将被永久移除。",
            new ViewModelBase.DialogButton("删除", () => ViewModel.DeleteSelectedManager(), true),
            new ViewModelBase.DialogButton("取消"));
    }

    /// <summary>
    /// DataGrid 选择变化事件：同步选中项到 ViewModel.SelectedManager。
    /// </summary>
    private void ManagerDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dataGrid && dataGrid.SelectedItem is MCServerManagerItemModel manager)
        {
            ViewModel.SelectedManager = manager;
        }
        else
        {
            ViewModel.SelectedManager = null;
        }
    }

    /// <summary>分页 - 跳转到首页。</summary>
    private void FirstPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToFirst();
    }

    /// <summary>分页 - 跳转到上一页。</summary>
    private void PreviousPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToPrevious();
    }

    /// <summary>分页 - 跳转到下一页。</summary>
    private void NextPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToNext();
    }

    /// <summary>分页 - 跳转到末页。</summary>
    private void LastPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToLast();
    }

    /// <summary>
    /// 附加到可视化树时：创建资源管理器，注册 DataGrid 选择事件与数据包订阅，
    /// 初始化列宽持久化，重新初始化 ViewModel 事件订阅，并加载管理器列表。
    /// 页面缓存机制导致 InitializePage 仅在首次创建时调用，重新挂载时需在此补全订阅。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _disposableManager = new DisposableManager();

        if (this.FindControl<DataGrid>("ManagerDataGrid") is { } dataGrid)
        {
            // 记录指针按下位置，供右键菜单 ContextMenuOpening 时定位行
            dataGrid.PointerPressed += (s, e) => _lastPointerPos = e.GetPosition(dataGrid);
            _disposableManager.RegisterDataGridSelectionChanged(dataGrid, ManagerDataGrid_SelectionChanged);
            InitializeColumnWidths(dataGrid);
        }

        _disposableManager.RegisterDataPackSubscription(
            ViewModel.SubscribeToDataPacks,
            ViewModel.UnsubscribeFromDataPacks
        );

        // 页面从缓存重新挂载时，ViewModel 已在 OnDetachedFromVisualTree 中 Dispose 取消订阅，
        // 需要重新 Initialize 以恢复 Connected、Disconnected、CollectionChanged 等事件订阅，
        // 确保后端连接变化能实时反映到管理器列表。
        if (Connection != null)
        {
            ViewModel.Initialize(Connection);
        }

        ViewModel.LoadManagers();
    }

    /// <summary>
    /// 从可视化树分离时：释放资源管理器、保存列宽、释放 ViewModel 事件订阅（防内存泄漏）。
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _disposableManager?.Dispose();
        _disposableManager = null;

        SaveColumnWidths();

        ViewModel.Dispose();
    }

    /// <summary>
    /// 初始化列宽持久化：缓存所有列的表头→列引用映射，并订阅列宽变化事件。
    /// </summary>
    /// <param name="dataGrid">待持久化列宽的 DataGrid。</param>
    private void InitializeColumnWidths(DataGrid dataGrid)
    {
        dataGrid.Loaded += DataGrid_Loaded;

        _columns.Clear();
        foreach (var column in dataGrid.Columns)
        {
            if (column is DataGridBoundColumn boundColumn && !string.IsNullOrEmpty(boundColumn.Header?.ToString()))
            {
                _columns[boundColumn.Header.ToString()!] = column;
                column.GetObservable(DataGridColumn.WidthProperty).Subscribe(_ => ColumnWidth_Changed());
            }
            else if (column is DataGridTemplateColumn templateColumn && !string.IsNullOrEmpty(templateColumn.Header?.ToString()))
            {
                _columns[templateColumn.Header.ToString()!] = column;
                column.GetObservable(DataGridColumn.WidthProperty).Subscribe(_ => ColumnWidth_Changed());
            }
        }
    }

    /// <summary>
    /// DataGrid 加载完成回调：从 <see cref="ColumnWidthStorage"/> 读取已保存的列宽并应用到对应列。
    /// </summary>
    private void DataGrid_Loaded(object? sender, EventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;

        _isLoadingColumnWidths = true;

        var config = ColumnWidthStorage.Load(PageKey);
        if (config != null)
        {
            foreach (var column in dataGrid.Columns)
            {
                string? header = column.Header?.ToString();
                if (header != null && config.ColumnWidths.TryGetValue(header, out double width))
                {
                    column.Width = new DataGridLength(width);
                }
            }
        }

        _isLoadingColumnWidths = false;
    }

    /// <summary>
    /// 列宽变化回调：跳过加载阶段触发的回调，否则异步保存列宽以避免阻塞 UI。
    /// </summary>
    private void ColumnWidth_Changed()
    {
        if (_isLoadingColumnWidths) return;

        Dispatcher.UIThread.Post(SaveColumnWidths, DispatcherPriority.Background);
    }

    /// <summary>
    /// 将当前所有绝对宽度类型的列宽写入 <see cref="ColumnWidthStorage"/> 进行持久化。
    /// </summary>
    private void SaveColumnWidths()
    {
        if (_columns.Count == 0) return;

        var columnWidths = new Dictionary<string, double>();
        foreach (var kvp in _columns)
        {
            if (kvp.Value.Width.IsAbsolute)
            {
                columnWidths[kvp.Key] = kvp.Value.Width.Value;
            }
        }

        if (columnWidths.Count > 0)
        {
            ColumnWidthStorage.Save(PageKey, columnWidths);
        }
    }
}

/// <summary>
/// 状态颜色转换器。根据 MC 服务端运行状态布尔值返回对应的 SolidColorBrush：
/// 运行中为绿色（#48BB80），未运行为灰色（#BDBDBD）。
/// </summary>
public class StatusColorConverter : IValueConverter
{
    /// <summary>单例实例，避免重复创建。</summary>
    public static StatusColorConverter Instance { get; } = new();

    /// <summary>
    /// 将运行状态转换为对应的颜色画刷。
    /// </summary>
    /// <param name="value">布尔值，true 表示运行中。</param>
    /// <returns>绿色画刷表示运行中；灰色画刷表示未运行或非布尔值。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isRunning)
        {
            return isRunning ? new SolidColorBrush(Color.FromRgb(72, 187, 120)) : new SolidColorBrush(Color.FromRgb(189, 189, 189));
        }
        return new SolidColorBrush(Color.FromRgb(189, 189, 189));
    }

    /// <summary>反向转换未实现。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 运行中管理器数量转换器。统计集合中处于运行状态的管理器数量并以字符串形式返回。
/// </summary>
public class RunningManagerCountConverter : IValueConverter
{
    /// <summary>单例实例，避免重复创建。</summary>
    public static RunningManagerCountConverter Instance { get; } = new();

    /// <summary>
    /// 统计 <paramref name="value"/> 中 IsMCServerRunning 为 true 的项数并转为字符串。
    /// </summary>
    /// <param name="value">管理器项集合，应为 <see cref="IEnumerable{MCServerManagerItemModel}"/>。</param>
    /// <returns>运行中管理器数量的字符串表示；非预期类型时返回 "0"。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is System.Collections.Generic.IEnumerable<MCServerManagerItemModel> managers)
        {
            return managers.Where(m => m.IsMCServerRunning).Count().ToString();
        }
        return "0";
    }

    /// <summary>反向转换未实现。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 已加载管理器数量转换器。统计集合中已加载（IsLoaded）的管理器数量并以字符串形式返回。
/// </summary>
public class LoadedManagerCountConverter : IValueConverter
{
    /// <summary>单例实例，避免重复创建。</summary>
    public static LoadedManagerCountConverter Instance { get; } = new();

    /// <summary>
    /// 统计 <paramref name="value"/> 中 IsLoaded 为 true 的项数并转为字符串。
    /// </summary>
    /// <param name="value">管理器项集合，应为 <see cref="IEnumerable{MCServerManagerItemModel}"/>。</param>
    /// <returns>已加载管理器数量的字符串表示；非预期类型时返回 "0"。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is System.Collections.Generic.IEnumerable<MCServerManagerItemModel> managers)
        {
            return managers.Where(m => m.IsLoaded).Count().ToString();
        }
        return "0";
    }

    /// <summary>反向转换未实现。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}