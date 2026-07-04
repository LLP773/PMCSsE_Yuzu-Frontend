using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
    }

    /// <summary>
    /// 在 Avalonia 框架初始化完成后被调用：
    /// 如果当前为经典桌面生命周期，则将主窗口设置为 <see cref="YuzuView"/>，
    /// 以便呈现完整的管理器主界面。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new YuzuView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
