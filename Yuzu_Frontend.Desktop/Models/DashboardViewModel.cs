using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using ReactiveUI;
using SukiUI.Toasts;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;
using RxVoid = System.Reactive.Unit;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 仪表盘（Dashboard）视图模型。
/// 负责聚合当前已连接后端与管理器的关键运行指标（卡片统计、后端状态、管理器运行概要），
/// 提供给 <see cref="Yuzu_Frontend.Desktop.Views.Dashboard"/> 页面绑定使用。
///
/// <para>设计说明：</para>
/// <list type="bullet">
///   <item>优先从 <see cref="GlobalCache"/> 读取缓存数据作为即时显示（首屏不白屏）。</item>
///   <item>在 <see cref="ConnectionViewModel.DataPackReceivedWithBackendId"/> 事件到达时
///         增量刷新统计与后端状态卡片，避免周期性轮询。</item>
///   <item>管理器状态数据来自后端推送的 MCServerManagerConfigs / Pack_ServerState 等事件。</item>
/// </list>
/// </summary>
public class DashboardViewModel : ReactiveObject, IDisposable
{
    // ============================================================
    //  依赖引用
    // ============================================================
    private readonly ISukiToastManager? _toastManager;
    private ConnectionViewModel? _connection;
    private readonly DisposableManager _disposables = new();
    private bool _disposed;

    // ============================================================
    //  属性支持字段
    // ============================================================
    private int _connectedBackendCount;
    private int _totalManagerCount;
    private int _runningManagerCount;
    private int _loadedManagerCount;
    private double _averageCpuPercent;
    private double _totalMemoryUsedGigaBytes;
    private double _totalMemoryGigaBytes;
    private string _lastRefreshText = "尚未刷新";
    private bool _isRefreshing;

    // ============================================================
    //  对外可绑定集合 / 属性
    // ============================================================

    /// <summary>已连接后端列表的 Dashboard 展示模型（含地址、后端ID、CPU/内存指标等）。</summary>
    public RangeObservableCollection<DashboardBackendCard> BackendCards { get; } = new();

    /// <summary>管理器运行概要：Top N（默认最多 8 个），用于快速浏览运行情况。</summary>
    public RangeObservableCollection<DashboardManagerEntry> ManagerOverview { get; } = new();

    /// <summary>当前已连接的后端数量。</summary>
    public int ConnectedBackendCount
    {
        get => _connectedBackendCount;
        set => this.RaiseAndSetIfChanged(ref _connectedBackendCount, value);
    }

    /// <summary>全部管理器总数。</summary>
    public int TotalManagerCount
    {
        get => _totalManagerCount;
        set => this.RaiseAndSetIfChanged(ref _totalManagerCount, value);
    }

    /// <summary>正在运行的管理器数量。</summary>
    public int RunningManagerCount
    {
        get => _runningManagerCount;
        set => this.RaiseAndSetIfChanged(ref _runningManagerCount, value);
    }

    /// <summary>已加载（加载状态存在）的管理器数量。</summary>
    public int LoadedManagerCount
    {
        get => _loadedManagerCount;
        set => this.RaiseAndSetIfChanged(ref _loadedManagerCount, value);
    }

    /// <summary>所有后端的平均 CPU 使用率（0-100）；无数据时返回 0。</summary>
    public double AverageCpuPercent
    {
        get => _averageCpuPercent;
        set => this.RaiseAndSetIfChanged(ref _averageCpuPercent, value);
    }

    /// <summary>所有后端的总内存使用量（GB）。</summary>
    public double TotalMemoryUsedGigaBytes
    {
        get => _totalMemoryUsedGigaBytes;
        set => this.RaiseAndSetIfChanged(ref _totalMemoryUsedGigaBytes, value);
    }

    /// <summary>所有后端的总内存容量（GB）。</summary>
    public double TotalMemoryGigaBytes
    {
        get => _totalMemoryGigaBytes;
        set => this.RaiseAndSetIfChanged(ref _totalMemoryGigaBytes, value);
    }

    /// <summary>上次刷新时间的显示文本。</summary>
    public string LastRefreshText
    {
        get => _lastRefreshText;
        set => this.RaiseAndSetIfChanged(ref _lastRefreshText, value);
    }

    /// <summary>是否正在刷新数据（用于 UI loading 指示）。</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => this.RaiseAndSetIfChanged(ref _isRefreshing, value);
    }

    /// <summary>手动刷新仪表盘数据的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshDashboardCommand { get; }

    // ============================================================
    //  构造 / 初始化
    // ============================================================
    public DashboardViewModel(ISukiToastManager? toastManager = null)
    {
        _toastManager = toastManager;

        // 刷新命令：若有连接则主动请求各后端数据，否则仅基于缓存刷新
        RefreshDashboardCommand = ReactiveCommand.CreateFromTask<RxVoid, RxVoid>(
            async _ =>
            {
                await RefreshAllAsync(forceRequest: true);
                return RxVoid.Default;
            });
    }

    /// <summary>
    /// 绑定 <see cref="ConnectionViewModel"/>，订阅连接与数据包事件，
    /// 并执行一次初始刷新。通常在页面 <c>OnAttachedToVisualTree</c> 时调用。
    /// </summary>
    public void Attach(ConnectionViewModel? connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 先取消旧订阅
        _disposables.Clear();

        _connection = connection;
        if (_connection == null)
        {
            // 无连接：仍基于缓存展示可能残留的数据，避免空白页
            _ = RefreshAllAsync(forceRequest: false);
            return;
        }

        async void RefreshNoRequest() => await RefreshAllAsync(forceRequest: false);

        // Attach 通常在 UI 线程触发（OnAttachedToVisualTree），捕获当前同步上下文，
        // 用于将 Throttle 后的回调封送回 UI 线程（ReactiveUI 24 已移除 RxApp.MainThreadScheduler）。
        var uiScheduler = new SynchronizationContextScheduler(SynchronizationContext.Current ?? new SynchronizationContext());

        // 连接建立 / 断开回调：重建后端列表 + 刷新统计（ConnectionViewModel 的事件为 Action 风格）
        var connectedObs = Observable.FromEvent(
                h => _connection.Connected += h,
                h => _connection.Connected -= h);
        _disposables.Register(connectedObs.Subscribe(_ => RefreshNoRequest()));

        var disconnectedObs = Observable.FromEvent(
                h => _connection.Disconnected += h,
                h => _connection.Disconnected -= h);
        _disposables.Register(disconnectedObs.Subscribe(_ => RefreshNoRequest()));

        var discBackendObs = Observable.FromEvent<string>(
                h => _connection.DisconnectedWithBackendId += h,
                h => _connection.DisconnectedWithBackendId -= h)
            .Select(_ => RxVoid.Default);
        _disposables.Register(discBackendObs.Subscribe(_ => RefreshNoRequest()));

        // 到达任意数据包时做增量刷新（成本很低，直接在 UI 线程汇总缓存）
        var dataPackObs = Observable.FromEvent<Action<string, RespondTypeEnum, object?>, object?>(
                handler => (_, _, pack) => handler(pack),
                h => _connection.DataPackReceivedWithBackendId += h,
                h => _connection.DataPackReceivedWithBackendId -= h)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(uiScheduler);
        _disposables.Register(dataPackObs.Subscribe(_ => RefreshNoRequest()));

        // ConnectedBackends 集合变更（选择态 / 状态文本更新）
        if (_connection.ConnectedBackends is INotifyCollectionChanged ncc)
        {
            var obs = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => ncc.CollectionChanged += h,
                    h => ncc.CollectionChanged -= h)
                .Throttle(TimeSpan.FromMilliseconds(100))
                .ObserveOn(uiScheduler);
            _disposables.Register(obs.Subscribe(_ => RefreshNoRequest()));
        }

        _ = RefreshAllAsync(forceRequest: false);
    }

    // ============================================================
    //  核心刷新逻辑
    // ============================================================

    /// <summary>
    /// 刷新仪表盘全量数据。
    /// </summary>
    /// <param name="forceRequest">是否向后端主动请求最新列表/状态。</param>
    private async Task<RxVoid> RefreshAllAsync(bool forceRequest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            IsRefreshing = true;
            var conn = _connection;

            // 1. 若主动刷新：向每个已连接后端请求管理器列表 + 服务器状态
            if (forceRequest && conn != null && conn.IsConnected)
            {
                var snap = conn.ConnectedBackends.ToList();
                foreach (var be in snap)
                {
                    var backendId = be.BackendId;
                    if (string.IsNullOrEmpty(backendId)) continue;
                    try
                    {
                        conn.RequestBackendTo(backendId, RequestTypeEnum.GetMCServerManagersList,
                            new Pack_GetMCServerManagerConfigsList());
                        conn.RequestBackendTo(backendId, RequestTypeEnum.GetServerState,
                            new Pack_GetServerState());
                    }
                    catch
                    {
                        // 单个后端请求失败不影响整体
                    }
                }
            }

            // 2. 汇总统计数据
            var backendCards = new List<DashboardBackendCard>();
            var managersOverview = new List<DashboardManagerEntry>();

            var connectedBackendIds = conn?.ConnectedBackends
                .Where(x => !string.IsNullOrEmpty(x.BackendId))
                .Select(x => x.BackendId)
                .Distinct()
                .ToList() ?? new List<string>();

            // 缓存中所有 backendId 都可能有数据（含未连接的，但若 connection 存在则以连接为主）
            var allBackendIds = GlobalCache.GetAllBackendIds();
            var backendIdsToShow = connectedBackendIds.Count > 0
                ? connectedBackendIds.Union(allBackendIds).ToList()
                : allBackendIds;

            int totalMgr = 0, runningMgr = 0, loadedMgr = 0;
            double cpuSum = 0.0;
            int cpuDataPoints = 0;
            double totalMemGb = 0.0, usedMemGb = 0.0;

            foreach (var bid in backendIdsToShow)
            {
                var be = conn?.ConnectedBackends.FirstOrDefault(x => x.BackendId == bid);

                // 后端基础信息
                var beCfg = GlobalCache.BackendConfigCache(bid);
                var addr = be?.BackendAddress ?? beCfg.Address ?? "";
                var port = be?.BackendPort ?? beCfg.Port ?? "";
                var connStatus = be?.ConnectionStatus ?? (beCfg.Exists ? "已缓存" : "未连接");
                var fingerprint = be?.FingerprintStatus ?? "未知";

                // 服务器性能
                var ss = GlobalCache.ServerStateCache(bid);
                var snap = ss.Latest;
                double cpuPercent = 0.0;
                if (snap != null && snap.Cpus.Count > 0)
                {
                    cpuPercent = snap.Cpus.Average(c => c.UsagePercent);
                    cpuSum += cpuPercent;
                    cpuDataPoints++;
                }
                double totalGb = ss.TotalGigaBytes;
                double usedGb = ss.UsedGigaBytes;
                totalMemGb += totalGb;
                usedMemGb += usedGb;

                // 该后端下的管理器
                var mgrIds = beCfg.GetManagerIds();
                var managerDisplayList = new List<DashboardBackendManagerRef>();

                foreach (var mgrId in mgrIds)
                {
                    totalMgr++;
                    var mgrAcc = GlobalCache.ManagerConfigCache(bid, mgrId);
                    var mgrName = mgrAcc.Name;
                    if (string.IsNullOrEmpty(mgrName)) mgrName = mgrId;

                    // 运行状态：优先从 config 的 IsRunning 读取；无数据时视为未加载
                    bool isRunning = false;
                    bool isLoaded = false;
                    var cfg = mgrAcc.Config;
                    if (cfg != null)
                    {
                        // 通过反射或属性访问判断：MCServerManagerConfig 可能提供 IsRunning / IsLoaded
                        isRunning = TryReadBool(cfg, "IsRunning", "IsMCServerRunning");
                        isLoaded = TryReadBool(cfg, "IsLoaded");
                    }
                    if (isRunning) runningMgr++;
                    if (isLoaded) loadedMgr++;

                    managerDisplayList.Add(new DashboardBackendManagerRef
                    {
                        ManagerId = mgrId,
                        Name = mgrName,
                        IsRunning = isRunning,
                        IsLoaded = isLoaded,
                    });
                }

                // 合并到管理器总览（最多 8 个，优先正在运行的）
                foreach (var m in managerDisplayList.OrderByDescending(x => x.IsRunning).Take(8))
                {
                    managersOverview.Add(new DashboardManagerEntry
                    {
                        BackendId = bid,
                        BackendName = $"{addr}:{(string.IsNullOrEmpty(port) ? "?" : port)}",
                        ManagerId = m.ManagerId,
                        Name = m.Name,
                        IsRunning = m.IsRunning,
                        IsLoaded = m.IsLoaded,
                    });
                }

                backendCards.Add(new DashboardBackendCard
                {
                    BackendId = bid,
                    BackendName = $"{addr}:{(string.IsNullOrEmpty(port) ? "?" : port)}",
                    Address = addr,
                    Port = port,
                    ConnectionStatus = connStatus,
                    FingerprintStatus = fingerprint,
                    ManagerCount = managerDisplayList.Count,
                    RunningManagerCount = managerDisplayList.Count(x => x.IsRunning),
                    LoadedManagerCount = managerDisplayList.Count(x => x.IsLoaded),
                    CpuUsagePercent = cpuPercent,
                    MemoryUsedGigaBytes = usedGb,
                    MemoryTotalGigaBytes = totalGb,
                    Managers = managerDisplayList,
                });
            }

            // 3. 推送 UI 更新（在 Dispatcher 上）
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ConnectedBackendCount = connectedBackendIds.Count;
                TotalManagerCount = totalMgr;
                RunningManagerCount = runningMgr;
                LoadedManagerCount = loadedMgr;
                AverageCpuPercent = cpuDataPoints > 0 ? (cpuSum / cpuDataPoints) : 0.0;
                TotalMemoryUsedGigaBytes = usedMemGb;
                TotalMemoryGigaBytes = totalMemGb;
                LastRefreshText = $"更新时间：{DateTime.Now:HH:mm:ss}";

                // 刷新集合（批量更新减少 UI 刷新次数）
                BackendCards.ReplaceAll(backendCards);
                ManagerOverview.ReplaceAll(managersOverview);
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            try
            {
                _toastManager?.CreateToast()
                    .WithTitle("仪表盘刷新失败")
                    .WithContent(ex.Message)
                    .OfType(NotificationType.Error)
                    .Dismiss().After(TimeSpan.FromSeconds(4))
                    .Queue();
            }
            catch { /* 忽略 toast 自身的异常 */ }
        }
        finally
        {
            IsRefreshing = false;
        }

        return RxVoid.Default;
    }

    /// <summary>
    /// 从对象尝试读取多个候选命名的 bool 属性（兼容不同版本的数据包）。
    /// </summary>
    /// <remarks>
    /// Native AOT 说明：此处读取的 MCServerManagerConfig 为 PMCSsE_Communicator 库的外部类型，
    /// 无法静态确认属性保留，使用 UnconditionalSuppressMessage 局部抑制 IL2075 警告；
    /// 运行时若属性被裁剪将捕获异常返回 false，不影响仪表盘其他功能。
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "外部库类型属性裁剪风险可接受，异常已捕获并返回 false 兜底")]
    private static bool TryReadBool(object obj, params string[] propNames)
    {
        try
        {
            foreach (var name in propNames)
            {
                var p = obj.GetType().GetProperty(name);
                if (p != null && p.GetValue(obj) is bool b)
                    return b;
            }
        }
        catch { /* 忽略反射异常 */ }
        return false;
    }

    // ============================================================
    //  IDisposable
    // ============================================================
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
    }
}

// ============================================================
//  Dashboard 展示用内部模型（UI 只读）
// ============================================================

/// <summary>单个后端在仪表盘上的展示卡片数据。</summary>
public class DashboardBackendCard : ReactiveObject
{
    private string _backendId = "";
    private string _backendName = "";
    private string _address = "";
    private string _port = "";
    private string _connectionStatus = "";
    private string _fingerprintStatus = "";
    private int _managerCount;
    private int _runningManagerCount;
    private int _loadedManagerCount;
    private double _cpuUsagePercent;
    private double _memoryUsedGigaBytes;
    private double _memoryTotalGigaBytes;

    public string BackendId
    {
        get => _backendId;
        set { this.RaiseAndSetIfChanged(ref _backendId, value); this.RaisePropertyChanged(nameof(MemoryUsagePercent)); }
    }

    public string BackendName
    {
        get => _backendName;
        set => this.RaiseAndSetIfChanged(ref _backendName, value);
    }

    public string Address
    {
        get => _address;
        set => this.RaiseAndSetIfChanged(ref _address, value);
    }

    public string Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    public string FingerprintStatus
    {
        get => _fingerprintStatus;
        set => this.RaiseAndSetIfChanged(ref _fingerprintStatus, value);
    }

    public int ManagerCount
    {
        get => _managerCount;
        set => this.RaiseAndSetIfChanged(ref _managerCount, value);
    }

    public int RunningManagerCount
    {
        get => _runningManagerCount;
        set => this.RaiseAndSetIfChanged(ref _runningManagerCount, value);
    }

    public int LoadedManagerCount
    {
        get => _loadedManagerCount;
        set => this.RaiseAndSetIfChanged(ref _loadedManagerCount, value);
    }

    public double CpuUsagePercent
    {
        get => _cpuUsagePercent;
        set => this.RaiseAndSetIfChanged(ref _cpuUsagePercent, value);
    }

    public double MemoryUsedGigaBytes
    {
        get => _memoryUsedGigaBytes;
        set { this.RaiseAndSetIfChanged(ref _memoryUsedGigaBytes, value); this.RaisePropertyChanged(nameof(MemoryUsagePercent)); }
    }

    public double MemoryTotalGigaBytes
    {
        get => _memoryTotalGigaBytes;
        set { this.RaiseAndSetIfChanged(ref _memoryTotalGigaBytes, value); this.RaisePropertyChanged(nameof(MemoryUsagePercent)); }
    }

    public List<DashboardBackendManagerRef> Managers { get; set; } = new();

    public double MemoryUsagePercent => MemoryTotalGigaBytes > 0
        ? (MemoryUsedGigaBytes * 100.0 / MemoryTotalGigaBytes)
        : 0;
}

/// <summary>后端卡片内的管理器简要引用。</summary>
public class DashboardBackendManagerRef
{
    public string ManagerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool IsLoaded { get; set; }
}

/// <summary>仪表盘管理器快速浏览条目（跨后端汇总）。</summary>
public class DashboardManagerEntry : ReactiveObject
{
    private string _backendId = "";
    private string _backendName = "";
    private string _managerId = "";
    private string _name = "";
    private bool _isRunning;
    private bool _isLoaded;

    public string BackendId
    {
        get => _backendId;
        set => this.RaiseAndSetIfChanged(ref _backendId, value);
    }

    public string BackendName
    {
        get => _backendName;
        set => this.RaiseAndSetIfChanged(ref _backendName, value);
    }

    public string ManagerId
    {
        get => _managerId;
        set => this.RaiseAndSetIfChanged(ref _managerId, value);
    }

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        set => this.RaiseAndSetIfChanged(ref _isLoaded, value);
    }
}
