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

/// <summary>
/// 新增 MC 服务端管理器的视图模型。
/// 负责收集表单输入、字段验证、向后端发起创建/修改管理器请求，
/// 并订阅后端返回的数据包以同步更新 UI 状态。
/// </summary>
public class MCServerManagerAddViewModel : ViewModelBase
{
    private ConnectionViewModel? _connection;  // 关联的连接视图模型

    private string _mcServerName = "";              // 服务端名称
    private string _mcServerType = "Vanilla";       // 服务端类型，默认 Vanilla
    private string _mcServerDirectory = "";          // 服务端工作目录
    private string _javaPath = "";                  // Java 可执行文件路径
    private string _startUpArguments = "";           // MC 服务端启动参数
    private bool _isSubmitting;                     // 是否正在提交请求
    private bool _hasError;                         // 是否存在错误
    private string _errorMessage = "";              // 错误提示信息
    private bool _isCreating;                      // 是否处于创建新管理器流程中
    private bool _isModifyingAfterCreate;          // 是否在创建成功后正在进行二次修改配置
    private string? _pendingNewManagerId;         // 创建成功后待进行修改的 ManagerId
    private ConnectionBackendEntry? _selectedBackend;  // 当前选中的目标后端

    /// <summary>Connection 事件是否已订阅（防重复订阅）。</summary>
    private bool _connectionEventsSubscribed;

    private readonly RangeObservableCollection<string> _supportedTypes = [];          // 后端支持的服务端类型列表（支持批量 ReplaceAll）
    private readonly RangeObservableCollection<ConnectionBackendEntry> _availableBackends = [];  // 可用的已连接后端列表（支持批量 ReplaceAll）

    /// <summary>关联的连接视图模型，提供后端连接状态与客户端通信能力。</summary>
    public ConnectionViewModel? Connection
    {
        get => _connection;
        set => this.RaiseAndSetIfChanged(ref _connection, value);
    }

    /// <summary>后端支持的服务端类型集合。</summary>
    public ObservableCollection<string> SupportedTypes => _supportedTypes;
    /// <summary>当前可用的已连接后端集合。</summary>
    public ObservableCollection<ConnectionBackendEntry> AvailableBackends => _availableBackends;

    /// <summary>当前选中的目标后端，创建请求将发送至此后端。</summary>
    public ConnectionBackendEntry? SelectedBackend
    {
        get => _selectedBackend;
        set => this.RaiseAndSetIfChanged(ref _selectedBackend, value);
    }

    /// <summary>服务端名称。</summary>
    public string MCServerName
    {
        get => _mcServerName;
        set => this.RaiseAndSetIfChanged(ref _mcServerName, value);
    }

    /// <summary>服务端类型（Vanilla / Forge / Paper 等）。</summary>
    public string MCServerType
    {
        get => _mcServerType;
        set => this.RaiseAndSetIfChanged(ref _mcServerType, value);
    }

    /// <summary>服务端工作目录路径。</summary>
    public string MCServerDirectory
    {
        get => _mcServerDirectory;
        set => this.RaiseAndSetIfChanged(ref _mcServerDirectory, value);
    }

    /// <summary>Java 可执行文件路径。</summary>
    public string JavaPath
    {
        get => _javaPath;
        set => this.RaiseAndSetIfChanged(ref _javaPath, value);
    }

    /// <summary>MC 服务端启动参数。</summary>
    public string StartUpArguments
    {
        get => _startUpArguments;
        set => this.RaiseAndSetIfChanged(ref _startUpArguments, value);
    }

    /// <summary>是否正在提交请求（创建或修改）。</summary>
    public bool IsSubmitting
    {
        get => _isSubmitting;
        set => this.RaiseAndSetIfChanged(ref _isSubmitting, value);
    }

    /// <summary>是否存在验证错误。</summary>
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    /// <summary>错误提示信息。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>是否处于创建新管理器流程中。</summary>
    public bool IsCreating
    {
        get => _isCreating;
        set => this.RaiseAndSetIfChanged(ref _isCreating, value);
    }

    /// <summary>是否允许提交：非提交中且必填字段已填写并选中后端。</summary>
    public bool CanSubmit =>
        !IsSubmitting && 
        !string.IsNullOrWhiteSpace(MCServerName) && 
        !string.IsNullOrWhiteSpace(MCServerDirectory) && 
        !string.IsNullOrWhiteSpace(JavaPath) &&
        SelectedBackend != null &&
        Connection?.HasConnectedBackends == true;

    /// <summary>
    /// 初始化视图模型，绑定连接并注册连接状态事件（幂等）。
    /// </summary>
    /// <param name="connectionViewModel">外部传入的连接视图模型。</param>
    public void Initialize(ConnectionViewModel connectionViewModel)
    {
        Connection = connectionViewModel;

        if (Connection != null && !_connectionEventsSubscribed)
        {
            _connectionEventsSubscribed = true;
            Connection.Connected += OnConnectionConnected;
            Connection.Disconnected += OnConnectionDisconnected;
            Connection.ConnectedBackends.CollectionChanged += OnConnectedBackendsChanged;
            SyncAvailableBackends();
        }
    }

    /// <summary>连接成功回调：加载支持类型、同步后端列表并重置表单。</summary>
    private void OnConnectionConnected()
    {
        LoadSupportedTypes();
        SyncAvailableBackends();
        ResetForm();
    }

    /// <summary>已连接后端集合变化回调：重新同步可用后端列表。</summary>
    private void OnConnectedBackendsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SyncAvailableBackends();
    }

    /// <summary>
    /// 将 Connection 中已连接的后端同步到 AvailableBackends 集合。
    /// 在 UI 线程执行，并自动选中第一个后端（若未选中）。
    /// </summary>
    private void SyncAvailableBackends()
    {
        // 后端列表同步：DataBind 级 Normal（影响下拉选中）
        Dispatcher.UIThread.Post(() =>
        {
            // 批量替换：Clear + N Add → 1 次 Reset 通知
            _availableBackends.ReplaceAll(Connection?.ConnectedBackends);

            // 列表非空但未选中后端时默认选中第一项；列表为空时清空选中项
            if (_availableBackends.Count > 0 && SelectedBackend == null)
            {
                SelectedBackend = _availableBackends[0];
            }
            else if (_availableBackends.Count == 0)
            {
                SelectedBackend = null;
            }

            this.RaisePropertyChanged(nameof(CanSubmit));
        });
    }

    /// <summary>连接断开回调：标记错误状态并停止提交。</summary>
    private void OnConnectionDisconnected()
    {
        HasError = true;
        ErrorMessage = "请先连接到后端";
        IsSubmitting = false;
    }

    /// <summary>当前订阅事件的客户端（多后端场景下需精确匹配 SelectedBackend）。</summary>
    private NativeClient? _subscribedPackClient;

    /// <summary>
    /// 订阅后端返回的数据包：支持类型、创建/修改结果、错误信息。
    /// 使用 SelectedBackend 对应的客户端精确订阅，避免多后端场景下接收错误后端的响应。
    /// </summary>
    public void SubscribeToDataPacks()
    {
        UnsubscribeFromDataPacks();

        NativeClient? client = null;
        if (SelectedBackend != null && Connection != null)
        {
            client = Connection.GetClientByBackendId(SelectedBackend.BackendId);
        }
        if (client == null)
        {
            client = Connection?.Client;
        }
        _subscribedPackClient = client;

        if (client != null)
        {
            client.DataPackBus.Subscribe<Pack_SupportedMCServerTypes>(OnSupportedTypesReceived);
            client.DataPackBus.Subscribe<Pack_CreatedNewMCServerManager>(OnCreatedNewManager);
            client.DataPackBus.Subscribe<Pack_ModifiedMCServerManagerConfig>(OnModifiedManagerConfig);
            client.DataPackBus.Subscribe<Pack_CreatNewMCServerManagerFailed>(OnCreateManagerFailed);
            client.DataPackBus.Subscribe<Pack_ErrorInfo>(OnErrorReceived);
        }
    }

    /// <summary>取消订阅所有数据包事件。</summary>
    public void UnsubscribeFromDataPacks()
    {
        if (_subscribedPackClient != null)
        {
            try
            {
                _subscribedPackClient.DataPackBus.Unsubscribe<Pack_SupportedMCServerTypes>(OnSupportedTypesReceived);
                _subscribedPackClient.DataPackBus.Unsubscribe<Pack_CreatedNewMCServerManager>(OnCreatedNewManager);
                _subscribedPackClient.DataPackBus.Unsubscribe<Pack_ModifiedMCServerManagerConfig>(OnModifiedManagerConfig);
                _subscribedPackClient.DataPackBus.Unsubscribe<Pack_CreatNewMCServerManagerFailed>(OnCreateManagerFailed);
                _subscribedPackClient.DataPackBus.Unsubscribe<Pack_ErrorInfo>(OnErrorReceived);
            }
            catch { }
        }
        _subscribedPackClient = null;
    }

    /// <summary>接收到支持的服务端类型列表回调：更新 SupportedTypes 并校正当前选中类型。</summary>
    private void OnSupportedTypesReceived(Pack_SupportedMCServerTypes pack)
    {
        // 支持类型回包：DataBind 级 Normal（影响下拉选中）
        Dispatcher.UIThread.Post(() =>
        {
            // 批量替换：Clear + N Add → 1 次 Reset 通知
            _supportedTypes.ReplaceAll(pack.SupportedMCServerTypes);

            // 当前类型不在支持列表中时回退为第一项
            if (_supportedTypes.Count > 0 && !_supportedTypes.Contains(MCServerType))
            {
                MCServerType = _supportedTypes[0];
            }
        });
    }

    /// <summary>创建新管理器成功回调：两阶段提交的第二阶段。
    /// 后端 CreatNewMCServerManager 返回的是默认配置（空包创建），
    /// 必须紧接着发送 ModifyMCServerManagerConfig 将用户输入的配置写入后端。
    /// </summary>
    private void OnCreatedNewManager(Pack_CreatedNewMCServerManager pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var createdConfig = pack.MCServerManagerConfig;
                if (createdConfig == null)
                {
                    IsSubmitting = false;
                    IsCreating = false;
                    HasError = true;
                    ErrorMessage = "创建管理器成功但后端未返回 ManagerID";
                    ShowToast("创建失败", ErrorMessage, ToastType.Error);
                    return;
                }

                string newManagerId = createdConfig.ManagerID;

                // 回填后端生成的默认值到表单（仅用于 UI 显示）
                MCServerName = createdConfig.MCServerName;
                MCServerType = createdConfig.MCServerType;
                MCServerDirectory = createdConfig.MCServerDirectory;
                JavaPath = createdConfig.JavaPath;
                StartUpArguments = createdConfig.StartUpArguments;

                // ====== 两阶段提交流程：阶段 2 立即修改配置 ======
                // Pack_CreatNewMCServerManager 是空包，后端创建的是默认配置，
                // 用户在表单中填写的配置必须通过 ModifyMCServerManagerConfig 再发送一次
                _pendingNewManagerId = newManagerId;
                _isModifyingAfterCreate = true;
                IsCreating = false;

                var modifyConfig = new MCServerManagerConfig
                {
                    ManagerID = newManagerId,
                    MCServerName = string.IsNullOrWhiteSpace(_mcServerName) ? createdConfig.MCServerName : _mcServerName,
                    MCServerType = string.IsNullOrWhiteSpace(_mcServerType) ? createdConfig.MCServerType : _mcServerType,
                    MCServerDirectory = string.IsNullOrWhiteSpace(_mcServerDirectory) ? createdConfig.MCServerDirectory : _mcServerDirectory,
                    JavaPath = string.IsNullOrWhiteSpace(_javaPath) ? createdConfig.JavaPath : _javaPath,
                    StartUpArguments = _startUpArguments ?? "",
                    BackupManagerConfig = createdConfig.BackupManagerConfig ?? new BackupManagerConfig(),
                    OnlineChattingSystemConfig = createdConfig.OnlineChattingSystemConfig ?? new OnlineChattingSystemConfig()
                };

                if (SelectedBackend == null || Connection == null)
                {
                    IsSubmitting = false;
                    _isModifyingAfterCreate = false;
                    HasError = true;
                    ErrorMessage = "选中后端丢失，无法同步配置";
                    ShowToast("创建部分成功", $"管理器 {newManagerId} 已创建，但配置未同步。请重新进入配置页编辑。", ToastType.Warning);
                    return;
                }

                bool sendOk = Connection.RequestBackendTo(
                    SelectedBackend.BackendId,
                    RequestTypeEnum.ModifyMCServerManagerConfig,
                    new Pack_ModifyMCServerManagerConfig(modifyConfig));

                if (!sendOk)
                {
                    IsSubmitting = false;
                    _isModifyingAfterCreate = false;
                    HasError = true;
                    ErrorMessage = "创建管理器成功但发送修改配置请求失败";
                    ShowToast("创建部分成功", $"管理器 {newManagerId} 已创建，但配置未同步。请重新进入配置页编辑。", ToastType.Warning);
                    return;
                }

                ShowToast("管理器已创建，正在同步配置...", $"ID: {newManagerId}", ToastType.Info);
            }
            catch (Exception ex)
            {
                IsSubmitting = false;
                IsCreating = false;
                _isModifyingAfterCreate = false;
                HasError = true;
                ErrorMessage = $"处理创建成功响应失败: {ex.Message}";
                ShowToast("创建失败", ErrorMessage, ToastType.Error);
            }
        });
    }

    /// <summary>修改管理器配置成功回调：回填字段并提示成功。
    /// 如果是新建管理器后的二次修改成功（_isModifyingAfterCreate=true），
    /// 则结束整个两阶段提交流程并给出最终成功提示。
    /// </summary>
    private void OnModifiedManagerConfig(Pack_ModifiedMCServerManagerConfig pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = false;

            var config = pack.MCServerManagerConfig;
            MCServerName = config.MCServerName;
            MCServerType = config.MCServerType;
            MCServerDirectory = config.MCServerDirectory;
            JavaPath = config.JavaPath;
            StartUpArguments = config.StartUpArguments;

            if (_isModifyingAfterCreate)
            {
                // 两阶段提交完成
                _isModifyingAfterCreate = false;
                _pendingNewManagerId = null;
                ShowToast("创建成功", $"管理器 {config.ManagerID} 已创建并同步配置", ToastType.Success);
            }
            else
            {
                ShowToast("修改成功", $"管理器 {config.ManagerID} 配置已更新", ToastType.Success);
            }
        });
    }

    /// <summary>创建管理器失败回调：标记错误状态并提示。</summary>
    private void OnCreateManagerFailed(Pack_CreatNewMCServerManagerFailed _)
    {
        // 创建失败：DataBind 级 Normal（用户交互反馈要立刻）
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = true;
            ErrorMessage = "创建管理器失败";
            IsCreating = false;
            _isModifyingAfterCreate = false;
            _pendingNewManagerId = null;
            ShowToast("创建失败", ErrorMessage, ToastType.Error);
        });
    }

    /// <summary>接收到后端错误信息回调：标记错误并提示。</summary>
    private void OnErrorReceived(Pack_ErrorInfo pack)
    {
        // 错误回调：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = true;
            ErrorMessage = pack.ErrorInfo ?? "未知错误";
            IsCreating = false;
            _isModifyingAfterCreate = false;
            _pendingNewManagerId = null;
            ShowToast("错误", ErrorMessage, ToastType.Error);
        });
    }

    /// <summary>
    /// 向选中后端请求获取支持的 MC 服务端类型列表。
    /// 优先使用 SelectedBackend，未选时回退到第一个已连接后端。
    /// </summary>
    public void LoadSupportedTypes()
    {
        if (Connection == null || !Connection.HasConnectedBackends)
            return;

        var targetBackend = SelectedBackend ?? Connection.ConnectedBackends.FirstOrDefault();
        if (targetBackend == null)
            return;

        try
        {
            Connection.RequestBackendTo(targetBackend.BackendId, RequestTypeEnum.GetSupportedMCServerTypes);
        }
        catch (Exception ex)
        {
            ShowToast("加载失败", $"获取支持的服务端类型失败: {ex.Message}", ToastType.Error);
        }
    }

    /// <summary>
    /// 提交创建新管理器请求。先进行表单验证，通过后向选中后端发送创建请求。
    /// </summary>
    public void CreateNewManager()
    {
        if (!ValidateForm())
            return;

        IsCreating = true;
        IsSubmitting = true;
        HasError = false;

        if (SelectedBackend == null)
        {
            HasError = true;
            ErrorMessage = "请选择目标后端服务器";
            IsSubmitting = false;
            IsCreating = false;
            return;
        }

        try
        {
            bool success = Connection?.RequestBackendTo(SelectedBackend.BackendId, RequestTypeEnum.CreatNewMCServerManager) ?? false;
            if (success)
            {
                ShowToast("创建中", $"正在向后端 {SelectedBackend.BackendAddress}:{SelectedBackend.BackendPort} 创建新的MC服务端管理器...", ToastType.Info);
            }
            else
            {
                IsSubmitting = false;
                IsCreating = false;
                HasError = true;
                ErrorMessage = $"发送请求到后端失败";
                ShowToast("创建失败", ErrorMessage, ToastType.Error);
            }
        }
        catch (Exception ex)
        {
            IsSubmitting = false;
            IsCreating = false;
            HasError = true;
            ErrorMessage = $"创建失败: {ex.Message}";
            ShowToast("创建失败", ex.Message, ToastType.Error);
        }
    }

    /// <summary>
    /// 提交修改已存在管理器的配置请求。
    /// 向 SelectedBackend 对应的后端发送 ModifyMCServerManagerConfig，避免多后端场景下路由错误。
    /// </summary>
    /// <param name="managerId">待修改管理器的 ID。</param>
    public void ModifyManagerConfig(string managerId)
    {
        if (!ValidateForm())
            return;

        IsSubmitting = true;
        HasError = false;

        if (Connection == null || SelectedBackend == null)
        {
            HasError = true;
            ErrorMessage = "未连接到后端或未选择后端";
            IsSubmitting = false;
            return;
        }

        var config = new MCServerManagerConfig
        {
            ManagerID = managerId,
            MCServerName = MCServerName,
            MCServerType = MCServerType,
            MCServerDirectory = MCServerDirectory,
            JavaPath = JavaPath,
            StartUpArguments = StartUpArguments
        };

        try
        {
            bool sendOk = Connection.RequestBackendTo(
                SelectedBackend.BackendId,
                RequestTypeEnum.ModifyMCServerManagerConfig,
                new Pack_ModifyMCServerManagerConfig(config));
            if (sendOk)
            {
                ShowToast("提交中", "正在提交配置修改...", ToastType.Info);
            }
            else
            {
                IsSubmitting = false;
                HasError = true;
                ErrorMessage = $"向后端发送请求失败";
                ShowToast("提交失败", ErrorMessage, ToastType.Error);
            }
        }
        catch (Exception ex)
        {
            IsSubmitting = false;
            HasError = true;
            ErrorMessage = $"提交失败: {ex.Message}";
            ShowToast("提交失败", ex.Message, ToastType.Error);
        }
    }

    /// <summary>
    /// 校验表单必填字段。失败时累积错误信息并提示。
    /// </summary>
    /// <returns>校验通过返回 true，否则返回 false。</returns>
    private bool ValidateForm()
    {
        var errors = new List<string>();

        if (SelectedBackend == null)
            errors.Add("请选择目标后端服务器");

        if (string.IsNullOrWhiteSpace(MCServerName))
            errors.Add("服务端名称不能为空");

        if (string.IsNullOrWhiteSpace(MCServerDirectory))
            errors.Add("服务端目录不能为空");

        if (string.IsNullOrWhiteSpace(JavaPath))
            errors.Add("Java路径不能为空");

        if (errors.Any())
        {
            HasError = true;
            ErrorMessage = string.Join("\n", errors);
            ShowToast("验证失败", ErrorMessage, ToastType.Warning);
            return false;
        }

        HasError = false;
        return true;
    }

    /// <summary>重置表单为初始状态，并选中第一个可用后端。</summary>
    public void ResetForm()
    {
        MCServerName = "";
        MCServerType = _supportedTypes.Count > 0 ? _supportedTypes[0] : "Vanilla";
        MCServerDirectory = "";
        JavaPath = "";
        StartUpArguments = "";
        SelectedBackend = _availableBackends.Count > 0 ? _availableBackends[0] : null;
        HasError = false;
        ErrorMessage = "";
        IsSubmitting = false;
        IsCreating = false;
    }

    /// <summary>通知外部更新 CanSubmit 属性。</summary>
    public void UpdateSubmitState()
    {
        this.RaisePropertyChanged(nameof(CanSubmit));
    }

    /// <summary>
    /// 释放资源：取消所有 Connection 事件订阅。
    /// 由页面 OnDetachedFromVisualTree 调用，避免内存泄漏。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            if (Connection != null)
            {
                Connection.Connected -= OnConnectionConnected;
                Connection.Disconnected -= OnConnectionDisconnected;
                Connection.ConnectedBackends.CollectionChanged -= OnConnectedBackendsChanged;
                _connectionEventsSubscribed = false;
            }
        }
        base.Dispose(disposing);
    }
}