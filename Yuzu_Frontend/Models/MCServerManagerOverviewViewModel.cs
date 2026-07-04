using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using ReactiveUI;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

public class MCServerManagerOverviewViewModel : ViewModelBase
{
    private readonly ObservableCollection<MCServerManagerItemModel> _managers = new();
    private readonly ObservableCollection<MCServerManagerItemModel> _filteredManagers = new();

    private string _searchText = "";
    private string _selectedStatusFilter = "全部";
    private string _selectedSortOption = "名称";
    private string _selectedSortOrder = "升序";
    private bool _isLoading;
    private bool _hasError;
    private string _errorMessage = "";
    private MCServerManagerItemModel? _selectedManager;

    private ConnectionViewModel? _connection;

    public PaginationViewModel<MCServerManagerItemModel> Pagination { get; } = new();

    public ConnectionViewModel? Connection
    {
        get => _connection;
        set => this.RaiseAndSetIfChanged(ref _connection, value);
    }

    public ObservableCollection<MCServerManagerItemModel> Managers => _managers;
    public ObservableCollection<MCServerManagerItemModel> FilteredManagers => _filteredManagers;
    public ObservableCollection<MCServerManagerItemModel> PagedManagers => Pagination.PagedItems;

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ApplyFilterAndSort();
        }
    }

    public string SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStatusFilter, value);
            ApplyFilterAndSort();
        }
    }

    public string SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSortOption, value);
            ApplyFilterAndSort();
        }
    }

    public string SelectedSortOrder
    {
        get => _selectedSortOrder;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSortOrder, value);
            ApplyFilterAndSort();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public int ManagerCount => _managers.Count;
    public int ConnectedBackendCount => Connection?.ConnectedBackends.Count ?? 0;

    public bool IsEmpty => !IsLoading && !HasError && _managers.Count == 0;
    public bool HasManagers => !IsLoading && !HasError && _managers.Count > 0;
    public bool CanRefresh => !IsLoading && Connection?.IsConnected == true;

    public string EmptyMessage => Connection?.IsConnected == true ? "当前后端没有添加任何MC服务端管理器" : "请先连接到后端";

    public bool CanLoadManager => _selectedManager != null && !_selectedManager.IsLoaded;
    public bool CanStartManager => _selectedManager != null && _selectedManager.IsLoaded && !_selectedManager.IsMCServerRunning;
    public bool CanStopManager => _selectedManager != null && _selectedManager.IsMCServerRunning;

    public List<string> StatusFilterOptions => new() { "全部", "运行中", "已加载", "未加载" };
    public List<string> SortOptions => new() { "名称", "类型", "状态", "目录" };
    public List<string> SortOrderOptions => new() { "升序", "降序" };

    public MCServerManagerItemModel? SelectedManager
    {
        get => _selectedManager;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedManager, value);
            UpdateSelectedManagerState();
        }
    }

    public void Initialize(ConnectionViewModel connectionViewModel)
    {
        Connection = connectionViewModel;

        if (Connection != null)
        {
            Connection.Connected += OnConnectionConnected;
            Connection.Disconnected += OnConnectionDisconnected;
            Connection.ConnectedBackends.CollectionChanged += OnConnectedBackendsChanged;
        }
    }

    private void OnConnectedBackendsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(ConnectedBackendCount));
    }

    private void OnConnectionConnected()
    {
        LoadManagers();
        RefreshConnectionDependentProperties();
    }

    private void OnConnectionDisconnected()
    {
        ClearManagers();
        RefreshConnectionDependentProperties();
    }

    private void RefreshConnectionDependentProperties()
    {
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(HasManagers));
        this.RaisePropertyChanged(nameof(CanRefresh));
        this.RaisePropertyChanged(nameof(EmptyMessage));
        this.RaisePropertyChanged(nameof(ManagerCount));
        this.RaisePropertyChanged(nameof(ConnectedBackendCount));
    }

    public void SubscribeToDataPacks()
    {
        if (Connection?.Client != null)
        {
            Connection.Client.DataPackBus.Subscribe<Pack_MCServerManagerConfigs>(OnManagerConfigsReceived);
            Connection.Client.DataPackBus.Subscribe<Pack_LoadedMCServerManager>(OnManagerLoaded);
            Connection.Client.DataPackBus.Subscribe<Pack_StoppedMCServerManager>(OnManagerStopped);
            Connection.Client.DataPackBus.Subscribe<Pack_RunMCServerSucceed>(OnMCServerStarted);
            Connection.Client.DataPackBus.Subscribe<Pack_ShutdownMCServerSucceed>(OnMCServerStopped);
            Connection.Client.DataPackBus.Subscribe<Pack_KillMCServerSucceed>(OnMCServerKilled);
            Connection.Client.DataPackBus.Subscribe<Pack_ErrorInfo>(OnErrorReceived);
        }
    }

    public void UnsubscribeFromDataPacks()
    {
        if (Connection?.Client != null)
        {
            Connection.Client.DataPackBus.Unsubscribe<Pack_MCServerManagerConfigs>(OnManagerConfigsReceived);
            Connection.Client.DataPackBus.Unsubscribe<Pack_LoadedMCServerManager>(OnManagerLoaded);
            Connection.Client.DataPackBus.Unsubscribe<Pack_StoppedMCServerManager>(OnManagerStopped);
            Connection.Client.DataPackBus.Unsubscribe<Pack_RunMCServerSucceed>(OnMCServerStarted);
            Connection.Client.DataPackBus.Unsubscribe<Pack_ShutdownMCServerSucceed>(OnMCServerStopped);
            Connection.Client.DataPackBus.Unsubscribe<Pack_KillMCServerSucceed>(OnMCServerKilled);
            Connection.Client.DataPackBus.Unsubscribe<Pack_ErrorInfo>(OnErrorReceived);
        }
    }

    private void OnManagerConfigsReceived(Pack_MCServerManagerConfigs pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = false;
            HasError = false;

            if (pack.MCServerManagerConfigs?.MCServerManagerConfigsList == null)
            {
                ClearManagers();
                return;
            }

            _managers.Clear();
            Pagination.ClearItems();
            foreach (var config in pack.MCServerManagerConfigs.MCServerManagerConfigsList)
            {
                var item = new MCServerManagerItemModel(config);
                _managers.Add(item);
                Pagination.AddItem(item);
            }

            ApplyFilterAndSort();
            ShowToast("管理器数据已更新", ToastType.Success);
        });
    }

    private void OnManagerLoaded(Pack_LoadedMCServerManager pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isLoaded: true);
            ShowToast("管理器已加载", pack.ManagerID, ToastType.Success);
        });
    }

    private void OnManagerStopped(Pack_StoppedMCServerManager pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isLoaded: false, isRunning: false);
            ShowToast("管理器已停止", pack.ManagerID, ToastType.Info);
        });
    }

    private void OnMCServerStarted(Pack_RunMCServerSucceed pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isRunning: true);
            ShowToast("MC服务端已启动", pack.ManagerID, ToastType.Success);
        });
    }

    private void OnMCServerStopped(Pack_ShutdownMCServerSucceed pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isRunning: false);
            ShowToast("MC服务端已关闭", pack.ManagerID, ToastType.Info);
        });
    }

    private void OnMCServerKilled(Pack_KillMCServerSucceed pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateManagerStatus(pack.ManagerID, isRunning: false);
            ShowToast("MC服务端已强制终止", pack.ManagerID, ToastType.Warning);
        });
    }

    private void OnErrorReceived(Pack_ErrorInfo pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = pack.ErrorInfo ?? "未知错误";
            ShowToast("错误", pack.ErrorInfo ?? "未知错误", ToastType.Error);
        });
    }

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

    private void ApplyFilterAndSort()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyFilterAndSort);
            return;
        }

        _filteredManagers.Clear();

        var filtered = _managers.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchLower = SearchText.ToLower();
            filtered = filtered.Where(m =>
                m.MCServerName.ToLower().Contains(searchLower) ||
                m.MCServerType.ToLower().Contains(searchLower));
        }

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

        filtered = SortOptions.IndexOf(SelectedSortOption) switch
        {
            0 => filtered.OrderBy(m => m.MCServerName),
            1 => filtered.OrderBy(m => m.MCServerType),
            2 => filtered.OrderBy(m => m.MCServerStatus),
            3 => filtered.OrderBy(m => m.MCServerDirectory),
            _ => filtered.OrderBy(m => m.MCServerName)
        };

        if (SelectedSortOrder == "降序")
        {
            filtered = filtered.Reverse();
        }

        foreach (var manager in filtered)
        {
            _filteredManagers.Add(manager);
        }

        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(HasManagers));
        this.RaisePropertyChanged(nameof(ManagerCount));
        this.RaisePropertyChanged(nameof(EmptyMessage));
        this.RaisePropertyChanged(nameof(CanRefresh));
    }

    private void UpdateSelectedManagerState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateSelectedManagerState);
            return;
        }

        this.RaisePropertyChanged(nameof(CanLoadManager));
        this.RaisePropertyChanged(nameof(CanStartManager));
        this.RaisePropertyChanged(nameof(CanStopManager));
    }

    public void LoadManagers()
    {
        if (Connection?.Client == null || !Connection.IsConnected)
        {
            ClearManagers();
            return;
        }

        IsLoading = true;
        HasError = false;
        RefreshConnectionDependentProperties();
        SubscribeToDataPacks();

        try
        {
            Connection.Client.RequestBackend(RequestTypeEnum.GetMCServerManagersList, new Pack_GetMCServerManagerConfigsList());
        }
        catch (Exception ex)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = $"加载管理器列表失败: {ex.Message}";
            RefreshConnectionDependentProperties();
        }
    }

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

    public void Refresh()
    {
        LoadManagers();
    }

    public void ViewArguments(MCServerManagerItemModel manager)
    {
        ShowMessageDialog("启动参数", manager.StartUpArguments);
    }

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
}