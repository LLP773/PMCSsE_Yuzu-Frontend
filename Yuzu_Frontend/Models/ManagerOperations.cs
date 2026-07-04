using System;
using System.Threading.Tasks;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

/// <summary>
/// MC 服务端管理器的操作类型枚举。
/// 描述可对单个 Manager 执行的生命周期控制动作。
/// </summary>
public enum OperationType
{
    Start,          // 启动 MC 服务端进程
    Stop,           // 正常关闭 MC 服务端进程
    Kill,           // 强制终止 MC 服务端进程
    Load,           // 加载管理器到后端内存
    Unload,         // 从后端内存卸载管理器
    Delete,         // 删除管理器配置
    StatusCheck     // 状态查询
}

/// <summary>
/// 操作执行结果状态枚举。
/// </summary>
public enum OperationResultStatus
{
    Success,    // 操作成功完成
    Failed,     // 操作失败
    Pending,    // 请求已发送，等待后端响应
    Timeout     // 操作超时
}

/// <summary>
/// 单次管理器操作的执行结果，携带操作类型、状态、消息与时间戳。
/// </summary>
public class OperationResult
{
    /// <summary>操作类型。</summary>
    public OperationType OperationType { get; set; }

    /// <summary>操作结果状态。</summary>
    public OperationResultStatus Status { get; set; }

    /// <summary>结果描述消息（可空）。</summary>
    public string? Message { get; set; }

    /// <summary>目标管理器 ID。</summary>
    public string? ManagerId { get; set; }

    /// <summary>结果产生时间。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 创建一个表示成功的 <see cref="OperationResult"/> 实例。
    /// </summary>
    /// <param name="type">操作类型。</param>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <param name="message">自定义消息（为空时使用默认成功提示）。</param>
    public static OperationResult SuccessResult(OperationType type, string managerId, string? message = null)
    {
        return new OperationResult
        {
            OperationType = type,
            Status = OperationResultStatus.Success,
            ManagerId = managerId,
            Message = message ?? $"{type} 操作成功"
        };
    }

    /// <summary>
    /// 创建一个表示失败的 <see cref="OperationResult"/> 实例。
    /// </summary>
    /// <param name="type">操作类型。</param>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <param name="message">失败原因描述。</param>
    public static OperationResult FailedResult(OperationType type, string managerId, string message)
    {
        return new OperationResult
        {
            OperationType = type,
            Status = OperationResultStatus.Failed,
            ManagerId = managerId,
            Message = message
        };
    }
}

/// <summary>
/// 操作执行器接口。封装对 MC 服务端管理器执行各类操作的能力，
/// 提供"前置校验 -> 发送请求 -> 异步等待结果"的统一抽象。
/// </summary>
public interface IOperationExecutor
{
    /// <summary>
    /// 执行指定操作（带回调）。
    /// </summary>
    /// <param name="operationType">操作类型。</param>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <param name="callback">操作结果回调（可空）。</param>
    void ExecuteOperation(OperationType operationType, string managerId, Action<OperationResult>? callback = null);

    /// <summary>
    /// 异步执行指定操作并返回结果。
    /// </summary>
    /// <param name="operationType">操作类型。</param>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <returns>操作结果。</returns>
    Task<OperationResult> ExecuteOperationAsync(OperationType operationType, string managerId);

    /// <summary>
    /// 判断指定操作在当前状态下是否可执行。
    /// </summary>
    /// <returns>可执行返回 true，否则返回 false。</returns>
    bool CanExecute(OperationType operationType, string managerId);
}

/// <summary>
/// 管理器实体抽象接口，描述其加载状态与 MC 服务端运行状态。
/// </summary>
public interface IManagerEntity
{
    /// <summary>管理器唯一标识。</summary>
    string ManagerId { get; }

    /// <summary>是否已加载到后端内存。</summary>
    bool IsLoaded { get; }

    /// <summary>MC 服务端进程是否正在运行。</summary>
    bool IsRunning { get; }
}

/// <summary>
/// 管理器操作的抽象基类（命令模式）。
/// 封装"校验 -> 映射请求类型 -> 发送数据包 -> 回调结果"的通用流程，
/// 子类通过实现 <see cref="CanExecute"/> 与 <see cref="MapOperationToRequestType"/> 提供具体业务规则。
/// </summary>
/// <typeparam name="TEntity">管理器实体类型，需实现 <see cref="IManagerEntity"/>。</typeparam>
public abstract class ManagerOperationBase<TEntity> : IOperationExecutor where TEntity : class, IManagerEntity
{
    /// <summary>共享的连接视图模型，用于发送后端请求。</summary>
    protected readonly ConnectionViewModel Connection;

    /// <summary>所属视图模型，用于弹出 Toast 提示。</summary>
    protected readonly ViewModelBase OwnerViewModel;

    /// <summary>
    /// 初始化 <see cref="ManagerOperationBase{TEntity}"/> 新实例。
    /// </summary>
    /// <param name="connection">共享连接视图模型。</param>
    /// <param name="ownerViewModel">所属视图模型（用于日志/Toast 输出）。</param>
    protected ManagerOperationBase(ConnectionViewModel connection, ViewModelBase ownerViewModel)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        OwnerViewModel = ownerViewModel ?? throw new ArgumentNullException(nameof(ownerViewModel));
    }

    /// <summary>
    /// 执行操作的标准流程：校验可执行性 -> 映射请求类型 -> 发送数据包 -> 回调结果。
    /// 任意阶段失败都会通过回调返回失败结果。
    /// </summary>
    /// <param name="operationType">操作类型。</param>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <param name="callback">操作结果回调（可空）。</param>
    public void ExecuteOperation(OperationType operationType, string managerId, Action<OperationResult>? callback = null)
    {
        try
        {
            // 前置校验：当前状态是否允许执行
            if (!CanExecute(operationType, managerId))
            {
                callback?.Invoke(OperationResult.FailedResult(operationType, managerId, "当前状态不允许执行此操作"));
                return;
            }

            // 映射操作类型到后端请求类型
            var requestType = MapOperationToRequestType(operationType);
            if (requestType == null)
            {
                callback?.Invoke(OperationResult.FailedResult(operationType, managerId, "不支持的操作类型"));
                return;
            }

            SendRequest(requestType.Value, managerId, operationType);
            // 请求已发送，结果为 Pending（最终结果由后续数据包回调决定）
            callback?.Invoke(new OperationResult
            {
                OperationType = operationType,
                Status = OperationResultStatus.Pending,
                ManagerId = managerId,
                Message = $"{operationType} 请求已发送"
            });
        }
        catch (Exception ex)
        {
            LogError(operationType, managerId, ex);
            callback?.Invoke(OperationResult.FailedResult(operationType, managerId, ex.Message));
        }
    }

    /// <summary>
    /// 异步执行操作，内部包装为 TaskCompletionSource 以便 await。
    /// </summary>
    /// <returns>操作结果。</returns>
    public async Task<OperationResult> ExecuteOperationAsync(OperationType operationType, string managerId)
    {
        return await Task.Run(() =>
        {
            var tcs = new TaskCompletionSource<OperationResult>();

            ExecuteOperation(operationType, managerId, result =>
            {
                tcs.SetResult(result);
            });

            return tcs.Task;
        });
    }

    /// <summary>判断指定操作在当前状态下是否可执行（由子类实现具体规则）。</summary>
    public abstract bool CanExecute(OperationType operationType, string managerId);

    /// <summary>将操作类型映射到后端 RequestTypeEnum（不支持时返回 null）。</summary>
    protected abstract RequestTypeEnum? MapOperationToRequestType(OperationType operationType);

    /// <summary>
    /// 根据请求类型构造对应数据包并发送到后端。
    /// 大部分操作携带 managerId 作为唯一载荷；列表/查询类请求为空载荷。
    /// </summary>
    protected virtual void SendRequest(RequestTypeEnum requestType, string managerId, OperationType operationType)
    {
        try
        {
            switch (requestType)
            {
                case RequestTypeEnum.LoadMCServerManager:
                    Connection.RequestBackend(requestType, new Pack_LoadMCServerManager(managerId));
                    break;
                case RequestTypeEnum.StopMCServerManager:
                    Connection.RequestBackend(requestType, new Pack_StopMCServerManager(managerId));
                    break;
                case RequestTypeEnum.DeleteMCServerManager:
                    Connection.RequestBackend(requestType, new Pack_DeleteMCServerManager(managerId));
                    break;
                case RequestTypeEnum.RunMCServer:
                    Connection.RequestBackend(requestType, new Pack_RunMCServer(managerId));
                    break;
                case RequestTypeEnum.ShutdownMCServer:
                    Connection.RequestBackend(requestType, new Pack_ShutdownMCServer(managerId));
                    break;
                case RequestTypeEnum.KillMCServer:
                    Connection.RequestBackend(requestType, new Pack_KillMCServer(managerId));
                    break;
                case RequestTypeEnum.GetMCServerManagersList:
                case RequestTypeEnum.GetLoadedMCServerManagers:
                    Connection.RequestBackend(requestType);
                    break;
                default:
                    throw new NotSupportedException($"不支持的请求类型: {requestType}");
            }

            LogInfo(operationType, managerId, $"请求已发送: {requestType}");
        }
        catch (Exception ex)
        {
            LogError(operationType, managerId, ex);
            throw;
        }
    }

    /// <summary>输出 Info 级别 Toast（吞掉异常避免影响主流程）。</summary>
    protected virtual void LogInfo(OperationType operationType, string managerId, string message)
    {
        try
        {
            OwnerViewModel.ShowToast($"[{operationType}] {managerId}", message, Yuzu_Frontend.ViewModels.ToastsViewModel.ToastType.Info);
        }
        catch { }
    }

    /// <summary>输出 Error 级别 Toast（吞掉异常避免影响主流程）。</summary>
    protected virtual void LogError(OperationType operationType, string managerId, Exception ex)
    {
        try
        {
            OwnerViewModel.ShowToast($"[{operationType}] 失败", $"{managerId}: {ex.Message}", Yuzu_Frontend.ViewModels.ToastsViewModel.ToastType.Error);
        }
        catch { }
    }

    /// <summary>输出 Success 级别 Toast（吞掉异常避免影响主流程）。</summary>
    protected virtual void LogSuccess(OperationType operationType, string managerId, string? message = null)
    {
        try
        {
            OwnerViewModel.ShowToast($"[{operationType}] 成功", message ?? $"{managerId} 操作成功", Yuzu_Frontend.ViewModels.ToastsViewModel.ToastType.Success);
        }
        catch { }
    }
}

/// <summary>
/// 针对 MC 服务端管理器（<see cref="MCServerManagerItemModel"/>）的具体操作执行器。
/// 实现 <see cref="CanExecute"/> 的状态机规则与操作类型到请求类型的映射。
/// </summary>
public class MCServerManagerOperations : ManagerOperationBase<MCServerManagerItemModel>
{
    private readonly Func<string, MCServerManagerItemModel?> _getManagerById;    // 根据 ID 查询管理器实例的委托

    /// <summary>
    /// 初始化 <see cref="MCServerManagerOperations"/> 新实例。
    /// </summary>
    /// <param name="connection">共享连接视图模型。</param>
    /// <param name="ownerViewModel">所属视图模型。</param>
    /// <param name="getManagerById">根据 ID 查询管理器实例的委托。</param>
    public MCServerManagerOperations(ConnectionViewModel connection, ViewModelBase ownerViewModel,
        Func<string, MCServerManagerItemModel?> getManagerById)
        : base(connection, ownerViewModel)
    {
        _getManagerById = getManagerById ?? throw new ArgumentNullException(nameof(getManagerById));
    }

    /// <summary>
    /// 判断指定操作在当前状态下是否可执行。
    /// 前置条件：已连接且管理器存在；具体规则依据管理器的加载/运行状态。
    /// </summary>
    public override bool CanExecute(OperationType operationType, string managerId)
    {
        if (!Connection.IsConnected || Connection.Client == null)
            return false;

        var manager = _getManagerById(managerId);
        if (manager == null)
            return false;

        // 状态机：各操作的前置条件
        return operationType switch
        {
            OperationType.Load => !manager.IsLoaded,                           // 加载需未加载
            OperationType.Unload => manager.IsLoaded && !manager.IsMCServerRunning, // 卸载需已加载且 MC 未运行
            OperationType.Start => manager.IsLoaded && !manager.IsMCServerRunning, // 启动需已加载且未运行
            OperationType.Stop => manager.IsMCServerRunning,                  // 停止需 MC 正在运行
            OperationType.Kill => manager.IsMCServerRunning,                   // 强杀需 MC 正在运行
            OperationType.Delete => !manager.IsLoaded,                        // 删除需未加载
            OperationType.StatusCheck => true,                                 // 状态查询始终允许
            _ => false
        };
    }

    /// <summary>
    /// 将操作类型映射到后端 RequestTypeEnum。
    /// </summary>
    /// <returns>对应的请求类型；不支持的操作返回 null。</returns>
    protected override RequestTypeEnum? MapOperationToRequestType(OperationType operationType)
    {
        return operationType switch
        {
            OperationType.Load => RequestTypeEnum.LoadMCServerManager,
            OperationType.Unload => RequestTypeEnum.StopMCServerManager,
            OperationType.Start => RequestTypeEnum.RunMCServer,
            OperationType.Stop => RequestTypeEnum.ShutdownMCServer,
            OperationType.Kill => RequestTypeEnum.KillMCServer,
            OperationType.Delete => RequestTypeEnum.DeleteMCServerManager,
            OperationType.StatusCheck => RequestTypeEnum.GetMCServerManagersList,
            _ => null
        };
    }
}

/// <summary>
/// <see cref="IOperationExecutor"/> 的扩展方法集合，
/// 为常见操作提供语义化的便捷调用入口。
/// </summary>
public static class ManagerOperationsExtensions
{
    /// <summary>加载管理器。</summary>
    public static void LoadManager(this IOperationExecutor executor, string managerId, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.Load, managerId, callback);
    }

    /// <summary>卸载管理器。</summary>
    public static void UnloadManager(this IOperationExecutor executor, string managerId, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.Unload, managerId, callback);
    }

    /// <summary>启动 MC 服务端。</summary>
    public static void StartMCServer(this IOperationExecutor executor, string managerId, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.Start, managerId, callback);
    }

    /// <summary>正常停止 MC 服务端。</summary>
    public static void StopMCServer(this IOperationExecutor executor, string managerId, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.Stop, managerId, callback);
    }

    /// <summary>强制终止 MC 服务端进程。</summary>
    public static void KillMCServer(this IOperationExecutor executor, string managerId, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.Kill, managerId, callback);
    }

    /// <summary>删除管理器配置。</summary>
    public static void DeleteManager(this IOperationExecutor executor, string managerId, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.Delete, managerId, callback);
    }

    /// <summary>刷新管理器列表状态。</summary>
    public static void RefreshStatus(this IOperationExecutor executor, Action<OperationResult>? callback = null)
    {
        executor.ExecuteOperation(OperationType.StatusCheck, string.Empty, callback);
    }

    /// <summary>异步加载管理器。</summary>
    public static Task<OperationResult> LoadManagerAsync(this IOperationExecutor executor, string managerId)
    {
        return executor.ExecuteOperationAsync(OperationType.Load, managerId);
    }

    /// <summary>异步启动 MC 服务端。</summary>
    public static Task<OperationResult> StartMCServerAsync(this IOperationExecutor executor, string managerId)
    {
        return executor.ExecuteOperationAsync(OperationType.Start, managerId);
    }

    /// <summary>异步停止 MC 服务端。</summary>
    public static Task<OperationResult> StopMCServerAsync(this IOperationExecutor executor, string managerId)
    {
        return executor.ExecuteOperationAsync(OperationType.Stop, managerId);
    }
}