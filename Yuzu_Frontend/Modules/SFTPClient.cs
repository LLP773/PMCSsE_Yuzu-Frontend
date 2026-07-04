using System;
using System.IO;
using System.Threading;
using PMCSsE_Communicator;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Text;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// SFTP 客户端封装。基于 Renci.SshNet 实现，负责与远程 SFTP 服务器的连接、
/// 文件上传（支持断点续传与失败重试）、远程文件存在性检查与删除等操作，
/// 并通过事件向上层上报日志、连接/上传完成状态及实时传输速度与进度。
/// </summary>
internal class SFTPClient : IDisposable
{
    // 底层 SSH.NET SFTP 客户端实例
    private readonly Renci.SshNet.SftpClient _sftpClient;
    // 当前正在传输的远程文件路径（含 .文件块 后缀），用于进度查询；未传输时为 null
    private string? _transferringRemoteFilePath;
    // 本地文件总字节数（用于计算上传百分比）
    private long _localFileByteCount;
    // 上一秒统计到的远程文件字节数（用于计算瞬时速度）
    private long _lastTimeByteCount = 0L;
    // 本秒统计到的远程文件字节数
    private long _latestByteCount = 0L;
    // 每秒触发的进度统计定时器
    private readonly System.Timers.Timer _timer = new() { Interval = 1000d, AutoReset = true };

    // 单次读写缓冲区大小（字节），默认约 10MB
    private long _bufferSize = 1024L * 1024L * 10L - 1L;

    /// <summary>日志上报事件：参数依次为 日志级别、来源、日志内容。</summary>
    public event Action<string, string, string> ReportLog = delegate { };

    /// <summary>客户端初始化完成事件。</summary>
    internal event Action Initialized = delegate { };

    /// <summary>连接服务器任务完成事件，参数为是否连接成功。</summary>
    internal event Action<bool> ConnectTaskDone = delegate { };

    /// <summary>上传文件任务完成事件，参数为是否上传成功。</summary>
    internal event Action<bool> UploadFileTaskDone = delegate { };

    /// <summary>删除远程文件任务完成事件，参数为是否删除成功。</summary>
    internal event Action<bool> DeleteFileTaskDone = delegate { };

    /// <summary>测试任务完成事件，参数为是否成功。</summary>
    internal event Action<bool> TestTaskDone = delegate { };

    /// <summary>传输速度与进度上报事件：参数依次为 综合文本、进度百分比文本、速度文本。</summary>
    internal event Action<string, string, string> ReportTransmissionSpeedAndProcess = delegate { };

    /// <summary>
    /// 通过显式连接参数初始化客户端。
    /// </summary>
    /// <param name="host">服务器主机。</param>
    /// <param name="port">服务器端口。</param>
    /// <param name="username">登录用户名。</param>
    /// <param name="password">登录密码。</param>
    /// <param name="bufferSize">读写缓冲区大小（MB），默认 10。</param>
    internal SFTPClient(string host, int port, string username, string password, int bufferSize = 10)
    {
        _bufferSize = bufferSize * 1024 * 1024 - 1;
        PasswordConnectionInfo passwordConnectionInfo = new(host, port, username, password) { Encoding = Encoding.UTF8 };
        _sftpClient = new(passwordConnectionInfo);
        _timer.Elapsed += (sender, e) => CalculateTransmissionSpeedAndProcess();

        ReportLog("信息", "SFTP客户端", "已初始化客户端");
        Initialized();
    }

    /// <summary>
    /// 通过 SFTPClientConfig 配置对象初始化客户端。
    /// </summary>
    /// <param name="sftpClientConfig">SFTP 连接配置。</param>
    internal SFTPClient(SFTPClientConfig sftpClientConfig)
    {
        _bufferSize = sftpClientConfig.BufferSize * 1024 * 1024 - 1;
        PasswordConnectionInfo passwordConnectionInfo = new(sftpClientConfig.Host,
            sftpClientConfig.Port,
            sftpClientConfig.UserName,
            sftpClientConfig.Password)
        { Encoding = Encoding.UTF8 };
        _sftpClient = new(passwordConnectionInfo);
        _timer.Elapsed += (sender, e) => CalculateTransmissionSpeedAndProcess();

        ReportLog("信息", "SFTP客户端", "已初始化客户端");
        Initialized();
    }

    /// <summary>
    /// 连接 SFTP 服务器。若已连接则直接上报成功；
    /// 连接异常会被捕获并通过 ConnectTaskDone 事件反馈失败。
    /// </summary>
    internal void ConnectSFTPServer()
    {
        if (!_sftpClient.IsConnected)
        {
            try
            {
                _sftpClient.Connect();
            }
            catch (Exception ex)
            {
                ReportLog("错误", "SFTP客户端", $"错误:({ex.Message})");
                ReportLog("失败", "SFTP客户端", "连接失败");
                ConnectTaskDone(false);
                return;
            }

            if (_sftpClient.IsConnected)
            {
                ReportLog("成功", "SFTP客户端", "已连接至服务器");
                ConnectTaskDone(true);
            }
            else
            {
                ReportLog("失败", "SFTP客户端", "连接失败");
                ConnectTaskDone(false);
            }
        }
        else
        {
            ReportLog("成功", "SFTP客户端", "已连接至服务器");
        }
    }

    /// <summary>
    /// 每秒计算一次传输速度与进度。
    /// 通过查询远程文件当前字节数，与上一秒的差值得到瞬时速度，与本地文件总字节比得到进度百分比。
    /// </summary>
    private void CalculateTransmissionSpeedAndProcess()
    {
        try
        {
            if (_transferringRemoteFilePath == null) { return; }
            _latestByteCount = _sftpClient.Get(_transferringRemoteFilePath).Length;
            // 瞬时速度 = 本秒字节数 - 上秒字节数
            string speed = $"{ConversionUnits(_latestByteCount - _lastTimeByteCount)}/s";
            // 进度百分比 = 远程已接收字节 / 本地文件总字节
            string process = $"{(byte)((float)_latestByteCount / _localFileByteCount * 100)}%";
            string speedAndProcessText = $"[SFTP客户端]:当前进度:{process} | 传输速度:{speed}/s";

            ReportTransmissionSpeedAndProcess(speedAndProcessText, process, speed);
            ReportLog("信息", "SFTP客户端", speedAndProcessText);

            // 记录本秒字节数，供下一秒计算差值
            _lastTimeByteCount = _latestByteCount;
        }
        catch
        {
            return;
        }
    }

    /// <summary>
    /// 将字节数转换为带单位的可读字符串（B/KB/MB/GB）。
    /// </summary>
    /// <param name="byteCount">字节数。</param>
    /// <returns>带单位的字符串，如 "1.5 MB"。</returns>
    private static string ConversionUnits(long byteCount)
    {
        string[] unitsArray = ["B", "KB", "MB", "GB"];

        // 逐级除以 1024 直到小于 1024 或达到最高单位
        int index = 0;
        while (byteCount >= 1024L && index < unitsArray.Length - 1)
        {
            byteCount /= 1024L;
            ++index;
        }
        return $"{byteCount} {unitsArray[index]}";
    }

    /// <summary>
    /// 上传本地文件到远程目录。流程包含：参数校验 → 规范化远程目录 →
    /// 递归创建远程目录 → 计算起始位置（覆盖/断点续传/全新上传）→ 分块读写上传（带重试）→
    /// 完成后将临时 ".文件块" 文件重命名为正式文件名。
    /// 上传过程支持断点续传（基于远程 .文件块 文件已写入字节数）和最多 5 次失败重试。
    /// </summary>
    /// <param name="localFilePath">本地文件完整路径。</param>
    /// <param name="remoteFileDirectory">远程目标目录（可为相对/绝对路径，方法内会规范化）。</param>
    internal void UploadLocalFile(string localFilePath, string remoteFileDirectory)
    {
        // 前置校验：连接状态、本地路径、本地文件存在性
        if (!_sftpClient.IsConnected)
        {
            ReportLog("错误", "SFTP客户端", "未连接至SFTP服务器");
            ReportLog("失败", "SFTP客户端", "已终止上传操作");
            UploadFileTaskDone(false);
            return;
        }
        else if (string.IsNullOrEmpty(localFilePath))
        {
            ReportLog("错误", "SFTP客户端", "未指定要上传的本地文件");
            ReportLog("失败", "SFTP客户端", "已终止上传操作");
            UploadFileTaskDone(false);
            return;
        }
        else if (!File.Exists(localFilePath))
        {
            ReportLog("错误", "SFTP客户端", "指定的本地文件不存在");
            ReportLog("失败", "SFTP客户端", "已终止上传操作");
            UploadFileTaskDone(false);
            return;
        }
        else
        {
            // 规范化远程目录：统一为以 / 开头、正斜杠、末尾无 / 的 Unix 风格路径
            string fixedRemoteFileDirectory;
            if (!remoteFileDirectory.StartsWith('/') && !remoteFileDirectory.StartsWith('\\'))
            {
                fixedRemoteFileDirectory = $"/{remoteFileDirectory}";
            }
            else if (!remoteFileDirectory.StartsWith('/') && remoteFileDirectory.StartsWith('\\'))
            {
                fixedRemoteFileDirectory = remoteFileDirectory.Replace('\\', '/');
            }
            else
            {
                fixedRemoteFileDirectory = remoteFileDirectory;
            }
            fixedRemoteFileDirectory = fixedRemoteFileDirectory.Replace('\\', '/').TrimEnd('/');

            if (string.IsNullOrEmpty(fixedRemoteFileDirectory))
            {
                fixedRemoteFileDirectory = "/";
            }

            // 远程目录存在性检查与逐级创建
            if (_sftpClient.Exists(fixedRemoteFileDirectory))
            {
                ReportLog("信息", "SFTP客户端", "远程目录已存在");
            }
            else
            {
                ReportLog("信息", "SFTP客户端", "远程目录不存在");
                ReportLog("程序操作", "SFTP客户端", "将创建远程目录");

                // 按路径层级逐级创建（部分 SFTP 服务器不支持递归创建目录）
                string[] splitedFixedRemoteFileDirectory = fixedRemoteFileDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
                string currentDirectory = "/";
                int index = 0;
                while (index < splitedFixedRemoteFileDirectory.Length)
                {
                    string nextDirectoryName = splitedFixedRemoteFileDirectory[index];
                    string nextDirectory = $"{currentDirectory.TrimEnd('/')}/{nextDirectoryName}";
                    try
                    {
                        if (!_sftpClient.Exists(nextDirectory))
                        {
                            _sftpClient.ChangeDirectory(currentDirectory);
                            _sftpClient.CreateDirectory(nextDirectoryName);
                            if (!_sftpClient.Exists(nextDirectory))
                            {
                                ReportLog("错误", "SFTP客户端", "远程目录创建失败");
                                ReportLog("失败", "SFTP客户端", "已终止上传操作");
                                UploadFileTaskDone(false);
                                return;
                            }
                            currentDirectory = nextDirectory;
                            ++index;
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportLog("错误", "SFTP客户端", $"错误:({ex.Message})");
                        ReportLog("失败", "SFTP客户端", "远程目录创建失败");
                        ReportLog("失败", "SFTP客户端", "已终止上传操作");
                        return;
                    }
                }

                ReportLog("成功", "SFTP客户端", "远程目录创建成功");
            }

            // 计算远程正式文件路径与临时传输路径（带 .文件块 后缀）
            FileInfo localFileInfo = new(localFilePath);
            string localFileName = localFileInfo.Name;
            string remoteFilePath = fixedRemoteFileDirectory.EndsWith('/') ? $"{fixedRemoteFileDirectory}{localFileName}" : $"{fixedRemoteFileDirectory}/{localFileName}";
            string transferringRemoteFilePath = $"{remoteFilePath}.文件块";
            long startPosition;

            // 决定起始上传位置的三种情形
            if (_sftpClient.Exists(remoteFilePath))
            {
                // 情形一：远程已存在同名正式文件 → 删除后从头覆盖
                ReportLog("信息", "SFTP客户端", "远程文件已存在");
                ReportLog("程序操作", "SFTP客户端", "将覆盖现有文件");

                _sftpClient.DeleteFile(remoteFilePath);
                if (_sftpClient.Exists(remoteFilePath))
                {
                    ReportLog("错误", "SFTP客户端", "远程文件删除失败");
                    ReportLog("失败", "SFTP客户端", "已取消上传文件操作");
                    UploadFileTaskDone(false);
                    return;
                }
                else
                {
                    ReportLog("成功", "SFTP客户端", "远程文件删除成功");
                    ReportLog("程序操作", "SFTP客户端", "开始上传文件");
                    startPosition = 0L;
                }
            }
            else if (_sftpClient.Exists(transferringRemoteFilePath))
            {
                // 情形二：存在未完成的 .文件块 临时文件 → 从其当前字节长度处断点续传
                ReportLog("信息", "SFTP客户端", "存在未传输完成的文件，将续传");

                try
                {
                    startPosition = _sftpClient.Get(transferringRemoteFilePath).Length;
                    ReportLog("调试", "SFTP客户端", $"已获取到远程文件的字节数[{startPosition}]");
                }
                catch (Exception)
                {
                    ReportLog("错误", "SFTP客户端", "读取远程文件信息失败");
                    ReportLog("失败", "SFTP客户端", "已终止上传操作");
                    return;
                }

                ReportLog("成功", "SFTP客户端", "成功获取到上次传输进度");
                ReportLog("程序操作", "SFTP客户端", "开始续传");
            }
            else
            {
                // 情形三：远程既无正式文件也无临时文件 → 全新上传
                ReportLog("信息", "SFTP客户端", "无已存在的同名文件或同名文件的片段(.文件块)");
                ReportLog("程序操作", "SFTP客户端", "开始上传文件");
                startPosition = 0L;
            }

            long localFileByteCount = localFileInfo.Length;
            _localFileByteCount = localFileByteCount;
            _transferringRemoteFilePath = transferringRemoteFilePath;
            int retryCount = 0;
            int maxRetryCount = 5;

            using FileStream localFileStream = new(localFilePath, FileMode.Open, FileAccess.Read);
            using SftpFileStream remoteFileStream = _sftpClient.OpenWrite(transferringRemoteFilePath);
            {
                byte[] buffer = new byte[_bufferSize];
                _timer.Start();

                // 上传主循环：失败后最多重试 maxRetryCount 次
                for (; retryCount <= maxRetryCount;)
                {
                    try
                    {
                        // 连接中断时尝试重连
                        if (!_sftpClient.IsConnected)
                        {
                            _sftpClient.Connect();
                            remoteFileStream.Dispose();
                            using var newRemoteStream = _sftpClient.OpenWrite(transferringRemoteFilePath);
                            ReportLog("成功", "SFTP客户端", "重连成功");
                        }

                        // 分块读写：从 startPosition 起，按 buffer 大小循环写入远程流
                        while (startPosition < localFileByteCount)
                        {
                            localFileStream.Position = startPosition;
                            remoteFileStream.Position = startPosition;

                            int readByteCount = localFileStream.Read(buffer, 0, buffer.Length);
                            remoteFileStream.Write(buffer, 0, readByteCount);
                            // 以远程文件实际落盘字节数作为下次写入起点，保证续传一致性
                            startPosition = _sftpClient.Get(transferringRemoteFilePath).Length;
                        }

                        // 全部字节写入完成
                        if (startPosition == localFileByteCount)
                        {
                            ReportLog("成功", "SFTP客户端", "文件已完整上传");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 达到最大重试次数则退出循环
                        if (retryCount == maxRetryCount)
                        {
                            retryCount++;
                            break;
                        }
                        retryCount++;
                        if (_sftpClient.IsConnected)
                        {
                            ReportLog("错误", "SFTP客户端", $"上传失败（{ex.Message}）");
                            ReportLog("程序操作", "SFTP客户端", $"5s后重试（第{retryCount}次）");
                        }
                        else
                        {
                            ReportLog("错误", "SFTP客户端", $"上传失败（{ex.Message}）");
                            ReportLog("程序操作", "SFTP客户端", $"连接中断，5s后尝试重新连接（第{retryCount}次重试）");
                        }
                        Thread.Sleep(5000);
                    }
                }

                _timer.Stop();
            }

            // 上传成功后将临时 .文件块 文件重命名为正式文件名
            if (retryCount <= maxRetryCount)
            {
                _sftpClient.RenameFile(transferringRemoteFilePath, remoteFilePath);
                if (_sftpClient.Exists(remoteFilePath))
                {
                    ReportLog("成功", "SFTP客户端", "文件重命名成功");
                    ReportLog("成功", "SFTP客户端", "文件上传成功");
                    UploadFileTaskDone(true);
                }
                else
                {
                    ReportLog("错误", "SFTP客户端", "文件重命名失败");
                    ReportLog("失败", "SFTP客户端", "文件上传失败");
                    UploadFileTaskDone(false);
                }
            }
            else
            {
                ReportLog("错误", "SFTP客户端", $"上传失败已达最大重试次数{maxRetryCount}");
                ReportLog("失败", "SFTP客户端", "文件上传失败");
                UploadFileTaskDone(false);
            }
        }
    }

    /// <summary>
    /// 检测远程文件是否存在。未连接或发生异常时返回 false 并上报。
    /// </summary>
    /// <param name="remoteFilePath">远程文件完整路径。</param>
    /// <returns>存在返回 true，否则 false。</returns>
    internal bool CheckIsRemoteFileExists(string remoteFilePath)
    {
        if (!_sftpClient.IsConnected)
        {
            ReportLog("错误", "SFTP客户端", "未连接至服务器");
            ReportLog("失败", "SFTP客户端", "检测远程文件是否存在失败");
            return false;
        }
        else
        {
            try
            {
                if (_sftpClient.Exists(remoteFilePath))
                {
                    ReportLog("信息", "SFTP客户端", "远程文件存在");
                    ReportLog("成功", "SFTP客户端", "检测远程文件是否存在成功");
                    return true;
                }
                else
                {
                    ReportLog("信息", "SFTP客户端", "远程文件不存在");
                    ReportLog("成功", "SFTP客户端", "检测远程文件是否存在成功");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ReportLog("错误", "SFTP客户端", $"错误:({ex.Message})");
                ReportLog("失败", "SFTP客户端", "检测远程文件是否存在失败");
                return false;
            }
        }
    }

    /// <summary>
    /// 删除远程文件。结果通过 DeleteFileTaskDone 事件与返回值同步反馈。
    /// </summary>
    /// <param name="remoteFilePath">远程文件完整路径。</param>
    /// <returns>删除成功返回 true，否则 false。</returns>
    internal bool DeleteRemoteFile(string remoteFilePath)
    {
        if (!_sftpClient.IsConnected)
        {
            ReportLog("错误", "SFTP客户端", "未连接至服务器");
            ReportLog("失败", "SFTP客户端", "删除远程文件失败");
            DeleteFileTaskDone(false);
            return false;
        }
        else
        {
            try
            {
                _sftpClient.DeleteFile(remoteFilePath);
                ReportLog("成功", "SFTP客户端", "成功删除远程文件");
                DeleteFileTaskDone(true);
                return true;
            }
            catch (Exception ex)
            {
                ReportLog("错误", "SFTP客户端", $"错误:({ex.Message})");
                ReportLog("失败", "SFTP客户端", "删除远程文件失败");
                DeleteFileTaskDone(false);
                return false;
            }
        }
    }

    /// <summary>
    /// 断开与 SFTP 服务器的连接。
    /// </summary>
    internal void Disconnect()
    {
        if (_sftpClient.IsConnected)
        {
            _sftpClient.Disconnect();
            ReportLog("成功", "SFTP客户端", "已断开连接");
        }
    }

    /// <summary>
    /// 释放底层 SFTP 客户端与定时器资源，并清空所有事件订阅以避免悬挂引用。
    /// </summary>
    public void Dispose()
    {
        if (_sftpClient != null && _sftpClient.IsConnected)
        {
            Disconnect();
        }
        _sftpClient?.Dispose();
        _timer?.Dispose();
        Initialized = delegate { };
        ConnectTaskDone = delegate { };
        UploadFileTaskDone = delegate { };
        DeleteFileTaskDone = delegate { };
        ReportTransmissionSpeedAndProcess = delegate { };
        ReportLog("信息", "SFTP客户端", "已释放资源");
        ReportLog = delegate { };
    }
}