using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PMCSsE_Communicator;
using ReactiveUI;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

/// <summary>
/// 后端连接池。统一管理多个 <see cref="BackendConnection"/> 实例的生命周期与事件聚合，
/// 维护一个"主连接"作为请求默认路由目标，并向上层提供连接增删、请求路由与广播能力。
/// 通过聚合所有子连接的事件，向上层屏蔽多后端差异。
/// </summary>
public sealed class BackendConnectionPool : ReactiveObject, IDisposable
{
    private readonly ConcurrentDictionary<string, BackendConnection> _connections = new(); // backendId → 连接
    private readonly object _primaryLock = new();   // 保护主连接标识切换的同步锁
    private string? _primaryBackendId;               // 当前主连接的 BackendId（请求默认路由目标）

    /// <summary>新连接加入连接池时触发。</summary>
    public event Action<BackendConnection>? ConnectionAdded;

    /// <summary>连接从池中移除时触发，参数为被移除连接的 BackendId。</summary>
    public event Action<string>? ConnectionRemoved;

    /// <summary>收到任意连接的数据包时触发：(后端 ID, 响应类型, 数据)。</summary>
    public event Action<string, RespondTypeEnum, object?>? DataPackReceived;

    /// <summary>任一连接成功建立时触发。</summary>
    public event Action<BackendConnection>? ConnectionConnected;

    /// <summary>任一连接断开时触发，携带断开原因。</summary>
    public event Action<BackendConnection, NativeClient.DisconnectedReasonEnum>? ConnectionDisconnected;

    /// <summary>需要输出日志时触发：(后端 ID, 消息, 类型)。</summary>
    public event Action<string, string, ToastsViewModel.ToastType>? LogMessage;

    /// <summary>当前连接池中的连接总数。</summary>
    public int Count => _connections.Count;

    /// <summary>是否存在至少一个已成功建立的连接。</summary>
    public bool HasConnected => _connections.Values.Any(c => c.IsConnected);

    /// <summary>连接池中所有连接的只读快照列表。</summary>
    public IReadOnlyList<BackendConnection> Connections => _connections.Values.ToList().AsReadOnly();

    /// <summary>
    /// 获取或设置主连接的 BackendId。主连接作为不带显式后端 ID 的请求的默认路由目标。
    /// </summary>
    public string? PrimaryBackendId
    {
        get
        {
            lock (_primaryLock)
                return _primaryBackendId;
        }
        set
        {
            lock (_primaryLock)
                _primaryBackendId = value;
        }
    }

    /// <summary>默认构造函数。</summary>
    public BackendConnectionPool()
    {
    }

    /// <summary>
    /// 创建（或复用）一个到指定后端的连接。
    /// 若同地址+端口连接已存在则直接返回已有实例，避免重复创建；
    /// 否则向 <see cref="BackendIdManager"/> 申请 ID、构造连接、订阅事件并入池。
    /// 新池首次创建连接时会自动设为主连接。
    /// </summary>
    /// <param name="address">后端服务地址。</param>
    /// <param name="port">后端服务端口。</param>
    /// <param name="password">访问密钥（可选）。</param>
    /// <returns>新创建或复用的 <see cref="BackendConnection"/> 实例。</returns>
    public BackendConnection CreateConnection(string address, string port, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(port))
            throw new ArgumentException("地址和端口不能为空");

        // 已存在同地址+端口的连接则直接复用
        var existing = _connections.Values.FirstOrDefault(c =>
            string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Port, port, StringComparison.Ordinal));

        if (existing != null)
            return existing;

        var backendId = BackendIdManager.GetOrAssignId(address, port);
        var connection = new BackendConnection(backendId, address, port, password);

        // 订阅连接事件以便向上层聚合
        connection.Connected += OnConnectionConnected;
        connection.Disconnected += OnConnectionDisconnected;
        connection.DataPackReceived += OnDataPackReceived;
        connection.LogMessage += OnLogMessage;
        connection.NeedPassword += OnNeedPassword;
        connection.NeedRSAPublicKeyVerification += OnNeedRSAVerify;

        if (!_connections.TryAdd(backendId, connection))
        {
            connection.Dispose();
            throw new InvalidOperationException($"BackendId {backendId} 已存在");
        }

        // 首个连接自动成为主连接
        lock (_primaryLock)
        {
            if (_primaryBackendId == null)
                _primaryBackendId = backendId;
        }

        ConnectionAdded?.Invoke(connection);

        return connection;
    }

    /// <summary>根据后端 ID 查询连接，找不到返回 null。</summary>
    public BackendConnection? GetConnection(string backendId)
    {
        if (string.IsNullOrEmpty(backendId)) return null;
        return _connections.TryGetValue(backendId, out var conn) ? conn : null;
    }

    /// <summary>根据地址和端口查询连接，找不到返回 null。</summary>
    public BackendConnection? GetConnection(string address, string port)
    {
        return _connections.Values.FirstOrDefault(c =>
            string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Port, port, StringComparison.Ordinal));
    }

    /// <summary>
    /// 获取主连接。若主连接 ID 仍有效则返回对应连接；否则回退到池中任意一个连接。
    /// </summary>
    public BackendConnection? GetPrimaryConnection()
    {
        lock (_primaryLock)
        {
            if (_primaryBackendId != null && _connections.TryGetValue(_primaryBackendId, out var conn))
                return conn;
        }

        return _connections.Values.FirstOrDefault();
    }

    /// <summary>
    /// 从池中移除并释放指定连接。取消订阅其事件、释放资源；
    /// 若移除的是主连接，则将主连接切换到池中剩余的第一个连接。
    /// </summary>
    /// <returns>移除成功返回 true；找不到指定 ID 返回 false。</returns>
    public bool RemoveConnection(string backendId)
    {
        if (string.IsNullOrEmpty(backendId)) return false;

        if (!_connections.TryRemove(backendId, out var connection))
            return false;

        // 取消订阅，避免事件悬挂
        connection.Connected -= OnConnectionConnected;
        connection.Disconnected -= OnConnectionDisconnected;
        connection.DataPackReceived -= OnDataPackReceived;
        connection.LogMessage -= OnLogMessage;
        connection.NeedPassword -= OnNeedPassword;
        connection.NeedRSAPublicKeyVerification -= OnNeedRSAVerify;

        connection.Dispose();

        // 主连接被移除时，自动迁移主连接到池中剩余的第一个
        lock (_primaryLock)
        {
            if (_primaryBackendId == backendId)
            {
                _primaryBackendId = _connections.Keys.FirstOrDefault();
            }
        }

        ConnectionRemoved?.Invoke(backendId);
        return true;
    }

    /// <summary>清空连接池：逐个移除所有连接并重置主连接标识。</summary>
    public void Clear()
    {
        var ids = _connections.Keys.ToList();
        foreach (var id in ids)
        {
            RemoveConnection(id);
        }

        lock (_primaryLock)
        {
            _primaryBackendId = null;
        }
    }

    /// <summary>
    /// 向指定后端发送请求（无内容）。未指定 backendId 时路由到主连接。
    /// </summary>
    /// <param name="type">请求类型枚举。</param>
    /// <param name="backendId">目标后端 ID（为空则使用主连接）。</param>
    public void RequestBackend(RequestTypeEnum type, string? backendId = null)
    {
        var conn = ResolveConnection(backendId);
        if (conn == null)
        {
            LogMessage?.Invoke(backendId ?? "?", $"请求路由失败：找不到目标连接 (backendId={backendId})", ToastsViewModel.ToastType.Error);
            return;
        }
        conn.RequestBackend(type);
    }

    /// <summary>
    /// 向指定后端发送请求（携带强类型载荷）。未指定 backendId 时路由到主连接。
    /// </summary>
    /// <typeparam name="T">载荷类型，需实现 <see cref="LightProto.IProtoParser{T}"/>。</typeparam>
    /// <param name="type">请求类型枚举。</param>
    /// <param name="payload">请求载荷对象。</param>
    /// <param name="backendId">目标后端 ID（为空则使用主连接）。</param>
    public void RequestBackend<T>(RequestTypeEnum type, T payload, string? backendId = null)
        where T : class, LightProto.IProtoParser<T>
    {
        var conn = ResolveConnection(backendId);
        if (conn == null)
        {
            LogMessage?.Invoke(backendId ?? "?", $"请求路由失败：找不到目标连接 (backendId={backendId})", ToastsViewModel.ToastType.Error);
            return;
        }
        conn.RequestBackend(type, payload);
    }

    /// <summary>向所有已连接的后端广播请求（无内容载荷）。</summary>
    public void BroadcastRequest(RequestTypeEnum type)
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.IsConnected)
                conn.RequestBackend(type);
        }
    }

    /// <summary>向所有已连接的后端广播请求（携带强类型载荷）。</summary>
    public void BroadcastRequest<T>(RequestTypeEnum type, T payload)
        where T : class, LightProto.IProtoParser<T>
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.IsConnected)
                conn.RequestBackend(type, payload);
        }
    }

    /// <summary>
    /// 解析目标连接：指定 ID 时按 ID 查找，否则使用主连接。
    /// </summary>
    private BackendConnection? ResolveConnection(string? backendId)
    {
        if (!string.IsNullOrEmpty(backendId))
        {
            return GetConnection(backendId);
        }
        return GetPrimaryConnection();
    }

    /// <summary>子连接 Connected 事件转发：通知上层并刷新聚合属性。</summary>
    private void OnConnectionConnected(BackendConnection conn)
    {
        ConnectionConnected?.Invoke(conn);
        this.RaisePropertyChanged(nameof(HasConnected));
        this.RaisePropertyChanged(nameof(Count));
    }

    /// <summary>子连接 Disconnected 事件转发：通知上层并刷新聚合属性。</summary>
    private void OnConnectionDisconnected(BackendConnection conn, NativeClient.DisconnectedReasonEnum reason)
    {
        ConnectionDisconnected?.Invoke(conn, reason);
        this.RaisePropertyChanged(nameof(HasConnected));
    }

    /// <summary>子连接数据包事件转发：携带后端 ID 向上层聚合。</summary>
    private void OnDataPackReceived(string backendId, RespondTypeEnum responseType, object? data)
    {
        DataPackReceived?.Invoke(backendId, responseType, data);
    }

    /// <summary>子连接日志事件转发：补充后端 ID 后向上层聚合。</summary>
    private void OnLogMessage(BackendConnection conn, string message, ToastsViewModel.ToastType type)
    {
        LogMessage?.Invoke(conn.BackendId, message, type);
    }

    /// <summary>子连接请求密码事件占位（由上层 UI 直接订阅具体连接处理）。</summary>
    private void OnNeedPassword(BackendConnection conn)
    {
    }

    /// <summary>子连接请求 RSA 指纹确认事件占位（由上层 UI 直接订阅具体连接处理）。</summary>
    private void OnNeedRSAVerify(BackendConnection conn, string fingerprint)
    {
    }

    /// <summary>释放连接池：清空所有连接。</summary>
    public void Dispose()
    {
        Clear();
    }
}