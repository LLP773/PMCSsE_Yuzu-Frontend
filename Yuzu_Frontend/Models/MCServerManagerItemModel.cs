using PMCSsE_Communicator;
using ReactiveUI;

namespace Yuzu_Frontend.Models;

/// <summary>
/// MC 服务端管理器列表项模型。
/// 表示一个管理器实例在 UI 列表中展示所需的全部信息，
/// 包括基本配置、运行状态及资源占用指标，供概览页与配置页共用。
/// </summary>
public class MCServerManagerItemModel : ReactiveObject, IManagerEntity
{
    private string _managerId = "";              // 管理器唯一标识
    private string _mcServerName = "";           // 服务端名称
    private string _backendId = "";              // 所属后端 ID
    private string _mcServerType = "";           // 服务端类型
    private string _mcServerDirectory = "";       // 服务端工作目录
    private string _javaPath = "";               // Java 可执行文件路径
    private string _startUpArguments = "";        // 启动参数
    private bool _isMCServerRunning;            // MC 服务端进程是否运行中
    private bool _isLoaded;                      // 管理器是否已加载

    private double _cpuUsagePercent;            // CPU 占用百分比
    private long _memoryUsageBytes;             // 已用内存（字节）
    private long _maxMemoryBytes;               // 最大内存上限（字节）
    private int _onlinePlayerCount;             // 在线玩家数
    private int _maxPlayerCount;                // 最大玩家数

    /// <summary>管理器唯一标识。</summary>
    public string ManagerId
    {
        get => _managerId;
        set => this.RaiseAndSetIfChanged(ref _managerId, value);
    }

    /// <summary>服务端名称。</summary>
    public string MCServerName
    {
        get => _mcServerName;
        set => this.RaiseAndSetIfChanged(ref _mcServerName, value);
    }

    /// <summary>所属后端 ID。</summary>
    public string BackendId
    {
        get => _backendId;
        set => this.RaiseAndSetIfChanged(ref _backendId, value);
    }

    /// <summary>服务端类型。</summary>
    public string MCServerType
    {
        get => _mcServerType;
        set => this.RaiseAndSetIfChanged(ref _mcServerType, value);
    }

    /// <summary>服务端工作目录。</summary>
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

    /// <summary>启动参数。</summary>
    public string StartUpArguments
    {
        get => _startUpArguments;
        set => this.RaiseAndSetIfChanged(ref _startUpArguments, value);
    }

    /// <summary>MC 服务端进程是否运行中。</summary>
    public bool IsMCServerRunning
    {
        get => _isMCServerRunning;
        set { this.RaiseAndSetIfChanged(ref _isMCServerRunning, value); this.RaisePropertyChanged(nameof(MCServerStatus)); }
    }

    /// <summary>管理器是否已加载。</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        set { this.RaiseAndSetIfChanged(ref _isLoaded, value); this.RaisePropertyChanged(nameof(MCServerStatus)); }
    }

    /// <summary>是否运行中（与 IsMCServerRunning 等价，满足 IManagerEntity 接口）。</summary>
    public bool IsRunning => IsMCServerRunning;

    /// <summary>服务端状态文本：运行中 / 已停止。</summary>
    public string MCServerStatus => IsMCServerRunning ? "运行中" : "已停止";

    /// <summary>CPU 占用百分比。</summary>
    public double CPUUsagePercent
    {
        get => _cpuUsagePercent;
        set => this.RaiseAndSetIfChanged(ref _cpuUsagePercent, value);
    }

    /// <summary>已用内存（字节）。</summary>
    public long MemoryUsageBytes
    {
        get => _memoryUsageBytes;
        set => this.RaiseAndSetIfChanged(ref _memoryUsageBytes, value);
    }

    /// <summary>最大内存上限（字节）。</summary>
    public long MaxMemoryBytes
    {
        get => _maxMemoryBytes;
        set => this.RaiseAndSetIfChanged(ref _maxMemoryBytes, value);
    }

    /// <summary>在线玩家数。</summary>
    public int OnlinePlayerCount
    {
        get => _onlinePlayerCount;
        set => this.RaiseAndSetIfChanged(ref _onlinePlayerCount, value);
    }

    /// <summary>最大玩家数。</summary>
    public int MaxPlayerCount
    {
        get => _maxPlayerCount;
        set => this.RaiseAndSetIfChanged(ref _maxPlayerCount, value);
    }

    /// <summary>内存使用百分比（MaxMemoryBytes 为 0 时返回 0）。</summary>
    public double MemoryUsagePercent => MaxMemoryBytes > 0 ? (MemoryUsageBytes * 100.0 / MaxMemoryBytes) : 0;

    /// <summary>玩家数量展示文本，格式为 "在线/最大"。</summary>
    public string PlayerCountDisplay => $"{OnlinePlayerCount}/{MaxPlayerCount}";

    /// <summary>默认构造函数。</summary>
    public MCServerManagerItemModel() { }

    /// <summary>
    /// 从后端配置构造列表项。
    /// 注意：不再在此处写入 GlobalCache —— 缓存写入统一由 ConnectionViewModel.OnDataReceived 处理，
    /// 避免因 backendId 不一致导致缓存键错配。
    /// </summary>
    /// <param name="config">后端返回的管理器配置。</param>
    /// <param name="backendId">所属后端 ID。</param>
    public MCServerManagerItemModel(MCServerManagerConfig config, string backendId = "")
    {
        ManagerId = config.ManagerID;
        MCServerName = config.MCServerName;
        BackendId = backendId;
        MCServerType = config.MCServerType;
        MCServerDirectory = config.MCServerDirectory;
        JavaPath = config.JavaPath;
        StartUpArguments = config.StartUpArguments;
    }
}