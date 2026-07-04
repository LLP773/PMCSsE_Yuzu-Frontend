using Avalonia;
using Avalonia.Controls;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 可导航页面的抽象基类。继承自 <see cref="UserControl"/> 并实现 <see cref="INavigatedPage"/>，
/// 为所有页面统一暴露 Toast/Dialog/连接等共享服务的 Avalonia 依赖属性，
/// 子类可直接在 XAML 中绑定这些属性，而无需各自实现注入逻辑。
/// </summary>
public abstract class NavigatedPageBase : UserControl, INavigatedPage
{
    /// <summary>当前页面关联的连接视图模型（可为空），用于与后端通信。</summary>
    public static readonly StyledProperty<ConnectionViewModel?> ConnectionProperty =
        AvaloniaProperty.Register<NavigatedPageBase, ConnectionViewModel?>(nameof(Connection));


    /// <summary>全局共享的 Toast 提示管理器依赖属性。</summary>
    public static readonly StyledProperty<ISukiToastManager?> ToastManagerProperty =
        AvaloniaProperty.Register<NavigatedPageBase, ISukiToastManager?>(nameof(ToastManager));

    /// <summary>全局共享的对话框管理器依赖属性。</summary>
    public static readonly StyledProperty<ISukiDialogManager?> DialogManagerProperty =
        AvaloniaProperty.Register<NavigatedPageBase, ISukiDialogManager?>(nameof(DialogManager));

    /// <summary>获取或设置当前页面关联的连接视图模型。</summary>
    public ConnectionViewModel? Connection
    {
        get => GetValue(ConnectionProperty);
        protected set => SetValue(ConnectionProperty, value);
    }

    /// <summary>获取或设置注入的 Toast 提示管理器。</summary>
    public ISukiToastManager? ToastManager
    {
        get => GetValue(ToastManagerProperty);
        protected set => SetValue(ToastManagerProperty, value);
    }

    /// <summary>获取或设置注入的对话框管理器。</summary>
    public ISukiDialogManager? DialogManager
    {
        get => GetValue(DialogManagerProperty);
        protected set => SetValue(DialogManagerProperty, value);
    }

    /// <summary>
    /// 构造函数。将 <see cref="Avalonia.Controls.Control.DataContext"/> 设置为页面自身，
    /// 以便 XAML 中可直接绑定本类暴露的依赖属性而无需额外的 DataContext 设置。
    /// </summary>
    protected NavigatedPageBase()
    {
        DataContext = this;
    }

    /// <summary>
    /// 由导航宿主在页面被展示前调用，注入共享的 Toast/Dialog 管理器与连接视图模型，
    /// 并将这些管理器同步传递给连接视图模型，保证业务层也能弹出提示与对话框。
    /// </summary>
    /// <param name="toastManager">全局 Toast 提示管理器。</param>
    /// <param name="dialogManager">全局对话框管理器。</param>
    /// <param name="connectionViewModel">连接视图模型（可选）。</param>
    public virtual void InitializePage(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel? connectionViewModel = null)

    {
        ToastManager = toastManager;
        DialogManager = dialogManager;
        Connection = connectionViewModel;

        // 将共享管理器透传给连接视图模型，便于业务逻辑直接使用
        if (Connection != null)
        {
            Connection.ToastManager = toastManager;
            Connection.DialogManager = dialogManager;
        }

    }
}
