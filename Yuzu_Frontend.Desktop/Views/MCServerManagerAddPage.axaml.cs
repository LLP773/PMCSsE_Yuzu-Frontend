using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Material.Icons;
using Material.Icons.Avalonia;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;

namespace Yuzu_Frontend.Desktop.Views;

[Page("添加管理器", MaterialIconKind.Plus, Order = 2, IsCollection = false)]
public partial class MCServerManagerAddPage : NavigatedPageBase
{
    public MCServerManagerAddViewModel ViewModel { get; } = new();
    private DisposableManager? _disposableManager;

    public MCServerManagerAddPage()
    {
        InitializeComponent();
        
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsCreating))
        {
            UpdateSubmitButtonState();
        }
    }

    private void UpdateSubmitButtonState()
    {
        if (SubmitIcon != null && SubmitText != null)
        {
            SubmitIcon.Kind = ViewModel.IsCreating ? MaterialIconKind.Loading : MaterialIconKind.Plus;
            SubmitText.Text = ViewModel.IsCreating ? "创建中..." : "创建";
        }
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

    private void OnFormInputChanged(object? sender, TextChangedEventArgs e)
    {
        ViewModel.UpdateSubmitState();
    }

    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择服务端目录"
        });

        if (result.Count >= 1)
        {
            ViewModel.MCServerDirectory = result[0].Path.LocalPath;
            ViewModel.UpdateSubmitState();
        }
    }

    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择Java可执行文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java可执行文件") { Patterns = new[] { "*.exe", "*.bin" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        if (result.Count >= 1)
        {
            ViewModel.JavaPath = result[0].Path.LocalPath;
            ViewModel.UpdateSubmitState();
        }
    }

    private void SubmitButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.CreateNewManager();
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ResetForm();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _disposableManager = new DisposableManager();

        _disposableManager.RegisterDataPackSubscription(
            ViewModel.SubscribeToDataPacks,
            ViewModel.UnsubscribeFromDataPacks
        );

        if (Connection?.IsConnected == true)
        {
            ViewModel.LoadSupportedTypes();
        }

        UpdateSubmitButtonState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _disposableManager?.Dispose();
        _disposableManager = null;

        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }
}