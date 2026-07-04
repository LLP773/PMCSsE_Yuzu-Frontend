using System;
using System.Collections.ObjectModel;
using System.Linq;
using PMCSsE_Communicator;
using PMCSsE_Communicator.DataPacks;
using ReactiveUI;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Models;

public class MCServerManagerConfigViewModel : ViewModelBase
{
    private MCServerManagerConfig? _originalConfig;
    private MCServerManagerConfig? _editingConfig;
    private string? _managerId;
    private bool _hasChanges;
    private bool _isSaving;
    private string _activeSection = "基本设置";

    private ObservableCollection<string> _excludedFilesList = new();
    private ObservableCollection<string> _excludedFileExtensionsList = new();
    private ObservableCollection<string> _excludedFoldersList = new();

    public ObservableCollection<string> SectionHeaders { get; } = new()
    {
        "基本设置",
        "备份设置",
        "备份排除",
        "远程备份",
        "在线聊天"
    };

    public string? ManagerId
    {
        get => _managerId;
        set => this.RaiseAndSetIfChanged(ref _managerId, value);
    }

    public bool HasChanges
    {
        get => _hasChanges;
        set
        {
            this.RaiseAndSetIfChanged(ref _hasChanges, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSaving, value);
            this.RaisePropertyChanged(nameof(CanSave));
        }
    }

    public bool CanSave => HasChanges && !IsSaving;

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

    public void Initialize(MCServerManagerConfig config)
    {
        _originalConfig = CloneConfig(config);
        _editingConfig = CloneConfig(config);
        ManagerId = config.ManagerID;
        HasChanges = false;

        _excludedFilesList = new ObservableCollection<string>(config.BackupManagerConfig.ExcludedFilesList);
        _excludedFileExtensionsList = new ObservableCollection<string>(config.BackupManagerConfig.ExcludedFileExtensionsList);
        _excludedFoldersList = new ObservableCollection<string>(config.BackupManagerConfig.ExcludedFoldersList);

        this.RaisePropertyChanged(nameof(ExcludedFilesList));
        this.RaisePropertyChanged(nameof(ExcludedFileExtensionsList));
        this.RaisePropertyChanged(nameof(ExcludedFoldersList));
    }

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
                ExcludedFilesList = new(config.BackupManagerConfig.ExcludedFilesList),
                ExcludedFileExtensionsList = new(config.BackupManagerConfig.ExcludedFileExtensionsList),
                ExcludedFoldersList = new(config.BackupManagerConfig.ExcludedFoldersList),
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
                PlayerAccountList = new(config.OnlineChattingSystemConfig.PlayerAccountList)
            }
        };
    }

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

    public void SaveConfig()
    {
        if (!ValidateConfig()) return;
        if (_editingConfig == null) return;

        _editingConfig.BackupManagerConfig.ExcludedFilesList = _excludedFilesList.ToList();
        _editingConfig.BackupManagerConfig.ExcludedFileExtensionsList = _excludedFileExtensionsList.ToList();
        _editingConfig.BackupManagerConfig.ExcludedFoldersList = _excludedFoldersList.ToList();

        IsSaving = true;

        var connection = App.Current?.Resources["Connection"] as ConnectionViewModel;
        if (connection?.Client == null)
        {
            ShowErrorDialog("保存失败", "未连接到后端");
            IsSaving = false;
            return;
        }

        connection.Client.RequestBackend(RequestTypeEnum.ModifyMCServerManagerConfig,
            new Pack_ModifyMCServerManagerConfig(_editingConfig));

        ShowToast("配置已保存", ToastType.Success);
        HasChanges = false;
        _originalConfig = CloneConfig(_editingConfig);
        IsSaving = false;
    }

    public void CancelChanges()
    {
        if (_originalConfig != null)
        {
            _editingConfig = CloneConfig(_originalConfig);
            HasChanges = false;
            RefreshProperties();
        }
    }

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

        _excludedFilesList.Clear();
        if (_originalConfig?.BackupManagerConfig.ExcludedFilesList != null)
        {
            foreach (var item in _originalConfig.BackupManagerConfig.ExcludedFilesList)
            {
                _excludedFilesList.Add(item);
            }
        }

        _excludedFileExtensionsList.Clear();
        if (_originalConfig?.BackupManagerConfig.ExcludedFileExtensionsList != null)
        {
            foreach (var item in _originalConfig.BackupManagerConfig.ExcludedFileExtensionsList)
            {
                _excludedFileExtensionsList.Add(item);
            }
        }

        _excludedFoldersList.Clear();
        if (_originalConfig?.BackupManagerConfig.ExcludedFoldersList != null)
        {
            foreach (var item in _originalConfig.BackupManagerConfig.ExcludedFoldersList)
            {
                _excludedFoldersList.Add(item);
            }
        }
    }

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

    public void RemoveExcludedFile(string file)
    {
        _excludedFilesList.Remove(file);
        CheckForChanges();
    }

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

    public void RemoveExcludedExtension(string ext)
    {
        _excludedFileExtensionsList.Remove(ext);
        CheckForChanges();
    }

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

    public void RemoveExcludedFolder(string folder)
    {
        _excludedFoldersList.Remove(folder);
        CheckForChanges();
    }
}