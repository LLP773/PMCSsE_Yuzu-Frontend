using System;
using System.Collections.Generic;
using System.Linq;
using PMCSsE_Communicator;
using Yuzu_Frontend.Models;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 全局动态配置缓存中心（独立静态类）。
/// 统一管理后端配置与管理器配置的内存缓存，采用嵌套字典结构实现两级索引：
///   第一级按 BackendId 索引后端配置，
///   第二级在每个后端下按 ManagerId 索引管理器配置。
///
/// <para>调用入口（按项目规范命名）：</para>
/// <list type="bullet">
///   <item><c>GlobalCache.BackendConfigCache(backendId)</c> → 获取指定后端的配置访问器</item>
///   <item><c>GlobalCache.ManagerConfigCache(backendId, managerId)</c> → 获取指定管理器的配置访问器</item>
/// </list>
///
/// <para>设计思路：</para>
/// <list type="number">
///   <item>外部通过「访问器结构体」读写具体字段，避免直接暴露内部字典，统一加锁逻辑。</item>
///   <item>访问器为只读 struct，无堆分配开销，可自由链式调用。</item>
///   <item>所有写入操作均在内部 <c>_lock</c> 互斥锁保护下执行，保证跨线程安全。</item>
///   <item>后端配置目前仅实现本地内存接口，不做网络请求与持久化，后续可按需扩展。</item>
/// </list>
///
/// <para>使用场景：</para>
/// <list type="bullet">
///   <item>连接建立成功后，写入后端连接信息与管理器配置列表。</item>
///   <item>各业务页面按 BackendId + ManagerId 快速查询管理器名称、Java 路径等配置项。</item>
///   <item>收到后端推送的 ModifiedMCServerManagerConfig 等事件时，更新对应缓存条目。</item>
///   <item>后端断连时，一次性移除该后端及其下属管理器的全部缓存。</item>
/// </list>
/// </summary>
public static class GlobalCache
{
    /// <summary>
    /// 内部读写互斥锁。
    /// 由于缓存可能被 UI 线程、网络回调线程、后台任务并发访问，
    /// 所有对 <c>_backends</c> 及其子字典的读写必须在此锁保护下完成。
    /// </summary>
    private static readonly object _lock = new();

    /// <summary>
    /// 后端配置主字典。
    /// Key = BackendId（由 BackendIdManager 分配的稳定标识），
    /// Value = 该后端的完整缓存条目（包含后端配置与下属管理器字典）。
    /// </summary>
    private static readonly Dictionary<string, BackendCacheEntry> _backends = new();

    // ============================================================
    //  入口方法：全局访问的三个公开方法
    // ============================================================

    /// <summary>
    /// 获取指定后端的配置访问器。
    /// 即使后端不存在于缓存中，也会返回一个空访问器（属性返回默认值，不会抛异常），
    /// 调用方可通过 <see cref="BackendConfigCacheAccessor.Exists"/> 判断是否有效。
    /// </summary>
    /// <param name="backendId">后端 ID，由 BackendIdManager 分配。</param>
    /// <returns>后端配置访问器。调用形态：GlobalCache.BackendConfigCache(id)。</returns>
    public static BackendConfigCacheAccessor BackendConfigCache(string backendId)
        => new(backendId);

    /// <summary>
    /// 获取指定后端下、指定管理器的配置访问器。
    /// 即使管理器不存在，也会返回空访问器而不抛异常。
    /// </summary>
    /// <param name="backendId">所属后端 ID。</param>
    /// <param name="managerId">管理器 ID。</param>
    /// <returns>管理器配置访问器。调用形态：GlobalCache.ManagerConfigCache(bid, mid)。</returns>
    public static ManagerConfigCacheAccessor ManagerConfigCache(string backendId, string managerId)
        => new(backendId, managerId);

    /// <summary>
    /// 获取指定后端的服务器状态（性能数据）缓存访问器。
    /// 用于存取 CPU/内存的当前快照与历史数据点。
    /// 即使后端尚不存在于缓存中，也会返回空访问器而不抛异常。
    /// </summary>
    /// <param name="backendId">后端 ID，由 BackendIdManager 分配。</param>
    /// <returns>服务器状态缓存访问器。调用形态：GlobalCache.ServerStateCache(bid)。</returns>
    public static ServerStateCacheAccessor ServerStateCache(string backendId)
        => new(backendId);

    // ============================================================
    //  内部数据结构（不对外暴露）
    // ============================================================

    /// <summary>
    /// 单个后端的缓存条目。
    /// 包含后端自身配置字段 + 下属管理器的嵌套字典。
    /// </summary>
    private sealed class BackendCacheEntry
    {
        /// <summary>后端 ID（冗余存储，便于调试与遍历）。</summary>
        public string BackendId { get; set; } = "";

        /// <summary>后端显示名称（配置项）。</summary>
        public string BackendName { get; set; } = "";

        /// <summary>后端网络地址（IP 或域名）。</summary>
        public string Address { get; set; } = "";

        /// <summary>后端监听端口（字符串形式，便于直接拼接 Key）。</summary>
        public string Port { get; set; } = "";

        /// <summary>该后端允许挂载的最大管理器数量（配置项）。</summary>
        public int MaxManagers { get; set; }

        /// <summary>后端运行日志级别（如 Debug / Info / Warning）。</summary>
        public string LogLevel { get; set; } = "";

        /// <summary>后端默认的 Java 可执行文件路径。
        /// 管理器未单独指定 Java 路径时，将回退使用此默认值。</summary>
        public string DefaultJavaPath { get; set; } = "";

        /// <summary>后端程序的工作目录。</summary>
        public string WorkDirectory { get; set; } = "";

        /// <summary>
        /// 后端下挂载的全部管理器。
        /// Key = ManagerId，Value = 管理器缓存条目。
        /// </summary>
        public Dictionary<string, ManagerCacheEntry> Managers { get; } = new();

        /// <summary>
        /// 该后端的服务器性能数据缓存（CPU/内存快照与历史数据点）。
        /// 后端未返回数据时为 null，访问器会自动处理空值情况。
        /// </summary>
        public ServerStateCacheEntry? ServerState { get; set; }
    }

    /// <summary>
    /// 单个管理器的缓存条目。
    /// 保存基础元数据（ID/名称）与完整的 <see cref="MCServerManagerConfig"/> 对象。
    /// </summary>
    private sealed class ManagerCacheEntry
    {
        /// <summary>管理器 ID。</summary>
        public string ManagerId { get; set; } = "";

        /// <summary>MC 服务端名称（与 Config.MCServerName 同步）。</summary>
        public string MCServerName { get; set; } = "";

        /// <summary>所属后端 ID（冗余存储）。</summary>
        public string BackendId { get; set; } = "";

        /// <summary>
        /// 管理器完整配置对象（来自后端 Proto 反序列化）。
        /// 可能为 null（如仅写入了元数据而未下发完整配置时）。
        /// </summary>
        public MCServerManagerConfig? Config { get; set; }
    }

    // ============================================================
    //  后端配置访问器（按规范命名为 BackendConfigCache + Accessor，避免与工厂方法名冲突）
    //  对外调用形态：GlobalCache.BackendConfigCache(backendId)
    // ============================================================

    /// <summary>
    /// 后端配置访问器（只读结构体）。
    /// 通过 <see cref="GlobalCache.BackendConfigCache(string)"/> 工厂方法获取实例。
    /// 封装对单个后端配置的所有读写操作，保证线程安全与调用简洁性。
    /// <para>按项目命名规范要求：对应原概念"BackendCache"，已重命名为"BackendConfigCache"，
    /// 由于 C# 不允许方法名与嵌套类型同名，实际结构体后缀加 "Accessor"，
    /// 对外调用入口仍保持 <c>GlobalCache.BackendConfigCache(id)</c> 的命名形式。</para>
    /// </summary>
    public readonly struct BackendConfigCacheAccessor
    {
        /// <summary>绑定的后端 ID。</summary>
        private readonly string _backendId;

        /// <summary>
        /// 内部构造函数，仅 <see cref="GlobalCache"/> 可创建访问器。
        /// 外部调用方请使用 <see cref="GlobalCache.BackendConfigCache(string)"/>。
        /// </summary>
        /// <param name="backendId">目标后端 ID。</param>
        internal BackendConfigCacheAccessor(string backendId) => _backendId = backendId;

        /// <summary>
        /// 该后端是否已存在于缓存中。
        /// 若为 <c>false</c>，则 Name/Address 等属性返回默认值。
        /// </summary>
        public bool Exists
        {
            get
            {
                lock (_lock) return _backends.ContainsKey(_backendId);
            }
        }

        /// <summary>后端显示名称；不存在时返回空串。</summary>
        public string Name
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.BackendName : "";
            }
        }

        /// <summary>后端地址；不存在时返回空串。</summary>
        public string Address
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.Address : "";
            }
        }

        /// <summary>后端端口字符串；不存在时返回空串。</summary>
        public string Port
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.Port : "";
            }
        }

        /// <summary>最大允许管理器数；不存在时返回 0。</summary>
        public int MaxManagers
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.MaxManagers : 0;
            }
        }

        /// <summary>日志级别；不存在时返回空串。</summary>
        public string LogLevel
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.LogLevel : "";
            }
        }

        /// <summary>默认 Java 路径；不存在时返回空串。</summary>
        public string DefaultJavaPath
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.DefaultJavaPath : "";
            }
        }

        /// <summary>后端工作目录；不存在时返回空串。</summary>
        public string WorkDirectory
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.WorkDirectory : "";
            }
        }

        /// <summary>该后端当前缓存的管理器数量；不存在时返回 0。</summary>
        public int ManagerCount
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b) ? b.Managers.Count : 0;
            }
        }

        // ---------- 写入方法 ----------

        /// <summary>
        /// 写入或更新后端连接信息（地址、端口）。
        /// 若后端条目尚不存在会自动创建。
        /// </summary>
        /// <param name="address">后端地址（IP 或域名）。</param>
        /// <param name="port">后端端口字符串。</param>
        public void SetConnectionInfo(string address, string port)
        {
            lock (_lock)
            {
                if (!_backends.TryGetValue(_backendId, out var b))
                {
                    b = new BackendCacheEntry { BackendId = _backendId };
                    _backends[_backendId] = b;
                }
                b.Address = address ?? "";
                b.Port = port ?? "";
            }
        }

        /// <summary>
        /// 更新后端配置项（全部字段批量更新）。
        /// 各参数传 null 时对应字段保持原值不变，不会被覆盖为 null。
        /// </summary>
        /// <param name="name">后端显示名称。</param>
        /// <param name="maxManagers">最大允许管理器数。</param>
        /// <param name="logLevel">日志级别。</param>
        /// <param name="defaultJavaPath">默认 Java 路径。</param>
        /// <param name="workDirectory">工作目录。</param>
        /// <remarks>
        /// 此方法为「本地接口」，只更新内存缓存，不发起网络请求、不做持久化。
        /// 若将来引入 BackendConfig Proto，可新增一个重载直接接收 Proto 对象。
        /// </remarks>
        public void UpdateConfig(
            string? name = null,
            int? maxManagers = null,
            string? logLevel = null,
            string? defaultJavaPath = null,
            string? workDirectory = null)
        {
            lock (_lock)
            {
                if (!_backends.TryGetValue(_backendId, out var b))
                {
                    b = new BackendCacheEntry { BackendId = _backendId };
                    _backends[_backendId] = b;
                }
                // 仅当参数非 null 时覆盖，保证调用方可以部分更新而不擦除已有值
                if (name != null) b.BackendName = name;
                if (maxManagers.HasValue) b.MaxManagers = maxManagers.Value;
                if (logLevel != null) b.LogLevel = logLevel;
                if (defaultJavaPath != null) b.DefaultJavaPath = defaultJavaPath;
                if (workDirectory != null) b.WorkDirectory = workDirectory;
            }
        }

        /// <summary>
        /// 仅更新后端名称。其他字段保持不变。
        /// </summary>
        /// <param name="name">新的后端显示名称。</param>
        public void UpdateName(string name)
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b))
                    b.BackendName = name ?? "";
            }
        }

        /// <summary>
        /// 从缓存中移除此后端，同时清空其下所有管理器配置。
        /// 典型调用时机：后端连接断开、或用户手动删除后端条目时。
        /// </summary>
        public void Remove()
        {
            lock (_lock)
            {
                _backends.Remove(_backendId);
            }
        }

        /// <summary>
        /// 获取该后端下全部已缓存管理器的 ID 列表副本。
        /// </summary>
        /// <returns>ManagerId 列表；后端不存在时返回空列表（不会返回 null）。</returns>
        public List<string> GetManagerIds()
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b))
                    return b.Managers.Keys.ToList();
                return new List<string>();
            }
        }
    }

    // ============================================================
    //  管理器配置访问器
    //  对外调用形态：GlobalCache.ManagerConfigCache(backendId, managerId)
    // ============================================================

    /// <summary>
    /// 管理器配置访问器（只读结构体）。
    /// 通过 <see cref="GlobalCache.ManagerConfigCache(string, string)"/> 工厂方法获取实例。
    /// 提供对单个管理器配置的线程安全读写，并封装了「管理器未配置时回退到后端默认值」的便捷逻辑。
    /// </summary>
    public readonly struct ManagerConfigCacheAccessor
    {
        private readonly string _backendId;
        private readonly string _managerId;

        /// <summary>
        /// 内部构造函数。请通过 GlobalCache.ManagerConfigCache 创建。
        /// </summary>
        internal ManagerConfigCacheAccessor(string backendId, string managerId)
        {
            _backendId = backendId;
            _managerId = managerId;
        }

        /// <summary>
        /// 该管理器条目是否存在于缓存中。
        /// 若为 false，则 Name/Config 等属性返回默认值。
        /// </summary>
        public bool Exists
        {
            get
            {
                lock (_lock)
                    return _backends.TryGetValue(_backendId, out var b)
                           && b.Managers.ContainsKey(_managerId);
            }
        }

        /// <summary>管理器名称（MCServerName）；不存在时返回空串。</summary>
        public string Name
        {
            get
            {
                lock (_lock)
                {
                    if (_backends.TryGetValue(_backendId, out var b)
                        && b.Managers.TryGetValue(_managerId, out var m))
                        return m.MCServerName;
                    return "";
                }
            }
        }

        /// <summary>所属后端 ID（与构造参数一致）。</summary>
        public string BackendId => _backendId;

        /// <summary>管理器 ID（与构造参数一致）。</summary>
        public string ManagerId => _managerId;

        /// <summary>
        /// 管理器完整配置对象；不存在或未下发完整配置时返回 null。
        /// 调用方请使用空合并或判空后再访问其字段。
        /// </summary>
        public MCServerManagerConfig? Config
        {
            get
            {
                lock (_lock)
                {
                    if (_backends.TryGetValue(_backendId, out var b)
                        && b.Managers.TryGetValue(_managerId, out var m))
                        return m.Config;
                    return null;
                }
            }
        }

        /// <summary>
        /// 有效的 Java 路径（智能回退）：
        ///   1. 优先取管理器自己配置的 JavaPath；
        ///   2. 若为空/不存在，回退到所属后端的 DefaultJavaPath；
        ///   3. 两者都没有则返回空串。
        /// </summary>
        /// <remarks>
        /// 这是一个典型的业务层封装，避免调用方在各页面重复写「管理器没配置 → 用后端默认」的判断逻辑。
        /// </remarks>
        public string EffectiveJavaPath
        {
            get
            {
                lock (_lock)
                {
                    if (_backends.TryGetValue(_backendId, out var b))
                    {
                        // 先尝试从管理器配置取
                        if (b.Managers.TryGetValue(_managerId, out var m)
                            && m.Config != null
                            && !string.IsNullOrWhiteSpace(m.Config.JavaPath))
                            return m.Config.JavaPath;
                        // 回退到后端默认
                        return b.DefaultJavaPath;
                    }
                    return "";
                }
            }
        }

        // ---------- 写入方法 ----------

        /// <summary>
        /// 新增或更新管理器缓存条目。
        /// 若所属后端条目不存在，会自动创建一个空的后端条目作为容器。
        /// </summary>
        /// <param name="mcServerName">管理器显示名称。</param>
        /// <param name="config">完整配置对象（可选，传 null 表示仅写元数据）。</param>
        public void UpdateConfig(string mcServerName, MCServerManagerConfig? config = null)
        {
            lock (_lock)
            {
                if (!_backends.TryGetValue(_backendId, out var b))
                {
                    b = new BackendCacheEntry { BackendId = _backendId };
                    _backends[_backendId] = b;
                }
                b.Managers[_managerId] = new ManagerCacheEntry
                {
                    ManagerId = _managerId,
                    MCServerName = mcServerName ?? "",
                    BackendId = _backendId,
                    Config = config
                };
            }
        }

        /// <summary>
        /// 从 <see cref="MCServerManagerConfig"/> 对象提取字段并更新缓存。
        /// 名称取自 config.MCServerName，完整对象也一并保存。
        /// </summary>
        /// <param name="config">后端下发的管理器配置对象；不能为 null。</param>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null 时抛出。</exception>
        public void UpdateConfigFromProto(MCServerManagerConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            UpdateConfig(config.MCServerName ?? "", config);
        }

        /// <summary>
        /// 仅更新管理器显示名称，不改动其他字段。
        /// </summary>
        /// <param name="name">新的管理器名称。</param>
        public void UpdateName(string name)
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b)
                    && b.Managers.TryGetValue(_managerId, out var m))
                    m.MCServerName = name ?? "";
            }
        }

        /// <summary>
        /// 仅更新管理器完整配置对象，不改动名称的快捷缓存值（若需同步名称请用 UpdateConfig）。
        /// </summary>
        /// <param name="config">新的配置对象。</param>
        public void UpdateConfigOnly(MCServerManagerConfig? config)
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b)
                    && b.Managers.TryGetValue(_managerId, out var m))
                    m.Config = config;
            }
        }

        /// <summary>
        /// 从缓存中移除此单个管理器（不影响其所属后端条目）。
        /// </summary>
        public void Remove()
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b))
                    b.Managers.Remove(_managerId);
            }
        }
    }

    // ============================================================
    //  全局批量工具方法
    // ============================================================

    /// <summary>当前缓存的后端总数。</summary>
    public static int BackendCount
    {
        get { lock (_lock) return _backends.Count; }
    }

    /// <summary>当前缓存的管理器总数（跨所有后端求和）。</summary>
    public static int TotalManagerCount
    {
        get
        {
            lock (_lock)
                return _backends.Values.Sum(b => b.Managers.Count);
        }
    }

    /// <summary>
    /// 获取全部已缓存后端的 ID 列表副本。
    /// </summary>
    /// <returns>BackendId 列表。</returns>
    public static List<string> GetAllBackendIds()
    {
        lock (_lock) return _backends.Keys.ToList();
    }

    /// <summary>
    /// 清空全部缓存。典型调用时机：应用退出或用户切换账号时。
    /// </summary>
    public static void ClearAll()
    {
        lock (_lock) _backends.Clear();
    }

    // ============================================================
    //  服务器状态（性能数据）缓存内部条目
    // ============================================================

    /// <summary>
    /// 单个后端的服务器性能数据缓存条目。
    /// 保存 CPU/内存的最新快照，以及用于绘制趋势图的历史数据点序列。
    /// 采用「容量固定、超出丢弃最旧」的环形策略管理历史数据点。
    /// </summary>
    private sealed class ServerStateCacheEntry
    {
        /// <summary>历史数据点的最大保留数量（默认 60，对应 1 分钟 × 1 秒/次 采样）。</summary>
        public const int MaxHistoryPoints = 60;

        /// <summary>最新一次的服务器状态快照；从未收到数据时为 null。</summary>
        public ServerStateSnapshot? LatestSnapshot { get; set; }

        /// <summary>
        /// 每个 CPU 的历史使用率数据点序列。
        /// Key = CpuCurrentInfo.NameAndId（CPU 唯一组合键），
        /// Value = 该 CPU 的按时间升序排列的数据点列表（最新追加到末尾）。
        /// </summary>
        public Dictionary<string, List<CpuUsageDataPoint>> CpuHistory { get; } = new();

        /// <summary>
        /// 系统整体内存占用的历史数据点序列（按时间升序，最新追加到末尾）。
        /// </summary>
        public List<MemoryUsageDataPoint> MemoryHistory { get; } = new();
    }

    // ============================================================
    //  服务器状态缓存访问器
    //  对外调用形态：GlobalCache.ServerStateCache(backendId)
    // ============================================================

    /// <summary>
    /// 服务器状态（性能数据）缓存访问器（只读结构体）。
    /// 通过 <see cref="GlobalCache.ServerStateCache(string)"/> 工厂方法获取实例。
    /// 提供对当前快照、CPU 历史曲线、内存历史曲线的线程安全读写，
    /// 并内置「历史数据点超出容量时自动丢弃最旧条目」的维护逻辑。
    /// </summary>
    public readonly struct ServerStateCacheAccessor
    {
        private readonly string _backendId;

        /// <summary>
        /// 内部构造函数，仅 <see cref="GlobalCache"/> 可创建访问器。
        /// 外部调用方请使用 <see cref="GlobalCache.ServerStateCache(string)"/>。
        /// </summary>
        /// <param name="backendId">目标后端 ID。</param>
        internal ServerStateCacheAccessor(string backendId) => _backendId = backendId;

        /// <summary>
        /// 所属后端条目是否存在（不代表其下一定有性能数据，
        /// 需结合 <see cref="HasSnapshot"/> 进一步判断）。
        /// </summary>
        public bool BackendExists
        {
            get
            {
                lock (_lock) return _backends.ContainsKey(_backendId);
            }
        }

        /// <summary>是否已收到至少一次服务器状态快照。</summary>
        public bool HasSnapshot
        {
            get
            {
                lock (_lock)
                {
                    return _backends.TryGetValue(_backendId, out var b)
                           && b.ServerState != null
                           && b.ServerState.LatestSnapshot != null;
                }
            }
        }

        /// <summary>
        /// 获取最新一次的服务器状态快照；未收到数据时返回 null。
        /// 调用方请配合 null 条件或 HasSnapshot 使用。
        /// </summary>
        public ServerStateSnapshot? Latest
        {
            get
            {
                lock (_lock)
                {
                    return _backends.TryGetValue(_backendId, out var b)
                        ? b.ServerState?.LatestSnapshot
                        : null;
                }
            }
        }

        /// <summary>
        /// 获取服务器总内存大小（GB）；无数据时返回 0。
        /// </summary>
        public double TotalGigaBytes
        {
            get
            {
                lock (_lock)
                {
                    if (_backends.TryGetValue(_backendId, out var b)
                        && b.ServerState?.LatestSnapshot != null)
                        return b.ServerState.LatestSnapshot.TotalGigaBytes;
                    return 0.0;
                }
            }
        }

        /// <summary>
        /// 获取当前已用内存大小（GB）；无数据时返回 0。
        /// </summary>
        public double UsedGigaBytes
        {
            get
            {
                lock (_lock)
                {
                    if (_backends.TryGetValue(_backendId, out var b)
                        && b.ServerState?.LatestSnapshot != null)
                        return b.ServerState.LatestSnapshot.UsedGigaBytes;
                    return 0.0;
                }
            }
        }

        /// <summary>
        /// 获取所有 CPU 的唯一组合键列表快照（用于 UI 构建系列图例）。
        /// 无数据时返回空列表（不会返回 null）。
        /// </summary>
        public List<string> GetCpuKeys()
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b) && b.ServerState != null)
                    return b.ServerState.CpuHistory.Keys.ToList();
                return new List<string>();
            }
        }

        /// <summary>
        /// 获取指定 CPU 的历史使用率数据点列表副本（按时间升序）。
        /// 找不到对应 CPU 时返回空列表。
        /// </summary>
        /// <param name="cpuKey">CPU 唯一组合键（NameAndId）。</param>
        /// <returns>该 CPU 的历史数据点副本；列表本身可自由修改，不影响缓存。</returns>
        public List<CpuUsageDataPoint> GetCpuHistory(string cpuKey)
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b)
                    && b.ServerState != null
                    && !string.IsNullOrEmpty(cpuKey)
                    && b.ServerState.CpuHistory.TryGetValue(cpuKey, out var list))
                    return new List<CpuUsageDataPoint>(list);
                return new List<CpuUsageDataPoint>();
            }
        }

        /// <summary>
        /// 获取系统整体内存占用的历史数据点列表副本（按时间升序）。
        /// 无数据时返回空列表。
        /// </summary>
        public List<MemoryUsageDataPoint> GetMemoryHistory()
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b) && b.ServerState != null)
                    return new List<MemoryUsageDataPoint>(b.ServerState.MemoryHistory);
                return new List<MemoryUsageDataPoint>();
            }
        }

        // ---------- 写入方法 ----------

        /// <summary>
        /// 根据后端返回的 Pack_ServerState 数据包更新缓存：
        /// 刷新 LatestSnapshot，并为每个 CPU / 整体内存追加历史数据点。
        /// 历史数据点超出 <see cref="ServerStateCacheEntry.MaxHistoryPoints"/> 时自动丢弃最旧条目。
        /// 若同一 CheckTime 的数据已存在（重复包），则直接跳过，避免重复数据点。
        /// </summary>
        /// <param name="pack">后端返回的 Pack_ServerState 数据包；不能为 null。</param>
        /// <exception cref="ArgumentNullException"><paramref name="pack"/> 为 null 时抛出。</exception>
        public void UpdateFromPack(PMCSsE_Communicator.DataPacks.Pack_ServerState pack)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            var snapshot = new ServerStateSnapshot(pack);
            UpdateFromSnapshot(snapshot);
        }

        /// <summary>
        /// 根据已构造的 <see cref="ServerStateSnapshot"/> 更新缓存。
        /// 通常由 UpdateFromPack 间接调用；此处暴露以便单元测试和手动构造。
        /// </summary>
        /// <param name="snapshot">最新快照；不能为 null。</param>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> 为 null 时抛出。</exception>
        public void UpdateFromSnapshot(ServerStateSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            lock (_lock)
            {
                // 确保后端条目存在 + ServerState 容器已创建
                if (!_backends.TryGetValue(_backendId, out var b))
                {
                    b = new BackendCacheEntry { BackendId = _backendId };
                    _backends[_backendId] = b;
                }
                b.ServerState ??= new ServerStateCacheEntry();
                var state = b.ServerState;

                // 同一 CheckTime 的重复包直接跳过，避免历史曲线出现重复点
                if (state.LatestSnapshot != null
                    && snapshot.CheckTime <= state.LatestSnapshot.CheckTime)
                    return;

                // 1) 更新当前快照
                state.LatestSnapshot = snapshot;

                // 2) 为每个 CPU 追加历史数据点（热插拔 CPU 时自动新增条目）
                foreach (var cpu in snapshot.Cpus)
                {
                    var key = cpu.NameAndId;
                    if (!state.CpuHistory.TryGetValue(key, out var cpuList))
                    {
                        cpuList = new List<CpuUsageDataPoint>(ServerStateCacheEntry.MaxHistoryPoints);
                        state.CpuHistory[key] = cpuList;
                    }
                    cpuList.Add(new CpuUsageDataPoint(snapshot.CheckTime, cpu.UsagePercent));
                    TrimList(cpuList);
                }

                // 3) 追加整体内存历史数据点
                state.MemoryHistory.Add(new MemoryUsageDataPoint(snapshot.CheckTime, snapshot.UsedGigaBytes));
                TrimList(state.MemoryHistory);
            }
        }

        /// <summary>
        /// 清空该后端的全部性能数据（但不删除后端条目本身）。
        /// 典型调用时机：用户手动要求重置曲线、或后端重新采集硬件信息时。
        /// </summary>
        public void ClearHistory()
        {
            lock (_lock)
            {
                if (_backends.TryGetValue(_backendId, out var b) && b.ServerState != null)
                {
                    b.ServerState.LatestSnapshot = null;
                    b.ServerState.CpuHistory.Clear();
                    b.ServerState.MemoryHistory.Clear();
                }
            }
        }

        /// <summary>
        /// 截断列表：如果元素数超过 MaxHistoryPoints，则移除开头（最旧）的条目。
        /// 由于每次仅追加 1 条，单次最多只需 RemoveRange(0, 1)，
        /// 但使用 while 循环可兼容一次性追加多条的扩展场景。
        /// </summary>
        private static void TrimList<T>(List<T> list)
        {
            while (list.Count > ServerStateCacheEntry.MaxHistoryPoints)
            {
                list.RemoveAt(0);
            }
        }
    }
}
