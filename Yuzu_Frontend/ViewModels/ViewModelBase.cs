using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SukiUI.Dialogs;

namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// ViewModel 抽象基类。在 ToastsViewModel 之上扩展对话框能力与 IDisposable 模式，
/// 统一封装信息、错误、确认、输入等常用对话框的显示逻辑。
/// 当对话框管理器不可用时，自动降级为 Toast 提示。
/// 所有需要订阅事件/持有非托管资源的派生类，应在 Dispose 中取消订阅并释放资源。
/// </summary>
public abstract class ViewModelBase : ToastsViewModel, IDisposable
{
    /// <summary>SukiUI 对话框管理器，由依赖注入赋值；为空时对话框调用降级为 Toast。</summary>
    public ISukiDialogManager? DialogManager { get; set; }

    /// <summary>Dispose 是否已调用（防重复释放）。</summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>
    /// 释放托管资源。默认实现仅设置标志位，
    /// 派生类应重写此方法取消所有事件订阅并释放资源。
    /// </summary>
    /// <param name="disposing">true 表示来自显式 Dispose 调用，false 来自终结器。</param>
    protected virtual void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        if (disposing)
        {
            // 派生类在此处：
            // 1. -= 取消所有 C# 事件订阅（Connection / ConnectedBackends.CollectionChanged 等）
            // 2. Dispose() 所有 Reactive / IObservable Subscription
            // 3. 取消数据包订阅 / NativeClient.DataPackBus 订阅
        }
        IsDisposed = true;
    }

    /// <summary>显式释放资源。页面 OnUnloaded/OnDetachedFromVisualTree 时调用。</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>若派生类忘记重写 Dispose，此处作为最后保障（非强制）。</summary>
    ~ViewModelBase()
    {
        Dispose(false);
    }

    /// <summary>
    /// 显示消息对话框（默认 Info 类型）。仅当未指定按钮文本时使用默认"确定"按钮。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文。</param>
    /// <param name="buttons">自定义按钮列表。</param>
    public void ShowDialog(string title, string message, params DialogButton[] buttons)
    {
        ShowDialog(title, message, DialogType.Info, buttons);
    }

    /// <summary>
    /// 显示消息对话框（带类型）。Error 类型会以红色加粗字体渲染正文。
    /// DialogManager 为空时降级为 Toast。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文。</param>
    /// <param name="dialogType">对话框类型（影响样式）。</param>
    /// <param name="buttons">自定义按钮列表。</param>
    public void ShowDialog(string title, string message, DialogType dialogType, params DialogButton[] buttons)
    {
        if (DialogManager == null)
        {
            ShowToast(message, dialogType.ToToastType());
            return;
        }

        try
        {
            var stackPanel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

            if (!string.IsNullOrEmpty(message))
            {
                stackPanel.Children.Add(new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    // 错误类型使用红色加粗以突出显示
                    Foreground = dialogType == DialogType.Error ? new SolidColorBrush(Colors.Red) : null,
                    FontWeight = dialogType == DialogType.Error ? FontWeight.Bold : FontWeight.Normal
                });
            }

            var dialog = DialogManager.CreateDialog()
                .WithTitle(title)
                .WithContent(stackPanel);

            // 逐个添加按钮，回调异常被吞掉以避免对话框崩溃
            foreach (var button in buttons)
            {
                dialog.WithActionButton(button.Text, _ =>
                {
                    try { button.Callback?.Invoke(); } catch { }
                }, button.IsDefault);
            }

            dialog.TryShow();
        }
        catch (Exception ex)
        {
            ShowToast("显示对话框失败", ex.Message, ToastType.Error);
        }
    }

    /// <summary>
    /// 显示自定义控件作为内容的对话框。DialogManager 为空时降级为 Toast。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="content">作为内容的 Avalonia 控件。</param>
    /// <param name="buttons">自定义按钮列表。</param>
    public void ShowDialog(string title, Control content, params DialogButton[] buttons)
    {
        if (DialogManager == null)
        {
            ShowToast("无法显示对话框（管理器未初始化）", ToastType.Error);
            return;
        }

        try
        {
            var dialog = DialogManager.CreateDialog()
                .WithTitle(title)
                .WithContent(content);

            foreach (var button in buttons)
            {
                dialog.WithActionButton(button.Text, _ =>
                {
                    try { button.Callback?.Invoke(); } catch { }
                }, button.IsDefault);
            }

            dialog.TryShow();
        }
        catch (Exception ex)
        {
            ShowToast("显示对话框失败", ex.Message, ToastType.Error);
        }
    }

    /// <summary>
    /// 显示仅含"确定"按钮的信息提示对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文。</param>
    /// <param name="okButtonText">确定按钮文本，默认"确定"。</param>
    public void ShowMessageDialog(string title, string message, string? okButtonText = null)
    {
        ShowDialog(title, message, DialogType.Info,
            new DialogButton(okButtonText ?? "确定", null, true));
    }

    /// <summary>
    /// 显示仅含"确定"按钮的错误提示对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文。</param>
    /// <param name="okButtonText">确定按钮文本，默认"确定"。</param>
    public void ShowErrorDialog(string title, string message, string? okButtonText = null)
    {
        ShowDialog(title, message, DialogType.Error,
            new DialogButton(okButtonText ?? "确定", null, true));
    }

    /// <summary>
    /// 显示确认对话框，含"确定/取消"两个按钮。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文。</param>
    /// <param name="onConfirmed">用户点击确定时的回调。</param>
    /// <param name="onCancelled">用户点击取消时的回调（可选）。</param>
    /// <param name="confirmButtonText">确定按钮文本，默认"确定"。</param>
    /// <param name="cancelButtonText">取消按钮文本，默认"取消"。</param>
    public void ShowConfirmDialog(string title, string message, Action onConfirmed, Action? onCancelled = null,
        string? confirmButtonText = null, string? cancelButtonText = null)
    {
        ShowDialog(title, message, DialogType.Confirmation,
            new DialogButton(confirmButtonText ?? "确定", onConfirmed, true),
            new DialogButton(cancelButtonText ?? "取消", onCancelled, false));
    }

    /// <summary>
    /// 显示输入对话框，含一个文本框与"确定/取消"按钮。
    /// 用户点击确定时通过 onSubmitted 回调返回输入文本。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">对话框正文（提示语）。</param>
    /// <param name="onSubmitted">用户提交输入时的回调，参数为输入文本。</param>
    /// <param name="onCancelled">用户取消时的回调（可选）。</param>
    /// <param name="submitButtonText">提交按钮文本，默认"确定"。</param>
    /// <param name="cancelButtonText">取消按钮文本，默认"取消"。</param>
    /// <param name="placeholder">输入框占位提示文本。</param>
    public void ShowInputDialog(string title, string message, Action<string> onSubmitted, Action? onCancelled = null,
        string? submitButtonText = null, string? cancelButtonText = null, string? placeholder = null)
    {
        if (DialogManager == null)
        {
            ShowToast(message, ToastType.Info);
            return;
        }

        try
        {
            var stackPanel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };

            if (!string.IsNullOrEmpty(message))
            {
                stackPanel.Children.Add(new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var textBox = new TextBox { PlaceholderText = placeholder };
            stackPanel.Children.Add(textBox);

            var dialog = DialogManager.CreateDialog()
                .WithTitle(title)
                .WithContent(stackPanel);

            dialog.WithActionButton(submitButtonText ?? "确定", _ =>
            {
                try { onSubmitted(textBox.Text ?? ""); } catch { }
            }, true);

            if (onCancelled != null)
            {
                dialog.WithActionButton(cancelButtonText ?? "取消", _ =>
                {
                    try { onCancelled(); } catch { }
                }, false);
            }

            dialog.TryShow();
        }
        catch (Exception ex)
        {
            ShowToast("显示输入对话框失败", ex.Message, ToastType.Error);
        }
    }

    /// <summary>
    /// 对话框类型枚举，决定对话框的视觉样式与语义。
    /// </summary>
    public enum DialogType
    {
        /// <summary>信息提示。</summary>
        Info,
        /// <summary>警告提示。</summary>
        Warning,
        /// <summary>错误提示（红色加粗）。</summary>
        Error,
        /// <summary>确认询问。</summary>
        Confirmation,
        /// <summary>输入交互。</summary>
        Input
    }

    /// <summary>
    /// 对话框按钮定义，包含文本、点击回调与是否为默认按钮。
    /// </summary>
    public class DialogButton
    {
        /// <summary>按钮显示文本。</summary>
        public string Text { get; }
        /// <summary>按钮点击回调（可为空）。</summary>
        public Action? Callback { get; }
        /// <summary>是否为默认按钮（影响样式/焦点）。</summary>
        public bool IsDefault { get; }

        /// <summary>
        /// 初始化对话框按钮。
        /// </summary>
        /// <param name="text">按钮显示文本。</param>
        /// <param name="callback">点击回调。</param>
        /// <param name="isDefault">是否为默认按钮。</param>
        public DialogButton(string text, Action? callback = null, bool isDefault = false)
        {
            Text = text;
            Callback = callback;
            IsDefault = isDefault;
        }
    }
}

/// <summary>
/// DialogType 枚举到 ToastType 枚举的扩展映射。
/// </summary>
internal static class DialogTypeExtensions
{
    /// <summary>
    /// 将对话框类型转换为对应的 Toast 类型（用于对话框不可用时的降级提示）。
    /// </summary>
    /// <param name="dialogType">对话框类型。</param>
    /// <returns>对应的 Toast 类型。</returns>
    public static ToastsViewModel.ToastType ToToastType(this ViewModelBase.DialogType dialogType) => dialogType switch
    {
        ViewModelBase.DialogType.Error => ToastsViewModel.ToastType.Error,
        ViewModelBase.DialogType.Warning => ToastsViewModel.ToastType.Warning,
        _ => ToastsViewModel.ToastType.Info
    };
}