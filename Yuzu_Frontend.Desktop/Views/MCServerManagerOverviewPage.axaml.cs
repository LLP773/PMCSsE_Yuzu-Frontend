using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;

namespace Yuzu_Frontend.Desktop.Views;

[Page("管理器总览", MaterialIconKind.Server, Order = 1, IsCollection = false)]
public partial class MCServerManagerOverviewPage : NavigatedPageBase
{
    public MCServerManagerOverviewViewModel ViewModel { get; } = new();
    private DisposableManager? _disposableManager;

    public MCServerManagerOverviewPage()
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

    private void RefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Refresh();
    }

    private void ViewArguments_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MCServerManagerItemModel manager)
        {
            ViewModel.ViewArguments(manager);
        }
    }

    private void LoadButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.LoadSelectedManager();
    }

    private void StartButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.StartMCServer();
    }

    private void StopButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.StopMCServer();
    }

    private void ManagerDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dataGrid && dataGrid.SelectedItem is MCServerManagerItemModel manager)
        {
            ViewModel.SelectedManager = manager;
        }
        else
        {
            ViewModel.SelectedManager = null;
        }
    }

    private void FirstPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToFirst();
    }

    private void PreviousPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToPrevious();
    }

    private void NextPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToNext();
    }

    private void LastPageButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.Pagination.NavigateToLast();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _disposableManager = new DisposableManager();

        if (this.FindControl<DataGrid>("ManagerDataGrid") is { } dataGrid)
        {
            _disposableManager.RegisterDataGridSelectionChanged(dataGrid, ManagerDataGrid_SelectionChanged);
        }

        _disposableManager.RegisterDataPackSubscription(
            ViewModel.SubscribeToDataPacks,
            ViewModel.UnsubscribeFromDataPacks
        );

        if (Connection?.IsConnected == true)
        {
            ViewModel.LoadManagers();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _disposableManager?.Dispose();
        _disposableManager = null;
    }
}

public class StatusColorConverter : IValueConverter
{
    public static StatusColorConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isRunning)
        {
            return isRunning ? new SolidColorBrush(Color.FromRgb(72, 187, 120)) : new SolidColorBrush(Color.FromRgb(189, 189, 189));
        }
        return new SolidColorBrush(Color.FromRgb(189, 189, 189));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class RunningManagerCountConverter : IValueConverter
{
    public static RunningManagerCountConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is System.Collections.Generic.IEnumerable<MCServerManagerItemModel> managers)
        {
            return managers.Where(m => m.IsMCServerRunning).Count().ToString();
        }
        return "0";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class LoadedManagerCountConverter : IValueConverter
{
    public static LoadedManagerCountConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is System.Collections.Generic.IEnumerable<MCServerManagerItemModel> managers)
        {
            return managers.Where(m => m.IsLoaded).Count().ToString();
        }
        return "0";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}