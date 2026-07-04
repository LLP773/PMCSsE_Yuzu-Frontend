using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CodeWF.Log.Core;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using PMCSsE_Communicator.SharedCodes;
using ReactiveUI;
using ReactiveUI.Primitives;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;
using MELogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Yuzu_Frontend.Models;

/// <summary>
/// MC 服务端管理器日志控制台视图模型。
/// 负责按选中管理器加载日志、定时拉取新日志、分页获取更旧日志、发送控制台命令。
/// 远程日志通过 <see cref="Logger"/> 静态方法写入全局 UserLogs feed，
/// 由 <c>CodeWF.LogViewer.Avalonia.LogView</c> 控件自动读取显示，内置级别颜色与虚拟化滚动。
/// </summary>
public class MCServerManagerLogConsoleViewModel : ViewModelBase
{
    // ============================================================
    //  常量
    // ============================================================
    private const int PageSize = 200;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaxRetries = 3;
    private const int MaxCommandHistory = 100;
    private static readonly TimeSpan CommandSendTimeout = TimeSpan.FromSeconds(15);
    private const int LoggerFeedCapacity = 30000;

    private static readonly Regex LevelRegex = new(
        @"\[(?:[^\]\[]*?/)?(INFO|INF|WARNING|WARN|ERROR|ERR|FATAL|CRITICAL|CRIT|SEVERE|DEBUG|DBG|TRACE|TRC)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // ============================================================
    //  字段
    // ============================================================
    private readonly DispatcherTimer _getNewerLogsTimer = new()
    { Interval = TimeSpan.FromMilliseconds(1000), IsEnabled = false };

    private readonly Dictionary<string, CancellationTokenSource> _pendingRequests = new();
    private readonly Dictionary<string, int> _retryCount = new();

    private ConnectionViewModel? _connection;

    private string? _selectedManagerId;
    private MCServerManagerItemModel? _selectedManager;
    private bool _isLoading;
    private bool _hasError;
    private string _errorMessage = "";
    private ulong _oldestLogId = ulong.MaxValue;
    private ulong _latestLogId = ulong.MinValue;
    private bool _isSendingCommand;
    private string _commandText = "";
    private IDisposable? _connectionStateSubscription;
    private bool _dataPackSubscribed;

    /// <summary>命令历史记录（最近发送的命令，最新的在尾部）。</summary>
    private readonly List<string> _commandHistory = new();
    /// <summary>当前浏览的历史索引，-1 表示正在输入新命令（不在历史中）。</summary>
    private int _commandHistoryIndex = -1;

    // ============================================================
    //  构造与初始化
    // ============================================================
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "WhenAnyValue 表达式树仅访问本 ViewModel 的公共属性，这些属性经 XAML 编译绑定被静态根保留，AOT 裁剪下安全")]
    public MCServerManagerLogConsoleViewModel()
    {
        // 使用 RangeObservableCollection 支持批量 ReplaceAll/AddRange，避免 Clear+N 次 Add 的 N 次通知
        ManagerList = new RangeObservableCollection<MCServerManagerItemModel>();
        ManagerTree = new RangeObservableCollection<BackendGroupNode>();
        HasSelectedManager = false;

        // 可选日志级别列表，供 UI 下拉选择 MinimumLevel
        LogLevelOptions = new[] { LogType.Debug, LogType.Info, LogType.Warn, LogType.Error, LogType.Fatal };

        var canClear = this.WhenAnyValue(vm => vm.LogCount)
            .Select(count => count > 0);
        ClearLogsCommand = ReactiveCommand.CreateFromTask(ClearLogsAsync, canClear);

        var canGetOlder = this.WhenAnyValue(
            vm => vm.OldestLogId,
            vm => vm.IsLoading,
            vm => vm.HasSelectedManager,
            (oldest, loading, hasMgr) => oldest > 1 && !loading && hasMgr);
        GetOlderLogsCommand = ReactiveCommand.CreateFromTask(GetOlderLogs, canGetOlder);

        RetryLoadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedManager != null) await LoadLogsForManager(SelectedManager, forceReload: true);
        }, this.WhenAnyValue(vm => vm.HasError).Select(err => err));

        var canSend = this.WhenAnyValue(
            vm => vm.CommandText,
            vm => vm.IsSendingCommand,
            vm => vm.HasSelectedManager,
            vm => vm.IsManagerStopped,
            (cmd, sending, hasMgr, stopped) => !string.IsNullOrWhiteSpace(cmd) && !sending && hasMgr && !stopped);
        SendCommand = ReactiveCommand.CreateFromTask<string, RxVoid>(async cmd =>
        {
            await SendManagerCommand(cmd);
            return RxVoid.Default;
        }, canSend);

        var canExport = this.WhenAnyValue(vm => vm.LogCount).Select(c => c > 0);
        ExportTxtCommand = ReactiveCommand.CreateFromTask<string, RxVoid>(async path =>
        {
            await ExportTxtAsync(path);
            return RxVoid.Default;
        }, canExport);
        ExportCsvCommand = ReactiveCommand.CreateFromTask<string, RxVoid>(async path =>
        {
            await ExportCsvAsync(path);
            return RxVoid.Default;
        }, canExport);

        // ============================================================
        //  管理器启停控制按钮（启动/停止/强制停止/重启）
        // ============================================================
        var canStart = this.WhenAnyValue(
            vm => vm.HasSelectedManager,
            vm => vm.IsManagerStopped,
            vm => vm.IsPerformingControlAction,
            (hasSel, stopped, busy) => hasSel && stopped && !busy);
        StartServerCommand = ReactiveCommand.CreateFromTask(StartMCServerAsync, canStart);

        var canStop = this.WhenAnyValue(
            vm => vm.HasSelectedManager,
            vm => vm.SelectedManager,
            vm => vm.IsPerformingControlAction,
            (hasSel, mgr, busy) => hasSel && mgr != null && mgr.IsMCServerRunning && !busy);
        StopServerCommand = ReactiveCommand.CreateFromTask(StopMCServerAsync, canStop);

        var canKill = this.WhenAnyValue(
            vm => vm.HasSelectedManager,
            vm => vm.SelectedManager,
            vm => vm.IsPerformingControlAction,
            (hasSel, mgr, busy) => hasSel && mgr != null && mgr.IsMCServerRunning && !busy);
        ForceKillServerCommand = ReactiveCommand.CreateFromTask(KillMCServerAsync, canKill);

        // 重启按钮：先停止（成功后）再启动
        var canRestart = this.WhenAnyValue
            (vm => vm.HasSelectedManager,
             vm => vm.SelectedManager,
             vm => vm.IsPerformingControlAction,
             (hasSel, mgr, busy) => hasSel && mgr != null && !busy);
        RestartServerCommand = ReactiveCommand.CreateFromTask(RestartMCServerAsync, canRestart);

        _getNewerLogsTimer.Tick += OnGetNewerLogsTick;
    }

    // ============================================================
    //  树状选择器：后端分组模型
    // ============================================================

    /// <summary>
    /// TreeView 节点基类，用于数据模板类型区分（父节点 vs 子节点）。
    /// </summary>
    public abstract class ManagerTreeNodeBase : ReactiveObject { }

    /// <summary>
    /// TreeView 第一级节点：后端地址（后端ID）分组容器。
    /// 此节点不可选择，仅用作分组和展开/折叠。
    /// </summary>
    public class BackendGroupNode : ManagerTreeNodeBase
    {
        private string _backendId = "";
        private bool _isExpanded = true;

        public string BackendId
        {
            get => _backendId;
            set => this.RaiseAndSetIfChanged(ref _backendId, value);
        }

        /// <summary>展开状态，用于 TreeView 绑定并保持用户交互。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
        }

        public RangeObservableCollection<ManagerItemNode> Children { get; } = new();
    }

    /// <summary>
    /// TreeView 第二级节点：管理器列表项，对应 MCServerManagerItemModel。
    /// 此节点可选择。
    /// </summary>
    public class ManagerItemNode : ManagerTreeNodeBase
    {
        public MCServerManagerItemModel Item { get; }

        public ManagerItemNode(MCServerManagerItemModel item)
        {
            Item = item;
        }
    }

    // ============================================================
    //  显示/过滤相关属性
    // ============================================================
    /// <summary>
    /// LogView 最低日志级别过滤。绑定到 LogView.MinimumLevel。
    /// 低于此级别的日志不会在 LogView 中显示。
    /// </summary>
    private LogType _minimumLevel = LogType.Debug;
    public LogType MinimumLevel
    {
        get => _minimumLevel;
        set => this.RaiseAndSetIfChanged(ref _minimumLevel, value);
    }

    /// <summary>
    /// 清空 LogView 显示的回调。
    /// 由页面代码隐藏设置，ViewModel 不持有 UI 控件引用。
    /// 回调内部通过触发 LogView 控件上下文菜单的"清空"项来重置显示。
    /// </summary>
    public Action? RequestClearLogView { get; set; }

    /// <summary>可选日志级别列表，供 UI ComboBox 选择。</summary>
    public LogType[] LogLevelOptions { get; }

    // ============================================================
    //  业务状态属性
    // ============================================================
    public ObservableCollection<MCServerManagerItemModel> ManagerList { get; }

    /// <summary>
    /// 管理器列表的树状视图展示集合：按后端 ID (BackendId) 分组。
    /// 第一级为后端分组（不可选），第二级为管理器（可选）。
    /// </summary>
    public RangeObservableCollection<BackendGroupNode> ManagerTree { get; }

    /// <summary>
    /// TreeView 选中项（双向绑定）。
    /// 仅子节点（ManagerItemNode）可触发选中并同步到 SelectedManager；
    /// 父节点（BackendGroupNode）会被视为无效选择，自动回滚。
    /// </summary>
    private ManagerTreeNodeBase? _selectedTreeNode;
    public ManagerTreeNodeBase? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (value is BackendGroupNode)
            {
                // 父节点不可选择：若当前已有选中的子节点，保留选中；否则清空
                this.RaisePropertyChanged();
                return;
            }

            if (ReferenceEquals(value, _selectedTreeNode)) return;
            _selectedTreeNode = value;
            this.RaisePropertyChanged();

            // 同步转换为 SelectedManager
            if (value is ManagerItemNode miNode)
                SelectedManager = miNode.Item;
            else
                SelectedManager = null;
        }
    }

    private bool _isPerformingControlAction;
    /// <summary>启停按钮是否正在执行操作（所有按钮会被禁用以防并发）。</summary>
    public bool IsPerformingControlAction
    {
        get => _isPerformingControlAction;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPerformingControlAction, value);
            // 通知 ReactiveCommand 重新评估 CanExecute
            this.RaisePropertyChanged(nameof(StartServerCommand));
            this.RaisePropertyChanged(nameof(StopServerCommand));
            this.RaisePropertyChanged(nameof(ForceKillServerCommand));
            this.RaisePropertyChanged(nameof(RestartServerCommand));
        }
    }

    private string _controlActionText = "";
    /// <summary>当前操作进度描述（如"正在启动..."），供按钮/提示显示。</summary>
    public string ControlActionText
    {
        get => _controlActionText;
        private set => this.RaiseAndSetIfChanged(ref _controlActionText, value);
    }

    /// <summary>重启流程状态机：None→WaitingStop→WaitingStart。</summary>
    private enum RestartPhase { None, WaitingStop, WaitingStart }
    private RestartPhase _restartPhase;
    private string? _restartPendingManagerId;

    public bool CanGetOlderLogs => OldestLogId > 1 && !IsLoading && HasSelectedManager && !IsManagerStopped;

    public ulong OldestLogId
    {
        get => _oldestLogId;
        private set { this.RaiseAndSetIfChanged(ref _oldestLogId, value); this.RaisePropertyChanged(nameof(CanGetOlderLogs)); }
    }

    public ulong LatestLogId
    {
        get => _latestLogId;
        private set => this.RaiseAndSetIfChanged(ref _latestLogId, value);
    }

    /// <summary>
    /// 当前 Logger feed 中的日志条数（用于 UI 显示和命令可用性判断）。
    /// 通过手动计数维护：推送日志时递增，重置 feed 时归零。
    /// </summary>
    private int _logCount;
    public int LogCount
    {
        get => _logCount;
        private set => this.RaiseAndSetIfChanged(ref _logCount, value);
    }

    public MCServerManagerItemModel? SelectedManager
    {
        get => _selectedManager;
        set
        {
            if (Equals(value, _selectedManager)) return;
            _selectedManager = value;
            this.RaisePropertyChanged();
            if (value == null)
            {
                HasSelectedManager = false;
                SelectedManagerId = string.Empty;
                IsManagerStopped = false;
                StopTimer();
            }
            else
            {
                HasSelectedManager = true;
                SelectedManagerId = value.ManagerId;

                // 检查管理器运行状态
                if (!value.IsMCServerRunning)
                {
                    // 管理器未运行，显示提示，跳过日志加载
                    IsManagerStopped = true;
                    StopTimer();
                    Dispatcher.UIThread.Post(() =>
                    {
                        ClearLogs();
                        HasLogs = false;
                        IsLoading = false;
                        HasError = false;
                    }, DispatcherPriority.Background);
                }
                else
                {
                    // 管理器运行中，正常加载日志
                    IsManagerStopped = false;
                    _ = LoadLogsForManager(value);
                }
            }
            this.RaisePropertyChanged(nameof(CanGetOlderLogs));
        }
    }

    public string SelectedManagerId
    {
        get => _selectedManagerId ?? string.Empty;
        private set
        {
            _selectedManagerId = value;
            HasSelectedManager = !string.IsNullOrEmpty(value);
        }
    }

    private bool _hasSelectedManager;
    public bool HasSelectedManager
    {
        get => _hasSelectedManager;
        private set => this.RaiseAndSetIfChanged(ref _hasSelectedManager, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(CanGetOlderLogs));
        }
    }

    public bool HasError
    {
        get => _hasError;
        set
        {
            this.RaiseAndSetIfChanged(ref _hasError, value);
            if (!value) ErrorMessage = "";
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    private bool _isEmpty;
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    private bool _hasLogs;
    public bool HasLogs
    {
        get => _hasLogs;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasLogs, value);
            IsEmpty = !value && !IsLoading && !HasError && !IsManagerStopped;
        }
    }

    private bool _isManagerStopped;
    /// <summary>当前选中的管理器是否处于停止状态（未运行）。</summary>
    public bool IsManagerStopped
    {
        get => _isManagerStopped;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isManagerStopped, value);
            IsEmpty = !value && !HasLogs && !IsLoading && !HasError && HasSelectedManager;
            this.RaisePropertyChanged(nameof(CanGetOlderLogs));
        }
    }

    public bool IsSendingCommand
    {
        get => _isSendingCommand;
        private set => this.RaiseAndSetIfChanged(ref _isSendingCommand, value);
    }

    public string CommandText
    {
        get => _commandText;
        set
        {
            this.RaiseAndSetIfChanged(ref _commandText, value ?? "");
            // 用户手动修改了输入内容，退出历史浏览模式
            _commandHistoryIndex = -1;
        }
    }

    /// <summary>是否有可回溯的命令历史。</summary>
    public bool HasCommandHistory => _commandHistory.Count > 0;

    /// <summary>
    /// 浏览命令历史：向上（更早的命令）或向下（更近的命令）。
    /// 到达底部后清空输入框，回到新命令输入模式。
    /// </summary>
    public void NavigateCommandHistory(bool up)
    {
        if (_commandHistory.Count == 0) return;

        if (up)
        {
            // 向上：浏览更早的命令
            if (_commandHistoryIndex == -1)
                _commandHistoryIndex = _commandHistory.Count - 1;
            else if (_commandHistoryIndex > 0)
                _commandHistoryIndex--;
        }
        else
        {
            // 向下：浏览更近的命令
            if (_commandHistoryIndex == -1) return;
            if (_commandHistoryIndex < _commandHistory.Count - 1)
                _commandHistoryIndex++;
            else
            {
                // 到达底部，清空回到新命令输入
                _commandHistoryIndex = -1;
                CommandText = "";
                return;
            }
        }

        if (_commandHistoryIndex >= 0 && _commandHistoryIndex < _commandHistory.Count)
        {
            // 直接设置字段避免触发 setter 中的索引重置
            _commandText = _commandHistory[_commandHistoryIndex];
            this.RaisePropertyChanged(nameof(CommandText));
        }
    }

    private void AddToCommandHistory(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        var trimmed = command.Trim();
        // 避免连续重复
        if (_commandHistory.Count > 0 && _commandHistory[^1] == trimmed) return;
        _commandHistory.Add(trimmed);
        while (_commandHistory.Count > MaxCommandHistory)
            _commandHistory.RemoveAt(0);
        this.RaisePropertyChanged(nameof(HasCommandHistory));
    }

    // ============================================================
    //  公开命令
    // ============================================================
    public ReactiveCommand<RxVoid, RxVoid> ClearLogsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> GetOlderLogsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RetryLoadCommand { get; }
    public ReactiveCommand<string, RxVoid> SendCommand { get; }
    public ReactiveCommand<string, RxVoid> ExportTxtCommand { get; }
    public ReactiveCommand<string, RxVoid> ExportCsvCommand { get; }

    /// <summary>启动管理器（RunMCServer）。</summary>
    public ReactiveCommand<RxVoid, RxVoid> StartServerCommand { get; }

    /// <summary>停止管理器（ShutdownMCServer）。</summary>
    public ReactiveCommand<RxVoid, RxVoid> StopServerCommand { get; }

    /// <summary>强制停止管理器（KillMCServer）。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ForceKillServerCommand { get; }

    /// <summary>重启管理器：先停止，收到停止成功响应后自动启动。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RestartServerCommand { get; }

    // ============================================================
    //  初始化 / 释放
    // ============================================================
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "WhenAnyValue 仅访问 ConnectionViewModel.IsConnected，该属性被 XAML 绑定静态根保留")]
    public void Initialize(ConnectionViewModel connectionViewModel)
    {
        _connection = connectionViewModel;

        _connectionStateSubscription?.Dispose();
        _connectionStateSubscription = System.ObservableExtensions.Subscribe(
            _connection.WhenAnyValue(x => x.IsConnected),
            OnConnectionStateChanged);

        if (!_dataPackSubscribed)
        {
            _dataPackSubscribed = true;
            connectionViewModel.DataPackReceived += OnDataPackReceived;
        }
    }

    public void OnViewAttached(bool connected, string? backendId)
    {
        if (!connected)
        {
            HasError = true;
            ErrorMessage = "尚未连接到后端，请在连接页面建立连接后再使用日志控制台。";
            return;
        }
        HasError = false;
        if (ManagerList.Count == 0)
        {
            _ = RefreshManagerList();
        }
    }

    public async Task RefreshManagerList()
    {
        if (_connection == null || !_connection.IsConnected) return;
        try
        {
            _connection.RequestBackend(RequestTypeEnum.GetLoadedMCServerManagers);
            await Task.Delay(80);
        }
        catch (Exception ex)
        {
            ShowError("加载管理器列表失败: " + ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            StopTimer();
            foreach (var cts in _pendingRequests.Values.ToArray())
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                cts.Dispose();
            }
            _pendingRequests.Clear();
            _connectionStateSubscription?.Dispose();
            _connectionStateSubscription = null;
            if (_connection != null)
            {
                _connection.DataPackReceived -= OnDataPackReceived;
            }
            _dataPackSubscribed = false;
        }
        base.Dispose(disposing);
    }

    // ============================================================
    //  公开能力：清空 / 导出
    // ============================================================
    /// <summary>
    /// 清空当前日志显示。通过回调通知 LogView 控件清空显示。
    /// </summary>
    public Task ClearLogsAsync()
    {
        ClearLogs();
        HasLogs = false;
        return Task.CompletedTask;
    }

    public Task ExportTxtAsync(string filePath)
    {
        var entries = Logger.UserLogs.GetRecentEntries();
        var lines = entries
            .OrderBy(e => e.Sequence)
            .Select(e => $"[{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{e.Level}] {e.Message}");
        return File.WriteAllLinesAsync(filePath, lines);
    }

    public Task ExportCsvAsync(string filePath)
    {
        var entries = Logger.UserLogs.GetRecentEntries();
        var lines = entries
            .OrderBy(e => e.Sequence)
            .Select(e => string.Join(",",
                CsvEscape(e.Sequence.ToString()),
                CsvEscape(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                CsvEscape(e.Level.ToString()),
                CsvEscape(e.Message ?? string.Empty)));
        var header = "Sequence,Timestamp,Level,Message";
        return File.WriteAllLinesAsync(filePath, new[] { header }.Concat(lines));
    }

    // ============================================================
    //  Logger feed 管理
    // ============================================================
    /// <summary>
    /// 清空日志显示。
    /// 通过回调通知 LogView 控件执行内部清空（设置 _clearSequence 过滤旧日志），
    /// 同时重置本地日志计数。
    /// 注意：CodeWF.Log.Core 12.1.0.16 的 Logger.ShutdownAsync 不会清空内部 _host，
    /// 因此不能通过 ShutdownAsync + Initialize 重置全局 feed；
    /// LogView 控件的 _clearSequence 机制是清除显示的正确方式。
    /// </summary>
    private void ClearLogs()
    {
        LogCount = 0;
        RequestClearLogView?.Invoke();
    }

    /// <summary>
    /// 将单条远程日志推入 Logger 全局 feed。
    /// LogView 控件会自动从 feed 读取并显示，内置级别颜色。
    /// </summary>
    private void PushLogToFeed(string text, LogType level)
    {
        // 同时传入 message 和 userMessage，确保日志同时写入内部存储和 UserLogs feed
        Logger.Log(level, text, text);
        LogCount++;
    }

    // ============================================================
    //  内部：日志拉取
    // ============================================================
    private async Task LoadLogsForManager(MCServerManagerItemModel manager, bool forceReload = false)
    {
        // 如果管理器未运行，跳过日志加载
        if (!manager.IsMCServerRunning)
        {
            IsManagerStopped = true;
            StopTimer();
            return;
        }

        StopTimer();
        IsLoading = true;
        HasError = false;
        IsManagerStopped = false;
        if (forceReload)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ClearLogs();
                _oldestLogId = ulong.MaxValue;
                _latestLogId = ulong.MinValue;
                HasLogs = false;
            }, DispatcherPriority.Background);
        }

        if (_connection == null || !_connection.IsConnected)
        {
            IsLoading = false;
            ShowError("尚未连接到后端。");
            return;
        }

        var opKey = $"{manager.ManagerId}:latest";
        ScheduleTimeout(opKey, () =>
        {
            if (IsLoading)
            {
                IsLoading = false;
                ShowError("加载日志超时，请点击重试。");
            }
        });

        try
        {
            var getLatestPack = new Pack_GetLatestMCServerLogs(manager.ManagerId, PageSize);
            _connection.RequestBackend(RequestTypeEnum.GetLatestLogs, getLatestPack);
            await Task.Delay(100).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CancelTimeout(opKey);
            if (!HandleRetry(manager.ManagerId, "latest", ex, () => _ = LoadLogsForManager(manager, forceReload)))
            {
                IsLoading = false;
                ShowError("加载日志失败: " + ex.Message);
            }
        }
    }

    private async Task GetOlderLogs()
    {
        if (SelectedManager == null || _connection == null || !_connection.IsConnected) return;
        if (_oldestLogId <= 1) return;

        IsLoading = true;
        var opKey = $"{SelectedManager.ManagerId}:older";
        ScheduleTimeout(opKey, () =>
        {
            if (IsLoading)
            {
                IsLoading = false;
                ShowError("加载历史日志超时，请点击重试。");
            }
        });

        try
        {
            var pack = new Pack_GetOlderMCServerLogs(SelectedManager.ManagerId, _oldestLogId - 1, PageSize);
            _connection.RequestBackend(RequestTypeEnum.GetOlderLogs, pack);
            await Task.Delay(50).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CancelTimeout(opKey);
            if (!HandleRetry(SelectedManager.ManagerId, "older", ex, () => _ = GetOlderLogs()))
            {
                IsLoading = false;
                ShowError("加载历史日志失败: " + ex.Message);
            }
        }
    }

    private void OnGetNewerLogsTick(object? sender, EventArgs e)
    {
        _getNewerLogsTimer.Stop();
        try
        {
            if (string.IsNullOrEmpty(_selectedManagerId) || _connection == null || !_connection.IsConnected) return;
            if (_latestLogId == ulong.MinValue) return;
            var pack = new Pack_GetNewerMCServerLogs(_selectedManagerId, _latestLogId, PageSize);
            _connection.RequestBackend(RequestTypeEnum.GetNewerLogs, pack);
        }
        catch (Exception ex)
        {
            ShowToast("拉取新日志失败", ex.Message, ToastType.Warning);
        }
        finally
        {
            if (_connection != null && !string.IsNullOrEmpty(_selectedManagerId))
                _getNewerLogsTimer.Start();
        }
    }

    private void StopTimer()
    {
        if (_getNewerLogsTimer.IsEnabled) _getNewerLogsTimer.Stop();
    }

    private void StartTimer()
    {
        if (!_getNewerLogsTimer.IsEnabled) _getNewerLogsTimer.Start();
    }

    // ============================================================
    //  内部：命令发送
    // ============================================================
    private async Task SendManagerCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || SelectedManager == null || _connection == null || !_connection.IsConnected) return;

        var trimmedCmd = command.Trim();

        // 立即记录到历史并清空输入框，让用户感知到命令已被接受
        AddToCommandHistory(trimmedCmd);
        CommandText = "";

        IsSendingCommand = true;
        var opKey = $"{SelectedManager.ManagerId}:send:{Guid.NewGuid():N}";
        ScheduleTimeout(opKey, () =>
        {
            if (IsSendingCommand)
            {
                IsSendingCommand = false;
                ShowToast("命令发送超时", "请检查后端状态后重试。", ToastType.Error);
            }
        }, timeout: CommandSendTimeout);

        try
        {
            var pack = new Pack_SendCommand(SelectedManager.ManagerId, trimmedCmd);

            // 定向路由到管理器所属后端，避免向所有后端广播
            if (!string.IsNullOrEmpty(SelectedManager.BackendId))
            {
                _connection.RequestBackendTo(SelectedManager.BackendId, RequestTypeEnum.SendCommand, pack);
            }
            else
            {
                // 兜底：BackendId 未知时回退到广播
                _connection.RequestBackend(RequestTypeEnum.SendCommand, pack);
            }
        }
        catch (Exception ex)
        {
            CancelTimeout(opKey);
            if (!HandleRetry(SelectedManager.ManagerId, "send", ex, () => _ = SendManagerCommand(trimmedCmd)))
            {
                IsSendingCommand = false;
                ShowToast("命令发送失败", ex.Message, ToastType.Error);
            }
        }
    }

    // ============================================================
    //  内部：连接/数据包事件回调
    // ============================================================
    private void OnConnectionStateChanged(bool connected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!connected)
            {
                StopTimer();
                ShowError("与后端的连接已断开，请重新连接。");
            }
            else if (HasError && ErrorMessage.Contains("尚未连接", StringComparison.Ordinal))
            {
                HasError = false;
                _ = RefreshManagerList();
            }
        });
    }

    private void OnDataPackReceived(RespondTypeEnum type, object? pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                switch (type)
                {
                    case RespondTypeEnum.MCServerLogs:
                        HandleMCServerLogs(pack as Pack_MCServerLogs);
                        break;
                    case RespondTypeEnum.LoadedMCServerManagers:
                        if (pack is PMCSsE_Communicator.DataPacks.Pack_MCServerManagers mgrs)
                            HandleManagerList(mgrs);
                        break;
                    case RespondTypeEnum.MCServerManagerConfigs:
                        if (pack is PMCSsE_Communicator.DataPacks.Pack_MCServerManagerConfigs cfgs)
                            HandleManagerConfigs(cfgs);
                        break;
                    case RespondTypeEnum.CreatedNewMCServerManager:
                    case RespondTypeEnum.ModifiedMCServerManagerConfig:
                    case RespondTypeEnum.LoadedMCServerManager:
                    case RespondTypeEnum.DeletedMCServerManager:
                        _ = RefreshManagerList();
                        break;
                    case RespondTypeEnum.StoppedMCServerManager:
                        _ = RefreshManagerList();
                        // 如果当前选中的管理器被停止，更新 UI 状态
                        if (SelectedManager != null && !string.IsNullOrEmpty(_selectedManagerId))
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                var mgr = ManagerList.FirstOrDefault(m => m.ManagerId == _selectedManagerId);
                                if (mgr != null)
                                {
                                    SelectedManager = mgr; // 触发 setter 中的状态检查
                                }
                            });
                        }
                        break;

                    // ============================================================
                    //  启停控制响应：启动/停止/强制停止/重启状态机
                    // ============================================================
                    case RespondTypeEnum.RunMCServerSucceed:
                        HandleRunSucceed(pack as Pack_RunMCServerSucceed);
                        break;
                    case RespondTypeEnum.RunMCServerFailed:
                        HandleRunFailed(pack as Pack_RunMCServerFailed);
                        break;
                    case RespondTypeEnum.ShutdownMCServerSucceed:
                        HandleShutdownSucceed(pack as Pack_ShutdownMCServerSucceed);
                        break;
                    case RespondTypeEnum.ShutdownMCServerFailed:
                        HandleShutdownFailed(pack as Pack_ShutdownMCServerFailed);
                        break;
                    case RespondTypeEnum.KillMCServerSucceed:
                        HandleKillSucceed(pack as Pack_KillMCServerSucceed);
                        break;
                    case RespondTypeEnum.KillMCServerFailed:
                        HandleKillFailed(pack as Pack_KillMCServerFailed);
                        break;

                    case RespondTypeEnum.SendCommandSucceed:
                        IsSendingCommand = false;
                        ShowToast("命令已发送成功", "", ToastType.Success);
                        foreach (var kv in _pendingRequests.Where(kv => kv.Key.Contains(":send:")).ToArray())
                            CancelTimeout(kv.Key);
                        // 清理重试计数
                        foreach (var k in _retryCount.Keys.Where(k => k.Contains(":send:")).ToArray())
                            _retryCount.Remove(k);
                        break;
                    case RespondTypeEnum.SendCommandFailed:
                        IsSendingCommand = false;
                        ShowToast("命令执行失败", "请检查日志获取错误详情。", ToastType.Error);
                        foreach (var kv in _pendingRequests.Where(kv => kv.Key.Contains(":send:")).ToArray())
                            CancelTimeout(kv.Key);
                        foreach (var k in _retryCount.Keys.Where(k => k.Contains(":send:")).ToArray())
                            _retryCount.Remove(k);
                        break;
                    case RespondTypeEnum.ErrorInfo:
                        if (pack is Pack_ErrorInfo err)
                        {
                            ShowToast("后端错误", err.ErrorInfo, ToastType.Error);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowToast("处理后端数据异常", ex.Message, ToastType.Warning);
            }
        });
    }

    // ============================================================
    //  启停控制响应处理器
    // ============================================================
    private void CancelAllControlTimeouts()
    {
        foreach (var kv in _pendingRequests.Where(kv => kv.Key.StartsWith("ctl:")).ToArray())
            CancelTimeout(kv.Key);
    }

    private void HandleRunSucceed(Pack_RunMCServerSucceed? pack)
    {
        if (pack == null) return;
        CancelAllControlTimeouts();
        ShowToast("启动成功", pack.ManagerID ?? "", ToastType.Success);

        // 重启流程中 WaitingStart → 完成
        if (_restartPhase == RestartPhase.WaitingStart &&
            pack.ManagerID == _restartPendingManagerId)
        {
            _restartPhase = RestartPhase.None;
            _restartPendingManagerId = null;
        }
        IsPerformingControlAction = false;
        ControlActionText = "";
        _ = RefreshManagerList();
    }

    private void HandleRunFailed(Pack_RunMCServerFailed? pack)
    {
        if (pack == null) return;
        CancelAllControlTimeouts();
        // 任何失败都会终止重启流程
        _restartPhase = RestartPhase.None;
        _restartPendingManagerId = null;
        IsPerformingControlAction = false;
        ControlActionText = "";
        var reason = SafeGetFailedReason(pack);
        ShowToast("启动失败", string.IsNullOrEmpty(reason) ? "未知错误" : reason, ToastType.Error);
    }

    private void HandleShutdownSucceed(Pack_ShutdownMCServerSucceed? pack)
    {
        if (pack == null) return;
        CancelAllControlTimeouts();
        ShowToast("停止成功", pack.ManagerID ?? "", ToastType.Success);

        // 重启流程中 WaitingStop → 自动继续启动
        if (_restartPhase == RestartPhase.WaitingStop &&
            pack.ManagerID == _restartPendingManagerId)
        {
            _restartPhase = RestartPhase.WaitingStart;
            var mgrId = pack.ManagerID;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var backendId = _connection?.ConnectedBackends.FirstOrDefault()?.BackendId ?? "";
                    var targetManager = ManagerList.FirstOrDefault(m => m.ManagerId == mgrId);
                    if (targetManager != null && !string.IsNullOrEmpty(targetManager.BackendId))
                        backendId = targetManager.BackendId;

                    if (_connection != null && !string.IsNullOrEmpty(backendId))
                    {
                        ShowToast("重启管理器（启动阶段）", $"正在启动 {mgrId}", ToastType.Info);
                        ControlActionText = "重启管理器（启动阶段）";
                        _connection.RequestBackendTo(backendId, RequestTypeEnum.RunMCServer,
                            new Pack_RunMCServer(mgrId!));
                    }
                }
                catch (Exception ex)
                {
                    _restartPhase = RestartPhase.None;
                    _restartPendingManagerId = null;
                    IsPerformingControlAction = false;
                    ControlActionText = "";
                    ShowToast("重启失败", ex.Message, ToastType.Error);
                }
                await Task.CompletedTask;
            }, DispatcherPriority.Background);
        }
        else
        {
            IsPerformingControlAction = false;
            ControlActionText = "";
        }
        _ = RefreshManagerList();
    }

    private void HandleShutdownFailed(Pack_ShutdownMCServerFailed? pack)
    {
        if (pack == null) return;
        CancelAllControlTimeouts();
        _restartPhase = RestartPhase.None;
        _restartPendingManagerId = null;
        IsPerformingControlAction = false;
        ControlActionText = "";
        var reason = SafeGetFailedReason(pack);
        ShowToast("停止失败", string.IsNullOrEmpty(reason) ? "未知错误" : reason, ToastType.Error);
    }

    private void HandleKillSucceed(Pack_KillMCServerSucceed? pack)
    {
        if (pack == null) return;
        CancelAllControlTimeouts();
        IsPerformingControlAction = false;
        ControlActionText = "";
        ShowToast("已强制终止", pack.ManagerID ?? "", ToastType.Warning);
        _ = RefreshManagerList();
    }

    private void HandleKillFailed(Pack_KillMCServerFailed? pack)
    {
        if (pack == null) return;
        CancelAllControlTimeouts();
        IsPerformingControlAction = false;
        ControlActionText = "";
        var reason = SafeGetFailedReason(pack);
        ShowToast("强制停止失败", string.IsNullOrEmpty(reason) ? "未知错误" : reason, ToastType.Error);
    }

    /// <summary>
    /// 从各类 Failed 数据包中提取失败原因。
    /// Native AOT 兼容：PMCSsE_Communicator 的 Failed 数据包仅携带 ManagerID，
    /// 不包含原因/错误字段，因此此处按具体类型返回静态的用户可读提示，
    /// 避免使用 GetType().GetProperty 在 AOT 裁剪下失效。
    /// </summary>
    private static string? SafeGetFailedReason(object pack) => pack switch
    {
        Pack_RunMCServerFailed => "启动管理器失败（请检查服务端日志获取详细原因）",
        Pack_ShutdownMCServerFailed => "停止管理器失败（请检查服务端日志获取详细原因）",
        Pack_KillMCServerFailed => "强制停止管理器失败（请检查服务端日志获取详细原因）",
        _ => null,
    };

    // ============================================================
    //  内部：管理器启停控制（启动/停止/强制停止/重启）
    // ============================================================
    private static readonly TimeSpan ControlActionTimeout = TimeSpan.FromSeconds(20);

    private async Task<RxVoid> StartMCServerAsync()
    {
        if (SelectedManager == null || _connection == null) return RxVoid.Default;
        await RunControlActionInternal(
            actionLabel: "启动管理器",
            toastInfo: $"正在启动 {SelectedManager.MCServerName}",
            actionKey: "start",
            run: backendId =>
            {
                _connection.RequestBackendTo(backendId, RequestTypeEnum.RunMCServer,
                    new Pack_RunMCServer(SelectedManager.ManagerId));
            });
        return RxVoid.Default;
    }

    private async Task<RxVoid> StopMCServerAsync()
    {
        if (SelectedManager == null || _connection == null) return RxVoid.Default;
        await RunControlActionInternal(
            actionLabel: "停止管理器",
            toastInfo: $"正在停止 {SelectedManager.MCServerName}",
            actionKey: "stop",
            run: backendId =>
            {
                _connection.RequestBackendTo(backendId, RequestTypeEnum.ShutdownMCServer,
                    new Pack_ShutdownMCServer(SelectedManager.ManagerId));
            });
        return RxVoid.Default;
    }

    private async Task<RxVoid> KillMCServerAsync()
    {
        if (SelectedManager == null || _connection == null) return RxVoid.Default;
        await RunControlActionInternal(
            actionLabel: "强制停止管理器",
            toastInfo: $"正在强制终止 {SelectedManager.MCServerName}",
            actionKey: "kill",
            run: backendId =>
            {
                _connection.RequestBackendTo(backendId, RequestTypeEnum.KillMCServer,
                    new Pack_KillMCServer(SelectedManager.ManagerId));
            });
        return RxVoid.Default;
    }

    /// <summary>
    /// 重启管理器：
    /// 1. 若当前运行中 → 发送停止请求，并标记 _restartPhase=WaitingStop；
    ///    收到 ShutdownMCServerSucceed 后 → 自动发送启动请求，标记 _restartPhase=WaitingStart。
    /// 2. 若当前已停止 → 直接启动。
    /// </summary>
    private async Task<RxVoid> RestartMCServerAsync()
    {
        if (SelectedManager == null || _connection == null) return RxVoid.Default;
        var mgr = SelectedManager;
        var backendId = mgr.BackendId;
        if (string.IsNullOrEmpty(backendId))
        {
            backendId = _connection.ConnectedBackends.FirstOrDefault()?.BackendId ?? "";
        }
        if (string.IsNullOrEmpty(backendId))
        {
            ShowToast("重启失败", "未找到所属后端。", ToastType.Error);
            return RxVoid.Default;
        }

        if (mgr.IsMCServerRunning)
        {
            // 先停止，然后等待停止成功回调再启动
            _restartPhase = RestartPhase.WaitingStop;
            _restartPendingManagerId = mgr.ManagerId;
            await RunControlActionInternal(
                actionLabel: "重启管理器（停止阶段）",
                toastInfo: $"正在停止 {mgr.MCServerName}（重启中）",
                actionKey: "restart-stop",
                run: bid =>
                {
                    _connection.RequestBackendTo(bid, RequestTypeEnum.ShutdownMCServer,
                        new Pack_ShutdownMCServer(mgr.ManagerId));
                },
                skipCompletionReset: true); // 成功/失败由重启状态机重置
        }
        else
        {
            // 已停止 → 直接启动
            _restartPhase = RestartPhase.None;
            _restartPendingManagerId = null;
            await RunControlActionInternal(
                actionLabel: "重启管理器（直接启动）",
                toastInfo: $"正在启动 {mgr.MCServerName}",
                actionKey: "restart-start",
                run: bid =>
                {
                    _connection.RequestBackendTo(bid, RequestTypeEnum.RunMCServer,
                        new Pack_RunMCServer(mgr.ManagerId));
                });
        }
        return RxVoid.Default;
    }

    /// <summary>
    /// 通用的控制请求骨架：
    /// - 设置 UI busy 状态；
    /// - 触发超时（若超时则自动重置 busy 并提示）；
    /// - 执行实际请求回调；
    /// - 成功/失败由 OnDataPackReceived 回调中的对应分支重置 busy。
    /// </summary>
    private async Task RunControlActionInternal(
        string actionLabel,
        string toastInfo,
        string actionKey,
        Action<string> run,
        bool skipCompletionReset = false)
    {
        if (SelectedManager == null || _connection == null) return;

        var mgr = SelectedManager;
        var backendId = mgr.BackendId;
        if (string.IsNullOrEmpty(backendId))
        {
            backendId = _connection.ConnectedBackends.FirstOrDefault()?.BackendId ?? "";
        }
        if (string.IsNullOrEmpty(backendId))
        {
            ShowToast($"{actionLabel}失败", "未找到所属后端。", ToastType.Error);
            return;
        }

        IsPerformingControlAction = true;
        ControlActionText = actionLabel;
        var opKey = $"ctl:{mgr.ManagerId}:{actionKey}:{Guid.NewGuid():N}";
        if (!skipCompletionReset)
        {
            ScheduleTimeout(opKey, () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsPerformingControlAction)
                    {
                        IsPerformingControlAction = false;
                        ControlActionText = "";
                    }
                    ShowToast($"{actionLabel}超时", "请检查后端状态后重试。", ToastType.Warning);
                });
            }, ControlActionTimeout);
        }

        ShowToast(actionLabel, toastInfo, ToastType.Info);
        try
        {
            run(backendId);
        }
        catch (Exception ex)
        {
            CancelTimeout(opKey);
            IsPerformingControlAction = false;
            ControlActionText = "";
            _restartPhase = RestartPhase.None;
            _restartPendingManagerId = null;
            ShowToast($"{actionLabel}失败", ex.Message, ToastType.Error);
            return;
        }
        await Task.CompletedTask;
    }

    /// <summary>按当前 ManagerList 重建 TreeView 数据源（按 BackendId 分组），并尽量保留选中项。</summary>
    private void RebuildManagerTree()
    {
        var selectedId = SelectedManager?.ManagerId;

        var groups = new Dictionary<string, BackendGroupNode>(StringComparer.Ordinal);
        BackendGroupNode EnsureGroup(string bid)
        {
            if (!groups.TryGetValue(bid, out var g))
            {
                g = new BackendGroupNode
                {
                    BackendId = string.IsNullOrEmpty(bid) ? "未命名后端" : bid,
                    IsExpanded = true,
                };
                groups[bid] = g;
            }
            return g;
        }

        foreach (var item in ManagerList)
        {
            var bid = string.IsNullOrEmpty(item.BackendId) ? "" : item.BackendId;
            var g = EnsureGroup(bid);
            g.Children.Add(new ManagerItemNode(item));
        }

        // 按后端 ID 排序，稳定展示
        var ordered = groups.Values.OrderBy(g => g.BackendId, StringComparer.Ordinal).ToList();
        ManagerTree.ReplaceAll(ordered);

        // 恢复选中项：遍历树找到匹配的子节点
        if (!string.IsNullOrEmpty(selectedId))
        {
            ManagerItemNode? found = null;
            foreach (var g in ManagerTree)
                foreach (var c in g.Children)
                    if (c.Item.ManagerId == selectedId) { found = c; break; }
            if (found != null)
            {
                _selectedTreeNode = found;
                this.RaisePropertyChanged(nameof(SelectedTreeNode));
            }
        }
    }

    private void HandleManagerList(PMCSsE_Communicator.DataPacks.Pack_MCServerManagers mgrPack)
    {
        IEnumerable<MCServerManagerItemModel>? items = null;
        if (mgrPack.MCServerManagersData?.MCServerManagerDataList != null)
        {
            // 获取当前后端 ID（如果只有一个后端则直接使用）
            var defaultBackendId = _connection?.ConnectedBackends.FirstOrDefault()?.BackendId ?? "";

            items = mgrPack.MCServerManagersData.MCServerManagerDataList
                .Select(data => new MCServerManagerItemModel
                {
                    ManagerId = data.ManagerID,
                    MCServerName = data.ManagerID,
                    IsMCServerRunning = data.IsMCServerRunning,
                    IsLoaded = true,
                    BackendId = defaultBackendId,
                });
        }
        // 一次性批量替换：1 次 Reset 通知
        (ManagerList as RangeObservableCollection<MCServerManagerItemModel>)?.ReplaceAll(items);
        // 同步重建树状视图
        RebuildManagerTree();

        // 如果有选中的管理器，更新其引用到新列表中的对象，并检查状态变化
        if (SelectedManager != null && !string.IsNullOrEmpty(_selectedManagerId))
        {
            var newSelected = ManagerList.FirstOrDefault(m => m.ManagerId == _selectedManagerId);
            if (newSelected != null && !ReferenceEquals(newSelected, SelectedManager))
            {
                // 触发 setter 以检查运行状态
                SelectedManager = newSelected;
            }
        }

        if (_connection != null && _connection.IsConnected)
        {
            _connection.RequestBackend(RequestTypeEnum.GetMCServerManagersList);
        }
    }

    private void HandleManagerConfigs(PMCSsE_Communicator.DataPacks.Pack_MCServerManagerConfigs configsPack)
    {
        var configs = configsPack.MCServerManagerConfigs?.MCServerManagerConfigsList;
        if (configs == null) return;

        // 获取默认后端 ID
        var defaultBackendId = _connection?.ConnectedBackends.FirstOrDefault()?.BackendId ?? "";

        bool treeDirty = false;
        if (ManagerList.Count == 0)
        {
            // 兜底场景：列表为空时一次性批量追加，避免 N 次 Add 通知
            var items = configs.Select(cfg =>
            {
                var cfgName = cfg.MCServerName;
                if (string.IsNullOrEmpty(cfgName)) cfgName = cfg.ManagerID;
                return new MCServerManagerItemModel
                {
                    ManagerId = cfg.ManagerID,
                    MCServerName = cfgName,
                    IsMCServerRunning = false,
                    IsLoaded = false,
                    BackendId = defaultBackendId,
                };
            });
            (ManagerList as RangeObservableCollection<MCServerManagerItemModel>)?.AddRange(items);
            treeDirty = true;
        }
        else
        {
            var dict = configs.ToDictionary(c => c.ManagerID, StringComparer.Ordinal);
            foreach (var item in ManagerList)
            {
                if (dict.TryGetValue(item.ManagerId, out var cfg))
                {
                    var oldBid = item.BackendId;
                    var name = cfg.MCServerName;
                    if (!string.IsNullOrEmpty(name)) item.MCServerName = name;
                    // 始终确保 BackendId 已设置
                    if (string.IsNullOrEmpty(item.BackendId))
                        item.BackendId = defaultBackendId;
                    if (item.BackendId != oldBid) treeDirty = true;
                }
            }
        }
        if (treeDirty) RebuildManagerTree();
    }

    /// <summary>
    /// 处理后端返回的 MC 服务器日志包。
    /// 将日志解析为级别+文本，推入 Logger 全局 feed 供 LogView 显示。
    /// </summary>
    private void HandleMCServerLogs(Pack_MCServerLogs? pack)
    {
        if (pack == null || string.IsNullOrEmpty(pack.ManagerID)) return;
        if (!string.Equals(pack.ManagerID, _selectedManagerId, StringComparison.Ordinal))
            return;

        CancelTimeout($"{pack.ManagerID}:latest");
        CancelTimeout($"{pack.ManagerID}:older");

        if (pack.ResetHint)
        {
            // 后端要求重置：清空 LogView 显示并重置日志 ID 游标
            ClearLogs();
            _oldestLogId = ulong.MaxValue;
            _latestLogId = ulong.MinValue;
        }

        if (pack.Logs == null || pack.Logs.Length == 0)
        {
            if (IsLoading) IsLoading = false;
            if (!string.IsNullOrEmpty(_selectedManagerId))
                StartTimer();
            return;
        }

        bool isFirstLoad = _latestLogId == ulong.MinValue;
        bool isIncremental = pack.Logs[0].ID > _latestLogId;
        bool isHistory = pack.Logs[^1].ID < _oldestLogId;

        if (isFirstLoad)
        {
            _oldestLogId = pack.Logs[0].ID;
            _latestLogId = pack.Logs[^1].ID;
        }
        else if (isIncremental)
        {
            _latestLogId = pack.Logs[^1].ID;
        }
        else if (isHistory)
        {
            _oldestLogId = pack.Logs[0].ID;
        }

        // 将每条日志解析并推入 Logger feed
        foreach (var logEntry in pack.Logs)
        {
            var text = logEntry.Log ?? string.Empty;
            var level = ParseLogLevel(text);
            PushLogToFeed(text, level);
        }

        if (isFirstLoad)
        {
            OldestLogId = pack.Logs[0].ID;
            LatestLogId = pack.Logs[^1].ID;
        }
        else if (isIncremental)
        {
            LatestLogId = pack.Logs[^1].ID;
        }
        else if (isHistory)
        {
            OldestLogId = pack.Logs[0].ID;
        }

        HasLogs = LogCount > 0;
        IsLoading = false;
        IsEmpty = LogCount == 0;
        this.RaisePropertyChanged(nameof(CanGetOlderLogs));

        _retryCount[$"{pack.ManagerID}:latest"] = 0;
        _retryCount[$"{pack.ManagerID}:older"] = 0;

        if (!string.IsNullOrEmpty(_selectedManagerId))
            StartTimer();
    }

    // ============================================================
    //  内部：日志级别解析
    // ============================================================
    /// <summary>
    /// 从日志文本中解析日志级别。
    /// 先匹配 [INFO]/[WARN] 等级别标记，再回退到关键词检测。
    /// </summary>
    private LogType ParseLogLevel(string text)
    {
        var meLevel = MELogLevel.Information;
        var lvMatch = LevelRegex.Match(text);
        if (lvMatch.Success)
        {
            var raw = lvMatch.Groups[1].Value.ToUpperInvariant();
            meLevel = raw switch
            {
                "INFO" or "INF" => MELogLevel.Information,
                "WARN" or "WARNING" => MELogLevel.Warning,
                "ERROR" or "ERR" => MELogLevel.Error,
                "FATAL" or "CRITICAL" or "CRIT" or "SEVERE" => MELogLevel.Critical,
                "DEBUG" or "DBG" => MELogLevel.Debug,
                "TRACE" or "TRC" => MELogLevel.Trace,
                _ => MELogLevel.Information,
            };
        }
        else
        {
            if (text.Contains("Exception", StringComparison.Ordinal) || text.Contains("ERROR", StringComparison.Ordinal))
                meLevel = MELogLevel.Error;
            else if (text.Contains("warn", StringComparison.OrdinalIgnoreCase))
                meLevel = MELogLevel.Warning;
        }

        return meLevel switch
        {
            MELogLevel.Trace or MELogLevel.Debug => LogType.Debug,
            MELogLevel.Information => LogType.Info,
            MELogLevel.Warning => LogType.Warn,
            MELogLevel.Error => LogType.Error,
            MELogLevel.Critical => LogType.Fatal,
            MELogLevel.None => LogType.Debug,
            _ => LogType.Info,
        };
    }

    // ============================================================
    //  内部：工具
    // ============================================================
    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"");
        return needQuote ? $"\"{escaped}\"" : escaped;
    }

    private void ScheduleTimeout(string key, Action onTimeout, TimeSpan? timeout = null)
    {
        CancelTimeout(key);
        var cts = new CancellationTokenSource(timeout ?? RequestTimeout);
        _pendingRequests[key] = cts;
        var registration = default(CancellationTokenRegistration);
        registration = cts.Token.Register(() =>
        {
            // CancellationToken 回调在线程池线程上执行，而 onTimeout 通常会设置
            // IsLoading/HasError 等绑定到 ReactiveCommand.CanExecute 的属性，
            // 这些变更必须回到 UI 线程，否则 Button.Command 会抛跨线程异常。
            Dispatcher.UIThread.Post(() =>
            {
                try { onTimeout(); }
                finally { registration.Dispose(); cts.Dispose(); _pendingRequests.Remove(key); }
            });
        });
    }

    private void CancelTimeout(string key)
    {
        if (_pendingRequests.Remove(key, out var cts))
        {
            try { cts.Cancel(false); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    private bool HandleRetry(string managerId, string operation, Exception ex, Action retryAction)
    {
        var key = $"{managerId}:{operation}";
        var count = _retryCount.TryGetValue(key, out var c) ? c : 0;
        if (count < MaxRetries)
        {
            _retryCount[key] = count + 1;
            ShowToast("请求重试", $"第 {count + 1} 次重试中...", ToastType.Info);
            _ = Task.Run(async () =>
            {
                await Task.Delay(500 * (count + 1));
                Dispatcher.UIThread.Post(retryAction, DispatcherPriority.Background);
            });
            return true;
        }
        _retryCount[key] = 0;
        return false;
    }

    private void ShowError(string msg)
    {
        ErrorMessage = msg;
        HasError = true;
        IsLoading = false;
        ShowToast("日志控制台错误", msg, ToastType.Error);
    }
}
