using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using PMCSsE_Communicator.SharedCodes;
using ReactiveUI;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

/// <summary>
/// 全局连接状态中心。持有 NativeClient 实例，订阅其内部事件并向外部暴露公开事件与命令。
/// </summary>
public class ConnectionViewModel : ViewModelBase
{
    private readonly ConcurrentDictionary<string, NativeClient> _nativeClients = new();              // 客户端键 → NativeClient
    private readonly ConcurrentDictionary<string, ClientEventSubscriptions> _clientEventSubscriptions = new(); // 客户端键 → 事件订阅缓存
    private readonly ConcurrentDictionary<string, string?> _pendingPasswords = new();                // 客户端键 → 待提交密码副本
    private readonly object _connectLock = new();                                                    // 保护连接创建的同步锁
    private string? _backendAddress;                                                                 // 当前后端地址
    private string _backendPort = "";                                                                 // 当前后端端口
    private string? _password = null;                                                                // 当前访问密钥
    private bool _isConnected;                                                                       // 是否已连接
    private bool _isConnecting;                                                                      // 是否正在连接
    private string _connectionStatus = "未连接";                                                     // 连接状态文本
    private string _fingerprintStatus = "未确认";                                                    // RSA 指纹校验状态文本
    private string _pendingFingerprint = "";                                                         // 等待用户确认的指纹
    private int _pendingPort;                                                                        // 等待确认的端口
    private string _pendingAddress = "";                                                             // 等待确认的地址
    private bool _savePassword = true;                                                               // 是否保存密码到本地
    private bool _saveToHistory = true;                                                              // 是否记入连接历史
    private bool _selectAllChecked = false;                                                          // 表格全选状态
    private bool _hasSelectedBackends = false;                                                       // 表格是否有选中项

    /// <summary>连接日志（可用于 ListBox 显示）。每条日志包含时间戳与内容。</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new RangeObservableCollection<LogEntry>();

    /// <summary>已连接的后端条目列表（多后端场景，按地址+端口区分）。</summary>
    public ObservableCollection<ConnectionBackendEntry> ConnectedBackends { get; } = new RangeObservableCollection<ConnectionBackendEntry>();

    /// <summary>是否有已连接的后端（随 <see cref="ConnectedBackends"/> 的变化自动更新）。</summary>
    public bool HasConnectedBackends => ConnectedBackends.Count > 0;

    /// <summary>
    /// 当前已连接的后端服务数量。
    /// 该值随 <see cref="ConnectedBackends"/> 集合的变化自动更新，
    /// 反映所有处于"已连接"状态的后端实例总数。
    /// </summary>
    public int ConnectedBackendCount => ConnectedBackends.Count;

    /// <summary>
    /// 表格"全选"复选框状态（与表格头 CheckBox 双向绑定）。
    /// 设置为 true/false 时会同步列表中所有条目的 <see cref="ConnectionBackendEntry.IsSelected"/>。
    /// </summary>
    public bool SelectAllChecked
    {
        get => _selectAllChecked;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectAllChecked, value);
            // 同步到每一条记录
            foreach (var entry in ConnectedBackends)
                entry.IsSelected = value;
            UpdateHasSelectedBackends();
        }
    }

    /// <summary>表格中是否至少有一条记录被选中（驱动断联按钮的启用状态）。</summary>
    public bool HasSelectedBackends
    {
        get => _hasSelectedBackends;
        private set => this.RaiseAndSetIfChanged(ref _hasSelectedBackends, value);
    }

    /// <summary>根据当前条目的选择状态刷新 <see cref="HasSelectedBackends"/>。</summary>
    private void UpdateHasSelectedBackends()
    {
        HasSelectedBackends = ConnectedBackends.Any(x => x.IsSelected);
    }

    /// <summary>
    /// 监听单个 <see cref="ConnectionBackendEntry"/> 的属性变化，
    /// 当 <see cref="ConnectionBackendEntry.IsSelected"/> 被用户切换时刷新汇总状态。
    /// </summary>
    private void OnBackendEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionBackendEntry.IsSelected))
            UpdateHasSelectedBackends();
    }

    // ============ 公共属性 ============

    /// <summary>获取任意一个已连接的 NativeClient 实例（兼容单实例场景）。</summary>
    public NativeClient? Client => _nativeClients.Values.FirstOrDefault();

    /// <summary>根据客户端键（地址:端口）获取对应的 NativeClient 实例。</summary>
    public NativeClient? GetClientByKey(string key) =>
        _nativeClients.TryGetValue(key, out var client) ? client : null;

    /// <summary>由地址和端口拼接客户端键（地址:端口）。</summary>
    private string GetClientKey(string address, string port) => $"{address}:{port}";

    /// <summary>连接对话框输入的后端地址（与 <see cref="BackendAddress"/> 同字段，仅 UI 绑定用途别名）。</summary>
    public string? ConnectBackendAddress
    {
        get => _backendAddress;
        set => this.RaiseAndSetIfChanged(ref _backendAddress, value);
    }

    /// <summary>连接对话框输入的后端端口（与 <see cref="BackendPort"/> 同字段，UI 绑定别名）。</summary>
    public string ConnectBackendPort
    {
        get => _backendPort;
        set => this.RaiseAndSetIfChanged(ref _backendPort, value);
    }

    /// <summary>连接对话框输入的访问密钥（与 <see cref="BackendPassword"/> 同字段，UI 绑定别名）。</summary>
    public string? ConnectBackendPassword
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    /// <summary>
    /// 向后端发起连接的地址
    /// </summary>
    public string? BackendAddress
    {
        get => _backendAddress;
        set => this.RaiseAndSetIfChanged(ref _backendAddress, value);
    }

    /// <summary>
    /// 向后端发起连接的端口
    /// </summary>
    public string BackendPort
    {
        get => _backendPort;
        set => this.RaiseAndSetIfChanged(ref _backendPort, value);
    }

    /// <summary>
    /// 目标后端设置的访问密钥
    /// </summary>
    public string? BackendPassword
    {
        get => _password;
        set => this.RaiseAndSetIfChanged(ref _password, value);
    }

    /// <summary>
    /// 是否已经与目标服务端建立连接
    /// </summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>
    /// 是否正在与服务端连接
    /// </summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    /// <summary>
    /// 与服务端连接的状态
    /// </summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    /// <summary>
    /// RSA 公钥指纹确认状态[未确认 / 待确认 / 已验证 / 指纹不匹配 / 已拒绝]
    /// </summary>
    public string FingerprintStatus
    {
        get => _fingerprintStatus;
        private set => this.RaiseAndSetIfChanged(ref _fingerprintStatus, value);
    }

    /// <summary>
    /// 是否在本地保存连接密码（勾选框控制）。
    /// 当为 false 时，连接成功后不会将密码写入配置文件。
    /// </summary>
    public bool SavePassword
    {
        get => _savePassword;
        set => this.RaiseAndSetIfChanged(ref _savePassword, value);
    }

    /// <summary>
    /// 是否将本次连接保存到历史记录（勾选框控制）。
    /// 当为 false 时，即使连接成功也不会新增历史记录条目。
    /// </summary>
    public bool SaveToHistory
    {
        get => _saveToHistory;
        set => this.RaiseAndSetIfChanged(ref _saveToHistory, value);
    }

    /// <summary>当前端口字符串是否为合法端口（1-65535）。</summary>
    public bool IsBackendPortValid => int.TryParse(_backendPort, out var p) && p > 0 && p <= 65535;

    /// <summary>
    /// 连接按钮的文本（随连接状态动态变化：连接 / 正在连接... / 断开连接）。
    /// 由 <see cref="IsConnected"/> / <see cref="IsConnecting"/> 的变化驱动刷新。
    /// </summary>
    public string ConnectionButtonText
    {
        get
        {
            if (IsConnecting) return "正在连接...";
            if (IsConnected) return "断开连接";
            return "连接";
        }
    }



    // ============ 公共事件 ============

    /// <summary>
    /// 连接成功建立时触发。
    /// 总是在 UI 线程触发。
    /// </summary>
    public event Action? Connected;

    /// <summary>
    /// 连接断开时触发。
    /// 总是在 UI 线程触发。
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// 连接断开时触发（带后端标识）。(后端标识)。
    /// 总是在 UI 线程触发。后端标识格式为"地址:端口"。
    /// </summary>
    public event Action<string>? DisconnectedWithBackendId;

    /// <summary>
    /// 收到后端数据包时触发。(响应类型, 反序列化后的对象)。
    /// 总是在 UI 线程触发。
    /// </summary>
    public event Action<RespondTypeEnum, object?>? DataPackReceived;

    /// <summary>
    /// 收到后端数据包时触发（带后端标识）。(后端标识, 响应类型, 反序列化后的对象)。
    /// 总是在 UI 线程触发。后端标识为通过 BackendIdManager 分配的稳定 ID，与 GlobalCache 中的 key 一致。
    /// </summary>
    public event Action<string, RespondTypeEnum, object?>? DataPackReceivedWithBackendId;

    /// <summary>
    /// 需要用户输入密码时触发。
    /// </summary>
    public event Action<string>? NeedPassword;

    /// <summary>
    /// 需要用户确认 RSA 公钥指纹时触发。(当前指纹, 记忆指纹, 是否一致?)
    /// </summary>
    public event Action<string, string, bool>? NeedRSAPublicKeyVerification;

    /// <summary>
    /// RSA 公钥指纹被确认通过。
    /// </summary>
    public event Action? RSAFingerprintConfirmed;

    /// <summary>
    /// RSA 公钥指纹被拒绝/不匹配。参数：原因。
    /// </summary>
    public event Action<string>? RSAFingerprintRejected;

    // ============ 命令 ============

    /// <summary>连接命令（仅在未连接/未连接中且端口有效时可执行）。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ConnectCommand { get; }

    /// <summary>断开连接命令（在已连接或正在连接时可执行）。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> DisconnectCommand { get; }

    /// <summary>清空日志列表命令。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ClearLogsCommand { get; }

    /// <summary>
    /// 初始化 <see cref="ConnectionViewModel"/> 新实例。
    /// 构造响应式命令的可执行条件，并挂接 <see cref="ConnectedBackends"/> 集合变更以驱动派生属性。
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "WhenAnyValue 表达式树仅访问本 ViewModel 的公共属性，这些属性经 XAML 编译绑定被静态根保留，AOT 裁剪下安全")]
    public ConnectionViewModel()
    {
        var canConnect = this.WhenAnyValue(
            x => x.IsConnected,
            x => x.IsConnecting,
            x => x.BackendAddress,
            x => x.IsBackendPortValid,
            (connected, connecting, addr, portOk) =>
                !connected && !connecting &&
                !string.IsNullOrWhiteSpace(addr) && portOk
        );

        var canDisconnect = this.WhenAnyValue(
            x => x.IsConnected,
            x => x.IsConnecting,
            (connected, connecting) => connected || connecting
        );

        ConnectCommand = ReactiveCommand.Create(ExecuteConnect, canConnect);
        DisconnectCommand = ReactiveCommand.Create(ExecuteDisconnect, canDisconnect);
        ClearLogsCommand = ReactiveCommand.Create(ClearLogs);

        // 监听已连接后端列表变化，同时为每个条目监听 IsSelected 变化。
        // 这样表格中的复选框状态可驱动 HasSelectedBackends 的刷新。
        ConnectedBackends.CollectionChanged += (_, _) =>
        {
            foreach (var entry in ConnectedBackends)
            {
                entry.PropertyChanged -= OnBackendEntryPropertyChanged;
                entry.PropertyChanged += OnBackendEntryPropertyChanged;
            }
            this.RaisePropertyChanged(nameof(HasConnectedBackends));
            this.RaisePropertyChanged(nameof(ConnectedBackendCount));
            UpdateHasSelectedBackends();
        };

        // 当 IsConnected / IsConnecting 变化时，也触发 ConnectionButtonText 的变更通知
        this.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ConnectionButtonText)));

        try { StaticConfigManagerClass.LoadConfig(); } catch (Exception ex) { AddLogMessage("加载配置失败", ex.Message, ToastType.Warning, false); }
    }

    // ============ 公共 API ============

    /// <summary>
    /// 向后端发送请求（无内容）。根据类型发送对应的空包。
    /// 在多实例模式下，向所有已连接的后端发送请求。
    /// </summary>
    public void RequestBackend(RequestTypeEnum type)
    {
        if (_nativeClients.IsEmpty) return;
        foreach (var client in _nativeClients.Values)
        {
            try
            {
                switch (type)
                {
                    case RequestTypeEnum.GetServerState:
                        client.RequestBackend(type, new Pack_GetServerState());
                        break;
                    case RequestTypeEnum.GetMCServerManagersList:
                        client.RequestBackend(type, new Pack_GetMCServerManagerConfigsList());
                        break;
                    case RequestTypeEnum.GetSupportedMCServerTypes:
                        client.RequestBackend(type, new Pack_GetSupportedMCServerTypes());
                        break;
                    case RequestTypeEnum.CreatNewMCServerManager:
                        client.RequestBackend(type, new Pack_CreatNewMCServerManager());
                        break;
                    case RequestTypeEnum.GetLoadedMCServerManagers:
                        client.RequestBackend(type, new Pack_GetMCServerManager());
                        break;
                    default:
                        client.RequestBackend(type, new Pack_GetMCServerManagerConfigsList());
                        break;
                }
            }
            catch (Exception ex)
            {
                AddLogMessage("发送请求失败", $"发送请求到某个后端失败: {ex.Message}", ToastType.Error);
            }
        }
    }

    /// <summary>向后端发送请求（含内容）。</summary>
    public void RequestBackend<T>(RequestTypeEnum type, T payload) where T : LightProto.IProtoParser<T>
    {
        foreach (var client in _nativeClients.Values)
        {
            try
            {
                client.RequestBackend(type, payload);
            }
            catch (Exception ex)
            {
                AddLogMessage("发送请求失败", $"发送请求到某个后端失败: {ex.Message}", ToastType.Error);
            }
        }
    }

    /// <summary>向后端发送请求（object 重载）。</summary>
    public void RequestBackendWithData(RequestTypeEnum type, object? payload = null)
    {
        if (_nativeClients.IsEmpty) return;
        try
        {
            if (payload == null)
                RequestBackend(type);
            else
                throw new NotSupportedException("RequestBackendWithData 不支持 object 类型参数，请使用 RequestBackend<T> 方法");
        }
        catch (Exception ex)
        {
            AddLogMessage("发送请求失败", $"发送请求失败, {ex.Message}", ToastType.Error);
        }
    }

    /// <summary>
    /// 向指定后端发送请求（无内容）。通过后端 ID 路由到目标 NativeClient 实例。
    /// </summary>
    public bool RequestBackendTo(string backendId, RequestTypeEnum type)
    {
        var client = GetClientByBackendId(backendId);
        if (client == null)
        {
            AddLogMessage("发送请求失败", $"未找到后端 ID 为 {backendId} 的连接", ToastType.Error);
            return false;
        }

        try
        {
            switch (type)
            {
                case RequestTypeEnum.GetMCServerManagersList:
                    client.RequestBackend(type, new Pack_GetMCServerManagerConfigsList());
                    break;
                case RequestTypeEnum.GetSupportedMCServerTypes:
                    client.RequestBackend(type, new Pack_GetSupportedMCServerTypes());
                    break;
                case RequestTypeEnum.CreatNewMCServerManager:
                    client.RequestBackend(type, new Pack_CreatNewMCServerManager());
                    break;
                case RequestTypeEnum.GetLoadedMCServerManagers:
                    client.RequestBackend(type, new Pack_GetMCServerManager());
                    break;
                default:
                    client.RequestBackend(type, new Pack_GetMCServerManagerConfigsList());
                    break;
            }
            return true;
        }
        catch (Exception ex)
        {
            AddLogMessage("发送请求失败", $"发送请求到后端 {backendId} 失败: {ex.Message}", ToastType.Error);
            return false;
        }
    }

    /// <summary>
    /// 向指定后端发送请求（含内容）。通过后端 ID 路由到目标 NativeClient 实例。
    /// </summary>
    public bool RequestBackendTo<T>(string backendId, RequestTypeEnum type, T payload) where T : LightProto.IProtoParser<T>
    {
        var client = GetClientByBackendId(backendId);
        if (client == null)
        {
            AddLogMessage("发送请求失败", $"未找到后端 ID 为 {backendId} 的连接", ToastType.Error);
            return false;
        }

        try
        {
            client.RequestBackend(type, payload);
            return true;
        }
        catch (Exception ex)
        {
            AddLogMessage("发送请求失败", $"发送请求到后端 {backendId} 失败: {ex.Message}", ToastType.Error);
            return false;
        }
    }

    /// <summary>
    /// 通过后端 ID 获取对应的 NativeClient 实例。
    /// </summary>
    public NativeClient? GetClientByBackendId(string backendId)
    {
        var entry = ConnectedBackends.FirstOrDefault(x => 
            string.Equals(x.BackendId, backendId, StringComparison.Ordinal));
        if (entry == null) return null;
        
        var clientKey = GetClientKey(entry.BackendAddress, entry.BackendPort);
        return GetClientByKey(clientKey);
    }

    /// <summary>使用指定参数连接（同步）。</summary>
    public void Connect(string address, int port, string? password = null)
    {
        _backendAddress = address;
        _backendPort = port.ToString();
        _password = string.IsNullOrWhiteSpace(password) ? null : password;
        this.RaisePropertyChanged(nameof(BackendAddress));
        this.RaisePropertyChanged(nameof(BackendPort));
        this.RaisePropertyChanged(nameof(BackendPassword));
        ExecuteConnect();
    }

    /// <summary>
    /// 使用指定地址、端口和密码连接到后端。
    /// 在多实例模式下，允许同时连接到多个不同的后端服务。
    /// 密码会保存到独立的副本字典中，避免被外部操作清空。
    /// </summary>
    public void ConnectToBackend(string address, string port, string? password = null)
    {
        _backendAddress = address;
        _backendPort = port;
        _password = string.IsNullOrWhiteSpace(password) ? null : password;
        
        var clientKey = GetClientKey(address, port);
        var passwordToStore = string.IsNullOrWhiteSpace(password) ? null : password;
        _pendingPasswords.TryAdd(clientKey, passwordToStore);
        
        this.RaisePropertyChanged(nameof(BackendAddress));
        this.RaisePropertyChanged(nameof(BackendPort));
        this.RaisePropertyChanged(nameof(BackendPassword));
        ExecuteConnect();
    }

    /// <summary>使用当前属性值连接（异步，返回是否启动成功）。</summary>
    public async Task<bool> ConnectAsync()
    {
        return await Task.Run(() =>
        {
            ExecuteConnect();
            return IsConnected || IsConnecting;
        });
    }

    /// <summary>断开连接。</summary>
    public void Disconnect() => ExecuteDisconnect();

    /// <summary>
    /// 断开指定地址/端口集合对应的后端连接。
    /// 仅向匹配的 NativeClient 发送断联请求，字典移除、条目清理和状态更新由 <see cref="OnClientDisconnected"/> 异步处理。
    /// </summary>
    public void DisconnectBackends(IEnumerable<ConnectionBackendEntry> entries)
    {
        if (entries == null) return;

        var list = entries.ToList();
        if (list.Count == 0) return;

        foreach (var entry in list)
        {
            var clientKey = GetClientKey(entry.BackendAddress, entry.BackendPort);

            if (_nativeClients.TryGetValue(clientKey, out var client))
            {
                DisconnectClient(clientKey, client);
            }
        }
    }

    // ============ 内部实现 ============

    /// <summary>
    /// 执行连接的核心实现。校验地址/端口 -> 加锁去重 -> 创建 NativeClient -> 订阅事件 -> 发起连接。
    /// </summary>
    private void ExecuteConnect()
    {
        int portNum = 0;
        if (string.IsNullOrWhiteSpace(_backendAddress) ||
            !int.TryParse(_backendPort, out portNum) || portNum <= 0 || portNum > 65535)
        {
            AddLogMessage("无法建立连接", "地址或端口无效，检查后重试", ToastType.Error);
            return;
        }

        lock (_connectLock)
        {
            var clientKey = GetClientKey(_backendAddress, _backendPort);
            if (_nativeClients.ContainsKey(clientKey))
            {
                AddLogMessage("连接失败", $"已存在到 {_backendAddress}:{_backendPort} 的连接", ToastType.Warning);
                return;
            }

            IsConnecting = true;
            ConnectionStatus = "正在连接...";

            try
            {
                var nativeClient = new NativeClient(_backendAddress, portNum);
                _nativeClients.TryAdd(clientKey, nativeClient);
                SubscribeToClientEvents(nativeClient, clientKey);
                nativeClient.Connect();
                AddLogMessage("连接中", $"正在连接 {_backendAddress}:{portNum} ...", ToastType.Info);
            }
            catch (Exception ex)
            {
                _nativeClients.TryRemove(clientKey, out _);
                AddLogMessage("连接失败", $"{ex.Message}", ToastType.Error);
                IsConnecting = false;
            }
        }
    }

    /// <summary>断开命令实现：向所有后端发送断联请求。</summary>
    private void ExecuteDisconnect()
    {
        if (_nativeClients.IsEmpty) return;
        ConnectionStatus = "正在断开...";
        AddLogMessage("断开连接", "正在向后端发送断联请求...", ToastType.Warning);
        DisconnectAll();
    }

    /// <summary>
    /// 断开所有后端连接。
    /// 仅向每个 NativeClient 发送断联请求，不立即清空字典或设置状态——
    /// 每个客户端的 <see cref="NativeClient.Disconnected"/> 事件触发后由 <see cref="OnClientDisconnected"/> 逐个清理，
    /// 当最后一个客户端断开后自动更新整体状态并触发 <see cref="Disconnected"/> 事件。
    /// </summary>
    private void DisconnectAll()
    {
        foreach (var kvp in _nativeClients.ToArray())
        {
            DisconnectClient(kvp.Key, kvp.Value);
        }
        _pendingPasswords.Clear();
    }

    /// <summary>
    /// 向单个 NativeClient 发送断联请求。
    /// 仅调用 <see cref="NativeClient.Disconnect"/>（内部会发送断联包并触发 <see cref="NativeClient.Disconnected"/> 事件），
    /// 不立即取消事件订阅或释放资源——这些由 <see cref="OnClientDisconnected"/> 异步处理，
    /// 确保断联包在实际关闭连接前被发送到后端。
    /// </summary>
    private void DisconnectClient(string clientKey, NativeClient client)
    {
        try
        {
            client.Disconnect();
        }
        catch (Exception ex)
        {
            AddLogMessage("断开连接失败", $"断开 {clientKey} 连接失败: {ex.Message}", ToastType.Error);
            // 异常时兜底清理
            UnsubscribeFromClientEvents(clientKey, client);
            try { client.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 订阅 NativeClient 的事件与各类数据包。
    /// 将事件委托缓存到 <see cref="ClientEventSubscriptions"/> 以便后续精确取消订阅，避免内存泄漏。
    /// </summary>
    private void SubscribeToClientEvents(NativeClient client, string clientKey)
    {
        if (client == null) return;

        var subscriptions = new ClientEventSubscriptions
        {
            Connected = () => OnClientConnected(clientKey),
            Disconnected = reason => OnClientDisconnected(clientKey, reason),
            NeedPassword = () => OnNeedPassword(clientKey),
            NeedToVerifyRSAPublicKey = fingerprint => OnNeedRSAPublicKeyVerification(clientKey, fingerprint)
        };
        _clientEventSubscriptions.TryAdd(clientKey, subscriptions);

        client.ReportLog += OnClientLog;
        client.Connected += subscriptions.Connected;
        client.Disconnected += subscriptions.Disconnected;
        client.NeedPassword += subscriptions.NeedPassword;
        client.NeedToVerifyRSAPublicKey += subscriptions.NeedToVerifyRSAPublicKey;

        // 订阅各类业务数据包，分别映射到对应的 RespondTypeEnum
        client.DataPackBus.Subscribe<Pack_ServerState>(pack => OnDataReceived(clientKey, RespondTypeEnum.ServerState, pack));
        client.DataPackBus.Subscribe<Pack_MCServerLogs>(pack => OnDataReceived(clientKey, RespondTypeEnum.MCServerLogs, pack));
        client.DataPackBus.Subscribe<Pack_SendCommandSucceed>(pack => OnDataReceived(clientKey, RespondTypeEnum.SendCommandSucceed, pack));
        client.DataPackBus.Subscribe<Pack_SendCommandFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.SendCommandFailed, pack));
        client.DataPackBus.Subscribe<Pack_MCServerManagerConfigs>(pack => OnDataReceived(clientKey, RespondTypeEnum.MCServerManagerConfigs, pack));
        client.DataPackBus.Subscribe<Pack_CreatedNewMCServerManager>(pack => OnDataReceived(clientKey, RespondTypeEnum.CreatedNewMCServerManager, pack));
        client.DataPackBus.Subscribe<Pack_CreatNewMCServerManagerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.CreatNewMCServerManagerFailed, pack));
        client.DataPackBus.Subscribe<Pack_LoadedMCServerManager>(pack => OnDataReceived(clientKey, RespondTypeEnum.LoadedMCServerManager, pack));
        client.DataPackBus.Subscribe<Pack_MCServerManagers>(pack => OnDataReceived(clientKey, RespondTypeEnum.LoadedMCServerManagers, pack));
        client.DataPackBus.Subscribe<Pack_LoadMCServerManagerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.LoadMCServerManagerFailed, pack));
        client.DataPackBus.Subscribe<Pack_StoppedMCServerManager>(pack => OnDataReceived(clientKey, RespondTypeEnum.StoppedMCServerManager, pack));
        client.DataPackBus.Subscribe<Pack_StopMCServerManagerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.StopMCServerManagerFailed, pack));
        client.DataPackBus.Subscribe<Pack_DeletedMCServerManager>(pack => OnDataReceived(clientKey, RespondTypeEnum.DeletedMCServerManager, pack));
        client.DataPackBus.Subscribe<Pack_DeleteMCServerManagerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.DeleteMCServerManagerFailed, pack));
        client.DataPackBus.Subscribe<Pack_ModifiedMCServerManagerConfig>(pack => OnDataReceived(clientKey, RespondTypeEnum.ModifiedMCServerManagerConfig, pack));
        client.DataPackBus.Subscribe<Pack_RunMCServerSucceed>(pack => OnDataReceived(clientKey, RespondTypeEnum.RunMCServerSucceed, pack));
        client.DataPackBus.Subscribe<Pack_RunMCServerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.RunMCServerFailed, pack));
        client.DataPackBus.Subscribe<Pack_ShutdownMCServerSucceed>(pack => OnDataReceived(clientKey, RespondTypeEnum.ShutdownMCServerSucceed, pack));
        client.DataPackBus.Subscribe<Pack_ShutdownMCServerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.ShutdownMCServerFailed, pack));
        client.DataPackBus.Subscribe<Pack_KillMCServerSucceed>(pack => OnDataReceived(clientKey, RespondTypeEnum.KillMCServerSucceed, pack));
        client.DataPackBus.Subscribe<Pack_KillMCServerFailed>(pack => OnDataReceived(clientKey, RespondTypeEnum.KillMCServerFailed, pack));
        client.DataPackBus.Subscribe<Pack_MCServerExited>(pack => OnDataReceived(clientKey, RespondTypeEnum.MCServerExited, pack));
        client.DataPackBus.Subscribe<Pack_ErrorInfo>(pack => OnDataReceived(clientKey, RespondTypeEnum.ErrorInfo, pack));
    }

    /// <summary>取消订阅 NativeClient 事件并释放订阅缓存与数据包总线。</summary>
    private void UnsubscribeFromClientEvents(string clientKey, NativeClient client)
    {
        if (client == null) return;

        client.ReportLog -= OnClientLog;

        if (_clientEventSubscriptions.TryRemove(clientKey, out var subscriptions))
        {
            client.Connected -= subscriptions.Connected;
            client.Disconnected -= subscriptions.Disconnected;
            client.NeedPassword -= subscriptions.NeedPassword;
            client.NeedToVerifyRSAPublicKey -= subscriptions.NeedToVerifyRSAPublicKey;
        }

        client.DataPackBus.Dispose();
    }

    /// <summary>NativeClient 日志回调：以 [PMCSsE] 前缀转发。</summary>
    private void OnClientLog(string message)
    {
        AddLogMessage($"[PMCSsE]", message, ToastType.Info);
    }

    /// <summary>连接成功回调：清除密码副本、更新状态、写入后端条目表并触发 Connected 事件。</summary>
    private void OnClientConnected(string clientKey)
    {
        _pendingPasswords.TryRemove(clientKey, out _);

        // 连接成功：状态 + 触发 Connected 事件属于 DataBind（Normal 优先级）；条目更新、日志在 AddLogMessage/AddOrUpdateBackendEntry 内已是 Background
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = true;
            IsConnecting = false;
            ConnectionStatus = "已连接";
            FingerprintStatus = "已验证";

            var parts = clientKey.Split(':');
            var addr = parts.Length > 0 ? parts[0] : "";
            var port = parts.Length > 1 ? parts[1] : "";

            AddOrUpdateBackendEntry(addr, port, ConnectionStatus, FingerprintStatus);
            var backendId = BackendIdManager.GetOrAssignId(addr, port);
            AddLogMessage("连接成功", $"✅ 已连接到 {addr}:{port} (ID: {backendId})", ToastType.Success);

            // 写入 GlobalCache 后端连接信息（地址、端口），供页面查询
            GlobalCache.BackendConfigCache(backendId).SetConnectionInfo(addr, port);

            // 连接成功后主动刷新缓存：向后端请求最新数据
            if (_nativeClients.TryGetValue(clientKey, out var connectedClient))
            {
                try
                {
                    connectedClient.RequestBackend(
                        RequestTypeEnum.GetMCServerManagersList, new Pack_GetMCServerManagerConfigsList());
                    AddLogMessage("刷新缓存", $"正在向后端 {backendId} 请求管理器列表...", ToastType.Info, false);
                }
                catch (Exception ex)
                {
                    AddLogMessage("刷新缓存失败", $"请求后端数据失败: {ex.Message}", ToastType.Warning);
                }
            }

            try { Connected?.Invoke(); } catch { }
        });
    }

    /// <summary>
    /// 连接断开回调：记录断开原因、移除 NativeClient、清理后端条目并触发断开事件。
    /// 当最后一个连接断开时整体状态置为未连接。
    /// 同时清理 GlobalCache 中该后端的所有缓存数据，避免残留脏数据。
    /// 此方法由 <see cref="NativeClient.Disconnected"/> 事件触发（在 TCP 线程上调用），
    /// 事件取消订阅和客户端释放在此方法中同步完成，UI 更新通过 Dispatcher 推送到 UI 线程。
    /// </summary>
    private void OnClientDisconnected(string clientKey, NativeClient.DisconnectedReasonEnum reason)
    {
        _pendingPasswords.TryRemove(clientKey, out _);

        // 取出并清理 NativeClient：取消事件订阅 + 释放资源
        if (_nativeClients.TryRemove(clientKey, out var clientToCleanup))
        {
            UnsubscribeFromClientEvents(clientKey, clientToCleanup);
            try { clientToCleanup.Dispose(); } catch { }
        }

        // UI 更新推送到 UI 线程
        Dispatcher.UIThread.Post(() =>
        {
            var (status, message, type) = GetDisconnectionInfo(reason);
            AddLogMessage($"❌ {message}", "", type);

            var parts = clientKey.Split(':');
            var addr = parts.Length > 0 ? parts[0] : "";
            var port = parts.Length > 1 ? parts[1] : "";

            if (_nativeClients.IsEmpty)
            {
                IsConnected = false;
                IsConnecting = false;
                ConnectionStatus = status;
                FingerprintStatus = "未确认";
            }

            RemoveBackendEntry(addr, port);

            // 清理 GlobalCache 中该后端的全部缓存（连带其下所有管理器）
            var backendId = BackendIdManager.GetOrAssignId(addr, port);
            GlobalCache.BackendConfigCache(backendId).Remove();
            AddLogMessage("刷新缓存", $"已清理后端 {backendId} 的缓存资源", ToastType.Info, false);

            try { Disconnected?.Invoke(); } catch { }
            try { DisconnectedWithBackendId?.Invoke(clientKey); } catch { }
        });
    }

    /// <summary>
    /// 数据包回调：在 UI 线程触发数据接收事件（含/不含后端 ID 两个版本）。
    /// 在分发事件前，会先将管理器配置相关的数据包写入 GlobalCache 全局动态缓存，
    /// 确保所有页面打开时都能从缓存读到数据，不依赖网络请求时序。
    /// </summary>
    private void OnDataReceived(string clientKey, RespondTypeEnum responseType, object? data)
    {
        // clientKey 格式为 "addr:port"，解析后通过 BackendIdManager 获取稳定 BackendId。
        // 必须在方法开头计算，确保缓存写入和事件分发使用同一个 backendId，
        // 避免 ViewModel 中用 clientKey（地址:端口）作为 BackendId 与 GlobalCache 中的真实 BackendId 不一致。
        var parts = clientKey.Split(':');
        var addr = parts.Length > 0 ? parts[0] : "";
        var port = parts.Length > 1 ? parts[1] : "";
        var backendId = BackendIdManager.GetOrAssignId(addr, port);

        AddLogMessage("收到数据", $"📥 收到数据: {responseType} (后端: {backendId})", ToastType.Info);

        // ====== 在分发事件前，先将管理器配置写入 GlobalCache 全局动态缓存 ======
        // 这样即使页面在事件分发后才打开，也能从缓存读到数据，避免列表空白。
        try
        {
            switch (responseType)
            {
                // 管理器配置列表：后端返回的所有管理器配置，批量写入缓存
                case RespondTypeEnum.MCServerManagerConfigs when data is Pack_MCServerManagerConfigs configsPack:
                    if (configsPack.MCServerManagerConfigs?.MCServerManagerConfigsList != null)
                    {
                        foreach (var config in configsPack.MCServerManagerConfigs.MCServerManagerConfigsList)
                        {
                            GlobalCache.ManagerConfigCache(backendId, config.ManagerID)
                                .UpdateConfig(config.MCServerName ?? "", config);
                        }
                    }
                    break;

                // 管理器配置修改：单条更新，覆盖缓存中对应管理器的配置
                case RespondTypeEnum.ModifiedMCServerManagerConfig when data is Pack_ModifiedMCServerManagerConfig modifiedPack:
                    {
                        var config = modifiedPack.MCServerManagerConfig;
                        if (config != null)
                        {
                            GlobalCache.ManagerConfigCache(backendId, config.ManagerID)
                                .UpdateConfig(config.MCServerName ?? "", config);
                        }
                    }
                    break;

                // 管理器删除：从缓存中移除
                case RespondTypeEnum.DeletedMCServerManager when data is Pack_DeletedMCServerManager deletedPack:
                    GlobalCache.ManagerConfigCache(backendId, deletedPack.ManagerID).Remove();
                    break;

                // 服务器状态/性能数据：更新 CPU/内存的当前快照与历史数据点
                case RespondTypeEnum.ServerState when data is Pack_ServerState serverStatePack:
                    GlobalCache.ServerStateCache(backendId).UpdateFromPack(serverStatePack);
                    break;
            }
        }
        catch
        {
            // 缓存写入失败不影响主流程，事件仍会正常分发
        }

        // 数据包分发：属于 DataBind 级更新，Normal 优先级（让 ViewModel 立即响应）
        // 事件传出的是通过 BackendIdManager 分配的稳定 BackendId，与 GlobalCache 中的 key 一致
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DataPackReceived?.Invoke(responseType, data);
                DataPackReceivedWithBackendId?.Invoke(backendId, responseType, data);
            }
            catch (Exception ex)
            {
                AddLogMessage("处理数据包出错", $"处理数据包出错: {ex.Message}", ToastType.Error);
            }
        });
    }

    /// <summary>
    /// 后端要求输入访问密钥回调：优先使用密码副本或当前密码自动提交，
    /// 否则在 UI 线程触发 NeedPassword 事件并弹出密钥输入对话框。
    /// </summary>
    private void OnNeedPassword(string clientKey)
    {
        AddLogMessage("需要访问密钥", "🔐 需要访问密钥", ToastType.Warning);

        _pendingPasswords.TryGetValue(clientKey, out var passwordFromCopy);
        var passwordToUse = passwordFromCopy ?? _password;

        if (!string.IsNullOrWhiteSpace(passwordToUse) && _nativeClients.TryGetValue(clientKey, out var client))
        {
            try
            {
                client.TypePassword(passwordToUse);
                AddLogMessage("已提交记忆的访问密钥", "✅ 已提交记忆的访问密钥", ToastType.Success);
                _pendingPasswords.TryRemove(clientKey, out _);
                return;
            }
            catch (Exception ex)
            {
                AddLogMessage("提交密钥失败", $"提交密钥失败: {ex.Message}", ToastType.Error);
            }
        }

        // 需要密钥对话框：DataBind/用户交互，Normal 优先级
        Dispatcher.UIThread.Post(() =>
        {
            try { NeedPassword?.Invoke("请输入访问密钥"); } catch { }
            ShowPasswordDialog();
        });
    }

    /// <summary>
    /// RSA 公钥指纹校验回调（加密握手核心）。
    /// 处理三种情形：
    /// 1) 已记忆且匹配 -> 自动通过；
    /// 2) 已记忆但不匹配 -> 拒绝、告警并弹出警告对话框（防中间人攻击）；
    /// 3) 首次连接（无记忆指纹）-> 弹出确认对话框请求用户确认。
    /// </summary>
    private void OnNeedRSAPublicKeyVerification(string clientKey, string fingerprint)
    {
        AddLogMessage("验证 RSA 公钥指纹", $"🔑 验证 RSA 公钥指纹: {fingerprint}", ToastType.Info);
        _pendingFingerprint = fingerprint ?? "";

        var parts = clientKey.Split(':');
        _pendingAddress = parts.Length > 0 ? parts[0] : "";
        _pendingPort = parts.Length > 1 && int.TryParse(parts[1], out var pp) ? pp : 0;

        // 指纹确认：需要用户交互（弹对话框），Normal 优先级
        Dispatcher.UIThread.Post(() =>
        {
            var knownFp = StaticConfigManagerClass.GetKnownFingerprint(_pendingAddress, _pendingPort) ?? "";
            bool isMatch = !string.IsNullOrEmpty(knownFp) &&
                           string.Equals(knownFp, fingerprint, StringComparison.Ordinal);

            // 情形 1：指纹匹配记忆 -> 自动通过验证
            if (!string.IsNullOrEmpty(knownFp) && isMatch)
            {
                AddLogMessage("自动通过验证", "✅ 服务器指纹匹配记忆，自动通过验证", ToastType.Success);
                FingerprintStatus = "已验证";
                if (_nativeClients.TryGetValue(clientKey, out var client))
                    client.VerifyRSAPublicKey(true);
                try { RSAFingerprintConfirmed?.Invoke(); } catch { }
                return;
            }

            // 情形 2：指纹与记忆不一致 -> 拒绝并告警
            if (!string.IsNullOrEmpty(knownFp) && !isMatch)
            {
                AddLogMessage("指纹不匹配", "⚠️ 警告：服务器指纹与记忆不一致！可能存在中间人攻击", ToastType.Error);
                FingerprintStatus = "指纹不匹配";
                try { NeedRSAPublicKeyVerification?.Invoke(fingerprint ?? "", knownFp, false); } catch { }
                try { RSAFingerprintRejected?.Invoke("服务器指纹与记忆不一致"); } catch { }
                ShowRSAWarningDialog(fingerprint ?? "", knownFp);
                return;
            }

            // 情形 3：未知服务器 -> 请求用户确认
            FingerprintStatus = "待确认";
            try { NeedRSAPublicKeyVerification?.Invoke(fingerprint ?? "", "", true); } catch { }
            ShowRSAConfirmationDialog(fingerprint ?? "");
        });
    }

    // ============ 对话框（适配 ViewModelBase API） ============

    /// <summary>
    /// 显示 RSA 公钥指纹确认对话框。
    /// 适配说明：已从原始 DialogManager.CreateDialog() 重构为使用 ViewModelBase.ShowDialog() API，
    /// 统一了错误处理和回调保护机制。当 DialogManager 不可用时，自动降级为日志记录并允许连接。
    /// </summary>
    /// <param name="fingerprint">服务器的 RSA 公钥指纹</param>
    private void ShowRSAConfirmationDialog(string fingerprint)
    {
        var clientKey = GetClientKey(_pendingAddress, _pendingPort.ToString());
        if (!_nativeClients.TryGetValue(clientKey, out var nativeClient)) return;
        
        if (DialogManager == null)
        {
            AddLogMessage("自动通过 RSA 验证", "⚠️ 无对话框管理器，自动通过 RSA 验证", ToastType.Warning);
            nativeClient.VerifyRSAPublicKey(true);
            return;
        }

        bool alwaysTrust = false;
        var stackPanel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        stackPanel.Children.Add(new TextBlock
        {
            Text = "这是首次连接到此服务器，请确认以下 RSA 公钥指纹是否与服务器端显示一致：",
            TextWrapping = TextWrapping.Wrap
        });
        stackPanel.Children.Add(new TextBlock
        {
            Text = fingerprint,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });

        var cb = new CheckBox { Content = "始终信任此服务器" };
        cb.IsCheckedChanged += (s, e) => alwaysTrust = cb.IsChecked == true;
        stackPanel.Children.Add(cb);

        var nativeClientRef = nativeClient;
        var addressRef = _pendingAddress;
        var portRef = _pendingPort;
        var fpRef = fingerprint;

        ShowDialog("🔑 RSA 公钥验证", stackPanel,
            new DialogButton("✅ 确认匹配", () =>
            {
                if (nativeClientRef != null)
                {
                    nativeClientRef.VerifyRSAPublicKey(true);
                    FingerprintStatus = "已验证";
                    if (alwaysTrust)
                    {
                        StaticConfigManagerClass.SetKnownFingerprint(addressRef, portRef, fpRef);
                        AddLogMessage("已记录指纹", $"✅ 已记录指纹 ({addressRef}:{portRef})", ToastType.Success);
                    }
                    try { RSAFingerprintConfirmed?.Invoke(); } catch { }
                }
            }, true),
            new DialogButton("❌ 不匹配", () =>
            {
                if (nativeClientRef != null)
                {
                    nativeClientRef.VerifyRSAPublicKey(false);
                    FingerprintStatus = "已拒绝";
                    try { RSAFingerprintRejected?.Invoke("用户拒绝指纹"); } catch { }
                }
            }, true));
    }

    /// <summary>
    /// 显示 RSA 指纹变更警告对话框。
    /// 适配说明：已从原始 DialogManager.CreateDialog() 重构为使用 ViewModelBase.ShowDialog() API。
    /// 当检测到服务器指纹与记忆不一致时显示此警告，提示用户可能存在中间人攻击。
    /// </summary>
    /// <param name="currentFp">当前连接收到的指纹</param>
    /// <param name="knownFp">已记忆的指纹</param>
    private void ShowRSAWarningDialog(string currentFp, string knownFp)
    {
        var clientKey = GetClientKey(_pendingAddress, _pendingPort.ToString());
        if (!_nativeClients.TryGetValue(clientKey, out var nativeClient)) return;
        
        if (DialogManager == null)
        {
            AddLogMessage("拒绝连接", "⚠️ 无对话框管理器，拒绝连接（指纹不匹配）", ToastType.Error);
            nativeClient.VerifyRSAPublicKey(false);
            return;
        }

        var stackPanel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        stackPanel.Children.Add(new TextBlock
        {
            Text = "⚠️ 严重警告：当前服务器的 RSA 公钥指纹与您记忆的不一致！可能存在中间人攻击！",
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Colors.Red),
            TextWrapping = TextWrapping.Wrap
        });
        stackPanel.Children.Add(new TextBlock
        {
            Text = $"记忆指纹: {knownFp}\n当前指纹: {currentFp}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        var nativeClientRef = nativeClient;
        var addressRef = _pendingAddress;
        var portRef = _pendingPort;
        var fpRef = currentFp;

        ShowDialog("🔑 指纹变更警告", stackPanel,
            new DialogButton("✅ 信任新指纹", () =>
            {
                if (nativeClientRef != null)
                {
                    nativeClientRef.VerifyRSAPublicKey(true);
                    FingerprintStatus = "已验证";
                    StaticConfigManagerClass.SetKnownFingerprint(addressRef, portRef, fpRef);
                    AddLogMessage("继续连接", "⚠️ 已覆盖指纹记录，继续连接", ToastType.Warning);
                    try { RSAFingerprintConfirmed?.Invoke(); } catch { }
                }
            }, true),
            new DialogButton("❌ 拒绝并断开", () =>
            {
                if (nativeClientRef != null)
                {
                    nativeClientRef.VerifyRSAPublicKey(false);
                    FingerprintStatus = "已拒绝";
                    try { RSAFingerprintRejected?.Invoke("指纹不匹配，用户拒绝"); } catch { }
                }
            }, true));
    }

    /// <summary>
    /// 显示访问密钥输入对话框。
    /// 适配说明：已从原始 DialogManager.CreateDialog() 重构为使用 ViewModelBase.ShowDialog() API。
    /// 使用 PlaceholderText 替代已过时的 Watermark 属性以符合 Avalonia 最新规范。
    /// </summary>
    private void ShowPasswordDialog()
    {
        var clientKey = GetClientKey(_pendingAddress, _pendingPort.ToString());
        if (!_nativeClients.TryGetValue(clientKey, out var nativeClient)) return;
        
        if (DialogManager == null) return;

        var stackPanel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        var textBox = new TextBox { PlaceholderText = "访问密钥", PasswordChar = '*' };
        stackPanel.Children.Add(new TextBlock { Text = "请输入访问密钥：" });
        stackPanel.Children.Add(textBox);

        var nativeClientRef = nativeClient;

        ShowDialog("🔐 访问密钥", stackPanel,
            new DialogButton("✅ 提交", () =>
            {
                var pwd = textBox.Text;
                if (!string.IsNullOrWhiteSpace(pwd) && nativeClientRef != null)
                {
                    try
                    {
                        _password = pwd;
                        this.RaisePropertyChanged(nameof(BackendPassword));
                        nativeClientRef.TypePassword(pwd);
                        AddLogMessage("已提交访问密钥", "✅ 已提交访问密钥", ToastType.Success);
                    }
                    catch (Exception ex)
                    {
                        AddLogMessage("提交密钥失败",$"提交密钥失败: {ex.Message}", ToastType.Error);
                    }
                }
            }, true),
            new DialogButton("❌ 取消", () =>
            {
                ExecuteDisconnect();
            }, true));
    }

    // ============ 已连接后端条目表 ============

    /// <summary>
    /// 在 <see cref="ConnectedBackends"/> 中新增或更新一条记录，使 UI 表格与实际连接状态同步。
    /// </summary>
    private void AddOrUpdateBackendEntry(string addr, string port, string status, string fingerprintStatus)
    {
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(port)) return;

        // 后端条目增改：后台优先级（条目列表非关键路径）
        Dispatcher.UIThread.Post(() =>
        {
            var existing = ConnectedBackends.FirstOrDefault(x =>
                string.Equals(x.BackendAddress, addr, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.BackendPort, port, StringComparison.Ordinal));

            if (existing != null)
            {
                existing.ConnectionStatus = status;
                existing.FingerprintStatus = fingerprintStatus;
                if (string.IsNullOrEmpty(existing.BackendId))
                    existing.BackendId = BackendIdManager.GetOrAssignId(addr, port);
            }
            else
            {
                var backendId = BackendIdManager.GetOrAssignId(addr, port);
                ConnectedBackends.Insert(0, new ConnectionBackendEntry
                {
                    BackendAddress = addr,
                    BackendPort = port,
                    ConnectionStatus = status,
                    FingerprintStatus = fingerprintStatus,
                    BackendId = backendId
                });
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 在 <see cref="ConnectedBackends"/> 中移除指定后端条目（连接断开时调用）。
    /// </summary>
    private void RemoveBackendEntry(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(port)) return;
        // 后端条目移除：后台优先级
        Dispatcher.UIThread.Post(() =>
        {
            var existing = ConnectedBackends.FirstOrDefault(x =>
                string.Equals(x.BackendAddress, address, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.BackendPort, port, StringComparison.Ordinal));
            if (existing != null) ConnectedBackends.Remove(existing);
        }, DispatcherPriority.Background);
    }

    // ============ 日志/Toast 辅助 ============

    /// <summary>添加一条日志（仅标题，无内容）。</summary>
    private void AddLogMessage(string title, ToastType toastType = ToastType.Info, bool showToast = true)
    {
        AddLogMessage(title, string.Empty, toastType, showToast);
    }

    /// <summary>
    /// 添加一条日志：写入持久化日志、追加到 UI 日志列表（上限 500 条），
    /// 并在需要时弹出 Toast。Toast 仅在非 Info 级别时弹出。
    /// </summary>
    private void AddLogMessage(string title, string message, ToastType toastType = ToastType.Info, bool showToast = true)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = new LogEntry
        {
            TimeText = timestamp,
            Content = message
        };

        try
        {
            LogsWriterClass.AppendLog(message);
        }
        catch { }

        // 日志追加：后台优先级（日志列表属非关键展示，不抢占输入/布局）
        Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(entry);
            // 限制日志条数，超过 500 条时移除最早的
            while (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);
        }, DispatcherPriority.Background);

        if (showToast && toastType != ToastType.Info)
        {
            ShowToast(title, message, toastType);
        }
    }

    /// <summary>清空 UI 日志列表。</summary>
    private void ClearLogs()
    {
        // 清空日志：后台优先级
        Dispatcher.UIThread.Post(() => LogEntries.Clear(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 将 NativeClient 断开原因映射为 UI 状态文本、提示消息与 Toast 类型。
    /// </summary>
    private static (string status, string message, ToastsViewModel.ToastType type) GetDisconnectionInfo(
        NativeClient.DisconnectedReasonEnum reason)
    {
        return reason switch
        {
            NativeClient.DisconnectedReasonEnum.DisconnectingCalledByToken => ("未连接", "连接已断开", ToastsViewModel.ToastType.Info),
            NativeClient.DisconnectedReasonEnum.SocketException => ("连接失败", "网络连接错误", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.InvalidConnectionParameter => ("连接失败", "连接参数无效", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.OutOfMemory => ("连接失败", "内存不足", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.UnknownPackFormat => ("连接失败", "数据包格式不匹配，可能版本不兼容", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.VerifyRSAPublicKeyTimeOut => ("连接失败", "RSA 公钥验证超时", ToastsViewModel.ToastType.Warning),
            NativeClient.DisconnectedReasonEnum.RSAPublicKeyMismatch => ("连接失败", "RSA 公钥不匹配", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.PasswordMismatch => ("连接失败", "访问密钥错误", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.ConnectionUnexpectlyDisconnected => ("连接断开", "连接意外断开", ToastsViewModel.ToastType.Warning),
            NativeClient.DisconnectedReasonEnum.GenerateDataPackFailed => ("连接失败", "生成数据包失败", ToastsViewModel.ToastType.Error),
            NativeClient.DisconnectedReasonEnum.EncoderFallBack => ("连接失败", "编码转换失败", ToastsViewModel.ToastType.Error),
            _ => ("连接断开", "未知原因断开连接", ToastsViewModel.ToastType.Warning)
        };
    }

    /// <summary>
    /// 释放资源：应用退出时强制清理所有客户端。
    /// 与用户手动断开不同，此处同步取消事件订阅并释放资源，
    /// 不等待 <see cref="NativeClient.Disconnected"/> 异步事件（应用即将退出）。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            foreach (var kvp in _nativeClients.ToArray())
            {
                try
                {
                    UnsubscribeFromClientEvents(kvp.Key, kvp.Value);
                    kvp.Value.Disconnect();
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _nativeClients.Clear();
            _pendingPasswords.Clear();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 已连接后端的条目记录，供 UI 以表格形式展示。
/// 每个实例描述一个具体的后端地址/端口以及其指纹校验状态。
/// 继承 <see cref="ReactiveObject"/> 以保证属性变更能通知绑定的 UI 控件。
/// </summary>
public class ConnectionBackendEntry : ReactiveObject
{
    private string _backendAddress = "";
    private string _backendPort = "";
    private string _connectionStatus = "未连接";
    private string _fingerprintStatus = "未确认";
    private bool _isSelected = false;
    private string _backendId = "";

    /// <summary>后端地址（例如 127.0.0.1 或 myserver.com）。</summary>
    public string BackendAddress
    {
        get => _backendAddress;
        set => this.RaiseAndSetIfChanged(ref _backendAddress, value);
    }

    /// <summary>后端端口号（字符串形式展示，避免二次解析）。</summary>
    public string BackendPort
    {
        get => _backendPort;
        set => this.RaiseAndSetIfChanged(ref _backendPort, value);
    }

    /// <summary>当前连接状态（如 正在连接... / 已连接 / 未连接）。</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    /// <summary>RSA 公钥指纹校验状态（如 未确认 / 待确认 / 已验证 / 指纹不匹配 / 已拒绝）。</summary>
    public string FingerprintStatus
    {
        get => _fingerprintStatus;
        set => this.RaiseAndSetIfChanged(ref _fingerprintStatus, value);
    }

    /// <summary>UI 表格中的选择状态（由复选框双向绑定）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>用于 DataTemplate 显示的格式化地址（地址:端口）。</summary>
    public string BackendName => $"{BackendAddress}:{BackendPort}";

    /// <summary>后端服务的唯一标识 ID（Base62 短随机码），在连接成功时分配。</summary>
    public string BackendId
    {
        get => _backendId;
        set => this.RaiseAndSetIfChanged(ref _backendId, value);
    }
}

/// <summary>
/// 单条日志记录，供 <see cref="ConnectionViewModel.LogEntries"/> 使用。
/// 携带时间戳与消息内容，以便 UI 做分列展示。
/// </summary>
public class LogEntry
{
    /// <summary>时间戳文本（例如 "HH:mm:ss"）。</summary>
    public string TimeText { get; set; } = "";

    /// <summary>日志内容。</summary>
    public string Content { get; set; } = "";

    public override string ToString() => $"[{TimeText}] {Content}";
}

/// <summary>
/// 存储 NativeClient 事件订阅的委托引用，确保能够正确取消订阅避免内存泄漏。
/// </summary>
public class ClientEventSubscriptions
{
    /// <summary>连接成功事件委托。</summary>
    public Action? Connected { get; set; }

    /// <summary>连接断开事件委托。</summary>
    public Action<NativeClient.DisconnectedReasonEnum>? Disconnected { get; set; }

    /// <summary>请求密码事件委托。</summary>
    public Action? NeedPassword { get; set; }

    /// <summary>请求 RSA 指纹确认事件委托。</summary>
    public Action<string>? NeedToVerifyRSAPublicKey { get; set; }
}
