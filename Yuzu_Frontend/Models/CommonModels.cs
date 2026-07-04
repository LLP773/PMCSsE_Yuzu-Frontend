using System;
using System.Collections.Generic;
using ReactiveUI;
using PMCSsE_Communicator.SharedCodes.ServerMonitor;

namespace Yuzu_Frontend.Models;

/// <summary>
/// 服务器日志条目
/// </summary>
public class LogEntryModel : ReactiveObject
{
    private ulong _id;                          // 日志唯一 ID
    private string _content = "";               // 日志正文内容
    private DateTime _timestamp = DateTime.Now; // 日志产生时间
    private string _level = "INFO";             // 日志级别（INFO/WARN/ERROR 等）

    /// <summary>日志唯一 ID。</summary>
    public ulong Id
    {
        get => _id;
        set => this.RaiseAndSetIfChanged(ref _id, value);
    }

    /// <summary>日志正文内容。</summary>
    public string Content
    {
        get => _content;
        set => this.RaiseAndSetIfChanged(ref _content, value);
    }

    /// <summary>日志产生时间。</summary>
    public DateTime Timestamp
    {
        get => _timestamp;
        set => this.RaiseAndSetIfChanged(ref _timestamp, value);
    }

    /// <summary>日志级别文本（如 INFO、WARN、ERROR）。</summary>
    public string Level
    {
        get => _level;
        set => this.RaiseAndSetIfChanged(ref _level, value);
    }

    /// <summary>格式化的时间戳文本（yyyy-MM-dd HH:mm:ss）。</summary>
    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>格式化的完整显示文本，包含时间、级别与内容。</summary>
    public string DisplayText => $"[{TimeText}] [{Level}] {Content}";
}

/// <summary>
/// Toast/消息提示模型
/// </summary>
public class MessageItemModel : ReactiveObject
{
    private string _message = "";                // 提示消息内容
    private int _level; // 0=普通,1=警告,2=错误,3=成功
    private DateTime _createdAt = DateTime.Now;  // 创建时间

    /// <summary>提示消息内容。</summary>
    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// <summary>消息级别（0=普通，1=警告，2=错误，3=成功）。</summary>
    public int Level
    {
        get => _level;
        set => this.RaiseAndSetIfChanged(ref _level, value);
    }

    /// <summary>消息创建时间。</summary>
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => this.RaiseAndSetIfChanged(ref _createdAt, value);
    }

    /// <summary>级别对应的中文文本。</summary>
    public string LevelText => Level switch
    {
        1 => "警告",
        2 => "错误",
        3 => "成功",
        _ => "信息"
    };

    /// <summary>级别对应的图标名称（Material 图标）。</summary>
    public string LevelIcon => Level switch
    {
        1 => "Alert",
        2 => "AlertCircle",
        3 => "CheckCircle",
        _ => "Information"
    };
}

/// <summary>
/// 连接历史记录
/// </summary>
public class ConnectionHistoryItemModel : ReactiveObject
{
    private string _backendAddress = "";          // 后端地址
    private string _backendPort = "";             // 后端端口
    private string _backendPassword = "";         // 访问密钥（仅在记住密码时持久化）
    private DateTime _timestamp = DateTime.Now;   // 最近一次连接时间
    private int _useCount = 1;                     // 累计使用次数
    private bool _isRememberPassword;             // 是否记住密码

    /// <summary>后端服务地址。</summary>
    public string BackendAddress
    {
        get => _backendAddress;
        set => this.RaiseAndSetIfChanged(ref _backendAddress, value);
    }

    /// <summary>后端服务端口。</summary>
    public string BackendPort
    {
        get => _backendPort;
        set => this.RaiseAndSetIfChanged(ref _backendPort, value);
    }

    /// <summary>访问密钥（仅在记住密码时持久化）。</summary>
    public string BackendPassword
    {
        get => _backendPassword;
        set => this.RaiseAndSetIfChanged(ref _backendPassword, value);
    }

    /// <summary>最近一次连接时间。</summary>
    public DateTime Timestamp
    {
        get => _timestamp;
        set => this.RaiseAndSetIfChanged(ref _timestamp, value);
    }

    /// <summary>累计使用次数。</summary>
    public int UseCount
    {
        get => _useCount;
        set => this.RaiseAndSetIfChanged(ref _useCount, value);
    }

    /// <summary>是否记住密码。</summary>
    public bool IsRememberPassword
    {
        get => _isRememberPassword;
        set => this.RaiseAndSetIfChanged(ref _isRememberPassword, value);
    }

    /// <summary>格式化的后端名称（地址:端口）。</summary>
    public string BackendName => $"{BackendAddress}:{BackendPort}";

    /// <summary>格式化的连接时间文本（yyyy-MM-dd HH:mm）。</summary>
    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm");

    /// <summary>使用次数显示文本。</summary>
    public string UseCountText => $"已使用 {UseCount} 次";
}

// ============================================================
//  服务器性能数据相关模型
// ============================================================

/// <summary>
/// 单个 CPU 使用率的历史数据点。
/// 用于绘制 CPU 使用率趋势图，包含采样时间戳与使用率百分比（0~100）。
/// </summary>
public sealed class CpuUsageDataPoint
{
    /// <summary>采样时间（来自后端服务器的本地时间）。</summary>
    public DateTime Timestamp { get; }

    /// <summary>CPU 使用率百分比（0~100，对应后端 Pack_ServerState.CPUInfo.Usage）。</summary>
    public double UsagePercent { get; }

    /// <summary>
    /// 初始化 <see cref="CpuUsageDataPoint"/> 新实例。
    /// </summary>
    /// <param name="timestamp">采样时间。</param>
    /// <param name="usagePercent">使用率百分比（0~100）。</param>
    public CpuUsageDataPoint(DateTime timestamp, double usagePercent)
    {
        Timestamp = timestamp;
        UsagePercent = usagePercent;
    }
}

/// <summary>
/// 内存使用率的历史数据点。
/// 用于绘制内存占用趋势图，包含采样时间戳与已用内存量（GB）。
/// </summary>
public sealed class MemoryUsageDataPoint
{
    /// <summary>采样时间（来自后端服务器的本地时间）。</summary>
    public DateTime Timestamp { get; }

    /// <summary>已用内存大小（GB 单位，换算自后端返回的字节数）。</summary>
    public double UsedGigaBytes { get; }

    /// <summary>
    /// 初始化 <see cref="MemoryUsageDataPoint"/> 新实例。
    /// </summary>
    /// <param name="timestamp">采样时间。</param>
    /// <param name="usedGigaBytes">已用内存大小（GB）。</param>
    public MemoryUsageDataPoint(DateTime timestamp, double usedGigaBytes)
    {
        Timestamp = timestamp;
        UsedGigaBytes = usedGigaBytes;
    }
}

/// <summary>
/// 单个 CPU 的当前快照信息。
/// 来自后端 Pack_ServerState.CPUs 中的单个 CPUInfo 条目，
/// 包含 CPU 的稳定标识、显示名称与当前瞬时使用率。
/// </summary>
public sealed class CpuCurrentInfo
{
    /// <summary>CPU 稳定 ID（来自后端 Hardware.Info 的 ProcessorId）。</summary>
    public string Id { get; }

    /// <summary>CPU 显示名称（如 "Intel(R) Core(TM) i7-10700"）。</summary>
    public string Name { get; }

    /// <summary>当前瞬时使用率百分比（0~100）。</summary>
    public double UsagePercent { get; }

    /// <summary>
    /// 使用 Proto 反序列化后的 <see cref="CPUInfo"/> 初始化快照。
    /// </summary>
    /// <param name="cpu">后端返回的 CPUInfo 条目，不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="cpu"/> 为 null 时抛出。</exception>
    public CpuCurrentInfo(CPUInfo cpu)
    {
        if (cpu == null) throw new ArgumentNullException(nameof(cpu));
        Id = cpu.ID ?? "";
        Name = cpu.Name ?? "";
        UsagePercent = cpu.Usage;
    }

    /// <summary>
    /// 使用显式字段初始化快照（主要用于单元测试与占位数据）。
    /// </summary>
    /// <param name="id">CPU ID。</param>
    /// <param name="name">CPU 名称。</param>
    /// <param name="usagePercent">使用率百分比。</param>
    public CpuCurrentInfo(string id, string name, double usagePercent)
    {
        Id = id ?? "";
        Name = name ?? "";
        UsagePercent = usagePercent;
    }

    /// <summary>
    /// CPU 的唯一组合键（名称-ID），用于区分多 CPU 系统中不同处理器，
    /// 对应参考前端实现中的 NameAndID 拼接逻辑。
    /// </summary>
    public string NameAndId => $"{Name}-{Id}";
}

/// <summary>
/// 服务器整体状态的当前快照。
/// 聚合了采样时间点、CPU 列表、总内存与已用内存等核心指标，
/// 对应单个 Pack_ServerState 数据包的解析结果。
/// </summary>
public sealed class ServerStateSnapshot
{
    /// <summary>后端采集该次状态的时间戳。</summary>
    public DateTime CheckTime { get; }

    /// <summary>所有 CPU 的当前瞬时使用率快照（顺序与后端返回一致）。</summary>
    public IReadOnlyList<CpuCurrentInfo> Cpus { get; }

    /// <summary>服务器总物理内存大小（GB 单位）。</summary>
    public double TotalGigaBytes { get; }

    /// <summary>服务器当前已用物理内存大小（GB 单位）。</summary>
    public double UsedGigaBytes { get; }

    /// <summary>服务器当前可用物理内存大小（GB 单位，= Total - Used）。</summary>
    public double AvailableGigaBytes => TotalGigaBytes - UsedGigaBytes;

    /// <summary>已用内存占总内存的百分比（0~100）。</summary>
    public double MemoryUsagePercent => TotalGigaBytes > 0
        ? Math.Min(100.0, Math.Max(0.0, UsedGigaBytes / TotalGigaBytes * 100.0))
        : 0.0;

    /// <summary>
    /// 使用 Proto 反序列化后的 Pack_ServerState 初始化快照。
    /// </summary>
    /// <param name="pack">后端返回的 Pack_ServerState 数据包，不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="pack"/> 为 null 时抛出。</exception>
    public ServerStateSnapshot(PMCSsE_Communicator.DataPacks.Pack_ServerState pack)
    {
        if (pack == null) throw new ArgumentNullException(nameof(pack));
        const double bytesPerGigabyte = 1024.0 * 1024.0 * 1024.0;
        CheckTime = pack.CheckTime;
        TotalGigaBytes = pack.TotalMemory / bytesPerGigabyte;
        UsedGigaBytes = pack.UsingMemory / bytesPerGigabyte;

        var cpuList = new List<CpuCurrentInfo>(pack.CPUs?.Length ?? 0);
        if (pack.CPUs != null)
        {
            foreach (var cpu in pack.CPUs)
            {
                if (cpu != null)
                    cpuList.Add(new CpuCurrentInfo(cpu));
            }
        }
        Cpus = cpuList.AsReadOnly();
    }

    /// <summary>
    /// 显式字段构造（主要用于单元测试与占位数据）。
    /// </summary>
    /// <param name="checkTime">采样时间。</param>
    /// <param name="cpus">CPU 快照列表。</param>
    /// <param name="totalGigaBytes">总内存（GB）。</param>
    /// <param name="usedGigaBytes">已用内存（GB）。</param>
    public ServerStateSnapshot(DateTime checkTime, IReadOnlyList<CpuCurrentInfo> cpus,
        double totalGigaBytes, double usedGigaBytes)
    {
        CheckTime = checkTime;
        Cpus = cpus ?? Array.Empty<CpuCurrentInfo>();
        TotalGigaBytes = totalGigaBytes;
        UsedGigaBytes = usedGigaBytes;
    }
}
