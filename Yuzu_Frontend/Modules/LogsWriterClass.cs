using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 异步写入运行日志到 %AppData%/YuzuFrontend/Logs/yyyy-MM-dd.log。
/// 采用"入队 + 定时刷盘"策略：调用方将日志放入并发队列，
/// 由后台定时器每秒批量取出并追加写入当天日志文件，避免高频 IO。
/// </summary>
public static class LogsWriterClass
{
    // 待写入文件的日志行队列（线程安全）
    private static readonly ConcurrentQueue<string> _queue = new();
    // 批量刷盘定时器，延迟初始化以在首次写入时启动
    private static Timer? _timer;
    // 当前日志文件对应的日期（按天切换文件）
    private static string _currentDate = DateTime.Today.ToString("yyyy-MM-dd");
    // 文件写入锁，保证刷盘过程的互斥
    private static readonly object _lock = new();

    /// <summary>日志文件所在目录：%AppData%/YuzuFrontend/Logs。</summary>
    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YuzuFrontend", "Logs");

    /// <summary>
    /// 将一条日志加入写入队列。首次调用时自动启动每秒一次的刷盘定时器。
    /// </summary>
    /// <param name="message">日志正文（不含时间戳，方法内会自动添加）。</param>
    public static void AppendLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _queue.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " + message);
        _timer ??= new Timer(FlushTimerCallback, null, 1000, 1000);
    }

    /// <summary>
    /// 定时刷盘回调。将队列中所有待写日志一次性追加到当天日志文件。
    /// 跨天时自动切换到新的日志文件；IO 异常被静默忽略以保证调用方不中断。
    /// </summary>
    /// <param name="state">定时器状态对象（未使用）。</param>
    private static void FlushTimerCallback(object? state)
    {
        if (_queue.IsEmpty) return;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                // 跨天时更新当前日期，后续写入新文件
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                if (today != _currentDate)
                    _currentDate = today;
                var path = Path.Combine(LogDir, $"{_currentDate}.log");
                // 批量取出队列中的全部日志行
                var sb = new StringBuilder();
                while (_queue.TryDequeue(out var line))
                    sb.AppendLine(line);
                if (sb.Length > 0)
                    File.AppendAllText(path, sb.ToString());
            }
        }
        catch
        {
            // 忽略 IO 错误
        }
    }
}
