using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 异步写入运行日志到 %AppData%/YuzuFrontend/Logs/yyyy-MM-dd.log
/// </summary>
public static class LogsWriterClass
{
    private static readonly ConcurrentQueue<string> _queue = new();
    private static Timer? _timer;
    private static string _currentDate = DateTime.Today.ToString("yyyy-MM-dd");
    private static readonly object _lock = new();

    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YuzuFrontend", "Logs");

    public static void AppendLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        _queue.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " + message);
        _timer ??= new Timer(FlushTimerCallback, null, 1000, 1000);
    }

    private static void FlushTimerCallback(object? state)
    {
        if (_queue.IsEmpty) return;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                if (today != _currentDate)
                    _currentDate = today;
                var path = Path.Combine(LogDir, $"{_currentDate}.log");
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
