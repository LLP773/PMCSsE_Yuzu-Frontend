using System;
using Avalonia;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using ReactiveUI;
using SukiUI.Toasts;

namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// Toast通知功能的抽象基类，提供统一的Toast消息显示能力。
/// 继承自ReactiveObject，支持响应式属性变更通知。
/// 所有需要使用Toast通知功能的ViewModel应间接继承此类（通过ViewModelBase）。
/// </summary>
public abstract class ToastsViewModel : ReactiveObject
{
    /// <summary>
    /// SukiUI Toast管理器实例，用于创建和管理Toast通知。
    /// 通过依赖注入方式赋值，由DI容器在ViewModel初始化时注入。
    /// </summary>
    public ISukiToastManager? ToastManager { get; set; }

    /// <summary>
    /// 显示Toast通知消息（简洁版本）。
    /// 使用SukiUI标准的链式API创建Toast，支持多种通知类型和自动消失配置。
    /// 自动处理线程安全，确保Toast操作始终在UI线程上执行。
    /// </summary>
    /// <param name="title">Toast显示的主要标题信息（必填）</param>
    /// <param name="type">通知类型，Info/Success/Warning/Error</param>
    /// <exception cref="ArgumentNullException">当title为null时抛出</exception>
    /// <exception cref="ArgumentException">当title为空字符串时抛出</exception>
    public void ShowToast(string title, ToastType type = ToastType.Info)
    {
        ValidateTitle(title);
        
        if (ToastManager == null) return;
        
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowToastInternal(title, string.Empty, type);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ShowToastInternal(title, string.Empty, type));
            }
        }
        catch { }
    }

    /// <summary>
    /// 显示Toast通知消息（完整版本）。
    /// 使用SukiUI标准的链式API创建Toast，支持多种通知类型和自动消失配置。
    /// 自动处理线程安全，确保Toast操作始终在UI线程上执行。
    /// </summary>
    /// <param name="title">Toast显示的主要标题信息（必填），用于展示核心提示内容</param>
    /// <param name="message">Toast显示的详细补充信息（可选），用于展示错误码、详细描述等</param>
    /// <param name="type">通知类型，Info/Success/Warning/Error</param>
    /// <exception cref="ArgumentNullException">当title为null时抛出</exception>
    /// <exception cref="ArgumentException">当title为空字符串时抛出</exception>
    public void ShowToast(string title, string message, ToastType type)
    {
        ValidateTitle(title);
        
        if (ToastManager == null) return;
        
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowToastInternal(title, message, type);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ShowToastInternal(title, message, type));
            }
        }
        catch { }
    }

    /// <summary>
    /// 验证title参数是否有效。
    /// </summary>
    /// <param name="title">待验证的标题字符串</param>
    /// <exception cref="ArgumentNullException">当title为null时抛出</exception>
    /// <exception cref="ArgumentException">当title为空字符串时抛出</exception>
    private static void ValidateTitle(string title)
    {
        if (title == null)
        {
            throw new ArgumentNullException(nameof(title), "Toast标题不能为空");
        }
        
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Toast标题不能为空字符串", nameof(title));
        }
    }

    /// <summary>
    /// 内部方法，实际执行Toast显示逻辑。
    /// 必须在UI线程上调用。
    /// </summary>
    /// <param name="title">Toast显示的主要标题信息</param>
    /// <param name="message">Toast显示的详细补充信息</param>
    /// <param name="type">通知类型，Info/Success/Warning/Error</param>
    private void ShowToastInternal(string title, string message, ToastType type)
    {
        if (ToastManager == null) return;
        try
        {
            var toastBuilder = ToastManager.CreateToast()
                .OfType(GetNotificationType(type))
                .WithTitle(title)
                .Dismiss().After(TimeSpan.FromSeconds(3))
                .Dismiss().ByClicking();
            
            if (!string.IsNullOrEmpty(message))
            {
                toastBuilder.WithContent(message);
            }
            
            toastBuilder.Queue();
        }
        catch { }
    }

    /// <summary>
    /// 将项目自定义的ToastType枚举转换为SukiUI标准的NotificationType枚举。
    /// 实现项目内部类型与框架标准类型的映射。
    /// </summary>
    /// <param name="type">项目自定义的Toast通知类型</param>
    /// <returns>SukiUI标准的NotificationType枚举值</returns>
    private static NotificationType GetNotificationType(ToastType type) => type switch
    {
        ToastType.Success => NotificationType.Success,
        ToastType.Warning => NotificationType.Warning,
        ToastType.Error => NotificationType.Error,
        _ => NotificationType.Information
    };

    /// <summary>
    /// Toast通知类型枚举，定义项目支持的四种通知状态。
    /// 与Avalonia.Controls.Notifications.NotificationType对应映射。
    /// </summary>
    public enum ToastType
    {
        /// <summary>
        /// 信息通知，用于展示普通提示信息
        /// </summary>
        Info,

        /// <summary>
        /// 成功通知，用于展示操作成功的反馈
        /// </summary>
        Success,

        /// <summary>
        /// 警告通知，用于展示需要用户注意的警告信息
        /// </summary>
        Warning,

        /// <summary>
        /// 错误通知，用于展示操作失败或异常情况
        /// </summary>
        Error
    }
}