using Avalonia.Controls;

namespace Yuzu_Frontend.Views;

/// <summary>
/// 主窗口（经典桌面平台入口）。在桌面平台作为应用根窗口承载主界面内容，
/// 数据上下文由 <see cref="App.OnFrameworkInitializationCompleted"/> 设置为 <see cref="MainViewModel"/>。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>初始化 <see cref="MainWindow"/> 的新实例并加载 XAML。</summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}