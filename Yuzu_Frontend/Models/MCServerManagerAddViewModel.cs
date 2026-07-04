using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using ReactiveUI;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

public class MCServerManagerAddViewModel : ViewModelBase
{
    private ConnectionViewModel? _connection;
    
    private string _mcServerName = "";
    private string _mcServerType = "Vanilla";
    private string _mcServerDirectory = "";
    private string _javaPath = "";
    private string _startUpArguments = "";
    private bool _isSubmitting;
    private bool _hasError;
    private string _errorMessage = "";
    private bool _isCreating;

    private readonly ObservableCollection<string> _supportedTypes = [];

    public ConnectionViewModel? Connection
    {
        get => _connection;
        set => this.RaiseAndSetIfChanged(ref _connection, value);
    }

    public ObservableCollection<string> SupportedTypes => _supportedTypes;

    public string MCServerName
    {
        get => _mcServerName;
        set => this.RaiseAndSetIfChanged(ref _mcServerName, value);
    }

    public string MCServerType
    {
        get => _mcServerType;
        set => this.RaiseAndSetIfChanged(ref _mcServerType, value);
    }

    public string MCServerDirectory
    {
        get => _mcServerDirectory;
        set => this.RaiseAndSetIfChanged(ref _mcServerDirectory, value);
    }

    public string JavaPath
    {
        get => _javaPath;
        set => this.RaiseAndSetIfChanged(ref _javaPath, value);
    }

    public string StartUpArguments
    {
        get => _startUpArguments;
        set => this.RaiseAndSetIfChanged(ref _startUpArguments, value);
    }

    public bool IsSubmitting
    {
        get => _isSubmitting;
        set => this.RaiseAndSetIfChanged(ref _isSubmitting, value);
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

    public bool IsCreating
    {
        get => _isCreating;
        set => this.RaiseAndSetIfChanged(ref _isCreating, value);
    }

    public bool CanSubmit => 
        !IsSubmitting && 
        !string.IsNullOrWhiteSpace(MCServerName) && 
        !string.IsNullOrWhiteSpace(MCServerDirectory) && 
        !string.IsNullOrWhiteSpace(JavaPath) &&
        Connection?.IsConnected == true;

    public void Initialize(ConnectionViewModel connectionViewModel)
    {
        Connection = connectionViewModel;

        if (Connection != null)
        {
            Connection.Connected += OnConnectionConnected;
            Connection.Disconnected += OnConnectionDisconnected;
        }
    }

    private void OnConnectionConnected()
    {
        LoadSupportedTypes();
        ResetForm();
    }

    private void OnConnectionDisconnected()
    {
        HasError = true;
        ErrorMessage = "请先连接到后端";
        IsSubmitting = false;
    }

    public void SubscribeToDataPacks()
    {
        if (Connection?.Client != null)
        {
            Connection.Client.DataPackBus.Subscribe<Pack_SupportedMCServerTypes>(OnSupportedTypesReceived);
            Connection.Client.DataPackBus.Subscribe<Pack_CreatedNewMCServerManager>(OnCreatedNewManager);
            Connection.Client.DataPackBus.Subscribe<Pack_ModifiedMCServerManagerConfig>(OnModifiedManagerConfig);
            Connection.Client.DataPackBus.Subscribe<Pack_CreatNewMCServerManagerFailed>(OnCreateManagerFailed);
            Connection.Client.DataPackBus.Subscribe<Pack_ErrorInfo>(OnErrorReceived);
        }
    }

    public void UnsubscribeFromDataPacks()
    {
        if (Connection?.Client != null)
        {
            Connection.Client.DataPackBus.Unsubscribe<Pack_SupportedMCServerTypes>(OnSupportedTypesReceived);
            Connection.Client.DataPackBus.Unsubscribe<Pack_CreatedNewMCServerManager>(OnCreatedNewManager);
            Connection.Client.DataPackBus.Unsubscribe<Pack_ModifiedMCServerManagerConfig>(OnModifiedManagerConfig);
            Connection.Client.DataPackBus.Unsubscribe<Pack_CreatNewMCServerManagerFailed>(OnCreateManagerFailed);
            Connection.Client.DataPackBus.Unsubscribe<Pack_ErrorInfo>(OnErrorReceived);
        }
    }

    private void OnSupportedTypesReceived(Pack_SupportedMCServerTypes pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _supportedTypes.Clear();
            foreach (var type in pack.SupportedMCServerTypes)
            {
                _supportedTypes.Add(type);
            }

            if (_supportedTypes.Count > 0 && !_supportedTypes.Contains(MCServerType))
            {
                MCServerType = _supportedTypes[0];
            }
        });
    }

    private void OnCreatedNewManager(Pack_CreatedNewMCServerManager pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = false;

            var config = pack.MCServerManagerConfig;
            MCServerName = config.MCServerName;
            MCServerType = config.MCServerType;
            MCServerDirectory = config.MCServerDirectory;
            JavaPath = config.JavaPath;
            StartUpArguments = config.StartUpArguments;

            IsCreating = false;

            ShowToast("创建成功", $"管理器 {config.ManagerID} 已创建", ToastType.Success);
        });
    }

    private void OnModifiedManagerConfig(Pack_ModifiedMCServerManagerConfig pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = false;

            var config = pack.MCServerManagerConfig;
            MCServerName = config.MCServerName;
            MCServerType = config.MCServerType;
            MCServerDirectory = config.MCServerDirectory;
            JavaPath = config.JavaPath;
            StartUpArguments = config.StartUpArguments;

            ShowToast("修改成功", $"管理器 {config.ManagerID} 配置已更新", ToastType.Success);
        });
    }

    private void OnCreateManagerFailed(Pack_CreatNewMCServerManagerFailed _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = true;
            ErrorMessage = "创建管理器失败";
            IsCreating = false;
            ShowToast("创建失败", ErrorMessage, ToastType.Error);
        });
    }

    private void OnErrorReceived(Pack_ErrorInfo pack)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSubmitting = false;
            HasError = true;
            ErrorMessage = pack.ErrorInfo ?? "未知错误";
            IsCreating = false;
            ShowToast("错误", ErrorMessage, ToastType.Error);
        });
    }

    public void LoadSupportedTypes()
    {
        if (Connection?.Client == null || !Connection.IsConnected)
            return;

        try
        {
            Connection.Client.RequestBackend(RequestTypeEnum.GetSupportedMCServerTypes, new Pack_GetSupportedMCServerTypes());
        }
        catch (Exception ex)
        {
            ShowToast("加载失败", $"获取支持的服务端类型失败: {ex.Message}", ToastType.Error);
        }
    }

    public void CreateNewManager()
    {
        if (!ValidateForm())
            return;

        IsCreating = true;
        IsSubmitting = true;
        HasError = false;

        if (Connection?.Client == null || !Connection.IsConnected)
        {
            HasError = true;
            ErrorMessage = "未连接到后端";
            IsSubmitting = false;
            IsCreating = false;
            return;
        }

        try
        {
            Connection.Client.RequestBackend(RequestTypeEnum.CreatNewMCServerManager, new Pack_CreatNewMCServerManager());
            ShowToast("创建中", "正在创建新的MC服务端管理器...", ToastType.Info);
        }
        catch (Exception ex)
        {
            IsSubmitting = false;
            IsCreating = false;
            HasError = true;
            ErrorMessage = $"创建失败: {ex.Message}";
            ShowToast("创建失败", ex.Message, ToastType.Error);
        }
    }

    public void ModifyManagerConfig(string managerId)
    {
        if (!ValidateForm())
            return;

        IsSubmitting = true;
        HasError = false;

        if (Connection?.Client == null || !Connection.IsConnected)
        {
            HasError = true;
            ErrorMessage = "未连接到后端";
            IsSubmitting = false;
            return;
        }

        var config = new MCServerManagerConfig
        {
            ManagerID = managerId,
            MCServerName = MCServerName,
            MCServerType = MCServerType,
            MCServerDirectory = MCServerDirectory,
            JavaPath = JavaPath,
            StartUpArguments = StartUpArguments
        };

        try
        {
            Connection.Client.RequestBackend(RequestTypeEnum.ModifyMCServerManagerConfig, new Pack_ModifyMCServerManagerConfig(config));
            ShowToast("提交中", "正在提交配置修改...", ToastType.Info);
        }
        catch (Exception ex)
        {
            IsSubmitting = false;
            HasError = true;
            ErrorMessage = $"提交失败: {ex.Message}";
            ShowToast("提交失败", ex.Message, ToastType.Error);
        }
    }

    private bool ValidateForm()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(MCServerName))
            errors.Add("服务端名称不能为空");

        if (string.IsNullOrWhiteSpace(MCServerDirectory))
            errors.Add("服务端目录不能为空");

        if (string.IsNullOrWhiteSpace(JavaPath))
            errors.Add("Java路径不能为空");

        if (errors.Any())
        {
            HasError = true;
            ErrorMessage = string.Join("\n", errors);
            ShowToast("验证失败", ErrorMessage, ToastType.Warning);
            return false;
        }

        HasError = false;
        return true;
    }

    public void ResetForm()
    {
        MCServerName = "";
        MCServerType = _supportedTypes.Count > 0 ? _supportedTypes[0] : "Vanilla";
        MCServerDirectory = "";
        JavaPath = "";
        StartUpArguments = "";
        HasError = false;
        ErrorMessage = "";
        IsSubmitting = false;
        IsCreating = false;
    }

    public void UpdateSubmitState()
    {
        this.RaisePropertyChanged(nameof(CanSubmit));
    }
}