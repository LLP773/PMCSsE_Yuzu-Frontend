using Avalonia.Controls;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 所有被 <see cref="Services.PageDiscoveryService"/> 发现并装配为可导航页面的
/// <see cref="UserControl"/> 必须实现此接口。
/// 宿主在页面被展示之前会调用 <see cref="InitializePage"/>，
/// 以便注入应用程序全局共享的 Toast 管理器、对话框管理器以及业务 ViewModel。
/// </summary>
public interface INavigatedPage
{
    /// <summary>
    /// 由导航宿主在页面被展示前调用，用于注入共享的 UI 服务与业务视图模型。
    /// 实现类可以将传入的对象保存为私有字段，并在随后的交互中使用。
    /// </summary>
    /// <param name="toastManager">全局的 Toast 提示管理器，可用于显示简短通知。</param>
    /// <param name="dialogManager">全局的对话框管理器，可用于弹出模态对话框。</param>
    /// <param name="connectionViewModel">可选的连接视图模型，负责与后端通信并维护连接状态。</param>
    /// <param name="serverManagerViewModel">可选的服务器管理器视图模型，负责管理器的增删改查与启停。</param>
    /// <param name="logsViewModel">可选的日志视图模型，负责日志轮询、命令发送与历史记录。</param>
    void InitializePage(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel? connectionViewModel = null);
}
