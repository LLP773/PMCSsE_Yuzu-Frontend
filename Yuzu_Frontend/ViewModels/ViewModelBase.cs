using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SukiUI.Dialogs;

namespace Yuzu_Frontend.ViewModels;

public abstract class ViewModelBase : ToastsViewModel
{
    public ISukiDialogManager? DialogManager { get; set; }

    public void ShowDialog(string title, string message, params DialogButton[] buttons)
    {
        ShowDialog(title, message, DialogType.Info, buttons);
    }

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
                    Foreground = dialogType == DialogType.Error ? new SolidColorBrush(Colors.Red) : null,
                    FontWeight = dialogType == DialogType.Error ? FontWeight.Bold : FontWeight.Normal
                });
            }

            var dialog = DialogManager.CreateDialog()
                .WithTitle(title)
                .WithContent(stackPanel);

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

    public void ShowMessageDialog(string title, string message, string? okButtonText = null)
    {
        ShowDialog(title, message, DialogType.Info,
            new DialogButton(okButtonText ?? "确定", null, true));
    }

    public void ShowErrorDialog(string title, string message, string? okButtonText = null)
    {
        ShowDialog(title, message, DialogType.Error,
            new DialogButton(okButtonText ?? "确定", null, true));
    }

    public void ShowConfirmDialog(string title, string message, Action onConfirmed, Action? onCancelled = null,
        string? confirmButtonText = null, string? cancelButtonText = null)
    {
        ShowDialog(title, message, DialogType.Confirmation,
            new DialogButton(confirmButtonText ?? "确定", onConfirmed, true),
            new DialogButton(cancelButtonText ?? "取消", onCancelled, false));
    }

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

    public enum DialogType
    {
        Info,
        Warning,
        Error,
        Confirmation,
        Input
    }

    public class DialogButton
    {
        public string Text { get; }
        public Action? Callback { get; }
        public bool IsDefault { get; }

        public DialogButton(string text, Action? callback = null, bool isDefault = false)
        {
            Text = text;
            Callback = callback;
            IsDefault = isDefault;
        }
    }
}

internal static class DialogTypeExtensions
{
    public static ToastsViewModel.ToastType ToToastType(this ViewModelBase.DialogType dialogType) => dialogType switch
    {
        ViewModelBase.DialogType.Error => ToastsViewModel.ToastType.Error,
        ViewModelBase.DialogType.Warning => ToastsViewModel.ToastType.Warning,
        _ => ToastsViewModel.ToastType.Info
    };
}