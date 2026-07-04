using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.Threading.Tasks;
using Yuzu_Frontend.Models.PropertyGrid;

namespace Yuzu_Frontend.Desktop.Views;

/// <summary>
/// SFTP 文件管理视图。作为子控件嵌入到管理器相关页面中，
/// 提供远程文件浏览、上传与下载能力。文件选择器由视图侧实现并注入到 ViewModel。
/// </summary>
public partial class SftpFileManagerView : UserControl
{
    /// <summary>初始化 <see cref="SftpFileManagerView"/> 的新实例并加载 XAML。</summary>
    public SftpFileManagerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 数据上下文变化时：将本视图实现的下载/上传路径选择回调注入到 ViewModel，
    /// 以便 ViewModel 在需要时通过 Avalonia StorageProvider 弹出原生选择器。
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is SftpFileManagerViewModel viewModel)
        {
            viewModel.DownloadPathSelector = DownloadPathSelector;
            viewModel.UploadPathSelector = UploadPathSelector;
        }
    }

    /// <summary>
    /// 下载文件路径选择器：弹出保存文件对话框，返回用户选择的本地保存路径。
    /// </summary>
    /// <param name="fileItem">待下载的远程文件项，用于预填建议文件名。</param>
    /// <returns>用户选择的本地路径；取消选择时返回 null。</returns>
    private async Task<string?> DownloadPathSelector(SftpFileItem fileItem)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"下载 {fileItem.Name}",
            SuggestedFileName = fileItem.Name
        });

        return result?.Path.LocalPath;
    }

    /// <summary>
    /// 上传文件路径选择器：弹出打开文件对话框，返回用户选择的待上传本地文件路径。
    /// </summary>
    /// <returns>用户选择的本地文件路径；取消选择时返回 null。</returns>
    private async Task<string?> UploadPathSelector()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = false
        });

        return result?.FirstOrDefault()?.Path.LocalPath;
    }

    /// <summary>
    /// 文件列表项点击事件：当点击的项为目录时触发导航到该目录。
    /// </summary>
    private void Border_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is SftpFileItem item && item.IsDirectory)
        {
            var viewModel = (SftpFileManagerViewModel)DataContext!;
            viewModel.NavigateToCommand.Execute(item.FullPath);
        }
    }
}
