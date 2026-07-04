using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive;
using System.Threading;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using ReactiveUI;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.Modules;

namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// 服务器状态（性能数据）管理视图模型。
/// 负责针对指定后端定期发送 GetServerState 请求、接收 Pack_ServerState 响应，
/// 并通过 GlobalCache 持久化当前快照与历史数据点，同时提供 UI 绑定所需的响应式属性。
/// 采用「视图模型持有后端 ID」的单后端模式：
///   若业务需要同时展示多个后端的性能曲线，请在父级为每个后端实例化一个 ServerStateViewModel。
/// </summary>
public class ServerStateViewModel : ViewModelBase
{
    // ==================== 常量 ====================

    /// <summary>默认轮询间隔（毫秒）。与 Avalonia 参考前端保持一致：1 秒/次。</summary>
    private const int DefaultPollIntervalMs = 1000;

    /// <summary>网络请求最大重试次数（超出后禁用轮询并提示错误）。</summary>
    private const int MaxConsecutiveRequestFailures = 10;

    /// <summary>最近一次收到数据包后，视为「在线」的时间窗口（秒）。超过该窗口则显示「无数据」状态。</summary>
    private const int StaleDataThresholdSeconds = 5;

    // ==================== 字段 ====================

    /// <summary>目标后端 ID（不能为空字符串）。</summary>
    private string _backendId = "";

    /// <summary>关联的连接视图模型（用于请求路由与事件订阅）。</summary>
    private ConnectionViewModel? _connection;

    /// <summary>是否已初始化（防止重复订阅事件）。</summary>
    private bool _initialized;

    /// <summary>是否已订阅 DataPackReceivedWithBackendId 事件（防重复订阅）。</summary>
    private bool _dataPackSubscribed;

    /// <summary>轮询定时器（非 UI 线程触发，内部回调会自动调度到 UI 线程）。</summary>
    private Timer? _pollTimer;

    /// <summary>轮询间隔毫秒数（可在运行时动态调整）。</summary>
    private int _pollIntervalMs = DefaultPollIntervalMs;

    /// <summary>是否开启轮询（对外命令可切换）。</summary>
    private bool _isPollingEnabled;

    /// <summary>当前是否正在等待一次 Pack_ServerState 回包（避免重入并发请求）。</summary>
    private bool _requestInFlight;

    /// <summary>连续请求失败计数（达到阈值后自动禁用轮询）。</summary>
    private int _consecutiveFailureCount;

    /// <summary>最后一次收到有效 Pack_ServerState 的 UTC 时间（用于「数据是否新鲜」判断）。</summary>
    private DateTime _lastSuccessfulReceiveUtc = DateTime.MinValue;

    // ---- UI 绑定字段 ----

    private bool _hasData;
    private bool _isStale;
    private string _errorMessage = "";
    private bool _hasError;
    private ServerStateSnapshot? _latestSnapshot;
    private List<CpuCurrentInfo> _cpus = new();
    private double _totalGigaBytes;
    private double _usedGigaBytes;
    private double _memoryUsagePercent;

    // ==================== 构造函数 ====================

    /// <summary>
    /// 默认构造函数（支持 DI / 无参初始化）。
    /// 调用方需随后显式调用 <see cref="Initialize"/> 指定后端与连接。
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "WhenAnyValue 表达式树仅访问本 ViewModel 的公共属性，这些属性经 XAML 编译绑定被静态根保留，AOT 裁剪下安全")]
    public ServerStateViewModel()
    {
        EnablePollingCommand = ReactiveCommand.Create(EnablePolling, this.WhenAnyValue(vm => vm.CanControlPolling));
        DisablePollingCommand = ReactiveCommand.Create(DisablePolling, this.WhenAnyValue(vm => vm.IsPollingEnabled));
        RefreshOnceCommand = ReactiveCommand.Create(() => RequestOnce(true), this.WhenAnyValue(vm => vm.CanRefreshOnce));
        ClearHistoryCommand = ReactiveCommand.Create(ClearHistory);
    }

    // ==================== 命令 ====================

    /// <summary>启用轮询命令。仅在 CanControlPolling=true 时可用。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> EnablePollingCommand { get; }

    /// <summary>禁用轮询命令。仅在当前开启轮询时可用。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> DisablePollingCommand { get; }

    /// <summary>手动刷新一次性能数据命令。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> RefreshOnceCommand { get; }

    /// <summary>清空该后端的历史性能数据（曲线重置）。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ClearHistoryCommand { get; }

    // ==================== 绑定属性（只读/可控） ====================

    /// <summary>
    /// 目标后端 ID。仅在 Initialize 中设置一次。
    /// </summary>
    public string BackendId
    {
        get => _backendId;
        private set => this.RaiseAndSetIfChanged(ref _backendId, value);
    }

    /// <summary>关联的连接视图模型。</summary>
    public ConnectionViewModel? Connection
    {
        get => _connection;
        private set
        {
            bool wasEqual = EqualityComparer<ConnectionViewModel?>.Default.Equals(_connection, value);
            this.RaiseAndSetIfChanged(ref _connection, value);
            // 仅当值实际发生变化时刷新依赖属性（互斥可见性、按钮可用性等）
            if (!wasEqual)
            {
                this.RaisePropertyChanged(nameof(CanControlPolling));
                this.RaisePropertyChanged(nameof(CanRefreshOnce));
                this.RaisePropertyChanged(nameof(IsBackendConnected));
            }
        }
    }

    /// <summary>目标后端当前是否处于已连接状态。</summary>
    public bool IsBackendConnected => Connection != null && Connection.GetClientByBackendId(BackendId) != null;

    /// <summary>当前是否已开启轮询。</summary>
    public bool IsPollingEnabled
    {
        get => _isPollingEnabled;
        private set
        {
            if (this.RaiseAndSetIfChanged(ref _isPollingEnabled, value))
            {
                this.RaisePropertyChanged(nameof(CanControlPolling));
            }
        }
    }

    /// <summary>是否允许控制轮询（已关联连接、后端存在或可连接）。</summary>
    public bool CanControlPolling => Connection != null && !string.IsNullOrEmpty(BackendId);

    /// <summary>是否允许手动刷新（已连接后端 + 当前无正在飞行的请求）。</summary>
    public bool CanRefreshOnce => IsBackendConnected && !_requestInFlight;

    /// <summary>是否已收到至少一次有效性能数据。</summary>
    public bool HasData
    {
        get => _hasData;
        private set => this.RaiseAndSetIfChanged(ref _hasData, value);
    }

    /// <summary>
    /// 数据是否「陈旧」：超过 StaleDataThresholdSeconds 未收到新数据即视为陈旧。
    /// 前端可据此在曲线/数字上打灰或显示「连接中」占位。
    /// </summary>
    public bool IsStale
    {
        get => _isStale;
        private set => this.RaiseAndSetIfChanged(ref _isStale, value);
    }

    /// <summary>是否存在错误（连续请求超限 / 请求抛出异常等）。</summary>
    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (this.RaiseAndSetIfChanged(ref _hasError, value))
            {
                this.RaisePropertyChanged(nameof(ShowLoadingOverlay));
                this.RaisePropertyChanged(nameof(ShowErrorState));
                this.RaisePropertyChanged(nameof(ShowEmptyState));
                this.RaisePropertyChanged(nameof(ShowData));
            }
        }
    }

    /// <summary>最近一次错误的可读描述。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>最新一次的服务器状态快照；未收到数据时为 null。</summary>
    public ServerStateSnapshot? LatestSnapshot
    {
        get => _latestSnapshot;
        private set => this.RaiseAndSetIfChanged(ref _latestSnapshot, value);
    }

    /// <summary>当前后端的所有 CPU 信息列表（按 ID 排序，便于 UI 绑定）。无数据时返回空列表。</summary>
    public List<CpuCurrentInfo> Cpus
    {
        get => _cpus;
        private set => this.RaiseAndSetIfChanged(ref _cpus, value);
    }

    /// <summary>系统总内存（GB）。无数据时为 0。</summary>
    public double TotalGigaBytes
    {
        get => _totalGigaBytes;
        private set => this.RaiseAndSetIfChanged(ref _totalGigaBytes, value);
    }

    /// <summary>当前已用内存（GB）。无数据时为 0。</summary>
    public double UsedGigaBytes
    {
        get => _usedGigaBytes;
        private set => this.RaiseAndSetIfChanged(ref _usedGigaBytes, value);
    }

    /// <summary>
    /// 内存使用率百分比（0.0 ~ 100.0）。
    /// Total=0 时（无数据 / 异常数据）返回 0.0，避免除零错误。
    /// </summary>
    public double MemoryUsagePercent
    {
        get => _memoryUsagePercent;
        private set => this.RaiseAndSetIfChanged(ref _memoryUsagePercent, value);
    }

    /// <summary>最后一次成功接收数据的本地时间字符串（UI 可直接绑定显示）。</summary>
    public string LastUpdateTimeText
    {
        get
        {
            if (_lastSuccessfulReceiveUtc == DateTime.MinValue) return "尚未收到数据";
            try { return _lastSuccessfulReceiveUtc.ToLocalTime().ToString("HH:mm:ss"); }
            catch { return "时间无效"; }
        }
    }

    // ---------- 视图层便捷状态属性 ----------

    /// <summary>是否显示加载中遮罩（已开启轮询、无错误、尚无数据）。</summary>
    public bool ShowLoadingOverlay => IsPollingEnabled && !HasError && !HasData;

    /// <summary>是否显示错误状态层（发生错误时优先展示）。</summary>
    public bool ShowErrorState => HasError;

    /// <summary>是否显示空状态（未开启轮询、无错误、无数据）。</summary>
    public bool ShowEmptyState => !IsPollingEnabled && !HasError && !HasData;

    /// <summary>是否正常显示性能数据区域。</summary>
    public bool ShowData => HasData && !HasError;

    // ==================== 初始化 / 销毁 ====================

    /// <summary>
    /// 初始化视图模型：指定后端 ID 与连接对象，并订阅数据包事件。
    /// 幂等安全：重复调用不会重复订阅。
    /// 初始化完成后若 GlobalCache 已存在该后端数据，会立即填充绑定属性。
    /// </summary>
    /// <param name="connectionViewModel">连接视图模型；不能为 null。</param>
    /// <param name="backendId">目标后端 ID；不能为 null 或空。</param>
    /// <exception cref="ArgumentNullException">参数为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">backendId 为空字符串时抛出。</exception>
    public void Initialize(ConnectionViewModel connectionViewModel, string backendId)
    {
        if (connectionViewModel == null) throw new ArgumentNullException(nameof(connectionViewModel));
        if (string.IsNullOrEmpty(backendId)) throw new ArgumentException("后端 ID 不能为空", nameof(backendId));

        BackendId = backendId;
        Connection = connectionViewModel;

        if (_initialized)
        {
            // 已初始化过，仅尝试用缓存填充 UI（后端 ID 变了的情况下）
            RefreshFromCache();
            return;
        }
        _initialized = true;

        SubscribeDataPackEvents();
        SubscribeConnectionEvents();

        // 先尝试用已有缓存填充，避免页面打开即空白
        RefreshFromCache();
    }

    /// <summary>
    /// 订阅后端数据包接收事件，仅处理当前后端 ID 的 Pack_ServerState。
    /// </summary>
    private void SubscribeDataPackEvents()
    {
        if (Connection == null || _dataPackSubscribed) return;
        _dataPackSubscribed = true;
        Connection.DataPackReceivedWithBackendId += OnDataPackReceived;
    }

    /// <summary>
    /// 订阅连接状态事件（后端断开、整体断开/重连），用于同步 IsBackendConnected / 自动停止轮询。
    /// </summary>
    private void SubscribeConnectionEvents()
    {
        if (Connection == null) return;

        Connection.Disconnected += OnConnectionDisconnected;
        Connection.DisconnectedWithBackendId += OnConnectionDisconnectedWithBackendId;
        Connection.ConnectedBackends.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(IsBackendConnected));
            this.RaisePropertyChanged(nameof(CanRefreshOnce));
        };
    }

    /// <summary>
    /// 释放资源：停止轮询、取消所有事件订阅，确保无内存泄漏。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            StopPolling();
            _pollTimer?.Dispose();
            _pollTimer = null;

            if (Connection != null && _dataPackSubscribed)
            {
                Connection.DataPackReceivedWithBackendId -= OnDataPackReceived;
                _dataPackSubscribed = false;
            }
            if (Connection != null)
            {
                Connection.Disconnected -= OnConnectionDisconnected;
                Connection.DisconnectedWithBackendId -= OnConnectionDisconnectedWithBackendId;
            }
        }
        base.Dispose(disposing);
    }

    // ==================== 轮询控制 ====================

    /// <summary>
    /// 启用性能数据轮询。若当前未连接后端，会立即返回错误。
    /// 成功启用后会立刻发出一次请求（不等间隔），然后按 PollIntervalMs 周期发送。
    /// </summary>
    public void EnablePolling()
    {
        if (!CanControlPolling)
        {
            SetError("尚未初始化后端连接，无法启用轮询。");
            return;
        }
        if (IsPollingEnabled) return;

        if (!IsBackendConnected)
        {
            SetError($"后端 {BackendId} 当前未连接，无法启用轮询。");
            return;
        }

        ClearError();
        IsPollingEnabled = true;

        _pollTimer?.Dispose();
        _pollTimer = new Timer(OnPollTimerCallback, null, 0, _pollIntervalMs);

        // 立即触发一次（用户体验：打开后立刻看到数据）
        RequestOnce(false);
    }

    /// <summary>
    /// 停止轮询（保留已缓存数据）。UI 仍可显示最后一次数据。
    /// </summary>
    public void DisablePolling()
    {
        if (!IsPollingEnabled) return;
        StopPolling();
    }

    /// <summary>
    /// 内部停止轮询实现（对外暴露为 DisablePolling，内部重连等场景也会调用）。
    /// </summary>
    private void StopPolling()
    {
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        IsPollingEnabled = false;
        _requestInFlight = false;
    }

    /// <summary>
    /// 手动刷新一次性能数据（不论轮询是否开启）。
    /// </summary>
    public void RequestManualRefresh() => RequestOnce(true);

    /// <summary>
    /// 清空历史数据（重置曲线），但保留当前最新快照（若存在）。
    /// </summary>
    public void ClearHistory()
    {
        if (string.IsNullOrEmpty(BackendId)) return;
        GlobalCache.ServerStateCache(BackendId).ClearHistory();
        // 仅刷新当前快照绑定，历史数据需要 UI 侧在绘图前重新 GetXxxHistory
        RefreshFromCache();
    }

    // ==================== 定时器与请求 ====================

    /// <summary>
    /// 定时器回调（线程池线程）。将请求操作调度到 UI 线程以保证状态属性变更线程安全。
    /// </summary>
    private void OnPollTimerCallback(object? state)
    {
        // 已释放 / 被禁用 时直接跳过
        if (IsDisposed || !IsPollingEnabled) return;
        Dispatcher.UIThread.Post(() => RequestOnce(false));
    }

    /// <summary>
    /// 发送一次 GetServerState 请求。
    /// 若上一次请求仍在飞行中（未收到回包或超时），则跳过该轮避免请求堆积。
    /// </summary>
    /// <param name="isManual">是否为用户手动触发（出错时会显示更强的错误提示）。</param>
    private void RequestOnce(bool isManual)
    {
        if (Connection == null || string.IsNullOrEmpty(BackendId))
        {
            if (isManual) SetError("尚未初始化后端连接。");
            return;
        }

        if (!IsBackendConnected)
        {
            if (isManual) SetError($"后端 {BackendId} 未连接，无法获取性能数据。");
            MarkStaleIfNeeded();
            return;
        }

        if (_requestInFlight)
        {
            // 上一个请求还没回包：认为网络慢或后端卡顿，不重复发送；
            // 但若是用户手动触发，给一次覆盖机会（置 false 再发）。
            if (!isManual) return;
            _requestInFlight = false;
        }

        try
        {
            _requestInFlight = true;
            Connection.RequestBackendTo(BackendId, RequestTypeEnum.GetServerState, new Pack_GetServerState());

            // 手动刷新模式下，保留错误但把 HasError 降为「警告级」（不清空现有数据）
            if (isManual && HasError && _consecutiveFailureCount == 0)
            {
                // 无残留错误，无需处理
            }
        }
        catch (Exception ex)
        {
            _requestInFlight = false;
            HandleRequestFailure(ex, isManual);
        }
    }

    // ==================== 数据包处理 ====================

    /// <summary>
    /// 通用数据包路由：仅处理属于当前后端的 Pack_ServerState。
    /// </summary>
    private void OnDataPackReceived(string backendId, RespondTypeEnum type, object? data)
    {
        // 非目标后端 / 非目标响应类型直接过滤
        if (!string.Equals(backendId, BackendId, StringComparison.Ordinal)) return;
        if (type != RespondTypeEnum.ServerState || data is not Pack_ServerState pack) return;

        HandleServerStatePack(pack);
    }

    /// <summary>
    /// 处理单个 Pack_ServerState 数据包：
    ///   1. 更新 GlobalCache（自动管理历史数据点环形截断、重复包过滤）
    ///   2. 重置失败计数、清除错误状态
    ///   3. 刷新绑定属性（快照、CPU 列表、内存数值等）
    /// </summary>
    private void HandleServerStatePack(Pack_ServerState pack)
    {
        if (pack == null) return;

        // 写入缓存（ConnectionViewModel 的 OnDataReceived 其实也会写一次；
        // 这里再次写入不会产生副作用：GlobalCache 内会按 CheckTime 去重）
        GlobalCache.ServerStateCache(BackendId).UpdateFromPack(pack);

        // 请求回包，清空调度器的「飞行中」标志
        _requestInFlight = false;
        _consecutiveFailureCount = 0;
        _lastSuccessfulReceiveUtc = DateTime.UtcNow;
        ClearError();

        // 刷新绑定属性（全部在 UI 线程执行）
        Dispatcher.UIThread.Post(RefreshFromCache);
    }

    /// <summary>
    /// 从 GlobalCache 重新读取当前后端的最新快照并刷新所有绑定属性。
    /// 不触发网络请求，纯本地读取（线程安全）。
    /// </summary>
    public void RefreshFromCache()
    {
        if (string.IsNullOrEmpty(BackendId)) return;

        var accessor = GlobalCache.ServerStateCache(BackendId);
        var snap = accessor.Latest;
        if (snap == null)
        {
            // 缓存中暂无数据：保持字段为默认值，仅刷新派生状态
            HasData = false;
            LatestSnapshot = null;
            Cpus = new List<CpuCurrentInfo>();
            TotalGigaBytes = 0.0;
            UsedGigaBytes = 0.0;
            MemoryUsagePercent = 0.0;
        }
        else
        {
            HasData = true;
            LatestSnapshot = snap;
            // CPU 按 ID 升序返回，保证 UI 图例稳定
            Cpus = snap.Cpus.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            TotalGigaBytes = snap.TotalGigaBytes;
            UsedGigaBytes = snap.UsedGigaBytes;
            MemoryUsagePercent = snap.MemoryUsagePercent;
        }

        MarkStaleIfNeeded();
        RaiseStateProperties();
    }

    /// <summary>
    /// 检查数据是否已超出「新鲜窗口」并更新 IsStale 状态。
    /// </summary>
    private void MarkStaleIfNeeded()
    {
        bool stale;
        if (_lastSuccessfulReceiveUtc == DateTime.MinValue)
            stale = HasData == false;
        else
            stale = (DateTime.UtcNow - _lastSuccessfulReceiveUtc).TotalSeconds > StaleDataThresholdSeconds;

        if (_isStale != stale)
        {
            _isStale = stale;
            this.RaisePropertyChanged(nameof(IsStale));
        }
    }

    /// <summary>
    /// 统一刷新与视图状态相关的派生属性（HasData/ShowXxx 等实际通过 setter 触发；
    /// 此处保留 LastUpdateTimeText 的通知，因为它是计算属性）。
    /// </summary>
    private void RaiseStateProperties()
    {
        this.RaisePropertyChanged(nameof(LastUpdateTimeText));
        this.RaisePropertyChanged(nameof(CanRefreshOnce));
        this.RaisePropertyChanged(nameof(ShowLoadingOverlay));
        this.RaisePropertyChanged(nameof(ShowErrorState));
        this.RaisePropertyChanged(nameof(ShowEmptyState));
        this.RaisePropertyChanged(nameof(ShowData));
    }

    // ==================== 连接事件回调 ====================

    /// <summary>
    /// 整体连接断开：停止轮询，保留缓存但标记为陈旧。
    /// </summary>
    private void OnConnectionDisconnected()
    {
        StopPolling();
        MarkStaleIfNeeded();
        RaiseStateProperties();
    }

    /// <summary>
    /// 指定后端断开：若为当前后端，则停止轮询并清空状态。
    /// </summary>
    private void OnConnectionDisconnectedWithBackendId(string backendId)
    {
        if (!string.Equals(backendId, BackendId, StringComparison.Ordinal)) return;
        StopPolling();
        _lastSuccessfulReceiveUtc = DateTime.MinValue;
        MarkStaleIfNeeded();
        RaiseStateProperties();
        SetError($"后端 {BackendId} 已断开，已停止性能数据轮询。");
    }

    // ==================== 错误处理 ====================

    /// <summary>
    /// 处理请求失败：累计失败计数，超过阈值时自动禁用轮询并通过 Toast/Error 暴露。
    /// </summary>
    /// <param name="ex">异常对象（可为 null）。</param>
    /// <param name="isManual">是否由用户手动触发（是则错误提示更明显）。</param>
    private void HandleRequestFailure(Exception? ex, bool isManual)
    {
        _consecutiveFailureCount++;

        string msg;
        if (ex != null)
            msg = $"获取性能数据失败（第 {_consecutiveFailureCount} 次）：{ex.Message}";
        else
            msg = $"获取性能数据失败（第 {_consecutiveFailureCount} 次）";

        if (_consecutiveFailureCount >= MaxConsecutiveRequestFailures)
        {
            StopPolling();
            msg += $"，连续失败 {MaxConsecutiveRequestFailures} 次，已自动停止轮询。请检查后端连接后手动重试。";
            SetError(msg);
            if (isManual) ShowToast("性能数据轮询已停止", msg, ToastType.Error);
        }
        else
        {
            // 非致命：仅更新错误消息，不清空现有数据
            ErrorMessage = msg;
            if (isManual) ShowToast("获取性能数据失败", msg, ToastType.Warning);
        }
    }

    /// <summary>
    /// 设置错误状态：HasError=true 并写入错误信息。
    /// </summary>
    private void SetError(string message)
    {
        ErrorMessage = message ?? "";
        HasError = true;
    }

    /// <summary>
    /// 清除错误状态（收到新数据包、成功启用轮询等场景调用）。
    /// </summary>
    private void ClearError()
    {
        if (HasError || ErrorMessage.Length > 0)
        {
            ErrorMessage = "";
            HasError = false;
        }
    }

    // ==================== 便捷：历史数据读取代理 ====================
    // 这些方法本质是 GlobalCache.ServerStateCache(BackendId) 的薄封装，
    // 方便 UI 层在一个 ViewModel 上完成全部数据访问，而不必直接依赖 GlobalCache。

    /// <summary>
    /// 获取所有 CPU 的唯一组合键列表（用于 UI 构建曲线系列或图例）。
    /// </summary>
    public List<string> GetCpuKeys() =>
        string.IsNullOrEmpty(BackendId) ? new List<string>() : GlobalCache.ServerStateCache(BackendId).GetCpuKeys();

    /// <summary>
    /// 获取指定 CPU 的历史使用率数据点副本（按时间升序）。
    /// 找不到 CPU 时返回空列表。
    /// </summary>
    /// <param name="cpuKey">CPU 唯一组合键（NameAndId）。</param>
    public List<CpuUsageDataPoint> GetCpuHistory(string cpuKey) =>
        string.IsNullOrEmpty(BackendId) ? new List<CpuUsageDataPoint>() : GlobalCache.ServerStateCache(BackendId).GetCpuHistory(cpuKey);

    /// <summary>
    /// 获取系统整体内存占用的历史数据点副本（按时间升序）。
    /// </summary>
    public List<MemoryUsageDataPoint> GetMemoryHistory() =>
        string.IsNullOrEmpty(BackendId) ? new List<MemoryUsageDataPoint>() : GlobalCache.ServerStateCache(BackendId).GetMemoryHistory();
}
