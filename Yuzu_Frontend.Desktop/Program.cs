using System;
using Avalonia;
using ReactiveUI.Avalonia;

namespace Yuzu_Frontend.Desktop;

/// <summary>
/// 桌面平台的应用程序入口点。
/// 负责以经典桌面生命周期（Windows / macOS / Linux 经典桌面）启动 Avalonia 应用，
/// 并注册 ReactiveUI 与平台检测、字体、日志等基础设施。
/// </summary>
sealed class Program
{
    /// <summary>
    /// 应用程序主入口：构建 Avalonia 应用宿主并以经典桌面生命周期启动。
    /// </summary>
    /// <param name="args">命令行参数，将被转发给桌面生命周期宿主。</param>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// 构建 Avalonia <see cref="AppBuilder"/>：配置主应用类、启用平台检测、
    /// 启用跨平台字体、注册 ReactiveUI 以及日志输出。
    /// </summary>
    /// <returns>已配置好的 <see cref="AppBuilder"/> 实例。</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI(builder => { })
            .LogToTrace();
}
