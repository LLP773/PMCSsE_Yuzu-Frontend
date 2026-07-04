using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using ReactiveUI.Avalonia;
using Yuzu_Frontend;

/// <summary>
/// WebAssembly 浏览器平台程序入口。负责构建 Avalonia 应用、配置字体与 ReactiveUI 扩展，
/// 并以 "out" 为宿主元素启动浏览器应用。
/// </summary>
internal sealed partial class Program
{
    /// <summary>
    /// 浏览器应用主入口：构建 Avalonia App、应用扩展配置后异步启动浏览器应用。
    /// </summary>
    /// <param name="args">启动参数。</param>
    /// <returns>表示异步启动操作的 Task。</returns>
    private static Task Main(string[] args) => BuildAvaloniaApp()
            .WithInterFont()
            .UseReactiveUI()
            .StartBrowserAppAsync("out");

    /// <summary>
    /// 构建 Avalonia 应用配置：使用 <see cref="App"/> 作为应用类型。
    /// </summary>
    /// <returns>配置完成的 AppBuilder。</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}