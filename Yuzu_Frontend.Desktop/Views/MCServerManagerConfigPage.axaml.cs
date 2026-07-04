using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.Models.PropertyGrid;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Views;

/// <summary>
/// 管理器配置页面。展示并编辑当前管理器的基本设置、备份设置、备份排除项、
/// 远程备份及在线聊天等配置区块；同时维护侧栏分类与滚动区域的联动定位。
/// </summary>
[Page("管理器设置", MaterialIconKind.Cog, Order = 3, IsCollection = false)]
public partial class MCServerManagerConfigPage : NavigatedPageBase
{
    /// <summary>页面视图模型，承载配置数据与后端交互逻辑。</summary>
    public MCServerManagerConfigViewModel ViewModel { get; }

    // 当前活动连接的视图模型；用于订阅 ConnectedBackends 集合变化
    private ConnectionViewModel? _connection;

    // 防止 SyncAvailableBackends 重入的递归保护标志
    private bool _isSyncingBackends;

    /// <summary>
    /// 页面初始化入口：注入 Toast/Dialog 管理器，重新绑定 Connection 的事件订阅，
    /// 并触发一次可用后端列表同步。
    /// </summary>
    /// <param name="toastManager">全局 Toast 提示管理器。</param>
    /// <param name="dialogManager">全局对话框管理器。</param>
    /// <param name="connectionViewModel">当前活动连接的视图模型。</param>
    public override void InitializePage(SukiUI.Toasts.ISukiToastManager toastManager, SukiUI.Dialogs.ISukiDialogManager dialogManager, Yuzu_Frontend.Models.ConnectionViewModel? connectionViewModel = null)
    {
        base.InitializePage(toastManager, dialogManager, connectionViewModel);
        ViewModel.ToastManager = toastManager;
        ViewModel.DialogManager = dialogManager;

        // 解除旧连接的事件订阅
        if (_connection != null)
        {
            _connection.ConnectedBackends.CollectionChanged -= OnConnectedBackendsChanged;
        }

        _connection = connectionViewModel ?? (App.Current?.Resources["Connection"] as ConnectionViewModel);
        if (_connection != null)
        {
            _connection.ConnectedBackends.CollectionChanged += OnConnectedBackendsChanged;
        }

        // 订阅 ConnectionViewModel 的全局数据包事件，确保连接成功后能自动刷新管理器列表
        ViewModel.SubscribeConnectionEvents();

        SyncAvailableBackends();
    }

    /// <summary>
    /// 初始化 <see cref="MCServerManagerConfigPage"/> 的新实例：
    /// 创建 ViewModel、加载 XAML，并注册滚动/属性变更事件。
    /// 分区图标已通过 XAML 绑定 SectionNameToIconKindConverter 自动渲染，
    /// 无需再遍历可视化树查找 MaterialIcon 控件，避免 ListBox 项容器未生成时图标为空。
    /// </summary>
    public MCServerManagerConfigPage()
    {
        ViewModel = new MCServerManagerConfigViewModel();
        InitializeComponent();
        DataContext = ViewModel;
        ContentScrollViewer.ScrollChanged += OnScrollChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// 当 Connection.ConnectedBackends 集合变化时，同步到 ViewModel.AvailableBackends。
    /// </summary>
    private void OnConnectedBackendsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncAvailableBackends();
    }

    /// <summary>
    /// 将 Connection.ConnectedBackends 同步到 ViewModel.AvailableBackends，
    /// 并在首次有后端时自动选中第一个。
    /// </summary>
    private void SyncAvailableBackends()
    {
        if (_isSyncingBackends) return;
        _isSyncingBackends = true;

        try
        {
            // 后端下拉同步：Background 优先级（非即时交互）
            Dispatcher.UIThread.Post(() =>
            {
                var source = _connection?.ConnectedBackends;

                // 记录当前选中的后端 ID
                var selectedId = ViewModel.SelectedBackend?.BackendId;

                // 一次性批量替换：Clear + N Add → 1 次 Reset 通知
                if (ViewModel.AvailableBackends is RangeObservableCollection<ConnectionBackendEntry> ranged)
                    ranged.ReplaceAll(source);
                else
                {
                    // 兜底（理论上不会走，因为属性已初始化为 RangeObservableCollection）
                    ViewModel.AvailableBackends.Clear();
                    if (source != null)
                        foreach (var entry in source)
                            ViewModel.AvailableBackends.Add(entry);
                }

                // 恢复或自动选择
                if (selectedId != null)
                {
                    var restored = ViewModel.AvailableBackends.FirstOrDefault(b => b.BackendId == selectedId);
                    if (restored != null)
                    {
                        ViewModel.SelectedBackend = restored;
                    }
                    else if (ViewModel.AvailableBackends.Count > 0)
                    {
                        ViewModel.SelectedBackend = ViewModel.AvailableBackends[0];
                    }
                    else
                    {
                        ViewModel.SelectedBackend = null;
                    }
                }
                else if (ViewModel.AvailableBackends.Count > 0 && ViewModel.SelectedBackend == null)
                {
                    ViewModel.SelectedBackend = ViewModel.AvailableBackends[0];
                }
            });
        }
        finally
        {
            _isSyncingBackends = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.ActiveSection))
        {
            ScrollToSection(ViewModel.ActiveSection);
        }
    }

    /// <summary>滚动事件处理：根据当前滚动位置更新激活的分区项。</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateActiveSection();
    }

    /// <summary>
    /// 计算当前滚动位置所属的分区并更新 ViewModel.ActiveSection。
    /// 判定依据是分区中点是否落在视口中点附近。
    /// </summary>
    private void UpdateActiveSection()
    {
        var scrollViewer = ContentScrollViewer;
        if (scrollViewer == null) return;

        var scrollOffset = scrollViewer.Offset.Y;
        var viewportHeight = scrollViewer.Viewport.Height;
        var halfViewport = viewportHeight / 2;

        // 阶段1：BasicSection 现在包含 PropertyGrid（渲染基本设置/备份设置/在线聊天三个分类），
        // 备份设置和在线聊天区块已合并到 PropertyGrid 中，不再作为独立 GlassCard 存在。
        // 滚动检测仅覆盖三个物理区块，导航跳转时"备份设置"和"在线聊天"会落到 BasicSection。
        var sections = new List<Tuple<string, Control>>
        {
            Tuple.Create("基本设置", (Control)BasicSection),
            Tuple.Create("备份排除", (Control)ExclusionSection),
            Tuple.Create("远程备份", (Control)RemoteBackupSection)
        };

        foreach (var section in sections)
        {
            var sectionOffset = section.Item2.Bounds.Top;
            var sectionHeight = section.Item2.Bounds.Height;

            if (scrollOffset + halfViewport >= sectionOffset &&
                scrollOffset + halfViewport <= sectionOffset + sectionHeight)
            {
                ViewModel.ActiveSection = section.Item1;
                return;
            }
        }
    }

    /// <summary>"浏览服务端目录" 按钮点击事件：弹出文件夹选择器并写回路径。</summary>
    private async void BrowseServerDirectory(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择服务端目录"
        });

        if (result.Count >= 1)
        {
            ViewModel.MCServerDirectory = result[0].Path.LocalPath;
        }
    }

    /// <summary>"浏览 Java 路径" 按钮点击事件：弹出文件选择器（过滤 .exe/.bin）并写回路径。</summary>
    private async void BrowseJavaPath(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

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
        }
    }

    /// <summary>"浏览备份目录" 按钮点击事件：弹出文件夹选择器并写回备份输出目录。</summary>
    private async void BrowseBackupDirectory(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择备份目录"
        });

        if (result.Count >= 1)
        {
            ViewModel.BackupFileOutputDirectory = result[0].Path.LocalPath;
        }
    }

    /// <summary>
    /// PropertyGrid 中 FilePathViewModel 浏览按钮的点击事件处理。
    /// 根据 FilePathViewModel.PickerMode 打开文件夹或文件选择器，
    /// 将选中路径写回 FilePathViewModel.Value（通过 PropertyViewModelBase 双向同步到 FormModel）。
    /// </summary>
    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.DataContext is not FilePathViewModel filePathViewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        if (filePathViewModel.PickerMode == FilePickerMode.Folder)
        {
            var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"选择{filePathViewModel.DisplayName}"
            });

            if (result.Count >= 1)
            {
                filePathViewModel.Value = result[0].Path.LocalPath;
            }
        }
        else
        {
            var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"选择{filePathViewModel.DisplayName}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe", "*.bin" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });

            if (result.Count >= 1)
            {
                filePathViewModel.Value = result[0].Path.LocalPath;
            }
        }
    }

    /// <summary>移除指定的排除文件。</summary>
    private void RemoveExcludedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string file)
        {
            ViewModel.RemoveExcludedFile(file);
        }
    }

    /// <summary>移除指定的排除扩展名。</summary>
    private void RemoveExcludedExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string ext)
        {
            ViewModel.RemoveExcludedExtension(ext);
        }
    }

    /// <summary>移除指定的排除文件夹。</summary>
    private void RemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string folder)
        {
            ViewModel.RemoveExcludedFolder(folder);
        }
    }

    /// <summary>
    /// 页面加载完成：恢复后端列表订阅、同步后端列表，
    /// 并从 GlobalCache 预加载缓存管理器列表。
    /// 页面缓存机制导致 InitializePage 仅在首次创建时调用，重新加载时需在此补全订阅。
    /// 分区图标已由 XAML 绑定 SectionNameToIconKindConverter 直接渲染，无需再初始化。
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 页面从缓存重新加载时，_connection 在 OnUnloaded 中已取消了
        // ConnectedBackends.CollectionChanged 订阅，需要重新绑定以确保后端变化能实时反映。
        if (_connection != null)
        {
            _connection.ConnectedBackends.CollectionChanged -= OnConnectedBackendsChanged;
            _connection.ConnectedBackends.CollectionChanged += OnConnectedBackendsChanged;
        }

        SyncAvailableBackends();

        // 从全局动态缓存（GlobalCache）合并加载所有后端的管理器（兜底）：
        // 当 Connection.ConnectedBackends 尚未同步填充 SelectedBackend 时，也能保证左侧
        // ManagerList 区域立即显示缓存内容，不会出现"有数据却看不到选择控件"的情况。
        try
        {
            // 重要：clearFirst 传 false，避免遍历多个后端时被 _managers.Clear() 互相覆盖
            bool anyCached = false;
            foreach (var backendId in Yuzu_Frontend.Modules.GlobalCache.GetAllBackendIds())
            {
                if (ViewModel.LoadManagersFromCache(backendId, clearFirst: false))
                    anyCached = true;
            }
            if (anyCached && ViewModel.Managers.Count > 0 && ViewModel.SelectedManager == null)
            {
                // 缓存有数据但没选中项时，默认选中第一个，让编辑区立即有内容
                ViewModel.SelectedManager = ViewModel.Managers[0];
            }
        }
        catch
        {
            // 缓存预加载失败不影响主流程（网络请求仍会兜底），静默忽略。
        }
    }

    /// <summary>
    /// 页面卸载：清理事件订阅与 ViewModel 资源，避免内存泄漏。
    /// </summary>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        // 页面卸载时清理订阅，避免内存泄漏
        if (_connection != null)
        {
            _connection.ConnectedBackends.CollectionChanged -= OnConnectedBackendsChanged;
        }
        ViewModel.CleanupSubscriptions();
        ViewModel.Dispose();
    }

    /// <summary>
    /// 将指定分区滚动到视口中。用于点击侧栏分类时跳转到对应区块。
    /// </summary>
    /// <param name="sectionName">目标分区名称。</param>
    public void ScrollToSection(string sectionName)
    {
        var targetSection = FindSectionByName(sectionName);
        if (targetSection != null)
        {
            targetSection.BringIntoView(new Rect(0, 0, targetSection.Bounds.Width, targetSection.Bounds.Height));
        }
    }

    /// <summary>
    /// 根据分区名称查找对应的可视化区块控件。
    /// 阶段1：备份设置和在线聊天已合并到 PropertyGrid 内，统一映射到 BasicSection。
    /// </summary>
    /// <param name="sectionName">分区名称。</param>
    /// <returns>对应的控件；未匹配时返回 null。</returns>
    private Control? FindSectionByName(string sectionName)
    {
        // 阶段1：备份设置和在线聊天已合并到 PropertyGrid（位于 BasicSection 内），
        // 跳转时统一定位到 BasicSection。后续阶段可通过可视化树查找 PropertyGrid 内的 Expander 精确定位。
        return sectionName switch
        {
            "基本设置" => BasicSection,
            "备份设置" => BasicSection,
            "备份排除" => ExclusionSection,
            "远程备份" => RemoteBackupSection,
            "在线聊天" => BasicSection,
            _ => null
        };
    }
}

/// <summary>
/// 备份模式转换器。根据当前 <see cref="PMCSsE_Communicator.BackupTimingMode"/> 枚举值
/// 与传入的 parameter 字符串匹配，决定是否显示对应的备份模式配置区块。
/// </summary>
public class BackupModeConverter : IValueConverter
{
    /// <summary>单例实例，避免重复创建。</summary>
    public static readonly BackupModeConverter Instance = new();

    /// <summary>
    /// 将备份模式枚举值转换为布尔值：若当前模式与 <paramref name="parameter"/> 指定的模式相符则返回 true。
    /// </summary>
    /// <param name="value">当前 <see cref="PMCSsE_Communicator.BackupTimingMode"/> 枚举值。</param>
    /// <param name="parameter">目标模式字符串，取值为 "DayInterval_SpecificTime" 或 "FixedTimeInterval"。</param>
    /// <returns>模式匹配返回 true；否则返回 false。</returns>
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is PMCSsE_Communicator.BackupTimingMode mode && parameter is string param)
        {
            if (param == "DayInterval_SpecificTime")
            {
                return mode == PMCSsE_Communicator.BackupTimingMode.DayInterval_SpecificTime;
            }
            else if (param == "FixedTimeInterval")
            {
                return mode == PMCSsE_Communicator.BackupTimingMode.FixedTimeInterval;
            }
        }
        return false;
    }

    /// <summary>反向转换未实现。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

