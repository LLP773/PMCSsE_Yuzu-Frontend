using System;
using System.Collections.ObjectModel;
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
public class LogsViewModel : ViewModelBase, IDisposable
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

    public ReactiveCommand<Unit, Unit> SendCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAutoScrollCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportLogsCommand { get; }

    public LogsViewModel(ConnectionViewModel connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

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

    private void HandleServerLogs(Pack_MCServerLogs logs)
    {
        if (logs == null || logs.Logs == null || logs.Logs.Length == 0) return;
        if (!string.Equals(logs.ManagerID, ActiveManagerId, StringComparison.Ordinal)) return;

        Dispatcher.UIThread.Post(() =>
        {
            bool anyNewer = false;
            foreach (var entry in logs.Logs)
            {
                if (entry == null) continue;
                if (entry.ID <= _latestLogId) continue;

                _latestLogId = entry.ID;
                anyNewer = true;

                AppendLine($"[{DateTime.Now:HH:mm:ss}] {entry.Log}");
            }

            if (anyNewer && IsAutoScroll)
                RequestScrollToEnd?.Invoke();
        });
    }

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

    private void HandleCommandFailed(object? data)
    {
        try
        {
            var msgProp = data?.GetType().GetProperty("Message");
            var msg = msgProp?.GetValue(data)?.ToString() ?? "命令发送失败";
            AppendLine($"[{DateTime.Now:HH:mm:ss}] [错误] {msg}");
            if (IsAutoScroll) RequestScrollToEnd?.Invoke();
            ShowToast("命令发送失败", msg, ToastType.Error);
        }
        catch { }
    }

    private void HandleErrorInfo(object? data)
    {
        try
        {
            var msgProp = data?.GetType().GetProperty("Message");
            var msg = msgProp?.GetValue(data)?.ToString() ?? "未知错误";
            AppendLine($"[{DateTime.Now:HH:mm:ss}] [错误] {msg}");
            if (IsAutoScroll) RequestScrollToEnd?.Invoke();
            ShowToast("错误", msg, ToastType.Error);
        }
        catch { }
    }

    private void OnDisconnected()
    {
        _pollingTimer?.Dispose();
        _pollingTimer = null;
        AppendLine($"[{DateTime.Now:HH:mm:ss}] === 连接已断开 ===");
    }

    // ========== 命令实现 ==========

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

            Dispatcher.UIThread.Post(() =>
            {
                if (CommandHistory.Count == 0 || CommandHistory[0] != cmd)
                    CommandHistory.Insert(0, cmd);
                while (CommandHistory.Count > 50)
                    CommandHistory.RemoveAt(CommandHistory.Count - 1);
            });

            CommandInput = "";
        }
        catch
        {
            AppendLine($"[{DateTime.Now:HH:mm:ss}] [错误] 发送命令失败");
        }
    }

    private void ExecuteClearLogs()
    {
        ClearDocument();
    }

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

    private void RestartPollingIfActive()
    {
        if (!string.IsNullOrEmpty(ActiveManagerId))
            StartPolling();
    }

    // ========== 文档操作 ==========

    private void AppendLine(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var doc = _logDocument;
            if (doc == null) return;

            if (doc.TextLength > 0 && doc.GetCharAt(doc.TextLength - 1) != '\n')
                doc.Insert(doc.TextLength, "\n" + line);
            else
                doc.Insert(doc.TextLength, line + "\n");

            if (doc.LineCount > MaxLineCount && MaxLineCount > 0)
            {
                int removeLines = doc.LineCount - MaxLineCount;
                if (removeLines > 0 && removeLines < doc.LineCount)
                {
                    var lastLine = doc.GetLineByNumber(removeLines + 1);
                    doc.Remove(0, lastLine.Offset);
                }
            }
        });
    }

    private void ClearDocument()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogDocument ??= new TextDocument();
            LogDocument.Text = "";
            _latestLogId = 0;
        });
    }

    public void Dispose()
    {
        _connection.DataPackReceived -= OnDataPackReceived;
        _connection.Disconnected -= OnDisconnected;
        _pollingTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
