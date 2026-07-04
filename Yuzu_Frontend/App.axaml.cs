using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Yuzu_Frontend.ViewModels;
using Yuzu_Frontend.Views;

namespace Yuzu_Frontend;

/// <summary>
/// Avalonia 共享项目中的顶层应用程序类。负责解析应用资源并根据当前运行时（桌面 / 单视图）
/// 创建对应的主窗口或主视图。实际生产入口由 <see cref="Yuzu_Frontend.Desktop.Program"/> 驱动，
/// 且桌面平台项目内部的 <c>Desktop.App</c> 会替代此处的初始化逻辑。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 解析当前 <c>App.axaml</c> 中声明的应用程序资源（样式、主题、语言等），供后续控件引用。
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    /// <summary>
    /// 在 Avalonia 框架初始化完成时执行：
    /// 经典桌面平台使用 <see cref="MainWindow"/> 作为主窗体，单视图平台使用 <see cref="MainView"/>，
    /// 两者均绑定 <see cref="MainViewModel"/> 作为数据上下文。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

}