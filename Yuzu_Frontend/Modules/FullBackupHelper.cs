using System;
using System.IO;
using System.Threading;
using System.IO.Compression;
using PMCSsE_Communicator;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 全量备份执行器。将 MC 服务端目录打包为 ZIP 压缩包，
/// 并在启用 SFTP 远程备份时将压缩包上传至远程服务器。
/// 支持取消、文件排除规则与上传进度回调。
/// </summary>
internal class FullBackupHelper : IDisposable
{
    // 当前管理器配置
    private readonly MCServerManagerConfig _config;
    // 取消令牌源，用于响应中断请求
    private readonly CancellationTokenSource _cancellationTokenSource;
    // 本次备份生成的文件名（不含目录）
    private string _backupFileName = "";

    /// <summary>日志上报事件：参数依次为 日志级别、来源、日志内容。</summary>
    public event Action<string, string, string> ReportLog = delegate { };

    /// <summary>备份任务完成事件（无论成功或失败均会触发）。</summary>
    public event Action TaskDone = delegate { };

    /// <summary>
    /// 初始化全量备份执行器。
    /// </summary>
    /// <param name="config">MC 服务端管理器配置。</param>
    /// <param name="cancellationTokenSource">用于取消备份的令牌源。</param>
    internal FullBackupHelper(MCServerManagerConfig config, CancellationTokenSource cancellationTokenSource)
    {
        _config = config;
        _cancellationTokenSource = cancellationTokenSource;
    }

    /// <summary>
    /// 执行一次全量备份流程：创建输出目录 → 打包 ZIP → 视情况上传 SFTP。
    /// 任何阶段抛出异常都会被捕获并上报，同时触发 TaskDone。
    /// </summary>
    internal void Run()
    {
        // 文件名格式：{管理器ID}-{服务器名}的全量备份-{时间戳}.zip
        _backupFileName = $"{_config.ManagerID}-{_config.MCServerName}的全量备份-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip";

        try
        {
            // 确保输出目录存在
            if (!Directory.Exists(_config.BackupManagerConfig.BackupFileOutputDirectory))
            {
                Directory.CreateDirectory(_config.BackupManagerConfig.BackupFileOutputDirectory);
                ReportLog("信息", "MC服务端备份工具", $"创建备份目录: {_config.BackupManagerConfig.BackupFileOutputDirectory}");
            }

            string backupFilePath = Path.Combine(_config.BackupManagerConfig.BackupFileOutputDirectory, _backupFileName);

            CreateZipBackup(backupFilePath);

            ReportLog("成功", "MC服务端备份工具", $"本地备份文件已创建: {backupFilePath}");

            // 根据配置决定是否上传到 SFTP 远程服务器
            if (_config.BackupManagerConfig.SFTPClientConfig.Enabled)
            {
                UploadToSFTP(backupFilePath);
            }
            else
            {
                ReportLog("信息", "MC服务端备份工具", "未启用SFTP远程备份，备份完成");
                TaskDone();
            }
        }
        catch (Exception ex)
        {
            ReportLog("错误", "MC服务端备份工具", $"备份失败: {ex.Message}");
            TaskDone();
        }
    }

    /// <summary>
    /// 将 MC 服务端目录递归压缩为 ZIP 文件。
    /// 遍历所有文件，跳过命中排除规则的文件，每处理 100 个文件上报一次进度。
    /// </summary>
    /// <param name="outputPath">输出的 ZIP 文件完整路径。</param>
    private void CreateZipBackup(string outputPath)
    {
        // 已存在的同名文件先删除，避免压缩流冲突
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using FileStream zipFileStream = new(outputPath, FileMode.Create);
        using ZipArchive archive = new(zipFileStream, ZipArchiveMode.Create);

        // 递归获取 MC 服务端目录下的全部文件
        string[] allFiles = Directory.GetFiles(_config.MCServerDirectory, "*.*", SearchOption.AllDirectories);

        int totalFiles = allFiles.Length;
        int processedFiles = 0;

        foreach (string file in allFiles)
        {
            // 响应用户取消请求
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                ReportLog("用户操作", "MC服务端备份工具", "用户取消备份任务");
                throw new OperationCanceledException("备份任务已取消");
            }

            // 计算相对路径作为 ZIP 内部条目名，保留原始目录结构
            string relativePath = Path.GetRelativePath(_config.MCServerDirectory, file);

            // 命中排除规则的文件跳过
            if (IsExcluded(file, relativePath))
            {
                continue;
            }

            try
            {
                ZipArchiveEntry entry = archive.CreateEntryFromFile(file, relativePath);
                processedFiles++;

                // 每 100 个文件或最后一个文件上报一次进度，避免日志刷屏
                if (processedFiles % 100 == 0 || processedFiles == totalFiles)
                {
                    ReportLog("信息", "MC服务端备份工具", $"正在压缩: {processedFiles}/{totalFiles} 文件");
                }
            }
            catch (Exception ex)
            {
                // 单个文件压缩失败不中断整体流程，仅记录警告
                ReportLog("警告", "MC服务端备份工具", $"跳过文件 {file}: {ex.Message}");
            }
        }

        ReportLog("成功", "MC服务端备份工具", $"压缩完成，共处理 {processedFiles} 个文件");
    }

    /// <summary>
    /// 判断指定文件是否应被排除在备份之外。
    /// 排除规则：文件名命中、扩展名命中、相对路径以某个排除目录开头。
    /// </summary>
    /// <param name="fullPath">文件绝对路径。</param>
    /// <param name="relativePath">相对于 MC 服务端目录的路径。</param>
    /// <returns>应排除返回 true，否则返回 false。</returns>
    private bool IsExcluded(string fullPath, string relativePath)
    {
        string fileName = Path.GetFileName(fullPath);
        string fileExtension = Path.GetExtension(fullPath);
        string? directoryName = Path.GetDirectoryName(relativePath);

        // 规则一：文件名命中排除列表
        if (_config.BackupManagerConfig.ExcludedFilesList.Contains(fileName))
        {
            return true;
        }

        // 规则二：扩展名命中排除列表
        if (_config.BackupManagerConfig.ExcludedFileExtensionsList.Contains(fileExtension))
        {
            return true;
        }

        // 规则三：位于排除目录下（按相对路径前缀匹配，忽略大小写）
        if (!string.IsNullOrEmpty(directoryName))
        {
            foreach (string excludedFolder in _config.BackupManagerConfig.ExcludedFoldersList)
            {
                if (relativePath.StartsWith(excludedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 在独立线程中将本地备份文件上传到 SFTP 远程服务器。
    /// 通过事件回调串联连接 → 上传 → 完成 的异步流程。
    /// </summary>
    /// <param name="localFilePath">待上传的本地文件完整路径。</param>
    private void UploadToSFTP(string localFilePath)
    {
        Thread thread = new(() =>
        {
            SFTPClient sftpClient = new(_config.BackupManagerConfig.SFTPClientConfig);

            // 透传 SFTP 客户端的日志
            sftpClient.ReportLog += (type, sender, log) =>
            {
                ReportLog(type, sender, log);
            };

            // 连接完成回调：连接成功则开始上传，失败则结束任务
            sftpClient.ConnectTaskDone += (connected) =>
            {
                if (connected)
                {
                    sftpClient.UploadLocalFile(localFilePath, _config.BackupManagerConfig.RemoteBackupFileStoreDirectory);
                }
                else
                {
                    ReportLog("失败", "MC服务端备份工具", "SFTP连接失败，远程备份上传已取消");
                    sftpClient.Dispose();
                    TaskDone();
                }
            };

            // 上传完成回调：无论成功失败都释放客户端并通知任务结束
            sftpClient.UploadFileTaskDone += (success) =>
            {
                if (success)
                {
                    ReportLog("成功", "MC服务端备份工具", "远程备份上传成功");
                }
                else
                {
                    ReportLog("失败", "MC服务端备份工具", "远程备份上传失败");
                }
                sftpClient.Dispose();
                TaskDone();
            };

            sftpClient.ConnectSFTPServer();
        });

        thread.Start();
    }

    /// <summary>
    /// 释放资源，清空事件订阅以避免悬挂引用。
    /// </summary>
    public void Dispose()
    {
        ReportLog = delegate { };
        TaskDone = delegate { };
        GC.SuppressFinalize(this);
    }
}