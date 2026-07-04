using System;
using ReactiveUI;

namespace Yuzu_Frontend.Models;

/// <summary>
/// 服务器日志条目
/// </summary>
public class LogEntryModel : ReactiveObject
{
    private ulong _id;
    private string _content = "";
    private DateTime _timestamp = DateTime.Now;
    private string _level = "INFO";

    public ulong Id
    {
        get => _id;
        set => this.RaiseAndSetIfChanged(ref _id, value);
    }

    public string Content
    {
        get => _content;
        set => this.RaiseAndSetIfChanged(ref _content, value);
    }

    public DateTime Timestamp
    {
        get => _timestamp;
        set => this.RaiseAndSetIfChanged(ref _timestamp, value);
    }

    public string Level
    {
        get => _level;
        set => this.RaiseAndSetIfChanged(ref _level, value);
    }

    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    public string DisplayText => $"[{TimeText}] [{Level}] {Content}";
}

/// <summary>
/// Toast/消息提示模型
/// </summary>
public class MessageItemModel : ReactiveObject
{
    private string _message = "";
    private int _level; // 0=普通,1=警告,2=错误,3=成功
    private DateTime _createdAt = DateTime.Now;

    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public int Level
    {
        get => _level;
        set => this.RaiseAndSetIfChanged(ref _level, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => this.RaiseAndSetIfChanged(ref _createdAt, value);
    }

    public string LevelText => Level switch
    {
        1 => "警告",
        2 => "错误",
        3 => "成功",
        _ => "信息"
    };

    public string LevelIcon => Level switch
    {
        1 => "Alert",
        2 => "AlertCircle",
        3 => "CheckCircle",
        _ => "Information"
    };
}

/// <summary>
/// 连接历史记录
/// </summary>
public class ConnectionHistoryItemModel : ReactiveObject
{
    private string _backendAddress = "";
    private string _backendPort = "";
    private string _backendPassword = "";
    private DateTime _timestamp = DateTime.Now;
    private int _useCount = 1;
    private bool _isRememberPassword;

    public string BackendAddress
    {
        get => _backendAddress;
        set => this.RaiseAndSetIfChanged(ref _backendAddress, value);
    }

    public string BackendPort
    {
        get => _backendPort;
        set => this.RaiseAndSetIfChanged(ref _backendPort, value);
    }

    public string BackendPassword
    {
        get => _backendPassword;
        set => this.RaiseAndSetIfChanged(ref _backendPassword, value);
    }

    public DateTime Timestamp
    {
        get => _timestamp;
        set => this.RaiseAndSetIfChanged(ref _timestamp, value);
    }

    public int UseCount
    {
        get => _useCount;
        set => this.RaiseAndSetIfChanged(ref _useCount, value);
    }

    public bool IsRememberPassword
    {
        get => _isRememberPassword;
        set => this.RaiseAndSetIfChanged(ref _isRememberPassword, value);
    }

    public string BackendName => $"{BackendAddress}:{BackendPort}";

    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm");

    public string UseCountText => $"已使用 {UseCount} 次";
}
