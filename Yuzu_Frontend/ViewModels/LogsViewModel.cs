using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Document;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using ReactiveUI;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.Modules;

namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// 负责单个 MC 服务器管理器的日志展示、增量拉取与命令发送。
/// </summary>
public class LogsViewModel : ViewModelBase
{
    private readonly ConnectionViewModel _connection;

    private TextDocument? _logDocument = new TextDocument();
    private string? _activeManagerId;
    private string? _activeManagerName;
    private bool _isAutoScroll = true;
    private double _logPollingIntervalMs = 2000;
    private int _maxLineCount = 5000;
    private string _commandInput = "";

    /// <summary>日志文档（AvaloniaEdit 的绑定目标）。</summary>
    public TextDocument? LogDocument
    {
        get => _logDocument;
        private set => this.RaiseAndSetIfChanged(ref _logDocument, value);
    }

    /// <summary>当前显示的管理器 ID。</summary>
    public string? ActiveManagerId
    {
        get => _activeManagerId;
        private set => this.RaiseAndSetIfChanged(ref _activeManagerId, value);
    }

    /// <summary>当前显示的管理器名称。</summary>
    public string? ActiveManagerName
    {
        get => _activeManagerName;
        private set => this.RaiseAndSetIfChanged(ref _activeManagerName, value);
    }

    /// <summary>是否在追加新日志后滚动到末尾。</summary>
    public bool IsAutoScroll
    {
        get => _isAutoScroll;
        set => this.RaiseAndSetIfChanged(ref _isAutoScroll, value);
    }

    /// <summary>增量日志轮询间隔（毫秒）。</summary>
    public double LogPollingIntervalMs
    {
        get => _logPollingIntervalMs;
        set => this.RaiseAndSetIfChanged(ref _logPollingIntervalMs, value);
    }

    /// <summary>文档最大行数，超过后从开头截断。</summary>
    public int MaxLineCount
    {
        get => _maxLineCount;
        set => this.RaiseAndSetIfChanged(ref _maxLineCount, value);
    }

    /// <summary>正在编辑的命令输入。</summary>
    public string CommandInput
    {
        get => _commandInput;
        set => this.RaiseAndSetIfChanged(ref _commandInput, value);
    }

    /// <summary>最近的日志 ID（用于增量请求）。</summary>
    private ulong _latestLogId;

    // 增量日志轮询定时器订阅句柄
    private IDisposable? _pollingTimer;

    /// <summary>命令历史（保留最近 50 条）。</summary>
    public ObservableCollection<string> CommandHistory { get; } = new();

    /// <summary>常用命令提示。</summary>
    public ObservableCollection<string> CommandsHint { get; } = new(new[]
    {
        "stop", "list", "save-on", "save-off", "save-all",
        "help", "version", "reload", "kick", "ban", "pardon", "op",
        "deop", "whitelist on", "whitelist off", "whitelist add",
        "whitelist remove", "say"
    });

    /// <summary>当需要请求 View 滚动到文档末尾时触发。</summary>
    public event Action? RequestScrollToEnd;

    // ========== 命令 ==========

    /// <summary>发送命令到 MC 服务端。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> SendCommandCommand { get; }
    /// <summary>清空当前日志显示。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ClearLogsCommand { get; }
    /// <summary>切换自动滚动开关。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ToggleAutoScrollCommand { get; }
    /// <summary>导出当前日志到本地文件。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ExportLogsCommand { get; }

    /// <summary>
    /// 构造函数。绑定连接对象，创建响应式命令并订阅数据报与连接断开事件。
    /// </summary>
    /// <param name="connection">当前与后端的连接视图模型。</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "WhenAnyValue 表达式树仅访问本 ViewModel 的公共属性，这些属性经 XAML 编译绑定被静态根保留，AOT 裁剪下安全")]
    public LogsViewModel(ConnectionViewModel connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

        // 仅当命令非空且已选中管理器时允许发送
        var canSend = this.WhenAnyValue(
            x => x.CommandInput,
            x => x.ActiveManagerId,
            (cmd, id) => !string.IsNullOrWhiteSpace(cmd) && !string.IsNullOrEmpty(id)
        );

        SendCommandCommand = ReactiveCommand.Create(ExecuteSendCommand, canSend);
        ClearLogsCommand = ReactiveCommand.Create(ExecuteClearLogs);
        ToggleAutoScrollCommand = ReactiveCommand.Create(() => { IsAutoScroll = !IsAutoScroll; });
        ExportLogsCommand = ReactiveCommand.Create(ExecuteExportLogs);

        _connection.DataPackReceived += OnDataPackReceived;
        _connection.Disconnected += OnDisconnected;

        // 当轮询间隔改变时重新启动
        this.WhenAnyValue(x => x.LogPollingIntervalMs)
            .Skip(1)
            .Subscribe(_ => RestartPollingIfActive());
    }


    // ========== 数据报处理 ==========

    /// <summary>
    /// 后端数据报分发。根据响应类型路由到对应的处理方法。
    /// </summary>
    /// <param name="type">响应类型。</param>
    /// <param name="data">数据负载。</param>
    private void OnDataPackReceived(RespondTypeEnum type, object? data)
    {
        if (type == RespondTypeEnum.MCServerLogs && data is Pack_MCServerLogs logs)
            HandleServerLogs(logs);
        else if (type == RespondTypeEnum.SendCommandSucceed)
            HandleCommandSent();
        else if (type == RespondTypeEnum.SendCommandFailed)
            HandleCommandFailed(data);
        else if (type == RespondTypeEnum.ErrorInfo)
            HandleErrorInfo(data);
    }

    /// <summary>
    /// 处理后端推送的 MC 服务端日志。过滤掉非当前管理器与已展示过的旧日志，
    /// 仅追加比已记录 _latestLogId 更新的条目。
    /// </summary>
    /// <param name="logs">日志数据包。</param>
    private void HandleServerLogs(Pack_MCServerLogs logs)
    {
        if (logs == null || logs.Logs == null || logs.Logs.Length == 0) return;
        // 仅处理当前激活管理器的日志
        if (!string.Equals(logs.ManagerID, ActiveManagerId, StringComparison.Ordinal)) return;

        // 日志批量追加：后台优先级（日志展示不抢占输入）
        Dispatcher.UIThread.Post(() =>
        {
            bool anyNewer = false;
            foreach (var entry in logs.Logs)
            {
                if (entry == null) continue;
                // 跳过已展示过的旧日志（按 ID 增量过滤）
                if (entry.ID <= _latestLogId) continue;

                _latestLogId = entry.ID;
                anyNewer = true;

                AppendLine($"[{DateTime.Now:HH:mm:ss}] {entry.Log}");
            }

            // 有新日志且开启自动滚动时通知 View 滚动到底部
            if (anyNewer && IsAutoScroll)
                RequestScrollToEnd?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 命令发送成功处理：追加提示并立即拉取一次最新日志。
    /// </summary>
    private void HandleCommandSent()
    {
        AppendLine($"[{DateTime.Now:HH:mm:ss}] > 命令已发送");
        if (IsAutoScroll) RequestScrollToEnd?.Invoke();
        try
        {
            if (!string.IsNullOrEmpty(ActiveManagerId))
                _connection.RequestBackend(
                    RequestTypeEnum.GetNewerLogs,
                    new Pack_GetNewerMCServerLogs(ActiveManagerId!, _latestLogId, 100));
        }
        catch { }
    }

    /// <summary>
    /// 命令发送失败处理：从强类型数据包提取失败原因并提示错误。
    /// <br/><b>AOT 改造：</b>不再使用 <c>GetType().GetProperty("Message")</c> 反射读取属性，
    /// 改用 C# 模式匹配直接转换为 PMCSsE 定义的强类型数据包，按属性名读取。
    /// </summary>
    /// <param name="data">失败信息载体（Pack_SendCommandFailed 或兼容的失败包）。</param>
    private void HandleCommandFailed(object? data)
    {
        try
        {
            string msg = ExtractErrorMessage(data) ?? "命令发送失败";
            AppendLine($"[{DateTime.Now:HH:mm:ss}] [错误] {msg}");
            if (IsAutoScroll) RequestScrollToEnd?.Invoke();
            ShowToast("命令发送失败", msg, ToastType.Error);
        }
        catch { }
    }

    /// <summary>
    /// 后端错误信息处理：从强类型数据包提取错误描述并提示错误。
    /// <br/><b>AOT 改造：</b>不再使用 <c>GetType().GetProperty("Message")</c> 反射读取属性，
    /// 改用 C# 模式匹配直接转换为 PMCSsE 定义的强类型数据包。
    /// </summary>
    /// <param name="data">错误信息载体（Pack_ErrorInfo 或兼容的错误包）。</param>
    private void HandleErrorInfo(object? data)
    {
        try
        {
            string msg = ExtractErrorMessage(data) ?? "未知错误";
            AppendLine($"[{DateTime.Now:HH:mm:ss}] [错误] {msg}");
            if (IsAutoScroll) RequestScrollToEnd?.Invoke();
            ShowToast("错误", msg, ToastType.Error);
        }
        catch { }
    }

    /// <summary>
    /// 从各类错误/失败数据包提取人类可读的错误消息。
    /// <b>AOT 安全：</b>仅使用 C# 静态类型的模式匹配（is 运算符 + 属性访问），
    /// 不引入任何反射调用，Native AOT 编译期可完整分析。
    /// <para>属性访问策略：仅对编译期确认存在的属性做强类型访问；
    /// 对于 PMCSsE 不同小版本间可能存在差异的失败包，采用"已知类型 + 类型名"的稳定兜底。</para>
    /// </summary>
    /// <param name="data">任意数据包对象（可能为 null）。</param>
    /// <returns>提取到的错误消息；无法识别时返回 null，由调用方提供默认值。</returns>
    private static string? ExtractErrorMessage(object? data)
    {
        if (data == null) return null;

        // === 按 PMCSsE_Communicator 已知强类型逐一匹配 ===
        // 通用错误信息：后端主动推送的错误（ErrorInfo 属性已在 MCServerManagerAddViewModel/LogConsoleViewModel 中确认）
        if (data is Pack_ErrorInfo errorInfo)
            return errorInfo.ErrorInfo;

        // 命令发送失败：专用数据包（无可靠强类型属性时使用通用描述）
        if (data is Pack_SendCommandFailed)
            return "命令执行失败（请检查服务端日志）";

        // === 管理器生命周期失败类数据包 ===
        // 类型名 → 人类可读描述的静态映射（避免访问不确定的属性）
        // 注：部分失败包（如 Run/Shutdown/Kill）在 Desktop 项目中可访问 .FailedReason，
        // 但此处共享项目需兼容所有引用场景，故用稳定的语义化描述。
        if (data is Pack_CreatNewMCServerManagerFailed)
            return "创建管理器失败";
        if (data is Pack_LoadMCServerManagerFailed)
            return "加载管理器失败";
        if (data is Pack_StopMCServerManagerFailed)
            return "卸载管理器失败";
        if (data is Pack_DeleteMCServerManagerFailed)
            return "删除管理器失败";
        if (data is Pack_RunMCServerFailed)
            return "启动 MC 服务端失败";
        if (data is Pack_ShutdownMCServerFailed)
            return "停止 MC 服务端失败";
        if (data is Pack_KillMCServerFailed)
            return "强制终止 MC 服务端失败";

        // 其它类型：返回 null 让调用方使用默认提示
        return null;
    }

    /// <summary>
    /// 连接断开处理：停止轮询并追加断开提示。
    /// </summary>
    private void OnDisconnected()
    {
        _pollingTimer?.Dispose();
        _pollingTimer = null;
        AppendLine($"[{DateTime.Now:HH:mm:ss}] === 连接已断开 ===");
    }

    // ========== 命令实现 ==========

    /// <summary>
    /// 执行发送命令：将命令发往后端，追加到日志，并记录到命令历史（最多 50 条）。
    /// </summary>
    private void ExecuteSendCommand()
    {
        var cmd = CommandInput?.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return;
        if (string.IsNullOrEmpty(ActiveManagerId)) return;

        try
        {
            _connection.RequestBackend(
                RequestTypeEnum.SendCommand,
                new Pack_SendCommand(ActiveManagerId!, cmd));

            AppendLine($"[{DateTime.Now:HH:mm:ss}] > {cmd}");
            if (IsAutoScroll) RequestScrollToEnd?.Invoke();

            // 命令历史维护：后台优先级（下拉提示不抢输入）
            Dispatcher.UIThread.Post(() =>
            {
                if (CommandHistory.Count == 0 || CommandHistory[0] != cmd)
                    CommandHistory.Insert(0, cmd);
                while (CommandHistory.Count > 50)
                    CommandHistory.RemoveAt(CommandHistory.Count - 1);
            }, DispatcherPriority.Background);

            CommandInput = "";
        }
        catch
        {
            AppendLine($"[{DateTime.Now:HH:mm:ss}] [错误] 发送命令失败");
        }
    }

    /// <summary>
    /// 执行清空日志：清空文档并重置增量日志 ID。
    /// </summary>
    private void ExecuteClearLogs()
    {
        ClearDocument();
    }

    /// <summary>
    /// 执行导出日志：将当前文档内容写入用户 Downloads 目录下的 txt 文件。
    /// </summary>
    private async void ExecuteExportLogs()
    {
        try
        {
            var text = LogDocument?.Text ?? "";
            var fileName = $"mc-logs-{ActiveManagerName ?? "unknown"}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, fileName);
            await File.WriteAllTextAsync(path, text, Encoding.UTF8);
            ShowToast("已导出日志", path, ToastType.Success);
        }
        catch (Exception ex)
        {
            ShowToast("导出日志失败", ex.Message, ToastType.Error);
        }
    }

    // ========== 轮询 ==========

    /// <summary>
    /// 启动增量日志轮询。按 LogPollingIntervalMs 间隔向后端请求比 _latestLogId 更新的日志。
    /// 间隔非正数时不启动。
    /// </summary>
    private void StartPolling()
    {
        _pollingTimer?.Dispose();
        if (LogPollingIntervalMs <= 0) return;

        _pollingTimer = Observable.Interval(TimeSpan.FromMilliseconds(LogPollingIntervalMs))
            .Subscribe(_ =>
            {
                if (string.IsNullOrEmpty(ActiveManagerId)) return;
                if (!_connection.IsConnected) return;
                try
                {
                    _connection.RequestBackend(
                        RequestTypeEnum.GetNewerLogs,
                        new Pack_GetNewerMCServerLogs(ActiveManagerId!, _latestLogId, 200));
                }
                catch { }
            });
    }

    /// <summary>
    /// 当前已激活管理器时重启轮询（用于配置变更后生效）。
    /// </summary>
    private void RestartPollingIfActive()
    {
        if (!string.IsNullOrEmpty(ActiveManagerId))
            StartPolling();
    }

    // ========== 文档操作 ==========

    /// <summary>
    /// 在 UI 线程向日志文档追加一行。自动补换行符，并在行数超过 MaxLineCount 时从开头截断。
    /// </summary>
    /// <param name="line">要追加的文本行。</param>
    private void AppendLine(string line)
    {
        // 日志文本追加：后台优先级
        Dispatcher.UIThread.Post(() =>
        {
            var doc = _logDocument;
            if (doc == null) return;

            // 末尾非换行时补一个换行再插入，避免粘连
            if (doc.TextLength > 0 && doc.GetCharAt(doc.TextLength - 1) != '\n')
                doc.Insert(doc.TextLength, "\n" + line);
            else
                doc.Insert(doc.TextLength, line + "\n");

            // 行数超限时从开头删除多余行，保留最近 MaxLineCount 行
            if (doc.LineCount > MaxLineCount && MaxLineCount > 0)
            {
                int removeLines = doc.LineCount - MaxLineCount;
                if (removeLines > 0 && removeLines < doc.LineCount)
                {
                    var lastLine = doc.GetLineByNumber(removeLines + 1);
                    doc.Remove(0, lastLine.Offset);
                }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 在 UI 线程清空日志文档并重置增量日志 ID。
    /// </summary>
    private void ClearDocument()
    {
        // 清空日志：后台优先级
        Dispatcher.UIThread.Post(() =>
        {
            LogDocument ??= new TextDocument();
            LogDocument.Text = "";
            _latestLogId = 0;
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 释放资源：取消事件订阅并停止轮询。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _connection.DataPackReceived -= OnDataPackReceived;
            _connection.Disconnected -= OnDisconnected;
            _pollingTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
