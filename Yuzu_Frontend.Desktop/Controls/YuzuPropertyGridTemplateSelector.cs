using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SukiUI.Controls;
using SukiUI.Dialogs;
using Yuzu_Frontend.Models.PropertyGrid;
using Yuzu_Frontend.Desktop.Views;

namespace Yuzu_Frontend.Desktop.Controls;

/// <summary>
/// 属性网格模板选择器。扩展 SukiUI 的 <see cref="PropertyGridTemplateSelector"/>，
/// 在属性网格中为复杂类型（如 SFTP 文件管理器视图模型）和 [FilePath] 标记的路径字段
/// 提供自定义的交互逻辑：点击"更多信息"时弹出 SFTP 管理对话框，
/// 点击"浏览"时弹出系统文件/文件夹选择器并把结果写回属性。
/// </summary>
public partial class YuzuPropertyGridTemplateSelector : PropertyGridTemplateSelector
{
    /// <summary>
    /// 初始化模板选择器并加载关联的 XAML。
    /// </summary>
    public YuzuPropertyGridTemplateSelector()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 加载与本控件关联的 XAML 资源。
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 重写属性网格"更多信息"按钮的点击处理。
    /// 当被点击项绑定的是一个 <see cref="SftpFileManagerViewModel"/> 时，弹出 SFTP 管理对话框；
    /// 否则回退到基类的默认行为。
    /// </summary>
    /// <param name="sender">触发事件的可视化控件。</param>
    /// <param name="e">路由事件参数。</param>
    protected override void OnMoreInfoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;

        // 仅处理复杂数据项，且其值非空的情况
        if (control.DataContext is not ComplexTypeViewModel childViewModel || childViewModel.Value is null)
        {
            return;
        }

        // 针对 SFTP 文件管理器视图模型使用专用对话框呈现
        if (childViewModel.Value is SftpFileManagerViewModel sftpViewModel)
        {
            _ = ShowSftpManagerDialog(control, sftpViewModel);
            return;
        }

        base.OnMoreInfoClick(sender, e);
    }

    /// <summary>
    /// 以 SukiUI 对话框或独立窗口的形式展示 SFTP 文件管理器界面。
    /// 优先级：当前控件关联的 <see cref="SukiDialogHost"/> → 父窗口中查找的 SukiDialogHost → 独立窗口。
    /// </summary>
    /// <param name="control">触发展示的控件，用于定位顶级窗口。</param>
    /// <param name="viewModel">SFTP 文件管理器视图模型。</param>
    private async Task ShowSftpManagerDialog(Control control, SftpFileManagerViewModel viewModel)
    {
        var sukiDialogHost = SukiDialogHost;
        if (UseSukiHost)
        {
            // 路径一：已具备 SukiDialogHost，直接以对话框形式展示
            if (sukiDialogHost is not null)
            {
                sukiDialogHost.Manager
                    .CreateDialog()
                    .WithContent(new SftpFileManagerView { DataContext = viewModel })
                    .WithTitle("SFTP 文件管理")
                    .Dismiss().ByClickingBackground()
                    .TryShow();
            }
            else
            {
                // 路径二：尝试从父窗口的 Hosts 集合中查找 SukiDialogHost
                var root = TopLevel.GetTopLevel(control);
                if (root is not SukiWindow parentWindow)
                {
                    // 父级不是 SukiWindow，无法借用其对话框宿主，退化为独立窗口
                    await ShowWindowDialogAsync(control, viewModel);
                    return;
                }

                sukiDialogHost = parentWindow.Hosts
                    .Where(p => p is SukiDialogHost)
                    .Cast<SukiDialogHost>()
                    .FirstOrDefault();

                if (sukiDialogHost is not null)
                {
                    sukiDialogHost.Manager
                        .CreateDialog()
                        .WithContent(new SftpFileManagerView { DataContext = viewModel })
                        .WithTitle("SFTP 文件管理")
                        .Dismiss().ByClickingBackground()
                        .TryShow();
                }
                else
                {
                    // 父窗口中未找到任何 SukiDialogHost，退化为独立窗口
                    await ShowWindowDialogAsync(control, viewModel);
                }
            }
        }
        else
        {
            // 未启用 SukiHost，直接使用独立窗口
            await ShowWindowDialogAsync(control, viewModel);
        }
    }

    /// <summary>
    /// 以独立模态窗口的形式展示 SFTP 文件管理器界面。
    /// </summary>
    /// <param name="control">触发展示的控件，用于定位父窗口。</param>
    /// <param name="viewModel">SFTP 文件管理器视图模型。</param>
    private static async Task ShowWindowDialogAsync(Control control, SftpFileManagerViewModel viewModel)
    {
        var root = TopLevel.GetTopLevel(control);
        if (root is not Window parentWindow)
        {
            return;
        }

        var window = new Window
        {
            DataContext = viewModel,
            Title = "SFTP 文件管理",
            Width = 700,
            Height = 500,
            Content = new SftpFileManagerView { DataContext = viewModel }
        };

        await window.ShowDialog(parentWindow);
    }

    /// <summary>
    /// "浏览"按钮点击处理：根据 <see cref="FilePathViewModel.PickerMode"/> 弹出文件夹或文件选择器，
    /// 并将用户选择的路径写回到对应属性。
    /// </summary>
    /// <param name="sender">触发事件的对象，预期为 Button。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.DataContext is not FilePathViewModel filePathViewModel) return;

        var topLevel = TopLevel.GetTopLevel(button);
        if (topLevel == null) return;

        if (filePathViewModel.PickerMode == FilePickerMode.Folder)
        {
            // 文件夹选择模式：用于选择目录路径
            var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"选择{filePathViewModel.DisplayName}"
            });

            if (result.Count >= 1)
            {
                filePathViewModel.Value = result[0].Path.LocalPath;
            }
        }
        else
        {
            // 文件选择模式：默认过滤可执行文件与所有文件
            var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"选择{filePathViewModel.DisplayName}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件") { Patterns = new[] { "*.exe", "*.bin" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                }
            });

            if (result.Count >= 1)
            {
                filePathViewModel.Value = result[0].Path.LocalPath;
            }
        }
    }
}
