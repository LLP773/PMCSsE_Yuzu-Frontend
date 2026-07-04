using System;
using ReactiveUI;

namespace Yuzu_Frontend.Models;

/// <summary>
/// 通用消息提示模型，供 UI 显示一条带级别的提示消息。
/// 继承 <see cref="ReactiveObject"/> 以支持属性变更通知。
/// </summary>
public class MessageModel : ReactiveObject
{
    private string _message = "";                // 消息内容
    private int _level; // 0=info, 1=success, 2=warning, 3=error
    private DateTime _createdAt = DateTime.Now;  // 创建时间

    /// <summary>消息内容。</summary>
    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// <summary>消息级别（0=信息，1=成功，2=警告，3=错误）。</summary>
    public int Level
    {
        get => _level;
        set => this.RaiseAndSetIfChanged(ref _level, value);
    }

    /// <summary>消息创建时间。</summary>
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => this.RaiseAndSetIfChanged(ref _createdAt, value);
    }

    /// <summary>级别对应的中文文本。</summary>
    public string LevelText => Level switch
    {
        0 => "信息",
        1 => "成功",
        2 => "警告",
        3 => "错误",
        _ => "信息"
    };

    /// <summary>级别对应的 Material 图标名称。</summary>
    public string LevelIcon => Level switch
    {
        0 => "Information",
        1 => "CheckCircle",
        2 => "Alert",
        3 => "AlertCircle",
        _ => "Information"
    };

    /// <summary>格式化的时间戳文本（HH:mm:ss）。</summary>
    public string TimeText => CreatedAt.ToString("HH:mm:ss");
}
