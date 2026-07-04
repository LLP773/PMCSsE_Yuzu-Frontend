using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Material.Icons;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;

namespace Yuzu_Frontend.Desktop.Views;

[Page("日志控制台", MaterialIconKind.FileText, Order = 3, IsCollection = false)]
public partial class MCServerManagerLogConsolePage : NavigatedPageBase
{
    public MCServerManagerLogConsoleViewModel ViewModel { get; } = new();
    private DisposableManager? _disposableManager;

    public MCServerManagerLogConsolePage()
    {
        InitializeComponent();
    }

    public override void InitializePage(ISukiToastManager toastManager, ISukiDialogManager dialogManager, ConnectionViewModel? connectionViewModel = null)
    {
        base.InitializePage(toastManager, dialogManager, connectionViewModel);

        ViewModel.ToastManager = toastManager;
        ViewModel.DialogManager = dialogManager;

        if (connectionViewModel != null)
        {
            ViewModel.Initialize(connectionViewModel);
        }
    }

    private void ClearLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearLogsCommand();
    }

    private void GetOlderLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.GetOlderLogs();
    }

    private void ClearFilterButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearFilter();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _disposableManager = new DisposableManager();

        if (Connection?.IsConnected == true)
        {
            ViewModel.RefreshManagerList();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _disposableManager?.Dispose();
        _disposableManager = null;
    }
}

public class BoolNegationConverter : IValueConverter
{
    public static BoolNegationConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return value;
    }
}