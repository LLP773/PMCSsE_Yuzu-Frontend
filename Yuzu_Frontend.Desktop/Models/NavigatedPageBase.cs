using Avalonia;
using Avalonia.Controls;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Models;

public abstract class NavigatedPageBase : UserControl, INavigatedPage
{
    public static readonly StyledProperty<ConnectionViewModel?> ConnectionProperty =
        AvaloniaProperty.Register<NavigatedPageBase, ConnectionViewModel?>(nameof(Connection));


    public static readonly StyledProperty<ISukiToastManager?> ToastManagerProperty =
        AvaloniaProperty.Register<NavigatedPageBase, ISukiToastManager?>(nameof(ToastManager));

    public static readonly StyledProperty<ISukiDialogManager?> DialogManagerProperty =
        AvaloniaProperty.Register<NavigatedPageBase, ISukiDialogManager?>(nameof(DialogManager));

    public ConnectionViewModel? Connection
    {
        get => GetValue(ConnectionProperty);
        protected set => SetValue(ConnectionProperty, value);
    }

    public ISukiToastManager? ToastManager
    {
        get => GetValue(ToastManagerProperty);
        protected set => SetValue(ToastManagerProperty, value);
    }

    public ISukiDialogManager? DialogManager
    {
        get => GetValue(DialogManagerProperty);
        protected set => SetValue(DialogManagerProperty, value);
    }

    protected NavigatedPageBase()
    {
        DataContext = this;
    }

    public virtual void InitializePage(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel? connectionViewModel = null)

    {
        ToastManager = toastManager;
        DialogManager = dialogManager;
        Connection = connectionViewModel;


        if (Connection != null)
        {
            Connection.ToastManager = toastManager;
            Connection.DialogManager = dialogManager;
        }

    }
}
