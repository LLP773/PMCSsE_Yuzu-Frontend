using System;
using System.Threading;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using PMCSsE_Communicator.SharedCodes;
using ReactiveUI;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

/// <summary>
/// 单个后端服务的加密连接封装。
/// 持有 <see cref="NativeClient"/> 实例并订阅其事件，负责连接生命周期管理、
/// RSA 公钥指纹校验、访问密钥提交、指数退避自动重连，以及将后端数据包转发给上层。
/// 所有 UI 相关的状态变更均通过 <see cref="Dispatcher.UIThread"/> 调度回主线程。
/// </summary>
public sealed class BackendConnection : ReactiveObject, IDisposable
{
    private NativeClient? _client;                  // 底层通信客户端实例
    private readonly object _clientLock = new();    // 保护 _client 并发访问的同步锁
    private bool _isConnected;                      // 是否已成功建立连接
    private bool _isConnecting;                     // 是否正在发起连接
    private string _connectionStatus = "未连接";    // 连接状态文本，供 UI 显示
    private string _fingerprintStatus = "未确认";   // RSA 指纹校验状态文本
    private bool _autoReconnect = true;            // 是否在意外断开后启用自动重连
    private int _reconnectAttempts;                 // 当前已尝试的重连次数（用于指数退避）
    private Timer? _reconnectTimer;                // 触发下一次重连的定时器
    private CancellationTokenSource? _cts;          // 连接生命周期取消令牌

    /// <summary>该连接对应的后端唯一标识（Base62 短随机码）。</summary>
    public string BackendId { get; }

    /// <summary>后端服务地址（IP 或域名）。</summary>
    public string Address { get; }

    /// <summary>后端地址的别名，语义等同于 <see cref="Address"/>。</summary>
    public string BackendAddress => Address;

    /// <summary>后端服务端口号。</summary>
    public string Port { get; }

    /// <summary>访问密钥（可为空），用于后端密码校验阶段自动提交。</summary>
    public string? Password { get; set; }

    /// <summary>格式化的后端名称（地址:端口），用于日志与 UI 显示。</summary>
    public string BackendName => $"{BackendAddress}:{Port}";

    /// <summary>是否已成功建立连接。</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>是否正在发起连接（连接进行中）。</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    /// <summary>当前连接状态文本，供 UI 直接显示。</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    /// <summary>RSA 公钥指纹校验状态文本（未确认 / 待确认 / 已验证 / 指纹不匹配）。</summary>
    public string FingerprintStatus
    {
        get => _fingerprintStatus;
        private set => this.RaiseAndSetIfChanged(ref _fingerprintStatus, value);
    }

    /// <summary>是否启用意外断开后的自动重连（默认开启）。</summary>
    public bool AutoReconnect
    {
        get => _autoReconnect;
        set => this.RaiseAndSetIfChanged(ref _autoReconnect, value);
    }

    /// <summary>
    /// 获取底层 <see cref="NativeClient"/> 实例（线程安全）。
    /// 返回值可能为 null（未连接或已断开时）。
    /// </summary>
    public NativeClient? Client
    {
        get
        {
            lock (_clientLock)
                return _client;
        }
    }

    /// <summary>连接成功建立时触发（在 UI 线程）。</summary>
    public event Action<BackendConnection>? Connected;

    /// <summary>连接断开时触发，携带断开原因（在 UI 线程）。</summary>
    public event Action<BackendConnection, NativeClient.DisconnectedReasonEnum>? Disconnected;

    /// <summary>收到后端数据包时触发：(响应类型, 反序列化后的对象)。</summary>
    public event Action<string, RespondTypeEnum, object?>? DataPackReceived;

    /// <summary>后端要求输入访问密钥且本地无记忆密码时触发。</summary>
    public event Action<BackendConnection>? NeedPassword;

    /// <summary>需要用户确认 RSA 公钥指纹时触发，参数为当前指纹。</summary>
    public event Action<BackendConnection, string>? NeedRSAPublicKeyVerification;

    /// <summary>需要输出日志/Toast 时触发：(消息, 类型)。</summary>
    public event Action<BackendConnection, string, ToastsViewModel.ToastType>? LogMessage;

    /// <summary>
    /// 初始化 <see cref="BackendConnection"/> 新实例。
    /// </summary>
    /// <param name="backendId">后端唯一标识（Base62 短随机码）。</param>
    /// <param name="address">后端服务地址。</param>
    /// <param name="port">后端服务端口。</param>
    /// <param name="password">访问密钥（可选，为空则后续通过事件请求用户输入）。</param>
    public BackendConnection(string backendId, string address, string port, string? password = null)
    {
        BackendId = backendId ?? throw new ArgumentNullException(nameof(backendId));
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Port = port ?? throw new ArgumentNullException(nameof(port));
        Password = password;
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// 发起连接。若已在连接/已连接则直接返回；端口校验失败时记录错误日志。
    /// 连接创建后会订阅 NativeClient 事件并异步触发，结果由事件回调处理。
    /// </summary>
    public void Connect()
    {
        if (IsConnecting || IsConnected) return;

        if (!int.TryParse(Port, out var portNum) || portNum <= 0 || portNum > 65535)
        {
            EmitLog("地址或端口无效", ToastsViewModel.ToastType.Error);
            return;
        }

        IsConnecting = true;
        ConnectionStatus = "正在连接...";

        try
        {
            var client = new NativeClient(Address, portNum);
            SubscribeClientEvents(client);

            lock (_clientLock)
            {
                _client = client;
            }

            client.Connect();
            EmitLog($"正在连接 {Address}:{portNum} ...", ToastsViewModel.ToastType.Info);
        }
        catch (Exception ex)
        {
            EmitLog($"连接失败: {ex.Message}", ToastsViewModel.ToastType.Error);
            // 连接失败状态更新：DataBind 级，Normal 优先级
            Dispatcher.UIThread.Post(() =>
            {
                IsConnecting = false;
                ConnectionStatus = "连接失败";
            });
        }
    }

    /// <summary>
    /// 主动断开连接。禁用自动重连并销毁重连定时器后调用内部断开流程。
    /// </summary>
    public void Disconnect()
    {
        _autoReconnect = false;
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        DisconnectInternal(NativeClient.DisconnectedReasonEnum.DisconnectingCalledByToken);
    }

    /// <summary>
    /// 断开连接的内部实现。卸载事件、释放 NativeClient，并在 UI 线程上更新状态、触发断开事件。
    /// </summary>
    /// <param name="reason">断开原因，传递给外部订阅者。</param>
    private void DisconnectInternal(NativeClient.DisconnectedReasonEnum reason)
    {
        lock (_clientLock)
        {
            if (_client != null)
            {
                UnsubscribeClientEvents(_client);
                try { _client.Disconnect(); } catch { }
                try { _client.Dispose(); } catch { }
                _client = null;
            }
        }

        // 主动断开：状态更新 + 事件分发 DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            if (IsConnected)
            {
                IsConnected = false;
                try { Disconnected?.Invoke(this, reason); } catch { }
            }
            IsConnecting = false;
            ConnectionStatus = "未连接";
            FingerprintStatus = "未确认";
        });
    }

    /// <summary>
    /// 安排一次自动重连。采用指数退避算法：每次重连后延迟时间翻倍，
    /// 起始 2 秒，上限 30 秒；由定时器在到期后触发 Connect。
    /// </summary>
    private void ScheduleReconnect()
    {
        if (!_autoReconnect || _cts?.IsCancellationRequested == true) return;

        _reconnectAttempts++;
        // 指数退避：2 * 2^(n-1)，最大不超过 30 秒
        var delay = Math.Min(2000 * Math.Pow(2, _reconnectAttempts - 1), 30000);

        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(_ =>
        {
            if (_autoReconnect && !IsConnected && !IsConnecting)
            {
                EmitLog($"自动重连中（第 {_reconnectAttempts} 次）...", ToastsViewModel.ToastType.Info);
                Connect();
            }
        }, null, (int)delay, Timeout.Infinite);
    }

    /// <summary>重置重连计数器并销毁定时器（连接成功后调用）。</summary>
    private void ResetReconnectAttempts()
    {
        _reconnectAttempts = 0;
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
    }

    /// <summary>
    /// 订阅 NativeClient 的连接事件与各类数据包。将底层事件桥接到本类的统一回调。
    /// </summary>
    private void SubscribeClientEvents(NativeClient client)
    {
        client.ReportLog += OnClientLog;
        client.Connected += OnClientConnected;
        client.Disconnected += OnClientDisconnected;
        client.NeedPassword += OnNeedPassword;
        client.NeedToVerifyRSAPublicKey += OnNeedRSAVerify;

        // 订阅各类业务数据包，分别映射到对应的 RespondTypeEnum
        client.DataPackBus.Subscribe<Pack_ServerState>(p => OnDataPack(RespondTypeEnum.ServerState, p));
        client.DataPackBus.Subscribe<Pack_MCServerLogs>(p => OnDataPack(RespondTypeEnum.MCServerLogs, p));
        client.DataPackBus.Subscribe<Pack_SendCommandSucceed>(p => OnDataPack(RespondTypeEnum.SendCommandSucceed, p));
        client.DataPackBus.Subscribe<Pack_SendCommandFailed>(p => OnDataPack(RespondTypeEnum.SendCommandFailed, p));
        client.DataPackBus.Subscribe<Pack_MCServerManagerConfigs>(p => OnDataPack(RespondTypeEnum.MCServerManagerConfigs, p));
        client.DataPackBus.Subscribe<Pack_CreatedNewMCServerManager>(p => OnDataPack(RespondTypeEnum.CreatedNewMCServerManager, p));
        client.DataPackBus.Subscribe<Pack_CreatNewMCServerManagerFailed>(p => OnDataPack(RespondTypeEnum.CreatNewMCServerManagerFailed, p));
        client.DataPackBus.Subscribe<Pack_LoadedMCServerManager>(p => OnDataPack(RespondTypeEnum.LoadedMCServerManager, p));
        client.DataPackBus.Subscribe<Pack_MCServerManagers>(p => OnDataPack(RespondTypeEnum.LoadedMCServerManagers, p));
        client.DataPackBus.Subscribe<Pack_LoadMCServerManagerFailed>(p => OnDataPack(RespondTypeEnum.LoadMCServerManagerFailed, p));
        client.DataPackBus.Subscribe<Pack_StoppedMCServerManager>(p => OnDataPack(RespondTypeEnum.StoppedMCServerManager, p));
        client.DataPackBus.Subscribe<Pack_StopMCServerManagerFailed>(p => OnDataPack(RespondTypeEnum.StopMCServerManagerFailed, p));
        client.DataPackBus.Subscribe<Pack_DeletedMCServerManager>(p => OnDataPack(RespondTypeEnum.DeletedMCServerManager, p));
        client.DataPackBus.Subscribe<Pack_DeleteMCServerManagerFailed>(p => OnDataPack(RespondTypeEnum.DeleteMCServerManagerFailed, p));
        client.DataPackBus.Subscribe<Pack_ModifiedMCServerManagerConfig>(p => OnDataPack(RespondTypeEnum.ModifiedMCServerManagerConfig, p));
        client.DataPackBus.Subscribe<Pack_RunMCServerSucceed>(p => OnDataPack(RespondTypeEnum.RunMCServerSucceed, p));
        client.DataPackBus.Subscribe<Pack_RunMCServerFailed>(p => OnDataPack(RespondTypeEnum.RunMCServerFailed, p));
        client.DataPackBus.Subscribe<Pack_ShutdownMCServerSucceed>(p => OnDataPack(RespondTypeEnum.ShutdownMCServerSucceed, p));
        client.DataPackBus.Subscribe<Pack_ShutdownMCServerFailed>(p => OnDataPack(RespondTypeEnum.ShutdownMCServerFailed, p));
        client.DataPackBus.Subscribe<Pack_KillMCServerSucceed>(p => OnDataPack(RespondTypeEnum.KillMCServerSucceed, p));
        client.DataPackBus.Subscribe<Pack_KillMCServerFailed>(p => OnDataPack(RespondTypeEnum.KillMCServerFailed, p));
        client.DataPackBus.Subscribe<Pack_MCServerExited>(p => OnDataPack(RespondTypeEnum.MCServerExited, p));
        client.DataPackBus.Subscribe<Pack_ErrorInfo>(p => OnDataPack(RespondTypeEnum.ErrorInfo, p));
    }

    /// <summary>取消订阅 NativeClient 的事件（断开/释放前调用，避免回调悬挂）。</summary>
    private void UnsubscribeClientEvents(NativeClient client)
    {
        client.ReportLog -= OnClientLog;
        client.Connected -= OnClientConnected;
        client.Disconnected -= OnClientDisconnected;
        client.NeedPassword -= OnNeedPassword;
        client.NeedToVerifyRSAPublicKey -= OnNeedRSAVerify;
    }

    /// <summary>NativeClient 日志回调：以 Info 级别转发。</summary>
    private void OnClientLog(string message)
    {
        EmitLog(message, ToastsViewModel.ToastType.Info);
    }

    /// <summary>连接成功回调：在 UI 线程更新状态、重置重连计数、触发 Connected 事件。</summary>
    private void OnClientConnected()
    {
        // 连接成功：状态 + Connected 事件 DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = true;
            IsConnecting = false;
            ConnectionStatus = "已连接";
            FingerprintStatus = "已验证";
            ResetReconnectAttempts();
            EmitLog($"✅ 已连接到 {BackendName} (ID: {BackendId})", ToastsViewModel.ToastType.Success);
            try { Connected?.Invoke(this); } catch { }
        });
    }

    /// <summary>
    /// 连接断开回调：清理 NativeClient、更新状态、触发 Disconnected 事件。
    /// 若非主动断开，则安排自动重连。
    /// </summary>
    /// <param name="reason">断开原因。</param>
    private void OnClientDisconnected(NativeClient.DisconnectedReasonEnum reason)
    {
        // 连接断开处理：状态更新 + Disconnected 事件 DataBind 级 Normal；日志由 EmitLog 转到 Background
        Dispatcher.UIThread.Post(() =>
        {
            EmitLog($"❌ 连接断开: {reason}", ToastsViewModel.ToastType.Warning);

            lock (_clientLock)
            {
                if (_client != null)
                {
                    UnsubscribeClientEvents(_client);
                    try { _client.Dispose(); } catch { }
                    _client = null;
                }
            }

            IsConnected = false;
            IsConnecting = false;
            ConnectionStatus = "未连接";
            FingerprintStatus = "未确认";

            try { Disconnected?.Invoke(this, reason); } catch { }

            // 主动断开不触发自动重连
            if (reason != NativeClient.DisconnectedReasonEnum.DisconnectingCalledByToken)
            {
                ScheduleReconnect();
            }
        });
    }

    /// <summary>数据包回调：在 UI 线程上转发给 DataPackReceived 事件。</summary>
    private void OnDataPack(RespondTypeEnum responseType, object? data)
    {
        // 数据包转发：DataBind 级 Normal（ViewModel 需要立刻响应）
        Dispatcher.UIThread.Post(() =>
        {
            try { DataPackReceived?.Invoke(BackendId, responseType, data); } catch { }
        });
    }

    /// <summary>
    /// 后端要求输入访问密钥回调：若本地有记忆密码则自动提交，
    /// 否则在 UI 线程触发 NeedPassword 事件请求用户输入。
    /// </summary>
    private void OnNeedPassword()
    {
        EmitLog("🔐 需要访问密钥", ToastsViewModel.ToastType.Warning);
        if (!string.IsNullOrWhiteSpace(Password))
        {
            var client = Client;
            if (client != null)
            {
                try
                {
                    client.TypePassword(Password);
                    EmitLog("✅ 已提交记忆的访问密钥", ToastsViewModel.ToastType.Success);
                    return;
                }
                catch (Exception ex)
                {
                    EmitLog($"提交密钥失败: {ex.Message}", ToastsViewModel.ToastType.Error);
                }
            }
        }
        // NeedPassword 事件（触发对话框）：用户交互 Normal 优先级
        Dispatcher.UIThread.Post(() => { try { NeedPassword?.Invoke(this); } catch { } });
    }

    /// <summary>
    /// RSA 公钥指纹校验回调（加密握手核心）。
    /// 处理三种情形：
    /// 1) 已记录且匹配 -> 自动通过；
    /// 2) 已记录但不匹配 -> 拒绝并告警（防中间人攻击）；
    /// 3) 首次连接（无记忆指纹）-> 触发 NeedRSAPublicKeyVerification 请求用户确认。
    /// </summary>
    /// <param name="fingerprint">服务器当前返回的 RSA 公钥指纹。</param>
    private void OnNeedRSAVerify(string fingerprint)
    {
        EmitLog($"🔑 验证 RSA 公钥指纹: {fingerprint}", ToastsViewModel.ToastType.Info);

        // 读取本地已记忆的指纹（首次连接时为空）
        var knownFp = StaticConfigManagerClass.GetKnownFingerprint(Address, int.TryParse(Port, out var pp) ? pp : 0) ?? "";
        bool isMatch = !string.IsNullOrEmpty(knownFp) &&
                       string.Equals(knownFp, fingerprint, StringComparison.Ordinal);

        var client = Client;

        // 情形 1：指纹匹配记忆 -> 自动通过验证
        if (!string.IsNullOrEmpty(knownFp) && isMatch && client != null)
        {
            // 指纹匹配：状态更新 + 自动通过属 DataBind 级 Normal
            Dispatcher.UIThread.Post(() =>
            {
                FingerprintStatus = "已验证";
                client.VerifyRSAPublicKey(true);
                EmitLog("✅ 服务器指纹匹配记忆，自动通过验证", ToastsViewModel.ToastType.Success);
            });
            return;
        }

        // 情形 2：指纹与记忆不一致 -> 拒绝并告警
        if (!string.IsNullOrEmpty(knownFp) && !isMatch)
        {
            // 指纹不匹配：状态 + 告警 DataBind 级 Normal
            Dispatcher.UIThread.Post(() =>
            {
                FingerprintStatus = "指纹不匹配";
                EmitLog("⚠️ 警告：服务器指纹与记忆不一致！", ToastsViewModel.ToastType.Error);
                if (client != null) client.VerifyRSAPublicKey(false);
            });
            return;
        }

        // 情形 3：首次连接，无记忆指纹 -> 请求用户确认（用户交互 Normal）
        Dispatcher.UIThread.Post(() =>
        {
            FingerprintStatus = "待确认";
            try { NeedRSAPublicKeyVerification?.Invoke(this, fingerprint); } catch { }
        });
    }

    /// <summary>
    /// 向后端发送请求（无内容载荷）。根据请求类型构造对应的空数据包。
    /// </summary>
    /// <param name="type">请求类型枚举。</param>
    public void RequestBackend(RequestTypeEnum type)
    {
        var client = Client;
        if (client == null)
        {
            EmitLog("发送请求失败：未连接", ToastsViewModel.ToastType.Error);
            return;
        }

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

    /// <summary>
    /// 向后端发送请求（携带强类型载荷）。
    /// </summary>
    /// <typeparam name="T">载荷类型，需实现 <see cref="LightProto.IProtoParser{T}"/>。</typeparam>
    /// <param name="type">请求类型枚举。</param>
    /// <param name="payload">请求载荷对象。</param>
    public void RequestBackend<T>(RequestTypeEnum type, T payload) where T : class, LightProto.IProtoParser<T>
    {
        var client = Client;
        if (client == null)
        {
            EmitLog("发送请求失败：未连接", ToastsViewModel.ToastType.Error);
            return;
        }
        client.RequestBackend(type, payload);
    }

    /// <summary>触发 LogMessage 事件输出日志（吞掉订阅者异常避免影响主流程）。</summary>
    private void EmitLog(string message, ToastsViewModel.ToastType type)
    {
        try { LogMessage?.Invoke(this, message, type); } catch { }
    }

    /// <summary>
    /// 释放资源：取消取消令牌、销毁重连定时器、断开并释放 NativeClient、清理取消令牌。
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _autoReconnect = false;

        lock (_clientLock)
        {
            if (_client != null)
            {
                UnsubscribeClientEvents(_client);
                try { _client.Disconnect(); } catch { }
                try { _client.Dispose(); } catch { }
                _client = null;
            }
        }

        _cts?.Dispose();
        _cts = null;
    }
}