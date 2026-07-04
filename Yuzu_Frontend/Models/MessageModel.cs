using System;
using ReactiveUI;

namespace Yuzu_Frontend.Models;

public class MessageModel : ReactiveObject
{
    private string _message = "";
    private int _level; // 0=info, 1=success, 2=warning, 3=error
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
        0 => "信息",
        1 => "成功",
        2 => "警告",
        3 => "错误",
        _ => "信息"
    };

    public string LevelIcon => Level switch
    {
        0 => "Information",
        1 => "CheckCircle",
        2 => "Alert",
        3 => "AlertCircle",
        _ => "Information"
    };

    public string TimeText => CreatedAt.ToString("HH:mm:ss");
}
