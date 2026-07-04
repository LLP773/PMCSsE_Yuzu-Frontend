using System;
using System.ComponentModel;
using PMCSsE_Communicator;
using ReactiveUI;
using Yuzu_Frontend.Models.PropertyGrid;

namespace Yuzu_Frontend.Models;

/// <summary>
/// MCServerManager 配置的表单模型，供 PropertyGrid 反射渲染使用。
/// 通过 [Category] 和 [DisplayName] 标记字段分类与显示名称。
/// 属性 getter/setter 代理到内部的 MCServerManagerConfig 实例，
/// 不克隆数据，保证编辑即时写回原始配置对象。
/// </summary>
public class MCServerManagerConfigFormModel : ReactiveObject
{
    private MCServerManagerConfig? _config;

    /// <summary>
    /// 当任意属性变化时触发，供 ViewModel 监听以更新 HasChanges。
    /// </summary>
    public event Action? ConfigChanged;

    /// <summary>
    /// 绑定的配置实例。设置时会刷新所有属性变更通知。
    /// </summary>
    public MCServerManagerConfig? Config
    {
        get => _config;
        set
        {
            _config = value;
            RefreshAllProperties();
        }
    }

    /// <summary>默认构造函数，Config 延迟设置。</summary>
    public MCServerManagerConfigFormModel() { }

    /// <summary>
    /// 初始化 <see cref="MCServerManagerConfigFormModel"/> 并绑定指定配置实例。
    /// </summary>
    /// <param name="config">被代理的配置实例。</param>
    public MCServerManagerConfigFormModel(MCServerManagerConfig config)
    {
        _config = config;
    }

    // ========== 基本设置 ==========

    /// <summary>服务端名称。</summary>
    [Category("基本设置")]
    [DisplayName("服务端名称")]
    public string MCServerName
    {
        get => _config?.MCServerName ?? "";
        set
        {
            if (_config != null && _config.MCServerName != value)
            {
                _config.MCServerName = value;
                this.RaisePropertyChanged(nameof(MCServerName));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>服务端类型。</summary>
    [Category("基本设置")]
    [DisplayName("服务端类型")]
    public string MCServerType
    {
        get => _config?.MCServerType ?? "Vanilla";
        set
        {
            if (_config != null && _config.MCServerType != value)
            {
                _config.MCServerType = value;
                this.RaisePropertyChanged(nameof(MCServerType));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>服务端工作目录。</summary>
    [Category("基本设置")]
    [DisplayName("服务端目录")]
    [FilePath(FilePickerMode.Folder)]
    public string MCServerDirectory
    {
        get => _config?.MCServerDirectory ?? "";
        set
        {
            if (_config != null && _config.MCServerDirectory != value)
            {
                _config.MCServerDirectory = value;
                this.RaisePropertyChanged(nameof(MCServerDirectory));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>Java 可执行文件路径。</summary>
    [Category("基本设置")]
    [DisplayName("Java路径")]
    [FilePath(FilePickerMode.File)]
    public string JavaPath
    {
        get => _config?.JavaPath ?? "";
        set
        {
            if (_config != null && _config.JavaPath != value)
            {
                _config.JavaPath = value;
                this.RaisePropertyChanged(nameof(JavaPath));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>MC 服务端启动参数。</summary>
    [Category("基本设置")]
    [DisplayName("启动参数")]
    public string StartUpArguments
    {
        get => _config?.StartUpArguments ?? "";
        set
        {
            if (_config != null && _config.StartUpArguments != value)
            {
                _config.StartUpArguments = value;
                this.RaisePropertyChanged(nameof(StartUpArguments));
                ConfigChanged?.Invoke();
            }
        }
    }

    // ========== 备份设置 ==========

    /// <summary>是否启用自动备份。</summary>
    [Category("备份设置")]
    [DisplayName("启用自动备份")]
    public bool AutoBackupEnabled
    {
        get => _config?.BackupManagerConfig.AutoBackupEnabled ?? false;
        set
        {
            if (_config != null && _config.BackupManagerConfig.AutoBackupEnabled != value)
            {
                _config.BackupManagerConfig.AutoBackupEnabled = value;
                this.RaisePropertyChanged(nameof(AutoBackupEnabled));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>备份定时模式（按天间隔/按时间间隔）。</summary>
    [Category("备份设置")]
    [DisplayName("备份定时模式")]
    public BackupTimingMode BackupTimingMode
    {
        get => _config?.BackupManagerConfig.BackupTimingMode ?? BackupTimingMode.DayInterval_SpecificTime;
        set
        {
            if (_config != null && _config.BackupManagerConfig.BackupTimingMode != value)
            {
                _config.BackupManagerConfig.BackupTimingMode = value;
                this.RaisePropertyChanged(nameof(BackupTimingMode));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>备份间隔天数。</summary>
    [Category("备份设置")]
    [DisplayName("间隔天数")]
    public string DayInterval
    {
        get => _config?.BackupManagerConfig.DayInterval ?? "1";
        set
        {
            if (_config != null && _config.BackupManagerConfig.DayInterval != value)
            {
                _config.BackupManagerConfig.DayInterval = value;
                this.RaisePropertyChanged(nameof(DayInterval));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>执行备份的特定时间点。</summary>
    [Category("备份设置")]
    [DisplayName("特定时间")]
    public string SpecificTime
    {
        get => _config?.BackupManagerConfig.SpecificTime ?? "04:00:00";
        set
        {
            if (_config != null && _config.BackupManagerConfig.SpecificTime != value)
            {
                _config.BackupManagerConfig.SpecificTime = value;
                this.RaisePropertyChanged(nameof(SpecificTime));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>备份时间间隔。</summary>
    [Category("备份设置")]
    [DisplayName("时间间隔")]
    public string TimeInterval
    {
        get => _config?.BackupManagerConfig.TimeInterval ?? "04:00:00";
        set
        {
            if (_config != null && _config.BackupManagerConfig.TimeInterval != value)
            {
                _config.BackupManagerConfig.TimeInterval = value;
                this.RaisePropertyChanged(nameof(TimeInterval));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>备份前是否先关闭服务端。</summary>
    [Category("备份设置")]
    [DisplayName("备份前关服")]
    public bool StopServerBeforeBackup
    {
        get => _config?.BackupManagerConfig.StopServerBeforeBackup ?? false;
        set
        {
            if (_config != null && _config.BackupManagerConfig.StopServerBeforeBackup != value)
            {
                _config.BackupManagerConfig.StopServerBeforeBackup = value;
                this.RaisePropertyChanged(nameof(StopServerBeforeBackup));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>备份模式（全量/增量）。</summary>
    [Category("备份设置")]
    [DisplayName("备份模式")]
    public BackupMode BackupMode
    {
        get => _config?.BackupManagerConfig.BackupMode ?? BackupMode.Full;
        set
        {
            if (_config != null && _config.BackupManagerConfig.BackupMode != value)
            {
                _config.BackupManagerConfig.BackupMode = value;
                this.RaisePropertyChanged(nameof(BackupMode));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>压缩等级（0-9）。</summary>
    [Category("备份设置")]
    [DisplayName("压缩等级")]
    public string CompactionLevel
    {
        get => _config?.BackupManagerConfig.CompactionLevel ?? "0";
        set
        {
            if (_config != null && _config.BackupManagerConfig.CompactionLevel != value)
            {
                _config.BackupManagerConfig.CompactionLevel = value;
                this.RaisePropertyChanged(nameof(CompactionLevel));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// SFTP 文件管理器视图模型，每次访问时从 SFTPClientConfig 新建实例。
    /// </summary>
    [Category("备份设置")]
    [DisplayName("SFTP文件管理")]
    public SftpFileManagerViewModel SftpFileManager
    {
        get
        {
            if (_config?.BackupManagerConfig.SFTPClientConfig != null)
            {
                return new SftpFileManagerViewModel(_config.BackupManagerConfig.SFTPClientConfig);
            }
            return new SftpFileManagerViewModel(new SFTPClientConfig());
        }
    }

    // ========== 在线聊天 ==========

    /// <summary>在线聊天服务器端口。</summary>
    [Category("在线聊天")]
    [DisplayName("服务器端口")]
    public string ServerPort
    {
        get => _config?.OnlineChattingSystemConfig.ServerPort ?? "8080";
        set
        {
            if (_config != null && _config.OnlineChattingSystemConfig.ServerPort != value)
            {
                _config.OnlineChattingSystemConfig.ServerPort = value;
                this.RaisePropertyChanged(nameof(ServerPort));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>第三方社交平台名称。</summary>
    [Category("在线聊天")]
    [DisplayName("第三方社交平台")]
    public string ThirdPartySocialPlatformName
    {
        get => _config?.OnlineChattingSystemConfig.ThirdPartySocialPlatformName ?? "";
        set
        {
            if (_config != null && _config.OnlineChattingSystemConfig.ThirdPartySocialPlatformName != value)
            {
                _config.OnlineChattingSystemConfig.ThirdPartySocialPlatformName = value;
                this.RaisePropertyChanged(nameof(ThirdPartySocialPlatformName));
                ConfigChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// 刷新所有属性的变更通知（在 Config 被替换或外部修改后调用）。
    /// </summary>
    public void RefreshAllProperties()
    {
        this.RaisePropertyChanged(nameof(MCServerName));
        this.RaisePropertyChanged(nameof(MCServerType));
        this.RaisePropertyChanged(nameof(MCServerDirectory));
        this.RaisePropertyChanged(nameof(JavaPath));
        this.RaisePropertyChanged(nameof(StartUpArguments));
        this.RaisePropertyChanged(nameof(AutoBackupEnabled));
        this.RaisePropertyChanged(nameof(BackupTimingMode));
        this.RaisePropertyChanged(nameof(DayInterval));
        this.RaisePropertyChanged(nameof(SpecificTime));
        this.RaisePropertyChanged(nameof(TimeInterval));
        this.RaisePropertyChanged(nameof(StopServerBeforeBackup));
        this.RaisePropertyChanged(nameof(BackupMode));
        this.RaisePropertyChanged(nameof(CompactionLevel));
        this.RaisePropertyChanged(nameof(ServerPort));
        this.RaisePropertyChanged(nameof(ThirdPartySocialPlatformName));
    }
}
