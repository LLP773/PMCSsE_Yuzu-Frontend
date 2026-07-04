using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using PMCSsE_Communicator;
using ReactiveUI;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Yuzu_Frontend.Models.PropertyGrid;

/// <summary>
/// SFTP 远程文件管理器视图模型。
/// 封装 SFTP 连接、目录浏览、文件上传/下载/删除等操作，
/// 用于在 PropertyGrid 中展示远程备份服务器的文件管理界面。
/// </summary>
public class SftpFileManagerViewModel : ReactiveObject, IDisposable
{
    private readonly SFTPClientConfig _config;     // SFTP 连接配置
    private SftpClient? _sftpClient;               // SSH.NET SFTP 客户端实例
    private bool _isConnected;                    // 是否已连接
    private bool _isConnecting;                   // 是否正在连接中
    private string _currentRemotePath = "/";       // 当前远程目录路径
    private string _connectionStatus = "未连接";   // 连接状态文本
    private string _transferProgress = "";         // 传输进度文本
    private bool _isTransferring;                 // 是否正在传输文件
    private ObservableCollection<SftpFileItem> _remoteFiles = [];  // 当前目录下的远程文件列表

    /// <summary>当前远程目录下的文件列表。</summary>
    public ObservableCollection<SftpFileItem> RemoteFiles
    {
        get => _remoteFiles;
        set => this.RaiseAndSetIfChanged(ref _remoteFiles, value);
    }

    /// <summary>是否已连接到 SFTP 服务器。</summary>
    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>是否正在建立连接。</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    /// <summary>连接状态文本（未连接 / 连接中 / 已连接 / 失败信息）。</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    /// <summary>当前浏览的远程目录路径。</summary>
    public string CurrentRemotePath
    {
        get => _currentRemotePath;
        set => this.RaiseAndSetIfChanged(ref _currentRemotePath, value);
    }

    /// <summary>文件传输进度文本。</summary>
    public string TransferProgress
    {
        get => _transferProgress;
        set => this.RaiseAndSetIfChanged(ref _transferProgress, value);
    }

    /// <summary>是否正在传输文件。</summary>
    public bool IsTransferring
    {
        get => _isTransferring;
        set => this.RaiseAndSetIfChanged(ref _isTransferring, value);
    }

    private bool _canConnect;
    /// <summary>是否允许执行连接操作（非连接中且非已连接）。</summary>
    public bool CanConnect
    {
        get => _canConnect;
        set => this.RaiseAndSetIfChanged(ref _canConnect, value);
    }

    private bool _canUpload;
    /// <summary>是否允许执行上传操作（已连接且非传输中）。</summary>
    public bool CanUpload
    {
        get => _canUpload;
        set => this.RaiseAndSetIfChanged(ref _canUpload, value);
    }

    /// <summary>SFTP 服务器主机地址。</summary>
    public string Host => _config.Host;
    /// <summary>SFTP 服务器端口。</summary>
    public int Port => _config.Port;
    /// <summary>SFTP 登录用户名。</summary>
    public string Username => _config.UserName;
    /// <summary>SFTP 登录密码。</summary>
    public string Password => _config.Password;

    /// <summary>连接命令。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> ConnectCommand { get; }
    /// <summary>断开连接命令。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> DisconnectCommand { get; }
    /// <summary>测试连接命令。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> TestConnectionCommand { get; }
    /// <summary>下载文件命令。</summary>
    public ReactiveCommand<SftpFileItem, ReactiveUI.Primitives.RxVoid> DownloadCommand { get; }
    /// <summary>上传文件命令。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> UploadCommand { get; }
    /// <summary>删除文件命令。</summary>
    public ReactiveCommand<SftpFileItem, ReactiveUI.Primitives.RxVoid> DeleteCommand { get; }
    /// <summary>导航到指定目录命令。</summary>
    public ReactiveCommand<string, ReactiveUI.Primitives.RxVoid> NavigateToCommand { get; }

    /// <summary>下载路径选择器回调，由外部 UI 提供保存文件对话框。</summary>
    public Func<SftpFileItem, Task<string?>>? DownloadPathSelector { get; set; }
    /// <summary>上传路径选择器回调，由外部 UI 提供打开文件对话框。</summary>
    public Func<Task<string?>>? UploadPathSelector { get; set; }

    /// <summary>
    /// 初始化 <see cref="SftpFileManagerViewModel"/> 的新实例，创建各操作命令并绑定异常处理。
    /// </summary>
    /// <param name="config">SFTP 连接配置。</param>
    public SftpFileManagerViewModel(SFTPClientConfig config)
    {
        _config = config;

        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync);
        DisconnectCommand = ReactiveCommand.Create(Disconnect);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        DownloadCommand = ReactiveCommand.CreateFromTask<SftpFileItem>(DownloadFileAsync);
        UploadCommand = ReactiveCommand.CreateFromTask(UploadFileAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask<SftpFileItem>(DeleteFileAsync);
        NavigateToCommand = ReactiveCommand.Create<string>(NavigateTo);

        ConnectCommand.ThrownExceptions.Subscribe(ex =>
        {
            ConnectionStatus = $"连接失败: {ex.Message}";
            IsConnecting = false;
            UpdateCanConnect();
        });

        UpdateCanConnect();
        UpdateCanUpload();
    }

    /// <summary>更新 CanConnect 状态：非连接中且非已连接时允许连接。</summary>
    private void UpdateCanConnect()
    {
        CanConnect = !IsConnecting && !IsConnected;
    }

    /// <summary>更新 CanUpload 状态：已连接且非传输中时允许上传。</summary>
    private void UpdateCanUpload()
    {
        CanUpload = IsConnected && !IsTransferring;
    }

    /// <summary>
    /// 异步连接到 SFTP 服务器，连接成功后列出根目录文件。
    /// </summary>
    private async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting) return;

        IsConnecting = true;
        ConnectionStatus = "连接中...";
        UpdateCanConnect();

        try
        {
            _sftpClient = new SftpClient(Host, Port, Username, Password);
            _sftpClient.Connect();

            IsConnected = true;
            ConnectionStatus = "已连接";
            await ListFilesAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"连接失败: {ex.Message}";
            IsConnected = false;
        }
        finally
        {
            IsConnecting = false;
            UpdateCanConnect();
            UpdateCanUpload();
        }
    }

    /// <summary>断开 SFTP 连接并清空文件列表。</summary>
    private void Disconnect()
    {
        if (!IsConnected) return;

        try
        {
            _sftpClient?.Disconnect();
            IsConnected = false;
            ConnectionStatus = "已断开";
            RemoteFiles.Clear();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"断开失败: {ex.Message}";
        }
        finally
        {
            UpdateCanConnect();
            UpdateCanUpload();
        }
    }

    /// <summary>
    /// 测试 SFTP 连接是否可用，测试完成后自动断开。
    /// </summary>
    private async Task TestConnectionAsync()
    {
        if (IsConnecting) return;

        IsConnecting = true;
        ConnectionStatus = "测试连接中...";

        try
        {
            using var testClient = new SftpClient(Host, Port, Username, Password);
            testClient.Connect();
            testClient.Disconnect();

            ConnectionStatus = "连接测试成功";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"连接测试失败: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>
    /// 列出当前远程目录下的文件和子目录。
    /// 非根目录时自动在列表头部添加 ".." 返回上级目录项。
    /// </summary>
    private async Task ListFilesAsync()
    {
        if (_sftpClient == null || !_sftpClient.IsConnected) return;

        try
        {
            var files = _sftpClient.ListDirectory(CurrentRemotePath);
            var items = new ObservableCollection<SftpFileItem>();

            // 非根目录时添加返回上级目录项
            if (CurrentRemotePath != "/")
            {
                var parentPath = CurrentRemotePath.LastIndexOf('/') switch
                {
                    -1 => "/",
                    0 => "/",
                    var idx => CurrentRemotePath.Substring(0, idx)
                };
                if (parentPath == "") parentPath = "/";
                items.Add(new SftpFileItem("..", true, parentPath, CurrentRemotePath));
            }

            // 过滤 "." 和 ".." 系统项，构造文件列表项
            foreach (var file in files)
            {
                if (file.Name == "." || file.Name == "..") continue;
                var fullPath = CurrentRemotePath.EndsWith('/')
                    ? $"{CurrentRemotePath}{file.Name}"
                    : $"{CurrentRemotePath}/{file.Name}";
                items.Add(new SftpFileItem(file.Name, file.IsDirectory, fullPath, CurrentRemotePath));
            }

            RemoteFiles = items;
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"列出文件失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 导航到指定远程目录并刷新文件列表。
    /// </summary>
    /// <param name="path">目标目录路径。</param>
    private void NavigateTo(string path)
    {
        CurrentRemotePath = path;
        _ = ListFilesAsync();
    }

    /// <summary>
    /// 下载远程文件到本地。通过 DownloadPathSelector 获取本地保存路径。
    /// </summary>
    /// <param name="fileItem">待下载的远程文件项。</param>
    private async Task DownloadFileAsync(SftpFileItem fileItem)
    {
        if (fileItem.IsDirectory || _sftpClient == null || !_sftpClient.IsConnected) return;
        if (DownloadPathSelector == null) return;

        var savePath = await DownloadPathSelector(fileItem);
        if (string.IsNullOrEmpty(savePath)) return;

        try
        {
            IsTransferring = true;
            UpdateCanUpload();
            TransferProgress = "下载中...";

            // 在后台线程执行文件下载，避免阻塞 UI
            await Task.Run(() =>
            {
                using var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write);
                _sftpClient.DownloadFile(fileItem.FullPath, stream);
            });

            TransferProgress = "下载完成";
        }
        catch (Exception ex)
        {
            TransferProgress = $"下载失败: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            UpdateCanUpload();
        }
    }

    /// <summary>
    /// 上传本地文件到当前远程目录。通过 UploadPathSelector 获取本地文件路径。
    /// </summary>
    private async Task UploadFileAsync()
    {
        if (_sftpClient == null || !_sftpClient.IsConnected) return;
        if (UploadPathSelector == null) return;

        var localFilePath = await UploadPathSelector();
        if (string.IsNullOrEmpty(localFilePath)) return;

        var fileName = Path.GetFileName(localFilePath);
        var remoteFilePath = CurrentRemotePath.EndsWith('/')
            ? $"{CurrentRemotePath}{fileName}"
            : $"{CurrentRemotePath}/{fileName}";

        try
        {
            IsTransferring = true;
            UpdateCanUpload();
            TransferProgress = "上传中...";

            // 在后台线程执行文件上传，避免阻塞 UI
            await Task.Run(() =>
            {
                using var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);
                _sftpClient.UploadFile(stream, remoteFilePath);
            });

            TransferProgress = "上传完成";
            await ListFilesAsync();
        }
        catch (Exception ex)
        {
            TransferProgress = $"上传失败: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            UpdateCanUpload();
        }
    }

    /// <summary>
    /// 删除远程文件或目录。
    /// </summary>
    /// <param name="fileItem">待删除的远程文件项。</param>
    private async Task DeleteFileAsync(SftpFileItem fileItem)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected) return;

        try
        {
            if (fileItem.IsDirectory)
            {
                _sftpClient.DeleteDirectory(fileItem.FullPath);
            }
            else
            {
                _sftpClient.DeleteFile(fileItem.FullPath);
            }

            await ListFilesAsync();
            TransferProgress = "删除成功";
        }
        catch (Exception ex)
        {
            TransferProgress = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>释放资源：断开连接并释放 SFTP 客户端。</summary>
    public void Dispose()
    {
        Disconnect();
        _sftpClient?.Dispose();
    }
}

/// <summary>
/// SFTP 远程文件项模型，表示远程目录中的一个文件或子目录。
/// </summary>
public class SftpFileItem
{
    /// <summary>文件或目录名称。</summary>
    public string Name { get; }
    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; }
    /// <summary>完整路径。</summary>
    public string FullPath { get; }
    /// <summary>父目录路径。</summary>
    public string ParentPath { get; }
    /// <summary>显示图标种类：目录使用 Folder，文件使用 File。</summary>
    public Material.Icons.MaterialIconKind IconKind => IsDirectory ? Material.Icons.MaterialIconKind.Folder : Material.Icons.MaterialIconKind.File;

    /// <summary>
    /// 初始化 <see cref="SftpFileItem"/> 的新实例。
    /// </summary>
    /// <param name="name">文件或目录名称。</param>
    /// <param name="isDirectory">是否为目录。</param>
    /// <param name="fullPath">完整路径。</param>
    /// <param name="parentPath">父目录路径。</param>
    public SftpFileItem(string name, bool isDirectory, string fullPath, string parentPath)
    {
        Name = name;
        IsDirectory = isDirectory;
        FullPath = fullPath;
        ParentPath = parentPath;
    }
}
