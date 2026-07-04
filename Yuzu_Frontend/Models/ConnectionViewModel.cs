using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Threading;
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
public class ConnectionViewModel : ViewModelBase, IDisposable
{
    private NativeClient? _nativeClient;
    private string? _backendAddress;
    private string _backendPort = "";
    private string? _password = null;
    private bool _isConnected;
    private bool _isConnecting;
    private string _connectionStatus = "未连接";
    private string _fingerprintStatus = "未确认";
    private string _pendingFingerprint = "";
    private int _pendingPort;
    private string _pendingAddress = "";
    private bool _savePassword = true;
    private bool _saveToHistory = true;
    private bool _selectAllChecked = false;
    private bool _hasSelectedBackends = false;

    /// <summary>连接日志（可用于 ListBox 显示）。每条日志包含时间戳与内容。</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>当前已连接的后端条目集合（按"服务器地址:端口"去重），供 UI 以表格形式展示。</summary>
    public ObservableCollection<ConnectionBackendEntry> ConnectedBackends { get; } = new();

    /// <summary>是否有已连接的后端（随 <see cref="ConnectedBackends"/> 的变化自动更新）。</summary>
    public bool HasConnectedBackends => ConnectedBackends.Count > 0;

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

    public NativeClient? Client => _nativeClient;
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
    /// 收到后端数据包时触发。(响应类型, 反序列化后的对象)。
    /// 总是在 UI 线程触发。
    /// </summary>
    public event Action<RespondTypeEnum, object?>? DataPackReceived;

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

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLogsCommand { get; }

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
            UpdateHasSelectedBackends();
        };

        // 当 IsConnected / IsConnecting 变化时，也触发 ConnectionButtonText 的变更通知
        this.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ConnectionButtonText)));

        try { StaticConfigManagerClass.LoadConfig(); } catch { }
    }

    // ============ 公共 API ============

    /// <summary>
    /// 向后端发送请求（无内容）。根据类型发送对应的空包。
    /// </summary>
    public void RequestBackend(RequestTypeEnum type)
    {
        if (_nativeClient == null) return;
        switch (type)
        {
            case RequestTypeEnum.GetMCServerManagersList:
                _nativeClient.RequestBackend(type, new Pack_GetMCServerManagerConfigsList());
                break;
            case RequestTypeEnum.GetSupportedMCServerTypes:
                _nativeClient.RequestBackend(type, new Pack_GetSupportedMCServerTypes());
                break;
            case RequestTypeEnum.CreatNewMCServerManager:
                _nativeClient.RequestBackend(type, new Pack_CreatNewMCServerManager());
                break;
            case RequestTypeEnum.GetLoadedMCServerManagers:
                _nativeClient.RequestBackend(type, new Pack_GetMCServerManager());
                break;
            default:
                _nativeClient.RequestBackend(type, new Pack_GetMCServerManagerConfigsList());
                break;
        }
    }

    /// <summary>向后端发送请求（含内容）。</summary>
    public void RequestBackend<T>(RequestTypeEnum type, T payload) where T : LightProto.IProtoParser<T>
    {
        _nativeClient?.RequestBackend(type, payload);
    }

    /// <summary>向后端发送请求（object 重载）。</summary>
    public void RequestBackendWithData(RequestTypeEnum type, object? payload = null)
    {
        if (_nativeClient == null) return;
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
    /// 对匹配当前活动连接的条目执行真正的断联；对其他条目仅从列表移除。
    /// </summary>
    public void DisconnectBackends(IEnumerable<ConnectionBackendEntry> entries)
    {
        if (entries == null) return;

        var list = entries.ToList();
        if (list.Count == 0) return;

        bool hitCurrent = false;

        foreach (var entry in list)
        {
            bool matchesCurrent =
                string.Equals(entry.BackendAddress, _backendAddress, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.BackendPort, _backendPort, StringComparison.Ordinal);

            if (matchesCurrent)
                hitCurrent = true;

            Dispatcher.UIThread.Post(() =>
            {
                var existing = ConnectedBackends.FirstOrDefault(x =>
                    string.Equals(x.BackendAddress, entry.BackendAddress, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.BackendPort, entry.BackendPort, StringComparison.Ordinal));
                if (existing != null) ConnectedBackends.Remove(existing);
            });
        }

        if (hitCurrent)
            ExecuteDisconnect();
    }

    // ============ 内部实现 ============

    private void ExecuteConnect()
    {
        if (IsConnecting || IsConnected) return;
        int portNum = 0;
        if (string.IsNullOrWhiteSpace(_backendAddress) ||
            !int.TryParse(_backendPort, out portNum) || portNum <= 0 || portNum > 65535)
        {
            AddLogMessage("无法建立连接", "地址或端口无效，检查后重试", ToastType.Error);
            return;
        }

        IsConnecting = true;
        ConnectionStatus = "正在连接...";

        try
        {
            _nativeClient = new NativeClient(_backendAddress, portNum);
            SubscribeToClientEvents();
            _nativeClient.Connect();
            AddLogMessage("连接中", $"正在连接 {_backendAddress}:{portNum} ...", ToastType.Info);
        }
        catch (Exception ex)
        {
            AddLogMessage("连接失败", $"{ex.Message}", ToastType.Error);
            IsConnecting = false;
        }
    }

    private void ExecuteDisconnect()
    {
        DisconnectInternal();
        AddLogMessage("断开连接",$"已断开与的连接" , ToastType.Warning);
    }

    private void DisconnectInternal()
    {
        if (_nativeClient != null)
        {
            UnsubscribeFromClientEvents();
            try { _nativeClient.Disconnect(); } catch { }
            try { _nativeClient.Dispose(); } catch { }
            _nativeClient = null;
        }

        if (IsConnected)
        {
            IsConnected = false;
            try { Disconnected?.Invoke(); } catch { }
        }
        IsConnecting = false;
        ConnectionStatus = "未连接";
        FingerprintStatus = "未确认";
    }

    private void SubscribeToClientEvents()
    {
        if (_nativeClient == null) return;

        _nativeClient.ReportLog += OnClientLog;
        _nativeClient.Connected += OnClientConnected;
        _nativeClient.Disconnected += OnClientDisconnected;
        _nativeClient.NeedPassword += OnNeedPassword;
        _nativeClient.NeedToVerifyRSAPublicKey += OnNeedRSAPublicKeyVerification;

        _nativeClient.DataPackBus.Subscribe<Pack_MCServerLogs>(pack => OnDataReceived(RespondTypeEnum.MCServerLogs, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_SendCommandSucceed>(pack => OnDataReceived(RespondTypeEnum.SendCommandSucceed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_SendCommandFailed>(pack => OnDataReceived(RespondTypeEnum.SendCommandFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_MCServerManagerConfigs>(pack => OnDataReceived(RespondTypeEnum.MCServerManagerConfigs, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_CreatedNewMCServerManager>(pack => OnDataReceived(RespondTypeEnum.CreatedNewMCServerManager, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_CreatNewMCServerManagerFailed>(pack => OnDataReceived(RespondTypeEnum.CreatNewMCServerManagerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_LoadedMCServerManager>(pack => OnDataReceived(RespondTypeEnum.LoadedMCServerManager, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_MCServerManagers>(pack => OnDataReceived(RespondTypeEnum.LoadedMCServerManagers, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_LoadMCServerManagerFailed>(pack => OnDataReceived(RespondTypeEnum.LoadMCServerManagerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_StoppedMCServerManager>(pack => OnDataReceived(RespondTypeEnum.StoppedMCServerManager, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_StopMCServerManagerFailed>(pack => OnDataReceived(RespondTypeEnum.StopMCServerManagerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_DeletedMCServerManager>(pack => OnDataReceived(RespondTypeEnum.DeletedMCServerManager, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_DeleteMCServerManagerFailed>(pack => OnDataReceived(RespondTypeEnum.DeleteMCServerManagerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_ModifiedMCServerManagerConfig>(pack => OnDataReceived(RespondTypeEnum.ModifiedMCServerManagerConfig, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_RunMCServerSucceed>(pack => OnDataReceived(RespondTypeEnum.RunMCServerSucceed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_RunMCServerFailed>(pack => OnDataReceived(RespondTypeEnum.RunMCServerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_ShutdownMCServerSucceed>(pack => OnDataReceived(RespondTypeEnum.ShutdownMCServerSucceed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_ShutdownMCServerFailed>(pack => OnDataReceived(RespondTypeEnum.ShutdownMCServerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_KillMCServerSucceed>(pack => OnDataReceived(RespondTypeEnum.KillMCServerSucceed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_KillMCServerFailed>(pack => OnDataReceived(RespondTypeEnum.KillMCServerFailed, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_MCServerExited>(pack => OnDataReceived(RespondTypeEnum.MCServerExited, pack));
        _nativeClient.DataPackBus.Subscribe<Pack_ErrorInfo>(pack => OnDataReceived(RespondTypeEnum.ErrorInfo, pack));
    }

    private void UnsubscribeFromClientEvents()
    {
        if (_nativeClient == null) return;

        _nativeClient.ReportLog -= OnClientLog;
        _nativeClient.Connected -= OnClientConnected;
        _nativeClient.Disconnected -= OnClientDisconnected;
        _nativeClient.NeedPassword -= OnNeedPassword;
        _nativeClient.NeedToVerifyRSAPublicKey -= OnNeedRSAPublicKeyVerification;
    }

    private void OnClientLog(string message)
    {
        AddLogMessage($"[PMCSsE]", message, ToastType.Info);
    }

    private void OnClientConnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = true;
            IsConnecting = false;
            ConnectionStatus = "已连接";
            FingerprintStatus = "已验证";
            AddOrUpdateBackendEntry(ConnectionStatus, FingerprintStatus);
            var backendId = BackendIdManager.GetOrAssignId(_backendAddress ?? "", _backendPort ?? "");
            AddLogMessage("连接成功", $"✅ 已连接到 {_backendAddress}:{_backendPort} (ID: {backendId})", ToastType.Success);
            try { Connected?.Invoke(); } catch { }
        });
    }

    private void OnClientDisconnected(NativeClient.DisconnectedReasonEnum reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var (status, message, type) = GetDisconnectionInfo(reason);
            AddLogMessage($"❌ {message}", "", type);

            var oldAddress = _backendAddress ?? "";
            var oldPort = _backendPort ?? "";

            IsConnected = false;
            IsConnecting = false;
            ConnectionStatus = status;
            FingerprintStatus = "未确认";

            RemoveBackendEntry(oldAddress, oldPort);

            if (_nativeClient != null)
            {
                UnsubscribeFromClientEvents();
                try { _nativeClient.Dispose(); } catch { }
                _nativeClient = null;
            }

            try { Disconnected?.Invoke(); } catch { }
        });
    }

    private void OnDataReceived(RespondTypeEnum responseType, object? data)
    {
        AddLogMessage("收到数据", $"📥 收到数据: {responseType}", ToastType.Info);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DataPackReceived?.Invoke(responseType, data);
            }
            catch (Exception ex)
            {
                AddLogMessage("处理数据包出错", $"处理数据包出错: {ex.Message}", ToastType.Error);
            }
        });
    }

    private void OnNeedPassword()
    {
        AddLogMessage("需要访问密钥", "🔐 需要访问密钥", ToastType.Warning);

        if (!string.IsNullOrWhiteSpace(_password))
        {
            try
            {
                _nativeClient?.TypePassword(_password);
                AddLogMessage("已提交记忆的访问密钥", "✅ 已提交记忆的访问密钥", ToastType.Success);
                return;
            }
            catch (Exception ex)
            {
                AddLogMessage("提交密钥失败", $"提交密钥失败: {ex.Message}", ToastType.Error);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            try { NeedPassword?.Invoke("请输入访问密钥"); } catch { }
            ShowPasswordDialog();
        });
    }

    private void OnNeedRSAPublicKeyVerification(string fingerprint)
    {
        AddLogMessage("验证 RSA 公钥指纹", $"🔑 验证 RSA 公钥指纹: {fingerprint}", ToastType.Info);
        _pendingFingerprint = fingerprint ?? "";
        _pendingAddress = _backendAddress ?? "";
        _pendingPort = int.TryParse(_backendPort, out var pp) ? pp : 0;

        Dispatcher.UIThread.Post(() =>
        {
            var knownFp = StaticConfigManagerClass.GetKnownFingerprint(_pendingAddress, _pendingPort) ?? "";
            bool isMatch = !string.IsNullOrEmpty(knownFp) &&
                           string.Equals(knownFp, fingerprint, StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(knownFp) && isMatch)
            {
                AddLogMessage("自动通过验证", "✅ 服务器指纹匹配记忆，自动通过验证", ToastType.Success);
                FingerprintStatus = "已验证";
                if (_nativeClient != null) _nativeClient.VerifyRSAPublicKey(true);
                try { RSAFingerprintConfirmed?.Invoke(); } catch { }
                return;
            }

            if (!string.IsNullOrEmpty(knownFp) && !isMatch)
            {
                AddLogMessage("指纹不匹配", "⚠️ 警告：服务器指纹与记忆不一致！可能存在中间人攻击", ToastType.Error);
                FingerprintStatus = "指纹不匹配";
                try { NeedRSAPublicKeyVerification?.Invoke(fingerprint ?? "", knownFp, false); } catch { }
                try { RSAFingerprintRejected?.Invoke("服务器指纹与记忆不一致"); } catch { }
                ShowRSAWarningDialog(fingerprint ?? "", knownFp);
                return;
            }

            // 未知服务器 -> 请求用户确认
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
        if (_nativeClient == null) return;
        if (DialogManager == null)
        {
            AddLogMessage("自动通过 RSA 验证", "⚠️ 无对话框管理器，自动通过 RSA 验证", ToastType.Warning);
            _nativeClient.VerifyRSAPublicKey(true);
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

        var nativeClientRef = _nativeClient;
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
        if (_nativeClient == null) return;
        if (DialogManager == null)
        {
            AddLogMessage("拒绝连接", "⚠️ 无对话框管理器，拒绝连接（指纹不匹配）", ToastType.Error);
            _nativeClient.VerifyRSAPublicKey(false);
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

        var nativeClientRef = _nativeClient;
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
        if (_nativeClient == null) return;
        if (DialogManager == null) return;

        var stackPanel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        var textBox = new TextBox { PlaceholderText = "访问密钥", PasswordChar = '*' };
        stackPanel.Children.Add(new TextBlock { Text = "请输入访问密钥：" });
        stackPanel.Children.Add(textBox);

        var nativeClientRef = _nativeClient;

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
    /// 以当前 <see cref="_backendAddress"/>/<see cref="_backendPort"/> 为键，
    /// 在 <see cref="ConnectedBackends"/> 中新增或更新一条记录，使 UI 表格与实际连接状态同步。
    /// </summary>
    private void AddOrUpdateBackendEntry(string status, string fingerprintStatus)
    {
        var addr = _backendAddress ?? "";
        var port = _backendPort ?? "";
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(port)) return;

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
        });
    }

    /// <summary>
    /// 在 <see cref="ConnectedBackends"/> 中移除指定后端条目（连接断开时调用）。
    /// </summary>
    private void RemoveBackendEntry(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(port)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var existing = ConnectedBackends.FirstOrDefault(x =>
                string.Equals(x.BackendAddress, address, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.BackendPort, port, StringComparison.Ordinal));
            if (existing != null) ConnectedBackends.Remove(existing);
        });
    }

    // ============ 日志/Toast 辅助 ============

    private void AddLogMessage(string title, ToastType toastType = ToastType.Info, bool showToast = true)
    {
        AddLogMessage(title, string.Empty, toastType, showToast);
    }

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

        Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(entry);
            while (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);
        });

        if (showToast && toastType != ToastType.Info)
        {
            ShowToast(title, message, toastType);
        }
    }

    private void ClearLogs()
    {
        Dispatcher.UIThread.Post(() => LogEntries.Clear());
    }

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

    public void Dispose()
    {
        DisconnectInternal();
        GC.SuppressFinalize(this);
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
