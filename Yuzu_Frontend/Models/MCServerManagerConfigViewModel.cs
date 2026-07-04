using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using PMCSsE_Communicator.DataPacks.Pack_nothing;
using PMCSsE_Communicator.DataPacks.Pack_StringOnly;
using ReactiveUI;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

/// <summary>
/// MC 服务端管理器配置视图模型。
/// 负责管理器列表的加载与选中、配置的编辑/克隆/变更检测/验证/保存，
/// 以及备份排除项的管理。通过 FormModel 代理 PropertyGrid 的双向绑定。
/// </summary>
public class MCServerManagerConfigViewModel : ViewModelBase
{
    private MCServerManagerConfig? _originalConfig;   // 原始配置快照，用于变更检测
    private MCServerManagerConfig? _editingConfig;    // 当前编辑中的配置实例
    private string? _managerId;                      // 当前管理器 ID
    private bool _hasChanges;                        // 是否存在未保存的修改
    private bool _isSaving;                          // 是否正在保存
    private string _activeSection = "基本设置";       // 当前激活的配置分区
    private bool _isLoading;                         // 是否正在加载配置
    private bool _hasError;                          // 是否存在错误
    private string _errorMessage = "";              // 错误信息
    private string _currentBackendId = "";          // 当前加载列表所用后端 ID
    private bool _awaitingModifyConfirmAfterSave;    // SaveConfig 后等待后端 ModifiedMCServerManagerConfig 响应
    private bool _awaitingCreateThenModifyAfterSave; // SaveConfig 新管理器场景：等待 CreatedNewMCServerManager 后再发 Modify

    private RangeObservableCollection<MCServerManagerItemModel> _managers = new();  // 管理器列表（支持批量 ReplaceAll，避免列表 Clear+Add 的 N 次通知）
    private MCServerManagerItemModel? _selectedManager;  // 当前选中的管理器

    private RangeObservableCollection<string> _excludedFilesList = new();              // 排除的文件列表
    private RangeObservableCollection<string> _excludedFileExtensionsList = new();      // 排除的文件扩展名列表
    private RangeObservableCollection<string> _excludedFoldersList = new();            // 排除的文件夹列表

    private ConnectionBackendEntry? _selectedBackend;     // 当前选中的后端
    private NativeClient? _subscribedManagersClient;      // 已订阅管理器列表事件的客户端
    private Action<Pack_MCServerManagerConfigs>? _managersSubscriptionDelegate;  // 管理器列表订阅的委托（用于精确取消订阅）
    private NativeClient? _subscribedConfigClient;        // 已订阅配置事件的客户端
    private ConnectionViewModel? _connection;             // 关联的连接视图模型
    private ConnectionViewModel? _subscribedConnection;    // 已订阅全局数据包事件的连接视图模型

    /// <summary>LoadManagers 网络请求冷却时间（10 秒），防止页面快速切换导致的请求风暴。</summary>
    private const int LoadManagersCooldownSeconds = 10;
    /// <summary>按 backendId 记录最后一次网络请求时间，用于 Cooldown 控制。</summary>
    private readonly Dictionary<string, DateTime> _lastLoadManagersUtc = new();

    private MCServerManagerConfigFormModel? _formModel;

    /// <summary>
    /// 供 PropertyGrid 渲染的表单模型，代理到 _editingConfig。
    /// 由 Initialize/ClearConfig/CancelChanges 维护引用同步；
    /// 使用可绑定属性（RaiseAndSetIfChanged）确保每次替换实例时，
    /// XAML 中 <c>PropertyGrid.Item="{Binding FormModel}"</c> 绑定会重新回调
    /// <c>YuzuPropertyGrid.SetItem</c>，强制重建 InstanceViewModel/Categories，
    /// 避免因同一对象引用复用导致分类区不刷新的"空 PropertyGrid"假象。
    /// </summary>
    public MCServerManagerConfigFormModel? FormModel
    {
        get => _formModel;
        private set => this.RaiseAndSetIfChanged(ref _formModel, value);
    }

    /// <summary>构造函数，创建初始 FormModel 并绑定变更回调。</summary>
    public MCServerManagerConfigViewModel()
    {
        AttachFormModel(new MCServerManagerConfigFormModel());
    }

    /// <summary>
    /// 将指定 FormModel 绑定到当前 ViewModel：挂接 ConfigChanged 事件并赋值给 FormModel 属性。
    /// </summary>
    /// <param name="formModel">新的表单模型实例。</param>
    private void AttachFormModel(MCServerManagerConfigFormModel formModel)
    {
        formModel.ConfigChanged -= OnFormModelChanged;
        formModel.ConfigChanged += OnFormModelChanged;
        FormModel = formModel;
    }

    /// <summary>
    /// FormModel 属性变化回调：触发变更检测以更新 HasChanges。
    /// PropertyGrid 的双向绑定已保证在 UI 线程触发，无需额外 Dispatcher 调度。
    /// </summary>
    private void OnFormModelChanged()
    {
        CheckForChanges();
    }

    /// <summary>配置页分区标题列表。</summary>
    public ObservableCollection<string> SectionHeaders { get; } = new()
    {
        "基本设置",
        "备份设置",
        "备份排除",
        "远程备份",
        "在线聊天"
    };

    /// <summary>当前管理器 ID。</summary>
    public string? ManagerId
    {
        get => _managerId;
        set => this.RaiseAndSetIfChanged(ref _managerId, value);
    }

    /// <summary>管理器列表。</summary>
    public ObservableCollection<MCServerManagerItemModel> Managers => _managers;

    /// <summary>
    /// 当前可用的已连接后端列表（由外部页面初始化时设置）。
    /// 使用 RangeObservableCollection 以支持 ReplaceAll/AddRange 批量更新，避免 N 次通知。
    /// </summary>
    public ObservableCollection<ConnectionBackendEntry> AvailableBackends { get; } = new RangeObservableCollection<ConnectionBackendEntry>();

    /// <summary>
    /// 当前选中的后端。切换后端会自动触发该后端的管理器列表加载。
    /// </summary>
    public ConnectionBackendEntry? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            bool changed = !ReferenceEquals(_selectedBackend, value);
            this.RaiseAndSetIfChanged(ref _selectedBackend, value);
            if (changed)
            {
                this.RaisePropertyChanged(nameof(HasAvailableBackend));
                OnSelectedBackendChanged();
            }
        }
    }

    /// <summary>是否存在可用的后端连接。</summary>
    public bool HasAvailableBackend => _selectedBackend != null;

    /// <summary>
    /// 当前选中的管理器。切换时：
    ///   1. 先尝试从 GlobalCache 缓存加载配置（毫秒级响应）；
    ///   2. 缓存未命中或后端已连接时，再发起网络请求加载最新配置（兜底与校验）。
    /// 取消选中则清空编辑区。
    /// </summary>
    public MCServerManagerItemModel? SelectedManager
    {
        get => _selectedManager;
        set
        {
            bool changed = !ReferenceEquals(_selectedManager, value);
            this.RaiseAndSetIfChanged(ref _selectedManager, value);
            if (changed)
            {
                this.RaisePropertyChanged(nameof(IsManagerSelected));
                this.RaisePropertyChanged(nameof(IsNoManagerSelected));
                if (value != null)
                {
                    string backendId = value.BackendId
                                       ?? _selectedBackend?.BackendId
                                       ?? "";
                    // 第 1 步：先从缓存加载（立即可见，编辑区马上填充值）
                    bool cachedOk = !string.IsNullOrEmpty(backendId)
                                 && LoadConfigFromCache(backendId, value.ManagerId);

                    // 第 2 步：缓存没命中 或 后端仍处于连接状态 → 发起网络请求
                    // （即使缓存命中，网络响应也会用最新数据覆盖，保证一致性）
                    var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
                    bool needNetwork = !cachedOk
                                       || (!string.IsNullOrEmpty(backendId)
                                           && connection?.GetClientByBackendId(backendId) != null);
                    if (needNetwork)
                    {
                        LoadConfigForManager(value.ManagerId);
                    }
                }
                else
                {
                    ClearConfig();
                }
            }
        }
    }

    /// <summary>是否未选中管理器。</summary>
    public bool IsNoManagerSelected => _selectedManager == null;

    /// <summary>是否已选中管理器。</summary>
    public bool IsManagerSelected => _selectedManager != null;

    /// <summary>是否正在加载配置。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    /// <summary>是否存在错误。</summary>
    public bool HasError
    {
        get => _hasError;
        set => this.RaiseAndSetIfChanged(ref _hasError, value);
    }

    /// <summary>错误信息。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>是否存在未保存的修改。</summary>
    public bool HasChanges
    {
        get => _hasChanges;
        set
        {
            this.RaiseAndSetIfChanged(ref _hasChanges, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>是否正在保存配置。</summary>
    public bool IsSaving
    {
        get => _isSaving;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSaving, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>是否允许保存（有修改且非保存中）。</summary>
    public bool CanSave => HasChanges && !IsSaving;

    /// <summary>当前激活的配置分区名称。</summary>
    public string ActiveSection
    {
        get => _activeSection;
        set => this.RaiseAndSetIfChanged(ref _activeSection, value);
    }

    #region 基本设置
    public string MCServerName
    {
        get => _editingConfig?.MCServerName ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.MCServerName != value)
            {
                _editingConfig.MCServerName = value;
                this.RaisePropertyChanged(nameof(MCServerName));
                CheckForChanges();
            }
        }
    }

    public string MCServerType
    {
        get => _editingConfig?.MCServerType ?? "Vanilla";
        set
        {
            if (_editingConfig != null && _editingConfig.MCServerType != value)
            {
                _editingConfig.MCServerType = value;
                this.RaisePropertyChanged(nameof(MCServerType));
                CheckForChanges();
            }
        }
    }

    public string MCServerDirectory
    {
        get => _editingConfig?.MCServerDirectory ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.MCServerDirectory != value)
            {
                _editingConfig.MCServerDirectory = value;
                this.RaisePropertyChanged(nameof(MCServerDirectory));
                CheckForChanges();
            }
        }
    }

    public string JavaPath
    {
        get => _editingConfig?.JavaPath ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.JavaPath != value)
            {
                _editingConfig.JavaPath = value;
                this.RaisePropertyChanged(nameof(JavaPath));
                CheckForChanges();
            }
        }
    }

    public string StartUpArguments
    {
        get => _editingConfig?.StartUpArguments ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.StartUpArguments != value)
            {
                _editingConfig.StartUpArguments = value;
                this.RaisePropertyChanged(nameof(StartUpArguments));
                CheckForChanges();
            }
        }
    }
    #endregion

    #region 备份设置
    public bool AutoBackupEnabled
    {
        get => _editingConfig?.BackupManagerConfig.AutoBackupEnabled ?? false;
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.AutoBackupEnabled != value)
            {
                _editingConfig.BackupManagerConfig.AutoBackupEnabled = value;
                this.RaisePropertyChanged(nameof(AutoBackupEnabled));
                CheckForChanges();
            }
        }
    }

    public int BackupTimingModeIndex
    {
        get => (int)(_editingConfig?.BackupManagerConfig.BackupTimingMode ?? BackupTimingMode.DayInterval_SpecificTime);
        set
        {
            if (_editingConfig != null)
            {
                var mode = (BackupTimingMode)value;
                if (_editingConfig.BackupManagerConfig.BackupTimingMode != mode)
                {
                    _editingConfig.BackupManagerConfig.BackupTimingMode = mode;
                    this.RaisePropertyChanged(nameof(BackupTimingModeIndex));
                    this.RaisePropertyChanged(nameof(BackupTimingMode));
                    CheckForChanges();
                }
            }
        }
    }

    public BackupTimingMode BackupTimingMode
    {
        get => _editingConfig?.BackupManagerConfig.BackupTimingMode ?? BackupTimingMode.DayInterval_SpecificTime;
    }

    public string DayInterval
    {
        get => _editingConfig?.BackupManagerConfig.DayInterval ?? "1";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.DayInterval != value)
            {
                _editingConfig.BackupManagerConfig.DayInterval = value;
                this.RaisePropertyChanged(nameof(DayInterval));
                CheckForChanges();
            }
        }
    }

    public string SpecificTime
    {
        get => _editingConfig?.BackupManagerConfig.SpecificTime ?? "04:00:00";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SpecificTime != value)
            {
                _editingConfig.BackupManagerConfig.SpecificTime = value;
                this.RaisePropertyChanged(nameof(SpecificTime));
                CheckForChanges();
            }
        }
    }

    public string TimeInterval
    {
        get => _editingConfig?.BackupManagerConfig.TimeInterval ?? "04:00:00";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.TimeInterval != value)
            {
                _editingConfig.BackupManagerConfig.TimeInterval = value;
                this.RaisePropertyChanged(nameof(TimeInterval));
                CheckForChanges();
            }
        }
    }

    public bool StopServerBeforeBackup
    {
        get => _editingConfig?.BackupManagerConfig.StopServerBeforeBackup ?? false;
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.StopServerBeforeBackup != value)
            {
                _editingConfig.BackupManagerConfig.StopServerBeforeBackup = value;
                this.RaisePropertyChanged(nameof(StopServerBeforeBackup));
                CheckForChanges();
            }
        }
    }

    public int BackupModeIndex
    {
        get => (int)(_editingConfig?.BackupManagerConfig.BackupMode ?? BackupMode.Full);
        set
        {
            if (_editingConfig != null)
            {
                var mode = (BackupMode)value;
                if (_editingConfig.BackupManagerConfig.BackupMode != mode)
                {
                    _editingConfig.BackupManagerConfig.BackupMode = mode;
                    this.RaisePropertyChanged(nameof(BackupModeIndex));
                    this.RaisePropertyChanged(nameof(BackupMode));
                    CheckForChanges();
                }
            }
        }
    }

    public BackupMode BackupMode
    {
        get => _editingConfig?.BackupManagerConfig.BackupMode ?? BackupMode.Full;
    }

    public string CompactionLevel
    {
        get => _editingConfig?.BackupManagerConfig.CompactionLevel ?? "0";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.CompactionLevel != value)
            {
                _editingConfig.BackupManagerConfig.CompactionLevel = value;
                this.RaisePropertyChanged(nameof(CompactionLevel));
                CheckForChanges();
            }
        }
    }
    #endregion

    #region 备份排除
    public ObservableCollection<string> ExcludedFilesList
    {
        get => _excludedFilesList;
    }

    public ObservableCollection<string> ExcludedFileExtensionsList
    {
        get => _excludedFileExtensionsList;
    }

    public ObservableCollection<string> ExcludedFoldersList
    {
        get => _excludedFoldersList;
    }

    public string NewExcludedFile { get; set; } = "";
    public string NewExcludedExtension { get; set; } = "";
    public string NewExcludedFolder { get; set; } = "";
    #endregion

    #region 远程备份
    public string BackupFileOutputDirectory
    {
        get => _editingConfig?.BackupManagerConfig.BackupFileOutputDirectory ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.BackupFileOutputDirectory != value)
            {
                _editingConfig.BackupManagerConfig.BackupFileOutputDirectory = value;
                this.RaisePropertyChanged(nameof(BackupFileOutputDirectory));
                CheckForChanges();
            }
        }
    }

    public string RemoteBackupFileStoreDirectory
    {
        get => _editingConfig?.BackupManagerConfig.RemoteBackupFileStoreDirectory ?? "/";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.RemoteBackupFileStoreDirectory != value)
            {
                _editingConfig.BackupManagerConfig.RemoteBackupFileStoreDirectory = value;
                this.RaisePropertyChanged(nameof(RemoteBackupFileStoreDirectory));
                CheckForChanges();
            }
        }
    }

    public bool SFTPEnabled
    {
        get => _editingConfig?.BackupManagerConfig.SFTPClientConfig.Enabled ?? false;
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SFTPClientConfig.Enabled != value)
            {
                _editingConfig.BackupManagerConfig.SFTPClientConfig.Enabled = value;
                this.RaisePropertyChanged(nameof(SFTPEnabled));
                CheckForChanges();
            }
        }
    }

    public string SFTPHost
    {
        get => _editingConfig?.BackupManagerConfig.SFTPClientConfig.Host ?? "127.0.0.1";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SFTPClientConfig.Host != value)
            {
                _editingConfig.BackupManagerConfig.SFTPClientConfig.Host = value;
                this.RaisePropertyChanged(nameof(SFTPHost));
                CheckForChanges();
            }
        }
    }

    public int SFTPPort
    {
        get => _editingConfig?.BackupManagerConfig.SFTPClientConfig.Port ?? 22;
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SFTPClientConfig.Port != value)
            {
                _editingConfig.BackupManagerConfig.SFTPClientConfig.Port = value;
                this.RaisePropertyChanged(nameof(SFTPPort));
                CheckForChanges();
            }
        }
    }

    public string SFTPUserName
    {
        get => _editingConfig?.BackupManagerConfig.SFTPClientConfig.UserName ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SFTPClientConfig.UserName != value)
            {
                _editingConfig.BackupManagerConfig.SFTPClientConfig.UserName = value;
                this.RaisePropertyChanged(nameof(SFTPUserName));
                CheckForChanges();
            }
        }
    }

    public string SFTPPassword
    {
        get => _editingConfig?.BackupManagerConfig.SFTPClientConfig.Password ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SFTPClientConfig.Password != value)
            {
                _editingConfig.BackupManagerConfig.SFTPClientConfig.Password = value;
                this.RaisePropertyChanged(nameof(SFTPPassword));
                CheckForChanges();
            }
        }
    }

    public int SFTPBufferSize
    {
        get => _editingConfig?.BackupManagerConfig.SFTPClientConfig.BufferSize ?? 1;
        set
        {
            if (_editingConfig != null && _editingConfig.BackupManagerConfig.SFTPClientConfig.BufferSize != value)
            {
                _editingConfig.BackupManagerConfig.SFTPClientConfig.BufferSize = value;
                this.RaisePropertyChanged(nameof(SFTPBufferSize));
                CheckForChanges();
            }
        }
    }
    #endregion

    #region 在线聊天
    public string ServerPort
    {
        get => _editingConfig?.OnlineChattingSystemConfig.ServerPort ?? "8080";
        set
        {
            if (_editingConfig != null && _editingConfig.OnlineChattingSystemConfig.ServerPort != value)
            {
                _editingConfig.OnlineChattingSystemConfig.ServerPort = value;
                this.RaisePropertyChanged(nameof(ServerPort));
                CheckForChanges();
            }
        }
    }

    public string ThirdPartySocialPlatformName
    {
        get => _editingConfig?.OnlineChattingSystemConfig.ThirdPartySocialPlatformName ?? "";
        set
        {
            if (_editingConfig != null && _editingConfig.OnlineChattingSystemConfig.ThirdPartySocialPlatformName != value)
            {
                _editingConfig.OnlineChattingSystemConfig.ThirdPartySocialPlatformName = value;
                this.RaisePropertyChanged(nameof(ThirdPartySocialPlatformName));
                CheckForChanges();
            }
        }
    }
    #endregion

    /// <summary>
    /// 使用指定配置初始化编辑环境。深拷贝原始配置与编辑配置，重置排除项列表并同步 FormModel。
    /// </summary>
    /// <param name="config">后端返回的管理器配置。</param>
    public void Initialize(MCServerManagerConfig config)
    {
        _originalConfig = CloneConfig(config);
        _editingConfig = CloneConfig(config);
        ManagerId = config.ManagerID;
        HasChanges = false;

        _excludedFilesList = new RangeObservableCollection<string>();
        _excludedFilesList.AddRange(config.BackupManagerConfig.ExcludedFilesList ?? new List<string>());
        _excludedFileExtensionsList = new RangeObservableCollection<string>();
        _excludedFileExtensionsList.AddRange(config.BackupManagerConfig.ExcludedFileExtensionsList ?? new List<string>());
        _excludedFoldersList = new RangeObservableCollection<string>();
        _excludedFoldersList.AddRange(config.BackupManagerConfig.ExcludedFoldersList ?? new List<string>());

        this.RaisePropertyChanged(nameof(ExcludedFilesList));
        this.RaisePropertyChanged(nameof(ExcludedFileExtensionsList));
        this.RaisePropertyChanged(nameof(ExcludedFoldersList));

        // 每次 Initialize 都使用全新的 FormModel 实例并把编辑配置写进其中，
        // 然后通过 AttachFormModel 触发 FormModel 属性变更，
        // 强制 XAML 中 <ctrls:YuzuPropertyGrid Item="{Binding FormModel}">
        // 重新回调 SetItem 并重建 Categories，避免编辑区显示为空白。
        var newFormModel = new MCServerManagerConfigFormModel(_editingConfig);
        AttachFormModel(newFormModel);

        // 触发 ViewModel 所有属性的 PropertyChanged，确保直接绑定 ViewModel 属性的 UI 控件也能更新
        RefreshProperties();
    }

    /// <summary>
    /// 深拷贝配置实例，包括备份管理器配置、SFTP 配置和在线聊天配置及其子列表。
    /// </summary>
    /// <param name="config">源配置。</param>
    /// <returns>深拷贝后的新配置实例。</returns>
    private MCServerManagerConfig CloneConfig(MCServerManagerConfig config)
    {
        return new MCServerManagerConfig
        {
            ManagerID = config.ManagerID,
            MCServerName = config.MCServerName,
            MCServerType = config.MCServerType,
            MCServerDirectory = config.MCServerDirectory,
            JavaPath = config.JavaPath,
            StartUpArguments = config.StartUpArguments,
            BackupManagerConfig = new BackupManagerConfig
            {
                AutoBackupEnabled = config.BackupManagerConfig.AutoBackupEnabled,
                BackupTimingMode = config.BackupManagerConfig.BackupTimingMode,
                DayInterval = config.BackupManagerConfig.DayInterval,
                SpecificTime = config.BackupManagerConfig.SpecificTime,
                TimeInterval = config.BackupManagerConfig.TimeInterval,
                StopServerBeforeBackup = config.BackupManagerConfig.StopServerBeforeBackup,
                BackupMode = config.BackupManagerConfig.BackupMode,
                CompactionLevel = config.BackupManagerConfig.CompactionLevel,
                ExcludedFilesList = new(config.BackupManagerConfig.ExcludedFilesList ?? new List<string>()),
                ExcludedFileExtensionsList = new(config.BackupManagerConfig.ExcludedFileExtensionsList ?? new List<string>()),
                ExcludedFoldersList = new(config.BackupManagerConfig.ExcludedFoldersList ?? new List<string>()),
                BackupFileOutputDirectory = config.BackupManagerConfig.BackupFileOutputDirectory,
                RemoteBackupFileStoreDirectory = config.BackupManagerConfig.RemoteBackupFileStoreDirectory,
                SFTPClientConfig = new SFTPClientConfig
                {
                    Enabled = config.BackupManagerConfig.SFTPClientConfig.Enabled,
                    Host = config.BackupManagerConfig.SFTPClientConfig.Host,
                    Port = config.BackupManagerConfig.SFTPClientConfig.Port,
                    UserName = config.BackupManagerConfig.SFTPClientConfig.UserName,
                    Password = config.BackupManagerConfig.SFTPClientConfig.Password,
                    BufferSize = config.BackupManagerConfig.SFTPClientConfig.BufferSize
                }
            },
            OnlineChattingSystemConfig = new OnlineChattingSystemConfig
            {
                ServerPort = config.OnlineChattingSystemConfig.ServerPort,
                ThirdPartySocialPlatformName = config.OnlineChattingSystemConfig.ThirdPartySocialPlatformName,
                PlayerAccountList = new(config.OnlineChattingSystemConfig.PlayerAccountList ?? new List<PlayerAccount>())
            }
        };
    }

    /// <summary>
    /// 逐字段比较原始配置与编辑配置，更新 HasChanges 状态。
    /// </summary>
    private void CheckForChanges()
    {
        if (_originalConfig == null || _editingConfig == null) return;

        bool changed = _originalConfig.MCServerName != _editingConfig.MCServerName ||
                       _originalConfig.MCServerType != _editingConfig.MCServerType ||
                       _originalConfig.MCServerDirectory != _editingConfig.MCServerDirectory ||
                       _originalConfig.JavaPath != _editingConfig.JavaPath ||
                       _originalConfig.StartUpArguments != _editingConfig.StartUpArguments ||
                       _originalConfig.BackupManagerConfig.AutoBackupEnabled != _editingConfig.BackupManagerConfig.AutoBackupEnabled ||
                       _originalConfig.BackupManagerConfig.BackupTimingMode != _editingConfig.BackupManagerConfig.BackupTimingMode ||
                       _originalConfig.BackupManagerConfig.DayInterval != _editingConfig.BackupManagerConfig.DayInterval ||
                       _originalConfig.BackupManagerConfig.SpecificTime != _editingConfig.BackupManagerConfig.SpecificTime ||
                       _originalConfig.BackupManagerConfig.TimeInterval != _editingConfig.BackupManagerConfig.TimeInterval ||
                       _originalConfig.BackupManagerConfig.StopServerBeforeBackup != _editingConfig.BackupManagerConfig.StopServerBeforeBackup ||
                       _originalConfig.BackupManagerConfig.BackupMode != _editingConfig.BackupManagerConfig.BackupMode ||
                       _originalConfig.BackupManagerConfig.CompactionLevel != _editingConfig.BackupManagerConfig.CompactionLevel ||
                       !_excludedFilesList.SequenceEqual(_originalConfig.BackupManagerConfig.ExcludedFilesList) ||
                       !_excludedFileExtensionsList.SequenceEqual(_originalConfig.BackupManagerConfig.ExcludedFileExtensionsList) ||
                       !_excludedFoldersList.SequenceEqual(_originalConfig.BackupManagerConfig.ExcludedFoldersList) ||
                       _originalConfig.BackupManagerConfig.BackupFileOutputDirectory != _editingConfig.BackupManagerConfig.BackupFileOutputDirectory ||
                       _originalConfig.BackupManagerConfig.RemoteBackupFileStoreDirectory != _editingConfig.BackupManagerConfig.RemoteBackupFileStoreDirectory ||
                       _originalConfig.BackupManagerConfig.SFTPClientConfig.Enabled != _editingConfig.BackupManagerConfig.SFTPClientConfig.Enabled ||
                       _originalConfig.BackupManagerConfig.SFTPClientConfig.Host != _editingConfig.BackupManagerConfig.SFTPClientConfig.Host ||
                       _originalConfig.BackupManagerConfig.SFTPClientConfig.Port != _editingConfig.BackupManagerConfig.SFTPClientConfig.Port ||
                       _originalConfig.BackupManagerConfig.SFTPClientConfig.UserName != _editingConfig.BackupManagerConfig.SFTPClientConfig.UserName ||
                       _originalConfig.BackupManagerConfig.SFTPClientConfig.Password != _editingConfig.BackupManagerConfig.SFTPClientConfig.Password ||
                       _originalConfig.BackupManagerConfig.SFTPClientConfig.BufferSize != _editingConfig.BackupManagerConfig.SFTPClientConfig.BufferSize ||
                       _originalConfig.OnlineChattingSystemConfig.ServerPort != _editingConfig.OnlineChattingSystemConfig.ServerPort ||
                       _originalConfig.OnlineChattingSystemConfig.ThirdPartySocialPlatformName != _editingConfig.OnlineChattingSystemConfig.ThirdPartySocialPlatformName;

        HasChanges = changed;
    }

    /// <summary>
    /// 校验编辑中的配置字段。包括必填项、数值范围、SFTP 参数及端口合法性。
    /// </summary>
    /// <returns>校验通过返回 true，否则弹出错误对话框并返回 false。</returns>
    public bool ValidateConfig()
    {
        if (_editingConfig == null) return false;

        if (string.IsNullOrWhiteSpace(_editingConfig.MCServerName))
        {
            ShowErrorDialog("验证失败", "服务端名称不能为空");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_editingConfig.MCServerDirectory))
        {
            ShowErrorDialog("验证失败", "服务端目录不能为空");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_editingConfig.JavaPath))
        {
            ShowErrorDialog("验证失败", "Java路径不能为空");
            return false;
        }

        int dayInterval;
        if (!int.TryParse(_editingConfig.BackupManagerConfig.DayInterval, out dayInterval) || dayInterval < 1)
        {
            ShowErrorDialog("验证失败", "备份间隔天数必须大于0");
            return false;
        }

        int compactionLevel;
        if (!int.TryParse(_editingConfig.BackupManagerConfig.CompactionLevel, out compactionLevel) || compactionLevel < 0 || compactionLevel > 9)
        {
            ShowErrorDialog("验证失败", "压缩等级必须在0-9之间");
            return false;
        }

        if (_editingConfig.BackupManagerConfig.SFTPClientConfig.Enabled)
        {
            if (string.IsNullOrWhiteSpace(_editingConfig.BackupManagerConfig.SFTPClientConfig.Host))
            {
                ShowErrorDialog("验证失败", "SFTP主机地址不能为空");
                return false;
            }

            if (_editingConfig.BackupManagerConfig.SFTPClientConfig.Port < 1 || _editingConfig.BackupManagerConfig.SFTPClientConfig.Port > 65535)
            {
                ShowErrorDialog("验证失败", "SFTP端口必须在1-65535之间");
                return false;
            }

            if (string.IsNullOrWhiteSpace(_editingConfig.BackupManagerConfig.SFTPClientConfig.UserName))
            {
                ShowErrorDialog("验证失败", "SFTP用户名不能为空");
                return false;
            }

            if (_editingConfig.BackupManagerConfig.SFTPClientConfig.BufferSize < 1)
            {
                ShowErrorDialog("验证失败", "SFTP缓冲区大小必须大于0");
                return false;
            }
        }

        int serverPort;
        if (!int.TryParse(_editingConfig.OnlineChattingSystemConfig.ServerPort, out serverPort) || serverPort < 1 || serverPort > 65535)
        {
            ShowErrorDialog("验证失败", "聊天服务器端口必须在1-65535之间");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 保存配置到后端。先校验，再同步排除项到编辑配置，然后通过 RequestBackendTo 发送修改请求。
    /// 等待后端 ModifiedMCServerManagerConfig 响应确认后，才更新原始配置快照与 HasChanges 状态。
    /// 若选中的管理器当前不在后端数据库中（新管理器场景），先发送 CreatNewMCServerManager 创建空实例，
    /// 收到 CreatedNewMCServerManager 后再发送 ModifyMCServerManagerConfig（两阶段提交）。
    /// </summary>
    public void SaveConfig()
    {
        if (!ValidateConfig()) return;
        if (_editingConfig == null) return;

        _editingConfig.BackupManagerConfig.ExcludedFilesList = _excludedFilesList.ToList();
        _editingConfig.BackupManagerConfig.ExcludedFileExtensionsList = _excludedFileExtensionsList.ToList();
        _editingConfig.BackupManagerConfig.ExcludedFoldersList = _excludedFoldersList.ToList();

        IsSaving = true;
        HasError = false;
        ErrorMessage = "";

        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection == null)
        {
            ShowErrorDialog("保存失败", "未找到连接管理器");
            IsSaving = false;
            return;
        }

        var backendId = _selectedManager?.BackendId ?? _selectedBackend?.BackendId;
        if (string.IsNullOrEmpty(backendId))
        {
            ShowErrorDialog("保存失败", "未指定后端");
            IsSaving = false;
            return;
        }

        var client = connection.GetClientByBackendId(backendId);
        if (client == null)
        {
            ShowErrorDialog("保存失败", $"后端 {backendId} 未连接。\n修改仅保存在本地缓存中，待重新连接后可再次尝试保存。");
            IsSaving = false;
            return;
        }

        try
        {
            // ====== 两阶段判定：如果 GlobalCache 中不存在该管理器，说明是新管理器 ======
            bool managerExistsInCache = GlobalCache.ManagerConfigCache(backendId, _editingConfig.ManagerID).Exists;

            if (!managerExistsInCache)
            {
                // 新管理器：先创建空实例，创建成功后 OnConnectionDataPackReceived 会发送 Modify 请求
                _awaitingCreateThenModifyAfterSave = true;
                ShowToast("正在保存", "新管理器：先创建实例，再同步配置...", ToastType.Info);
                client.RequestBackend(RequestTypeEnum.CreatNewMCServerManager, new Pack_CreatNewMCServerManager());
            }
            else
            {
                // 已有管理器：直接发送 Modify 请求，等待 ModifiedMCServerManagerConfig 响应确认
                _awaitingModifyConfirmAfterSave = true;
                client.RequestBackend(RequestTypeEnum.ModifyMCServerManagerConfig,
                    new Pack_ModifyMCServerManagerConfig(_editingConfig));
                ShowToast("正在保存", "已提交配置修改请求，等待后端确认...", ToastType.Info);
            }
        }
        catch (Exception ex)
        {
            ShowErrorDialog("保存失败", ex.Message);
            IsSaving = false;
            _awaitingModifyConfirmAfterSave = false;
            _awaitingCreateThenModifyAfterSave = false;
        }
    }

    /// <summary>放弃所有未保存的修改，从原始配置重新克隆编辑配置并刷新 UI。</summary>
    public void CancelChanges()
    {
        if (_originalConfig != null)
        {
            _editingConfig = CloneConfig(_originalConfig);
            HasChanges = false;
            // 取消修改同样需要重建 FormModel，强制 PropertyGrid 刷新绑定。
            var newFormModel = new MCServerManagerConfigFormModel(_editingConfig);
            AttachFormModel(newFormModel);
            RefreshProperties();
        }
    }

    /// <summary>刷新所有属性变更通知并重建排除项列表，用于取消修改后恢复 UI。</summary>
    private void RefreshProperties()
    {
        this.RaisePropertyChanged(nameof(MCServerName));
        this.RaisePropertyChanged(nameof(MCServerType));
        this.RaisePropertyChanged(nameof(MCServerDirectory));
        this.RaisePropertyChanged(nameof(JavaPath));
        this.RaisePropertyChanged(nameof(StartUpArguments));
        this.RaisePropertyChanged(nameof(AutoBackupEnabled));
        this.RaisePropertyChanged(nameof(BackupTimingModeIndex));
        this.RaisePropertyChanged(nameof(BackupTimingMode));
        this.RaisePropertyChanged(nameof(DayInterval));
        this.RaisePropertyChanged(nameof(SpecificTime));
        this.RaisePropertyChanged(nameof(TimeInterval));
        this.RaisePropertyChanged(nameof(StopServerBeforeBackup));
        this.RaisePropertyChanged(nameof(BackupModeIndex));
        this.RaisePropertyChanged(nameof(BackupMode));
        this.RaisePropertyChanged(nameof(CompactionLevel));
        this.RaisePropertyChanged(nameof(BackupFileOutputDirectory));
        this.RaisePropertyChanged(nameof(RemoteBackupFileStoreDirectory));
        this.RaisePropertyChanged(nameof(SFTPEnabled));
        this.RaisePropertyChanged(nameof(SFTPHost));
        this.RaisePropertyChanged(nameof(SFTPPort));
        this.RaisePropertyChanged(nameof(SFTPUserName));
        this.RaisePropertyChanged(nameof(SFTPPassword));
        this.RaisePropertyChanged(nameof(SFTPBufferSize));
        this.RaisePropertyChanged(nameof(ServerPort));
        this.RaisePropertyChanged(nameof(ThirdPartySocialPlatformName));

        // 三个备份排除列表：Clear+N Add → ReplaceAll，通知 1 次
        _excludedFilesList.ReplaceAll(_originalConfig?.BackupManagerConfig.ExcludedFilesList);
        _excludedFileExtensionsList.ReplaceAll(_originalConfig?.BackupManagerConfig.ExcludedFileExtensionsList);
        _excludedFoldersList.ReplaceAll(_originalConfig?.BackupManagerConfig.ExcludedFoldersList);
    }

    /// <summary>添加排除文件项到列表。</summary>
    public void AddExcludedFile()
    {
        if (!string.IsNullOrWhiteSpace(NewExcludedFile))
        {
            _excludedFilesList.Add(NewExcludedFile);
            NewExcludedFile = "";
            this.RaisePropertyChanged(nameof(NewExcludedFile));
            CheckForChanges();
        }
    }

    /// <summary>从列表中移除指定排除文件项。</summary>
    public void RemoveExcludedFile(string file)
    {
        _excludedFilesList.Remove(file);
        CheckForChanges();
    }

    /// <summary>添加排除扩展名项，自动补全前导点号。</summary>
    public void AddExcludedExtension()
    {
        if (!string.IsNullOrWhiteSpace(NewExcludedExtension))
        {
            string ext = NewExcludedExtension.StartsWith(".") ? NewExcludedExtension : "." + NewExcludedExtension;
            _excludedFileExtensionsList.Add(ext);
            NewExcludedExtension = "";
            this.RaisePropertyChanged(nameof(NewExcludedExtension));
            CheckForChanges();
        }
    }

    /// <summary>从列表中移除指定排除扩展名项。</summary>
    public void RemoveExcludedExtension(string ext)
    {
        _excludedFileExtensionsList.Remove(ext);
        CheckForChanges();
    }

    /// <summary>添加排除文件夹项到列表。</summary>
    public void AddExcludedFolder()
    {
        if (!string.IsNullOrWhiteSpace(NewExcludedFolder))
        {
            _excludedFoldersList.Add(NewExcludedFolder);
            NewExcludedFolder = "";
            this.RaisePropertyChanged(nameof(NewExcludedFolder));
            CheckForChanges();
        }
    }

    /// <summary>从列表中移除指定排除文件夹项。</summary>
    public void RemoveExcludedFolder(string folder)
    {
        _excludedFoldersList.Remove(folder);
        CheckForChanges();
    }

    /// <summary>
    /// 加载指定管理器的配置。优先从 GlobalCache 缓存读取，缓存未命中时向后端请求最新数据。
    /// 后端请求通过 ConnectionViewModel 的全局事件机制统一处理（OnConnectionDataPackReceived），
    /// 避免与 LoadManagers 的订阅冲突。
    /// </summary>
    /// <param name="managerId">目标管理器 ID。</param>
    public void LoadConfigForManager(string managerId)
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = "";

        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection == null)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = "未找到连接管理器";
            ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
            return;
        }

        var backendId = _selectedManager?.BackendId ?? _selectedBackend?.BackendId;
        if (string.IsNullOrEmpty(backendId))
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = "未指定后端";
            ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
            return;
        }

        // 第 1 步：尝试从缓存读取配置（毫秒级响应）
        var config = GlobalCache.ManagerConfigCache(backendId, managerId).Config;
        if (config != null)
        {
            Initialize(config);
            IsLoading = false;
            HasError = false;
            return;
        }

        // 第 2 步：缓存未命中，向后端请求最新数据（不订阅响应，由 OnConnectionDataPackReceived 统一处理）
        var client = connection.GetClientByBackendId(backendId);
        if (client == null)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = $"后端 {backendId} 未连接，且缓存中无配置";
            ShowToast("加载配置失败", ErrorMessage, ToastType.Warning);
            return;
        }

        try
        {
            client.RequestBackend(RequestTypeEnum.GetMCServerManagersList, new Pack_GetMCServerManagerConfigsList());
            // 请求发出后，OnConnectionDataPackReceived 会在数据包到达时自动更新列表和配置
            // 如果用户在等待期间选择了其他管理器，OnConnectionDataPackReceived 仍会正确处理
        }
        catch (Exception ex)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = $"加载配置失败: {ex.Message}";
            ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
        }
    }

    /// <summary>取消订阅配置响应事件。</summary>
    private void UnsubscribeConfigEvents()
    {
        if (_subscribedConfigClient != null)
        {
            try
            {
                _subscribedConfigClient.DataPackBus.Unsubscribe<Pack_MCServerManagerConfigs>(OnConfigReceived);
                _subscribedConfigClient.DataPackBus.Unsubscribe<Pack_ErrorInfo>(OnConfigError);
            }
            catch { }
            _subscribedConfigClient = null;
        }
    }

    /// <summary>
    /// 接收到配置响应回调：在列表中查找匹配管理器的配置并初始化编辑环境。
    /// </summary>
    private void OnConfigReceived(Pack_MCServerManagerConfigs pack)
    {
        // 配置回包：DataBind 级 Normal（编辑区同步需要立即可见）
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UnsubscribeConfigEvents();

                if (pack.MCServerManagerConfigs?.MCServerManagerConfigsList != null && pack.MCServerManagerConfigs.MCServerManagerConfigsList.Count > 0)
                {
                    var config = pack.MCServerManagerConfigs.MCServerManagerConfigsList.FirstOrDefault(c => c.ManagerID == _selectedManager?.ManagerId);
                    if (config != null)
                    {
                        Initialize(config);
                        IsLoading = false;
                        HasError = false;
                        ShowToast("配置加载成功", ToastType.Success);
                    }
                    else
                    {
                        IsLoading = false;
                        HasError = true;
                        ErrorMessage = "未找到指定管理器的配置";
                        ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
                    }
                }
                else
                {
                    IsLoading = false;
                    HasError = true;
                    ErrorMessage = "获取到空配置";
                    ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
                }
            }
            catch (Exception ex)
            {
                IsLoading = false;
                HasError = true;
                ErrorMessage = $"配置处理失败: {ex.Message}";
                ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
            }
        });
    }

    /// <summary>加载配置时接收到错误信息回调：标记错误状态并提示。</summary>
    private void OnConfigError(Pack_ErrorInfo pack)
    {
        // 配置加载错误：DataBind 级 Normal
        Dispatcher.UIThread.Post(() =>
        {
            UnsubscribeConfigEvents();

            IsLoading = false;
            HasError = true;
            ErrorMessage = pack.ErrorInfo ?? "未知错误";
            ShowToast("加载配置失败", ErrorMessage, ToastType.Error);
        });
    }

    /// <summary>清空配置编辑区，重置所有状态并同步 FormModel。</summary>
    public void ClearConfig()
    {
        _originalConfig = null;
        _editingConfig = null;
        ManagerId = null;
        HasChanges = false;
        IsLoading = false;
        HasError = false;
        ErrorMessage = "";

        _excludedFilesList.Clear();
        _excludedFileExtensionsList.Clear();
        _excludedFoldersList.Clear();

        // 空配置状态下，将 FormModel 切换为"空配置的新实例"，
        // 既避免 PropertyGrid 展示旧管理器残留内容，也不会触发 SetItem 的 null 分支短路。
        AttachFormModel(new MCServerManagerConfigFormModel());

        RefreshProperties();
    }

    /// <summary>
    /// 当选中后端发生变化时触发：先从 GlobalCache 缓存预加载管理器列表（毫秒级，UI 立即可见），
    /// 若后端已连接，再发起网络请求同步最新数据作为兜底。
    /// </summary>
    private void OnSelectedBackendChanged()
    {
        UnsubscribeManagersEvents();
        UnsubscribeConfigEvents();
        SelectedManager = null;
        ClearConfig();

        if (_selectedBackend == null)
        {
            _managers.Clear();
            return;
        }

        string backendId = _selectedBackend.BackendId;

        // 第 1 步：从全局动态缓存预填充管理器列表（不依赖网络，页面打开立即显示）
        bool hasCached = LoadManagersFromCache(backendId);

        // 第 2 步：若后端已连接，再发起网络请求拉取最新数据
        // （缓存未命中的情况下，网络响应会作为首次填充；缓存已命中则作为增量更新）
        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        var client = connection?.GetClientByBackendId(backendId);
        if (client != null)
        {
            LoadManagers(backendId);
        }
        else if (hasCached)
        {
            // 没有可用连接但缓存有数据 → 提示用户当前显示的是缓存数据
            ShowToast("已加载缓存数据", "后端未连接，当前显示为本地缓存的管理器列表，配置编辑仅供参考。", ToastType.Info);
        }
    }

    /// <summary>
    /// 从 GlobalCache 动态缓存中读取指定后端下的全部管理器并填充到 <see cref="Managers"/> 集合。
    /// 读取的数据直接构造 <see cref="MCServerManagerItemModel"/>，保持与网络响应相同的数据形态，
    /// 因此 XAML 中 <c>ListBox.ItemTemplate</c> 绑定（MCServerName / BackendId / ManagerId）无需任何修改。
    /// </summary>
    /// <param name="backendId">目标后端 ID。</param>
    /// <param name="clearFirst">
    /// 是否在填充前清空 <see cref="_managers"/>。
    /// <para>单后端加载（如 OnSelectedBackendChanged）传 <c>true</c>（默认）；</para>
    /// <para>多后端合并加载（如 OnLoaded 遍历所有后端兜底）传 <c>false</c>，避免互相覆盖。</para>
    /// </param>
    /// <returns>是否从缓存读取到至少一条管理器数据。</returns>
    public bool LoadManagersFromCache(string backendId, bool clearFirst = true)
    {
        // 先在内存中构造列表（避免 ObservableCollection 的 N 次通知）
        var loadedList = BuildManagersListFromCacheInternal(backendId);

        if (clearFirst)
        {
            // 保留当前选中项的 ManagerId，填充完后尽量恢复选中状态，提升用户体验
            string? prevSelectedId = _selectedManager?.ManagerId;

            // 一次性批量替换：1 次 Reset 通知
            _managers.ReplaceAll(loadedList);

            // 尝试恢复之前选中的管理器
            if (prevSelectedId != null)
            {
                var restored = _managers.FirstOrDefault(m => m.ManagerId == prevSelectedId);
                if (restored != null)
                {
                    _selectedManager = null;
                    SelectedManager = restored;
                }
            }
            return loadedList.Count > 0;
        }
        else
        {
            // 合并场景：在 _managers 现有基础上批量追加（去重后再 AddRange）
            var existingKeys = new HashSet<(string BackendId, string ManagerId)>(
                _managers.Select(m => (m.BackendId, m.ManagerId)));
            var toAppend = loadedList.Where(m => !existingKeys.Contains((m.BackendId, m.ManagerId))).ToList();
            if (toAppend.Count > 0) _managers.AddRange(toAppend);
            return loadedList.Count > 0;
        }
    }

    /// <summary>
    /// 内部辅助：从 GlobalCache 读取单个 backendId 下的管理器并构造新的 List（内存态，无通知）。
    /// 不修改 <see cref="_managers"/>，由外层按需 ReplaceAll / AddRange 实现批量通知。
    /// </summary>
    private List<MCServerManagerItemModel> BuildManagersListFromCacheInternal(string backendId)
    {
        var result = new List<MCServerManagerItemModel>();

        // 通过 GlobalCache 访问器获取该后端的管理器 ID 列表（嵌套字典结构，外层 backendId）
        var managerIds = GlobalCache.BackendConfigCache(backendId).GetManagerIds();
        if (managerIds.Count == 0)
            return result;

        foreach (var mgrId in managerIds)
        {
            // 按 backendId + managerId 复合索引读取配置（嵌套字典第二层）
            var cacheEntry = GlobalCache.ManagerConfigCache(backendId, mgrId);
            var config = cacheEntry.Config;
            if (config == null)
            {
                // 缓存里只存了元信息没有完整 Config，就跳过，避免 PropertyGrid 后续加载出错
                continue;
            }

            // 直接构造 ItemModel，构造函数内部的属性与 XAML 绑定完全匹配
            // MCServerName ↔ TextBlock Text={Binding MCServerName}
            // BackendId     ↔ TextBlock Text={Binding BackendId}
            // ManagerId     ↔ TextBlock Text={Binding ManagerId}
            result.Add(new MCServerManagerItemModel(config, backendId));
        }
        return result;
    }

    /// <summary>
    /// 尝试从 GlobalCache 缓存加载指定管理器的配置并初始化编辑区。
    /// 成功返回 true（调用方无需再发网络请求），失败返回 false（调用方应走网络兜底）。
    /// </summary>
    /// <param name="backendId">所属后端 ID。</param>
    /// <param name="managerId">目标管理器 ID。</param>
    /// <returns>是否从缓存成功加载到配置。</returns>
    public bool LoadConfigFromCache(string backendId, string managerId)
    {
        var config = GlobalCache.ManagerConfigCache(backendId, managerId).Config;
        if (config == null)
            return false;

        Initialize(config);
        IsLoading = false;
        HasError = false;
        return true;
    }

    /// <summary>
    /// 加载指定后端的管理器列表。
    /// 执行顺序：先从 GlobalCache 缓存预填充 → 判断 10 秒冷却 → 冷却外才通过 NativeClient 订阅+发请求。
    /// </summary>
    /// <param name="backendId">目标后端 ID。</param>
    /// <param name="force">是否强制跳过冷却（手动刷新时使用）。</param>
    public void LoadManagers(string backendId, bool force = false)
    {
        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection == null)
        {
            _managers.Clear();
            return;
        }

        _currentBackendId = backendId;
        _connection = connection;

        // 缓存优先：先从 GlobalCache 加载（毫秒级，立即显示）
        LoadManagersFromCache(backendId, clearFirst: true);

        var client = connection.GetClientByBackendId(backendId);
        if (client == null)
        {
            // 客户端未连接：若缓存已有数据则不提示错误，避免误报
            if (_managers.Count == 0)
                ShowToast("加载失败", $"后端 {backendId} 未连接", ToastType.Warning);
            return;
        }

        // Cooldown：同一 backendId 10 秒内不重复发网络请求
        _lastLoadManagersUtc.TryGetValue(backendId, out var lastLoad);
        bool cooldownActive = !force && (DateTime.UtcNow - lastLoad).TotalSeconds < LoadManagersCooldownSeconds;
        if (cooldownActive)
        {
            // 冷却期内保持现有订阅，不再发请求
            return;
        }
        _lastLoadManagersUtc[backendId] = DateTime.UtcNow;

        try
        {
            UnsubscribeManagersEvents();
            _subscribedManagersClient = client;
            var capturedBackendId = backendId;
            _managersSubscriptionDelegate = pack => OnManagersReceived(pack, capturedBackendId);
            client.DataPackBus.Subscribe<Pack_MCServerManagerConfigs>(_managersSubscriptionDelegate);
            client.RequestBackend(RequestTypeEnum.GetMCServerManagersList, new Pack_GetMCServerManagerConfigsList());
        }
        catch (Exception ex)
        {
            ShowToast("加载管理器列表失败", ex.Message, ToastType.Error);
        }
    }

    /// <summary>
    /// 兼容旧调用：使用当前选中后端加载管理器列表。
    /// </summary>
    public void LoadManagers()
    {
        if (_selectedBackend != null)
        {
            LoadManagers(_selectedBackend.BackendId);
        }
        else
        {
            _managers.Clear();
        }
    }

    /// <summary>取消订阅管理器列表响应事件。</summary>
    private void UnsubscribeManagersEvents()
    {
        if (_subscribedManagersClient != null && _managersSubscriptionDelegate != null)
        {
            try
            {
                _subscribedManagersClient.DataPackBus.Unsubscribe<Pack_MCServerManagerConfigs>(_managersSubscriptionDelegate);
            }
            catch { }
        }
        _managersSubscriptionDelegate = null;
        _subscribedManagersClient = null;
    }

    /// <summary>
    /// 接收到管理器列表响应回调：验证 GlobalCache 数据一致性后，从缓存读取并重建 Managers 集合。
    /// 使用捕获的 backendId（而非共享字段 _currentBackendId），避免竞态条件。
    /// 无论直接订阅路径（LoadManagers）还是全局事件路径（OnConnectionDataPackReceived），
    /// 最终都统一走 GlobalCache 读取，确保数据一致性。
    /// </summary>
    private void OnManagersReceived(Pack_MCServerManagerConfigs pack, string backendId)
    {
        // 管理器列表回包：DataBind 级 Normal（影响选中项和编辑区）
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UnsubscribeManagersEvents();

                // 将数据包中的配置写入 GlobalCache（确保缓存有数据）
                // 虽然 ConnectionViewModel.OnDataReceived 也会写入，但此处作为兜底，
                // 防止因时序问题导致 GlobalCache 尚未写入时读不到数据
                if (pack.MCServerManagerConfigs?.MCServerManagerConfigsList != null)
                {
                    foreach (var config in pack.MCServerManagerConfigs.MCServerManagerConfigsList)
                    {
                        GlobalCache.ManagerConfigCache(backendId, config.ManagerID)
                            .UpdateConfig(config.MCServerName ?? "", config);
                    }
                }

                // 统一从 GlobalCache 读取，确保与 OnConnectionDataPackReceived 路径一致
                bool selectedMatches = _selectedBackend != null && _selectedBackend.BackendId == backendId;
                if (selectedMatches || _selectedBackend == null)
                {
                    LoadManagersFromCache(backendId, clearFirst: true);
                }
            }
            catch (Exception ex)
            {
                ShowToast("处理管理器列表失败", ex.Message, ToastType.Error);
            }
        });
    }

    /// <summary>
    /// 清理所有订阅（页面卸载时调用）。
    /// </summary>
    public void CleanupSubscriptions()
    {
        UnsubscribeManagersEvents();
        UnsubscribeConfigEvents();
        UnsubscribeConnectionEvents();
    }

    /// <summary>
    /// 订阅 ConnectionViewModel 的全局数据包事件，确保连接成功后能自动刷新管理器列表。
    /// 当后端推送 Pack_MCServerManagerConfigs 时，自动更新当前后端的管理器列表和配置。
    /// </summary>
    public void SubscribeConnectionEvents()
    {
        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection == null) return;

        // 避免重复订阅
        if (_subscribedConnection == connection) return;

        UnsubscribeConnectionEvents();
        _subscribedConnection = connection;
        connection.DataPackReceivedWithBackendId += OnConnectionDataPackReceived;
    }

    /// <summary>
    /// 取消订阅 ConnectionViewModel 的全局数据包事件。
    /// </summary>
    private void UnsubscribeConnectionEvents()
    {
        if (_subscribedConnection != null)
        {
            try
            {
                _subscribedConnection.DataPackReceivedWithBackendId -= OnConnectionDataPackReceived;
            }
            catch { }
            _subscribedConnection = null;
        }
    }

    /// <summary>
    /// 处理来自 ConnectionViewModel 的全局数据包事件。
    /// 处理三类业务数据包：
    ///   1) Pack_MCServerManagerConfigs：自动更新当前后端的管理器列表（从缓存读取）
    ///   2) Pack_ModifiedMCServerManagerConfig：SaveConfig 保存成功确认 → 更新 HasChanges/_originalConfig
    ///   3) Pack_CreatedNewMCServerManager：新管理器两阶段保存 → 先创建再发 Modify
    ///   4) Pack_ErrorInfo / Pack_CreatNewMCServerManagerFailed：保存失败回滚
    /// 数据已由 ConnectionViewModel.OnDataReceived 写入 GlobalCache，列表更新统一从缓存读取。
    /// </summary>
    private void OnConnectionDataPackReceived(string backendId, RespondTypeEnum responseType, object? data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 与当前选中后端匹配才处理 save/create 确认，避免其他后端事件干扰
                bool targetBackendMatches =
                    (_selectedBackend != null && _selectedBackend.BackendId == backendId) ||
                    (_selectedManager != null && _selectedManager.BackendId == backendId);

                switch (responseType)
                {
                    // ====== 管理器列表刷新 ======
                    case RespondTypeEnum.MCServerManagerConfigs:
                        if (data is not Pack_MCServerManagerConfigs configsPack)
                        {
                            System.Diagnostics.Debug.WriteLine($"[OnConnectionDataPackReceived] 类型不匹配: 期望 Pack_MCServerManagerConfigs, 实际 {data?.GetType().Name ?? "null"}");
                            return;
                        }

                        if (_selectedBackend != null && _selectedBackend.BackendId == backendId)
                        {
                            LoadManagersFromCache(backendId, clearFirst: true);
                        }
                        else if (_selectedBackend == null)
                        {
                            LoadManagersFromCache(backendId, clearFirst: false);
                        }
                        break;

                    // ====== 保存成功确认（Modify 响应） ======
                    case RespondTypeEnum.ModifiedMCServerManagerConfig when targetBackendMatches:
                        if (data is Pack_ModifiedMCServerManagerConfig modifiedPack)
                        {
                            // 如果是当前编辑的管理器，更新快照与状态
                            if (_editingConfig != null &&
                                modifiedPack.MCServerManagerConfig?.ManagerID == _editingConfig.ManagerID)
                            {
                                HasChanges = false;
                                _originalConfig = CloneConfig(_editingConfig);
                                IsSaving = false;
                                _awaitingModifyConfirmAfterSave = false;
                                _awaitingCreateThenModifyAfterSave = false;
                                ShowToast("配置已保存", $"后端已确认管理器 {_editingConfig.ManagerID} 的配置更新", ToastType.Success);
                            }
                        }
                        break;

                    // ====== 新管理器创建成功（两阶段保存：阶段 1 完成） ======
                    case RespondTypeEnum.CreatedNewMCServerManager when targetBackendMatches:
                        if (data is Pack_CreatedNewMCServerManager createdPack &&
                            _awaitingCreateThenModifyAfterSave)
                        {
                            var createdId = createdPack.MCServerManagerConfig?.ManagerID ?? "";
                            if (_editingConfig != null)
                            {
                                // 阶段 2：立即发送 ModifyMCServerManagerConfig，同步用户编辑的配置
                                _editingConfig.ManagerID = createdId;
                                ManagerId = createdId;

                                var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
                                var client = connection?.GetClientByBackendId(backendId);
                                if (client != null)
                                {
                                    client.RequestBackend(RequestTypeEnum.ModifyMCServerManagerConfig,
                                        new Pack_ModifyMCServerManagerConfig(_editingConfig));
                                    ShowToast("保存中", $"管理器 {createdId} 已创建，正在同步配置...", ToastType.Info);
                                    // 标记状态，等阶段 2 的 ModifiedMCServerManagerConfig 响应
                                    _awaitingCreateThenModifyAfterSave = false;
                                    _awaitingModifyConfirmAfterSave = true;
                                }
                                else
                                {
                                    IsSaving = false;
                                    _awaitingCreateThenModifyAfterSave = false;
                                    ShowToast("保存部分成功", $"管理器 {createdId} 已创建，但同步配置失败（客户端丢失）", ToastType.Warning);
                                }
                            }
                        }
                        break;

                    // ====== 创建失败 ======
                    case RespondTypeEnum.CreatNewMCServerManagerFailed when targetBackendMatches:
                        if (_awaitingCreateThenModifyAfterSave)
                        {
                            IsSaving = false;
                            _awaitingCreateThenModifyAfterSave = false;
                            HasError = true;
                            ErrorMessage = "创建管理器实例失败，配置无法保存";
                            ShowErrorDialog("保存失败", ErrorMessage);
                        }
                        break;

                    // ====== 通用错误信息 ======
                    case RespondTypeEnum.ErrorInfo when targetBackendMatches:
                        if (data is Pack_ErrorInfo errPack)
                        {
                            bool pendingSave = _awaitingModifyConfirmAfterSave || _awaitingCreateThenModifyAfterSave;
                            if (pendingSave)
                            {
                                IsSaving = false;
                                _awaitingModifyConfirmAfterSave = false;
                                _awaitingCreateThenModifyAfterSave = false;
                                HasError = true;
                                ErrorMessage = errPack.ErrorInfo ?? "保存请求被后端拒绝";
                                ShowErrorDialog("保存失败", ErrorMessage);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnConnectionDataPackReceived] 处理数据包出错: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 释放资源：清理 NativeClient.DataPackBus 订阅 + FormModel 变更回调。
    /// 由页面 OnDetachedFromVisualTree / OnUnloaded 调用，避免内存泄漏。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            try
            {
                // 清理当前 FormModel 的事件绑定
                if (_formModel != null)
                {
                    _formModel.ConfigChanged -= OnFormModelChanged;
                }
            }
            catch { }
            CleanupSubscriptions();
        }
        base.Dispose(disposing);
    }
}