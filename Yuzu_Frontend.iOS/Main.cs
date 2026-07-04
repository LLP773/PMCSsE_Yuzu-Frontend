using UIKit;

namespace Yuzu_Frontend.iOS;

/// <summary>
/// iOS 平台应用入口类。包含静态 <see cref="Main"/> 方法，由 iOS 运行时调用以启动 UIApplication。
/// </summary>
public class Application
{
    /// <summary>
    /// iOS 应用程序入口。调用 <see cref="UIApplication.Main"/> 启动应用，
    /// 并将 <see cref="AppDelegate"/> 注册为应用委托以接管生命周期与 UI 初始化。
    /// </summary>
    /// <param name="args">启动参数。</param>
    static void Main(string[] args)
    {
        // 第二个参数为 UIApplicationDelegate 类型，可通过修改此处替换为自定义委托类
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
