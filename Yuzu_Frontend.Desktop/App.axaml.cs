using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CodeWF.Log.Core;

namespace Yuzu_Frontend.Desktop;

/// <summary>
/// 桌面平台版的应用程序根类。
/// 在 Avalonia 框架初始化完成后，将主窗口设置为 <see cref="YuzuView"/>，
/// 后者承载了带有侧栏与页签的管理器界面。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 加载与本类关联的 XAML 资源，完成 UI 层的基础初始化。
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    /// <summary>
    /// 在 Avalonia 框架初始化完成后被调用：
    /// 如果当前为经典桌面生命周期，则将主窗口设置为 <see cref="YuzuView"/>，
    /// 以便呈现完整的管理器主界面。
    /// 同时初始化 CodeWF.Log.Core 全局日志引擎，供 LogView 控件读取。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // 初始化全局日志引擎：日志控制台页面通过 Logger 静态方法写入，
        // CodeWF.LogViewer.Avalonia.LogView 从 Logger.UserLogs feed 自动读取显示。
        Logger.Initialize(new LoggerOptions
        {
            MinimumLevel = LogType.Debug,
            EnableConsole = false,
            RecentUserLogCapacity = 30000,
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new YuzuView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
