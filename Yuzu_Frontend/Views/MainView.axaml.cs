using Avalonia.Controls;

namespace Yuzu_Frontend.Views;

/// <summary>
/// 主视图（单视图平台入口）。在移动端、浏览器等单视图应用模式下作为根视图承载主界面内容，
/// 数据上下文由 <see cref="App.OnFrameworkInitializationCompleted"/> 设置为 <see cref="MainViewModel"/>。
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>初始化 <see cref="MainView"/> 的新实例并加载 XAML。</summary>
    public MainView()
    {
        InitializeComponent();
    }
}