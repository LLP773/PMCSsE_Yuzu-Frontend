using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using ReactiveUI;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

public class MCServerManagerLogConsoleViewModel : ViewModelBase
{
    private readonly DispatcherTimer _getNewerLogsTimer = new() { Interval = TimeSpan.FromMilliseconds(1000), IsEnabled = false };

    private string? _selectedManagerId;
    private MCServerManagerItemModel? _selectedManager;
    private bool _isLoading;
    private bool _hasError;
    private string _errorMessage = "";
    private string _filterText = "";
    private ulong _oldestLogId = ulong.MaxValue;
    private ulong _latestLogId = ulong.MinValue;
    private bool _canGetOlderLogs;

    public ObservableCollection<MCServerManagerItemModel> ManagerList { get; } = new();

    public ObservableCollection<LogEntryModel> LogEntries { get; } = new();

    public string? SelectedManagerId
    {
        get => _selectedManagerId;
        set
        {
            string? oldValue = _selectedManagerId;
            this.RaiseAndSetIfChanged(ref _selectedManagerId, value);
            if (oldValue != value)
            {
                this.RaiseAndSetIfChanged(ref _hasSelectedManager, !string.IsNullOrEmpty(value));
                if (!string.IsNullOrEmpty(value))
                {
                    LoadLogsForManager(value);
                }
                else
                {
                    ClearLogs();
                }
            }
        }
    }

    public bool HasSelectedManager
    {
        get => _hasSelectedManager;
        private set => this.RaiseAndSetIfChanged(ref _hasSelectedManager, value);
    }
    private bool _hasSelectedManager;

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            string oldValue = _filterText;
            this.RaiseAndSetIfChanged(ref _filterText, value);
            if (oldValue != value)
            {
                ApplyFilter();
            }
        }
    }

    public bool CanGetOlderLogs
    {
        get => _canGetOlderLogs;
        set => this.RaiseAndSetIfChanged(ref _canGetOlderLogs, value);
    }

    public bool HasFilter => !string.IsNullOrEmpty(_filterText);

    public bool IsEmpty => !_hasSelectedManager;

    public bool HasLogs => LogEntries.Count > 0;

    public MCServerManagerItemModel? SelectedManager
    {
        get => _selectedManager;
        set
        {
            MCServerManagerItemModel? oldValue = _selectedManager;
            this.RaiseAndSetIfChanged(ref _selectedManager, value);
            if (oldValue != value)
            {
                SelectedManagerId = value?.ManagerId;
            }
        }
    }

    public ObservableCollection<LogEntryModel> FilteredLogEntries { get; } = new();

    public MCServerManagerLogConsoleViewModel()
    {
        _getNewerLogsTimer.Tick += OnGetNewerLogsTick;
    }

    public void Initialize(ConnectionViewModel connectionViewModel)
    {
        connectionViewModel.DataPackReceived += OnDataPackReceived;
        connectionViewModel.Connected += OnConnected;
        connectionViewModel.Disconnected += OnDisconnected;
    }

    private void OnConnected()
    {
        RefreshManagerList();
    }

    private void OnDisconnected()
    {
        _getNewerLogsTimer.Stop();
        ManagerList.Clear();
        ClearLogs();
    }

    public void RefreshManagerList()
    {
        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        connection?.Client?.RequestBackend(PMCSsE_Communicator.RequestTypeEnum.GetMCServerManagersList, 
            new PMCSsE_Communicator.DataPacks.Pack_nothing.Pack_GetMCServerManagerConfigsList());
    }

    private void LoadLogsForManager(string managerId)
    {
        ClearLogs();
        IsLoading = true;
        HasError = false;

        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection?.Client == null)
        {
            ShowError("未连接到后端");
            return;
        }

        connection.Client.RequestBackend(PMCSsE_Communicator.RequestTypeEnum.GetLatestLogs,
            new Pack_GetLatestMCServerLogs(managerId, 200));

        _getNewerLogsTimer.Start();
    }

    private void ClearLogs()
    {
        _getNewerLogsTimer.Stop();
        LogEntries.Clear();
        FilteredLogEntries.Clear();
        _oldestLogId = ulong.MaxValue;
        _latestLogId = ulong.MinValue;
        CanGetOlderLogs = false;
        IsLoading = false;
        HasError = false;
    }

    private void OnGetNewerLogsTick(object? sender, EventArgs e)
    {
        _getNewerLogsTimer.Stop();

        if (string.IsNullOrEmpty(_selectedManagerId))
            return;

        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection?.Client == null)
            return;

        connection.Client.RequestBackend(PMCSsE_Communicator.RequestTypeEnum.GetNewerLogs,
            new Pack_GetNewerMCServerLogs(_selectedManagerId, _latestLogId, 200));

        _getNewerLogsTimer.Start();
    }

    public void GetOlderLogs()
    {
        if (string.IsNullOrEmpty(_selectedManagerId))
            return;

        if (_oldestLogId == 1)
        {
            ShowToast("没有更旧的日志了", ToastType.Info);
            return;
        }

        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection?.Client == null)
        {
            ShowError("未连接到后端");
            return;
        }

        connection.Client.RequestBackend(PMCSsE_Communicator.RequestTypeEnum.GetOlderLogs,
            new Pack_GetOlderMCServerLogs(_selectedManagerId, _oldestLogId, 200));
    }

    private void OnDataPackReceived(PMCSsE_Communicator.RespondTypeEnum responseType, object? data)
    {
        switch (responseType)
        {
            case PMCSsE_Communicator.RespondTypeEnum.MCServerManagerConfigs:
                HandleManagerConfigs(data);
                break;
            case PMCSsE_Communicator.RespondTypeEnum.MCServerLogs:
                HandleMCServerLogs(data);
                break;
            case PMCSsE_Communicator.RespondTypeEnum.ErrorInfo:
                HandleErrorInfo(data);
                break;
        }
    }

    private void HandleManagerConfigs(object? data)
    {
        if (data is not Pack_MCServerManagerConfigs pack)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            ManagerList.Clear();
            if (pack.MCServerManagerConfigs?.MCServerManagerConfigsList != null)
            {
                foreach (var config in pack.MCServerManagerConfigs.MCServerManagerConfigsList)
                {
                    ManagerList.Add(new MCServerManagerItemModel(config));
                }
            }
        });
    }

    private void HandleMCServerLogs(object? data)
    {
        if (data is not PMCSsE_Communicator.DataPacks.Pack_MCServerLogs pack)
            return;

        if (pack.ManagerID != _selectedManagerId)
            return;

        if (pack.ResetHint)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ClearLogs();
                IsLoading = true;
            });
            return;
        }

        if (pack.Logs == null || pack.Logs.Length == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = false;
            });
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = false;

            if (_latestLogId == ulong.MinValue)
            {
                _oldestLogId = pack.Logs[0].ID;
                _latestLogId = pack.Logs[^1].ID;
                CanGetOlderLogs = true;
            }
            else if (pack.Logs[0].ID > _latestLogId)
            {
                _latestLogId = pack.Logs[^1].ID;

                if (LogEntries.Count > 30000)
                {
                    int removeCount = LogEntries.Count - 30000;
                    for (int i = 0; i < removeCount; i++)
                    {
                        LogEntries.RemoveAt(0);
                    }
                }
            }
            else if (pack.Logs[^1].ID < _oldestLogId)
            {
                _oldestLogId = pack.Logs[0].ID;
            }

            foreach (var log in pack.Logs)
            {
                var entry = new LogEntryModel { Id = log.ID, Content = log.Log, Timestamp = DateTime.Now };
                if (pack.Logs[0].ID < _latestLogId)
                {
                    LogEntries.Insert(0, entry);
                }
                else
                {
                    LogEntries.Add(entry);
                }
            }

            ApplyFilter();
        });
    }

    private void HandleErrorInfo(object? data)
    {
        if (data is not PMCSsE_Communicator.DataPacks.Pack_StringOnly.Pack_ErrorInfo pack)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            ShowError(pack.ErrorInfo ?? "未知错误");
        });
    }

    private void ApplyFilter()
    {
        FilteredLogEntries.Clear();

        if (string.IsNullOrEmpty(_filterText))
        {
            foreach (var entry in LogEntries)
            {
                FilteredLogEntries.Add(entry);
            }
        }
        else
        {
            var filter = _filterText.ToLower();
            foreach (var entry in LogEntries.Where(e => 
                e.Content.ToLower().Contains(filter) || 
                e.TimeText.ToLower().Contains(filter)))
            {
                FilteredLogEntries.Add(entry);
            }
        }
    }

    private void ShowError(string message)
    {
        HasError = true;
        ErrorMessage = message;
        IsLoading = false;
        _getNewerLogsTimer.Stop();
        ShowToast(message, ToastType.Error);
    }

    public void ClearFilter()
    {
        FilterText = "";
    }

    public void ClearLogsCommand()
    {
        ClearLogs();
    }
}