
namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// 主视图模型。作为应用启动时的默认绑定上下文，提供欢迎页所需的展示文本。
/// </summary>
public class MainViewModel : ViewModelBase
{
    /// <summary>欢迎页展示的问候语。</summary>
    public string Greeting { get; } = "Welcome to Avalonia!";
}
