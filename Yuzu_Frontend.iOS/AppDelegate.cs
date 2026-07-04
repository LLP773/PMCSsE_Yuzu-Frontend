using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using ReactiveUI.Avalonia;

namespace Yuzu_Frontend.iOS;

/// <summary>
/// iOS 平台应用委托。继承自 <see cref="AvaloniaAppDelegate{TApp}"/>，
/// 负责 iOS 应用生命周期事件处理与 Avalonia <see cref="App"/> 的初始化配置。
/// </summary>
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    /// <summary>
    /// 自定义 Avalonia AppBuilder 配置：启用 Inter 内置字体与 ReactiveUI 扩展。
    /// </summary>
    /// <param name="builder">原始 AppBuilder 实例。</param>
    /// <returns>配置完成后的 AppBuilder。</returns>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }
}
