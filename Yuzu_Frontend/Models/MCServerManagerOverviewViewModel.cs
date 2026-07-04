using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using ReactiveUI;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

/// <summary>
/// MC 服务端管理器概览视图模型。
/// 负责聚合所有后端的管理器列表，提供搜索、状态过滤、排序与分页功能，
/// 并订阅后端推送的管理器状态变更事件（加载、启动、停止、强制终止等）实时更新 UI。
/// </summary>
public class MCServerManagerOverviewViewModel : ViewModelBase
{
    private readonly RangeObservableCollection<MCServerManagerItemModel> _managers = new();          // 全部管理器列表（支持 ReplaceAll/AddRange，避免 N 次通知）
    private readonly RangeObservableCollection<MCServerManagerItemModel> _filteredManagers = new(); // 过滤排序后的管理器列表（ApplyFilterAndSort 中批量替换）

    /// <summary>LoadManagers 冷却时间（默认 10 秒），避免页面快速切换触发重复网络请求。</summary>
    private const int LoadManagersCooldownSeconds = 10;
    /// <summary>最后一次通过网络请求管理器列表的 UTC 时间（Cooldown 控制）。</summary>
    private DateTime _lastLoadManagersNetworkUtc = DateTime.MinValue;
    /// <summary>DataPackReceivedWithBackendId 是否已订阅（防重复订阅）。</summary>
    private bool _dataPackSubscribed;

    /// <summary>构造函数，创建查看启动参数的命令。</summary>
    public MCServerManagerOverviewViewModel()
    {
        ViewArgumentsCommand = ReactiveCommand.Create<MCServerManagerItemModel>(ViewArguments);
    }

    private string _searchText = "";                    // 搜索文本
    private string _selectedStatusFilter = "全部";       // 状态过滤选项
    private string _selectedSortOption = "名称";          // 排序字段
    private string _selectedSortOrder = "升序";          // 排序方向
    private bool _isLoading;                           // 是否正在加载
    private bool _hasError;                            // 是否存在错误
    private string _errorMessage = "";                // 错误信息
    private MCServerManagerItemModel? _selectedManager;  // 当前选中的管理器

    private ConnectionViewModel? _connection;          // 关联的连接视图模型

    /// <summary>分页视图模型，管理当前页的管理器列表。</summary>
    public PaginationViewModel<MCServerManagerItemModel> Pagination { get; } = new();

    /// <summary>查看启动参数命令。</summary>
    public ReactiveCommand<MCServerManagerItemModel, ReactiveUI.Primitives.RxVoid> ViewArgumentsCommand { get; }

    /// <summary>关联的连接视图模型。</summary>
    public ConnectionViewModel? Connection
    {
        get => _connection;
        set
        {
            bool wasEqual = EqualityComparer<ConnectionViewModel?>.Default.Equals(_connection, value);
            this.RaiseAndSetIfChanged(ref _connection, value);
            // 仅当值实际发生变化时刷新依赖属性（互斥可见性、按钮可用性等）
            if (!wasEqual)
            {
                RefreshConnectionDependentProperties();
            }
        }
    }

    /// <summary>全部管理器列表。</summary>
    public ObservableCollection<MCServerManagerItemModel> Managers => _managers;
    /// <summary>过滤排序后的管理器列表。</summary>
    public ObservableCollection<MCServerManagerItemModel> FilteredManagers => _filteredManagers;
    /// <summary>当前分页的管理器列表。</summary>
    public ObservableCollection<MCServerManagerItemModel> PagedManagers => Pagination.PagedItems;

    /// <summary>搜索文本，变更时自动重新过滤排序。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ApplyFilterAndSort();
        }
    }

    /// <summary>状态过滤选项（全部/运行中/已加载/未加载）。</summary>
    public string SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStatusFilter, value);
            ApplyFilterAndSort();
        }
    }

    /// <summary>排序字段（名称/类型/状态/目录）。</summary>
    public string SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSortOption, value);
            ApplyFilterAndSort();
        }
    }

    /// <summary>排序方向（升序/降序）。</summary>
    public string SelectedSortOrder
    {
        get => _selectedSortOrder;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSortOrder, value);
            ApplyFilterAndSort();
        }
    }

    /// <summary>是否正在加载管理器列表。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            bool wasEqual = _isLoading == value;
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            // 仅当值实际变化时刷新互斥状态层、工具栏可用状态等依赖属性
            // （注意：不能用 RaiseAndSetIfChanged 返回值判定变化，其返回的是新值而非 bool 变化标志）
            if (!wasEqual)
            {
                RefreshConnectionDependentProperties();
            }
        }
    }

    /// <summary>是否存在错误。</summary>
    public bool HasError
    {
        get => _hasError;
        set
        {
            bool wasEqual = _hasError == value;
            this.RaiseAndSetIfChanged(ref _hasError, value);
            if (!wasEqual)
            {
                RefreshConnectionDependentProperties();
            }
        }
    }

    /// <summary>错误信息。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>管理器总数。</summary>
    public int ManagerCount => _managers.Count;
    /// <summary>已连接后端数量。</summary>
    public int ConnectedBackendCount => Connection?.ConnectedBackendCount ?? 0;

    /// <summary>是否为空列表状态（非加载中、无错误、列表为空）。</summary>
    public bool IsEmpty => !IsLoading && !HasError && _managers.Count == 0;
    /// <summary>是否有管理器（非加载中、无错误、列表非空）。</summary>
    public bool HasManagers => !IsLoading && !HasError && _managers.Count > 0;
    /// <summary>是否允许刷新（非加载中且已连接）。</summary>
    public bool CanRefresh => !IsLoading && Connection?.IsConnected == true;

    /// <summary>是否显示加载遮罩（加载中且未发生错误，错误状态优先显示）。</summary>
    public bool ShowLoadingOverlay => IsLoading && !HasError;
    /// <summary>是否显示错误状态层（发生错误时优先展示）。</summary>
    public bool ShowErrorState => HasError;
    /// <summary>是否显示空列表状态层（非加载、无错误且列表为空）。</summary>
    public bool ShowEmptyState => !IsLoading && !HasError && _managers.Count == 0;
    /// <summary>是否显示 DataGrid 数据列表（非加载、无错误、非空且已连接后端）。</summary>
    public bool ShowDataGrid => !IsLoading && !HasError && _managers.Count > 0 && (Connection?.IsConnected ?? false);
    /// <summary>工具栏/操作按钮是否可用（已连接后端且非错误状态）。</summary>
    public bool IsToolbarEnabled => Connection?.IsConnected == true && !HasError;

    /// <summary>空列表提示信息（根据连接状态显示不同文案）。</summary>
    public string EmptyMessage => Connection?.IsConnected == true ? "当前后端没有添加任何MC服务端管理器" : "请先连接到后端";

    /// <summary>是否允许加载管理器（已连接后端、已选中且未加载）。</summary>
    public bool CanLoadManager => Connection?.IsConnected == true && _selectedManager != null && !_selectedManager.IsLoaded;
    /// <summary>是否允许启动服务端（已连接后端、已加载且未运行）。</summary>
    public bool CanStartManager => Connection?.IsConnected == true && _selectedManager != null && _selectedManager.IsLoaded && !_selectedManager.IsMCServerRunning;
    /// <summary>是否允许停止服务端（已连接后端且运行中）。</summary>
    public bool CanStopManager => Connection?.IsConnected == true && _selectedManager != null && _selectedManager.IsMCServerRunning;
    /// <summary>是否允许删除管理器（已连接后端、已选中且未运行）。</summary>
    public bool CanDeleteManager => Connection?.IsConnected == true && _selectedManager != null && !_selectedManager.IsMCServerRunning;

    /// <summary>状态过滤可选项列表。</summary>
    public List<string> StatusFilterOptions => new() { "全部", "运行中", "已加载", "未加载" };
    /// <summary>排序字段可选项列表。</summary>
    public List<string> SortOptions => new() { "名称", "类型", "状态", "目录" };
    /// <summary>排序方向可选项列表。</summary>
    public List<string> SortOrderOptions => new() { "升序", "降序" };

    /// <summary>当前选中的管理器，变更时更新操作按钮状态。</summary>
    public MCServerManagerItemModel? SelectedManager
    {
        get => _selectedManager;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedManager, value);
            UpdateSelectedManagerState();
        }
    }

    /// <summary>防止重复订阅 Connection 事件的标记（Initialize 可能从多个入口调用）。</summary>
    private bool _connectionEventsSubscribed;

    /// <summary>
    /// 初始化视图模型，绑定连接并注册连接状态事件（幂等：重复调用不会重复订阅）。
    /// 绑定完成后会立即从 GlobalCache 动态缓存预填充管理器列表（页面打开即显示），
    /// 后续网络回包会作为增量更新（新增/覆盖）。
    /// </summary>
    /// <param name="connectionViewModel">外部传入的连接视图模型。</param>
    public void Initialize(ConnectionViewModel connectionViewModel)
    {
        Connection = connectionViewModel;

        if (Connection != null && !_connectionEventsSubscribed)
        {
            // 先标记再订阅，极端情况下避免重入
            _connectionEventsSubscribed = true;
            Connection.Connected += OnConnectionConnected;
            Connection.Disconnected += OnConnectionDisconnected;
            Connection.DisconnectedWithBackendId += OnConnectionDisconnectedWithBackendId;
            Connection.ConnectedBackends.CollectionChanged += OnConnectedBackendsChanged;
        }

        // 立即从全局缓存预加载（不依赖网络，毫秒级显示）
        LoadManagersFromCache();
    }

    /// <summary>连接成功回调：先尝试缓存兜底，再网络加载最新列表。</summary>
    private void OnConnectionConnected()
    {
        LoadManagersFromCache();
        LoadManagers();
        RefreshConnectionDependentProperties();
    }

    /// <summary>已连接后端集合变化回调：更新已连接后端计数。</summary>
    private void OnConnectedBackendsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ConnectedBackendCount));
    }

    /// <summary>连接断开回调：清空管理器列表并刷新状态属性。</summary>
    private void OnConnectionDisconnected()
    {
        ClearManagers();
        RefreshConnectionDependentProperties();
    }

    /// <summary>
    /// 指定后端断开回调：从列表中移除该后端的所有管理器并更新分页。
    /// </summary>
    /// <param name="backendId">断开的后端 ID。</param>
    private void OnConnectionDisconnectedWithBackendId(string backendId)
    {
        // 后端断开清理：DataBind 级 Normal（列表清空 + 属性刷新影响 UI 选中/按钮状态）
        Dispatcher.UIThread.Post(() =>
        {
            var toRemove = _managers.Where(m => m.BackendId == backendId).ToList();
            foreach (var manager in toRemove)
            {
                _managers.Remove(manager);
                Pagination.RemoveItem(m => m.ManagerId == manager.ManagerId && m.BackendId == manager.BackendId);
            }

            ApplyFilterAndSort();
            RefreshConnectionDependentProperties();
            ShowToast("后端断开", $"已移除来自后端 {backendId} 的管理器", ToastType.Info);
        });
    }

    /// <summary>刷新所有依赖连接状态的属性通知。</summary>
    private void RefreshConnectionDependentProperties()
    {
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(HasManagers));
        this.RaisePropertyChanged(nameof(CanRefresh));
        this.RaisePropertyChanged(nameof(EmptyMessage));
        this.RaisePropertyChanged(nameof(ManagerCount));
        this.RaisePropertyChanged(nameof(ConnectedBackendCount));
        // 互斥状态层可见性
        this.RaisePropertyChanged(nameof(ShowLoadingOverlay));
        this.RaisePropertyChanged(nameof(ShowErrorState));
        this.RaisePropertyChanged(nameof(ShowEmptyState));
        this.RaisePropertyChanged(nameof(ShowDataGrid));
        // 工具栏/按钮可用状态
        this.RaisePropertyChanged(nameof(IsToolbarEnabled));
        // 操作按钮可用性（依赖 Connection.IsConnected 与 SelectedManager）
        this.RaisePropertyChanged(nameof(CanLoadManager));
        this.RaisePropertyChanged(nameof(CanStartManager));
        this.RaisePropertyChanged(nameof(CanStopManager));
        this.RaisePropertyChanged(nameof(CanDeleteManager));
    }

    /// <summary>订阅带后端 ID 的数据包接收事件（幂等：重复调用不会重复订阅）。</summary>
    public void SubscribeToDataPacks()
    {
        if (Connection != null && !_dataPackSubscribed)
        {
            _dataPackSubscribed = true;
            Connection.DataPackReceivedWithBackendId += OnDataPackReceivedWithBackendId;
        }
    }

    /// <summary>取消订阅数据包接收事件。</summary>
    public void UnsubscribeFromDataPacks()
    {
        if (Connection != null && _dataPackSubscribed)
        {
            _dataPackSubscribed = false;
            Connection.DataPackReceivedWithBackendId -= OnDataPackReceivedWithBackendId;
        }
    }

    /// <summary>
    /// 带后端 ID 的数据包接收回调：根据响应类型分发到对应处理方法。
    /// 处理管理器配置列表、加载/停止、启动/关闭/强制终止、配置修改、错误信息等事件。
    /// </summary>
    private void OnDataPackReceivedWithBackendId(string backendId, RespondTypeEnum responseType, object? data)
    {
        switch (responseType)
        {
            case RespondTypeEnum.MCServerManagerConfigs when data is Pack_MCServerManagerConfigs pack:
                OnManagerConfigsReceived(pack, backendId);
                break;
            case RespondTypeEnum.LoadedMCServerManager when data is Pack_LoadedMCServerManager loadedPack:
                OnManagerLoaded(loadedPack);
                break;
            case RespondTypeEnum.StoppedMCServerManager when data is Pack_StoppedMCServerManager stoppedPack:
                OnManagerStopped(stoppedPack);
                break;
            case RespondTypeEnum.RunMCServerSucceed when data is Pack_RunMCServerSucceed startPack:
                OnMCServerStarted(startPack);
                break;
            case RespondTypeEnum.ShutdownMCServerSucceed when data is Pack_ShutdownMCServerSucceed shutdownPack:
                OnMCServerStopped(shutdownPack);
                break;
            case RespondTypeEnum.KillMCServerSucceed when data is Pack_KillMCServerSucceed killPack:
                OnMCServerKilled(killPack);
                break;
            case RespondTypeEnum.ModifiedMCServerManagerConfig when data is Pack_ModifiedMCServerManagerConfig modifiedPack:
                OnManagerConfigModified(modifiedPack);
                break;
            case RespondTypeEnum.DeletedMCServerManager when data is Pack_DeletedMCServerManager deletedPack:
                OnManagerDeleted(deletedPack, backendId);
                break;
            case RespondTypeEnum.ErrorInfo when data is Pack_ErrorInfo errorPack:
                OnErrorReceived(errorPack);
                break;
        }
    }

    /// <summary>
    /// 处理管理器配置列表响应：新增或更新管理器项。
    /// 已存在的管理器更新配置字段，不存在的创建新项并加入分页。
    /// </summary>
    private void OnManagerConfigsReceived(Pack_MCServerManagerConfigs pack, string backendId)
    {
        // 管理器列表回包：DataBind 级 Normal（表格数据需要立刻同步给选中项和按钮状态）
        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = false;
            HasError = false;

            if (pack.MCServerManagerConfigs?.MCServerManagerConfigsList == null)
            {
                return;
            }

            foreach (var config in pack.MCServerManagerConfigs.MCServerManagerConfigsList)
            {
                var existing = _managers.FirstOrDefault(m =>
                    m.BackendId == backendId && m.ManagerId == config.ManagerID);

                if (existing == null)
                {
                    // 新管理器：创建并加入列表与分页
                    var item = new MCServerManagerItemModel(config, backendId);
                    _managers.Add(item);
                    Pagination.AddItem(item);
                }
                else
                {
                    // 已有管理器：更新配置字段
                    existing.MCServerName = config.MCServerName;
                    existing.MCServerType = config.MCServerType;
                    existing.MCServerDirectory = config.MCServerDirectory;
                    existing.JavaPath = config.JavaPath;
                    existing.StartUpArguments = config.StartUpArguments;
                }
            }

            ApplyFilterAndSort();
            ShowToast("管理器数据已更新", ToastType.Success);
        });
    }

    /// <summary>管理器加载成功回调：更新状态为已加载。</summary>
    private void OnManagerLoaded(Pack_LoadedMCServerManager pack)
    {
        // 状态更新（加载/停止/启停）：DataBind 级 Normal（影响按钮可用性）
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isLoaded: true);
            ShowToast("管理器已加载", pack.ManagerID, ToastType.Success);
        });
    }

    /// <summary>管理器停止回调：更新状态为未加载且未运行。</summary>
    private void OnManagerStopped(Pack_StoppedMCServerManager pack)
    {
        // 状态更新：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isLoaded: false, isRunning: false);
            ShowToast("管理器已停止", pack.ManagerID, ToastType.Info);
        });
    }

    /// <summary>MC 服务端启动成功回调：更新状态为运行中。</summary>
    private void OnMCServerStarted(Pack_RunMCServerSucceed pack)
    {
        // 状态更新：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isRunning: true);
            ShowToast("MC服务端已启动", pack.ManagerID, ToastType.Success);
        });
    }

    /// <summary>MC 服务端关闭成功回调：更新状态为未运行。</summary>
    private void OnMCServerStopped(Pack_ShutdownMCServerSucceed pack)
    {
        // 状态更新：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isRunning: false);
            ShowToast("MC服务端已关闭", pack.ManagerID, ToastType.Info);
        });
    }

    /// <summary>MC 服务端强制终止回调：更新状态为未运行。</summary>
    private void OnMCServerKilled(Pack_KillMCServerSucceed pack)
    {
        // 状态更新：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isRunning: false);
            ShowToast("MC服务端已强制终止", pack.ManagerID, ToastType.Warning);
        });
    }

    /// <summary>管理器配置修改回调：更新对应管理器的配置字段并刷新列表。</summary>
    private void OnManagerConfigModified(Pack_ModifiedMCServerManagerConfig pack)
    {
        // 配置修改回包：DataBind 级 Normal（影响选中项和按钮状态）
        Dispatcher.UIThread.Post(() =>
        {
            var config = pack.MCServerManagerConfig;
            var manager = _managers.FirstOrDefault(m => m.ManagerId == config.ManagerID);
            if (manager != null)
            {
                manager.MCServerName = config.MCServerName;
                manager.MCServerType = config.MCServerType;
                manager.MCServerDirectory = config.MCServerDirectory;
                manager.JavaPath = config.JavaPath;
                manager.StartUpArguments = config.StartUpArguments;
                ApplyFilterAndSort();
                UpdateSelectedManagerState();
                ShowToast("配置已更新", config.MCServerName, ToastType.Success);
            }
        });
    }

    /// <summary>错误信息回调：标记错误状态并提示。</summary>
    private void OnErrorReceived(Pack_ErrorInfo pack)
    {
        // 错误状态：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = pack.ErrorInfo ?? "未知错误";
            ShowToast("错误", pack.ErrorInfo ?? "未知错误", ToastType.Error);
        });
    }

    /// <summary>
    /// 更新指定管理器的加载/运行状态，并刷新列表与按钮状态。
    /// </summary>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <param name="isLoaded">是否已加载，null 表示不修改。</param>
    /// <param name="isRunning">是否运行中，null 表示不修改。</param>
    private void UpdateManagerStatus(string managerId, bool? isLoaded = null, bool? isRunning = null)
    {
        var manager = _managers.FirstOrDefault(m => m.ManagerId == managerId);
        if (manager != null)
        {
            if (isLoaded.HasValue) manager.IsLoaded = isLoaded.Value;
            if (isRunning.HasValue) manager.IsMCServerRunning = isRunning.Value;
            ApplyFilterAndSort();
            UpdateSelectedManagerState();
        }
    }

    /// <summary>
    /// 根据搜索文本、状态过滤和排序选项重新生成 FilteredManagers 列表。
    /// 若不在 UI 线程则自动切换到 UI 线程执行。
    /// </summary>
    private void ApplyFilterAndSort()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // 筛选排序：Background 优先级（非即时交互动作，允许滞后）
            Dispatcher.UIThread.Post(ApplyFilterAndSort, DispatcherPriority.Background);
            return;
        }

        var filtered = _managers.AsEnumerable();

        // 按搜索文本过滤（匹配名称或类型）
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchLower = SearchText.ToLower();
            filtered = filtered.Where(m =>
                m.MCServerName.ToLower().Contains(searchLower) ||
                m.MCServerType.ToLower().Contains(searchLower));
        }

        // 按状态过滤
        if (SelectedStatusFilter != "全部")
        {
            filtered = filtered.Where(m =>
            {
                switch (SelectedStatusFilter)
                {
                    case "运行中":
                        return m.IsMCServerRunning;
                    case "已加载":
                        return m.IsLoaded && !m.IsMCServerRunning;
                    case "未加载":
                        return !m.IsLoaded;
                    default:
                        return true;
                }
            });
        }

        // 按选中字段排序
        filtered = SortOptions.IndexOf(SelectedSortOption) switch
        {
            0 => filtered.OrderBy(m => m.MCServerName),
            1 => filtered.OrderBy(m => m.MCServerType),
            2 => filtered.OrderBy(m => m.MCServerStatus),
            3 => filtered.OrderBy(m => m.MCServerDirectory),
            _ => filtered.OrderBy(m => m.MCServerName)
        };

        // 降序时反转结果
        if (SelectedSortOrder == "降序")
        {
            filtered = filtered.Reverse();
        }

        // 一次性批量替换：Clear + N Add → 1 次 Reset 通知
        _filteredManagers.ReplaceAll(filtered);

        // 统一刷新所有依赖属性（含互斥状态层可见性、工具栏可用状态）
        RefreshConnectionDependentProperties();
    }

    /// <summary>更新选中管理器的操作按钮状态。若不在 UI 线程则自动切换。</summary>
    private void UpdateSelectedManagerState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // 按钮可用性刷新：Background 优先级（非关键即时）
            Dispatcher.UIThread.Post(UpdateSelectedManagerState, DispatcherPriority.Background);
            return;
        }

        this.RaisePropertyChanged(nameof(CanLoadManager));
        this.RaisePropertyChanged(nameof(CanStartManager));
        this.RaisePropertyChanged(nameof(CanStopManager));
        this.RaisePropertyChanged(nameof(CanDeleteManager));
    }

    /// <summary>
    /// 加载管理器列表。
    /// 执行顺序：先从 GlobalCache 动态缓存预填充（UI 立即可见），再判断冷却时间决定是否发起网络请求做增量/覆盖更新。
    /// 无已连接后端时仅清空列表。
    /// <br/><b>Cooldown 机制</b>：10 秒内重复调用不重复发网络请求，避免快速切换页面导致的请求风暴。
    /// </summary>
    /// <param name="force">是否强制忽略冷却时间（用户点击手动刷新按钮时传 true）。</param>
    public void LoadManagers(bool force = false)
    {
        if (Connection == null || !Connection.HasConnectedBackends)
        {
            ClearManagers();
            return;
        }

        // 缓存优先：先填本地缓存，表格立即可见，避免等待网络时的空白感
        bool hasCached = LoadManagersFromCache();

        // 判断冷却：10 秒内相同请求不再重复发网络包，除非 force=true
        bool cooldownActive = !force && (DateTime.UtcNow - _lastLoadManagersNetworkUtc).TotalSeconds < LoadManagersCooldownSeconds;
        if (cooldownActive)
        {
            // 冷却期内：缓存已加载则直接显示，不遮罩不发请求
            if (hasCached)
            {
                IsLoading = false;
                HasError = false;
                RefreshConnectionDependentProperties();
            }
            // 只注册数据包订阅（保证后续推送能收到），不发网络请求
            SubscribeToDataPacks();
            return;
        }

        // 记录本次网络请求时间
        _lastLoadManagersNetworkUtc = DateTime.UtcNow;
        IsLoading = true;
        HasError = false;
        RefreshConnectionDependentProperties();
        SubscribeToDataPacks();

        try
        {
            Connection.RequestBackend(RequestTypeEnum.GetMCServerManagersList, new Pack_GetMCServerManagerConfigsList());
        }
        catch (Exception ex)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = $"加载管理器列表失败: {ex.Message}";
            RefreshConnectionDependentProperties();
        }

        // 如果缓存有数据，隐藏 Loading 遮罩，直接显示缓存内容；网络回包会走 OnManagerConfigsReceived 做刷新提示
        if (hasCached)
        {
            IsLoading = false;
            RefreshConnectionDependentProperties();
        }
    }

    /// <summary>手动刷新：强制跳过冷却期。</summary>
    public void Refresh() => LoadManagers(force: true);

    /// <summary>
    /// 从 GlobalCache 全局动态缓存预填充所有后端的管理器列表到 <see cref="_managers"/>。
    /// 按嵌套字典结构遍历：外层所有 BackendId → 每个 Backend 下的所有 ManagerId → 取 Config 构造 ItemModel。
    /// 若某条缓存只保存了元信息没保存完整 Config 则跳过，避免后续分页/筛选出空项。
    /// </summary>
    /// <returns>是否从缓存加载到至少 1 条管理器数据。</returns>
    public bool LoadManagersFromCache()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            bool capturedResult = false;
            // 缓存预填充：DataBind 级 Normal（需要立即显示避免白屏）
            Dispatcher.UIThread.Post(() =>
            {
                capturedResult = LoadManagersFromCache();
            });
            // 同步等待 UI 线程执行完成（因为必须在 UI 线程操作 ObservableCollection）
            return capturedResult;
        }

        // 保留当前选中项，加载完尝试恢复，保持用户操作连续性
        string? prevBackendId = _selectedManager?.BackendId;
        string? prevManagerId = _selectedManager?.ManagerId;

        // 只清理本地集合，不调用 ClearManagers 以免取消订阅事件
        Pagination.ClearItems();

        // 先在内存中构造列表（避免 ObservableCollection 多次通知）
        var loadedItems = new List<MCServerManagerItemModel>();

        // 遍历 GlobalCache 中所有已缓存的后端 ID（嵌套字典外层 key）
        foreach (var backendId in GlobalCache.GetAllBackendIds())
        {
            var managerIds = GlobalCache.BackendConfigCache(backendId).GetManagerIds();
            foreach (var mgrId in managerIds)
            {
                // 按 BackendId + ManagerId 复合索引读取完整配置
                var cacheEntry = GlobalCache.ManagerConfigCache(backendId, mgrId);
                var config = cacheEntry.Config;
                if (config == null)
                {
                    // 缓存只有元信息没存 Config，跳过（PropertyGrid/详情页无法工作）
                    continue;
                }
                loadedItems.Add(new MCServerManagerItemModel(config, backendId));
            }
        }

        int loadedCount = loadedItems.Count;

        // 一次性批量替换 + 批量添加到分页：通知次数从 O(N) 降到 1~2 次
        _managers.ReplaceAll(loadedItems);
        _filteredManagers.Clear();
        if (loadedCount > 0) Pagination.AddItems(loadedItems);

        if (loadedCount > 0)
        {
            ApplyFilterAndSort();
            RefreshConnectionDependentProperties();

            // 尝试恢复上次选中的管理器（按 BackendId + ManagerId 双字段匹配，避免多后端同名 ID 错配）
            if (prevManagerId != null && prevBackendId != null)
            {
                var restored = _managers.FirstOrDefault(m =>
                    m.BackendId == prevBackendId && m.ManagerId == prevManagerId);
                if (restored != null)
                {
                    _selectedManager = null;
                    SelectedManager = restored;
                }
            }
        }

        return loadedCount > 0;
    }

    /// <summary>清空管理器列表、分页与选中状态，并取消订阅。</summary>
    private void ClearManagers()
    {
        UnsubscribeFromDataPacks();
        _managers.Clear();
        _filteredManagers.Clear();
        Pagination.ClearItems();
        IsLoading = false;
        HasError = false;
        SelectedManager = null;
    }

    /// <summary>查看指定管理器的启动参数（弹窗展示）。</summary>
    public void ViewArguments(MCServerManagerItemModel manager)
    {
        ShowMessageDialog("启动参数", manager.StartUpArguments);
    }

    /// <summary>向后端请求加载选中的管理器。</summary>
    public void LoadSelectedManager()
    {
        if (SelectedManager != null && Connection?.Client != null)
        {
            try
            {
                Connection.Client.RequestBackend(RequestTypeEnum.LoadMCServerManager, new Pack_LoadMCServerManager(SelectedManager.ManagerId));
                ShowToast("正在加载管理器", SelectedManager.MCServerName, ToastType.Info);
            }
            catch (Exception ex)
            {
                ShowToast("加载管理器失败", ex.Message, ToastType.Error);
            }
        }
    }

    /// <summary>向后端请求启动选中管理器的 MC 服务端。</summary>
    public void StartMCServer()
    {
        if (SelectedManager != null && Connection?.Client != null)
        {
            try
            {
                Connection.Client.RequestBackend(RequestTypeEnum.RunMCServer, new Pack_RunMCServer(SelectedManager.ManagerId));
                ShowToast("正在启动服务端", SelectedManager.MCServerName, ToastType.Info);
            }
            catch (Exception ex)
            {
                ShowToast("启动服务端失败", ex.Message, ToastType.Error);
            }
        }
    }

    /// <summary>向后端请求关闭选中管理器的 MC 服务端。</summary>
    public void StopMCServer()
    {
        if (SelectedManager != null && Connection?.Client != null)
        {
            try
            {
                Connection.Client.RequestBackend(RequestTypeEnum.ShutdownMCServer, new Pack_ShutdownMCServer(SelectedManager.ManagerId));
                ShowToast("正在停止服务端", SelectedManager.MCServerName, ToastType.Info);
            }
            catch (Exception ex)
            {
                ShowToast("停止服务端失败", ex.Message, ToastType.Error);
            }
        }
    }

    /// <summary>向后端请求删除选中的管理器。</summary>
    public void DeleteSelectedManager()
    {
        if (SelectedManager != null && Connection?.Client != null)
        {
            try
            {
                Connection.Client.RequestBackend(RequestTypeEnum.DeleteMCServerManager, new Pack_DeleteMCServerManager(SelectedManager.ManagerId));
                ShowToast("正在删除管理器", SelectedManager.MCServerName, ToastType.Info);
            }
            catch (Exception ex)
            {
                ShowToast("删除管理器失败", ex.Message, ToastType.Error);
            }
        }
    }

    /// <summary>
    /// 管理器删除成功回调：从本地列表和分页中移除，清除 GlobalCache 缓存，刷新 UI。
    /// </summary>
    private void OnManagerDeleted(Pack_DeletedMCServerManager pack, string backendId)
    {
        // 删除回包：DataBind 级 Normal（影响列表、选中项、按钮状态）
        Dispatcher.UIThread.Post(() =>
        {
            var removed = _managers.FirstOrDefault(m =>
                m.BackendId == backendId && m.ManagerId == pack.ManagerID);
            if (removed != null)
            {
                _managers.Remove(removed);
                Pagination.RemoveItem(m => m.ManagerId == removed.ManagerId && m.BackendId == removed.BackendId);
            }

            // 如果当前选中项被删除，清空选中状态
            if (SelectedManager?.ManagerId == pack.ManagerID && SelectedManager?.BackendId == backendId)
            {
                SelectedManager = null;
            }

            ApplyFilterAndSort();
            RefreshConnectionDependentProperties();
            ShowToast("管理器已删除", pack.ManagerID, ToastType.Success);
        });
    }

    /// <summary>
    /// 释放资源：取消所有 Connection 事件订阅、取消数据包订阅。
    /// 由页面 OnDetachedFromVisualTree 时调用，避免内存泄漏与重复触发。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            if (Connection != null)
            {
                // 取消 Connection 生命周期事件
                Connection.Connected -= OnConnectionConnected;
                Connection.Disconnected -= OnConnectionDisconnected;
                Connection.DisconnectedWithBackendId -= OnConnectionDisconnectedWithBackendId;
                Connection.ConnectedBackends.CollectionChanged -= OnConnectedBackendsChanged;
                _connectionEventsSubscribed = false;

                // 取消数据包订阅（若仍订阅中）
                if (_dataPackSubscribed)
                {
                    Connection.DataPackReceivedWithBackendId -= OnDataPackReceivedWithBackendId;
                    _dataPackSubscribed = false;
                }
            }
        }
        base.Dispose(disposing);
    }
}