using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;

namespace Yuzu_Frontend.Android;

/// <summary>
/// Android 平台主 Activity。作为 Avalonia 应用在 Android 上的入口 Activity，
/// 声明应用标签、主题、图标及配置变化处理，并承载 <see cref="App"/> 的初始化。
/// </summary>
[Activity(
    Label = "Yuzu_Frontend.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
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
