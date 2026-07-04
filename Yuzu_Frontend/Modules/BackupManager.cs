using System;
using System.IO;
using System.Threading;
using PMCSsE_Communicator;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// MC 服务端备份管理器。负责根据配置的定时策略（按天间隔的指定时刻 / 固定时间间隔）
/// 调度全量备份任务，并通过倒计时进度事件向上层反馈下一次备份的剩余时间。
/// 该类持有两个计时器：CountDownTimer 用于触发备份时刻，OneSecondTimer 用于每秒刷新倒计时。
/// </summary>
internal class BackupManager : IDisposable
{
    // 当前管理器配置（含备份策略、目录、SFTP 配置等）
    private readonly MCServerManagerConfig _config;

    // 取消令牌源，用于在备份过程中通知取消
    internal CancellationTokenSource CancellationTokenSource;

    // 是否正在等待下一次备份触发时刻
    internal bool IsWaitingForBackup = false;

    // 备份执行器（FullBackupHelper）是否正在运行
    internal bool IsBackupHelperRunning = false;

    /// <summary>备份服务运行状态变化事件，参数为是否正在运行。</summary>
    public event Action<bool> ReportServiceRunningStatue = delegate { };

    /// <summary>日志上报事件：参数依次为 日志级别、来源、日志内容。</summary>
    public event Action<string, string, string> ReportLog = delegate { };

    /// <summary>进度上报事件：参数依次为 倒计时文本、倒计时进度百分比、备份进度文本、备份进度百分比。</summary>
    internal event Action<string, byte, string, byte> ReportProgress = delegate { };

    // 单次触发的计时器，到达备份时刻时触发备份并重新调度下一次
    internal System.Timers.Timer CountDownTimer = new() { AutoReset = false };

    // 每秒触发的计时器，用于刷新倒计时显示
    internal System.Timers.Timer OneSecondTimer = new() { AutoReset = true, Interval = 1000d };

    // 下一次执行备份的绝对时间
    private DateTime NextExecuteTime;
    // 下一次执行备份距当前的总时间跨度（用于计算倒计时百分比）
    private TimeSpan NextExecuteTimeSpan;

    /// <summary>
    /// 初始化备份管理器，绑定配置并装配计时器事件。
    /// 若配置中启用了自动备份，则立即启动服务。
    /// </summary>
    /// <param name="config">MC 服务端管理器配置。</param>
    public BackupManager(MCServerManagerConfig config)
    {
        _config = config;
        CancellationTokenSource = new();

        // 倒计时到达后：先停止当前等待状态，再启动下一轮调度（形成循环触发）
        CountDownTimer.Elapsed += (sender, e) =>
        {
            StopService();
            StartService();
        };

        // 每秒计算并上报剩余倒计时（天/时/分/秒 拆分）
        OneSecondTimer.Elapsed += (sender, e) =>
        {
            if (IsWaitingForBackup)
            {
                TimeSpan leftTimeSpan = NextExecuteTime - DateTime.Now;
                // 剩余毫秒数，避免负值
                double leftTimeSpanMs = leftTimeSpan.TotalMilliseconds < 0 ? 0 : leftTimeSpan.TotalMilliseconds;
                double totalTimeSpanMs = NextExecuteTimeSpan.TotalMilliseconds;
                // 倒计时进度百分比 = 剩余 / 总时长 * 100
                byte countDownProgress = (byte)((float)(leftTimeSpanMs / totalTimeSpanMs) * 100);
                // 以下将剩余毫秒数逐步拆解为 天/时/分/秒
                byte leftTimeSpanDays = (byte)(leftTimeSpanMs / 1000 / 60 / 60 / 24);
                byte leftTimeSpanHours = (byte)(leftTimeSpanMs / 1000 / 60 / 60 - leftTimeSpanDays * 24);
                byte leftTimeSpanMin = (byte)(leftTimeSpanMs / 1000 / 60 - leftTimeSpanHours * 60 - leftTimeSpanDays * 24 * 60);
                byte leftTimeSpanS = (byte)(leftTimeSpanMs / 1000 - leftTimeSpanMin * 60 - leftTimeSpanHours * 60 * 60 - leftTimeSpanDays * 24 * 60 * 60);

                ReportProgress($"等待备份倒计时[{leftTimeSpanDays}天 {leftTimeSpanHours}时 {leftTimeSpanMin}分 {leftTimeSpanS}秒]", countDownProgress, "无", 0);
            }
        };

        // 启用自动备份且当前无任务在跑时，立即启动调度
        if (!IsWaitingForBackup && !IsBackupHelperRunning && _config.BackupManagerConfig.AutoBackupEnabled)
        {
            StartService();
        }
    }

    /// <summary>
    /// 启动备份调度服务。根据配置的定时模式计算下一次备份时刻，
    /// 启动倒计时计时器与每秒进度计时器。
    /// </summary>
    public void StartService()
    {
        DateTime nowTime = DateTime.Now;
        double nextExecuteTimeSpanMS;

        switch (_config.BackupManagerConfig.BackupTimingMode)
        {
            case BackupTimingMode.DayInterval_SpecificTime:
                // 模式一：按 N 天间隔在每天的指定时刻执行
                // SpecificTime 格式为 "时:分:秒"
                string[] splitedExecuteTime = _config.BackupManagerConfig.SpecificTime.Split(':', StringSplitOptions.RemoveEmptyEntries);
                double[] splitedExecuteTimeDouble = [
                    Convert.ToDouble(splitedExecuteTime[0]),
                    Convert.ToDouble(splitedExecuteTime[1]),
                    Convert.ToDouble(splitedExecuteTime[2])];
                // 以今天 0:00 为基准叠加指定的时/分/秒
                DateTime nextExecuteTime = nowTime.Date;
                nextExecuteTime = nextExecuteTime.AddHours(splitedExecuteTimeDouble[0]);
                nextExecuteTime = nextExecuteTime.AddMinutes(splitedExecuteTimeDouble[1]);
                nextExecuteTime = nextExecuteTime.AddSeconds(splitedExecuteTimeDouble[2]);
                // 若计算出的时刻已过，则顺延 DayInterval 天
                if (nextExecuteTime <= nowTime)
                {
                    nextExecuteTime = nextExecuteTime.AddDays(Convert.ToDouble(_config.BackupManagerConfig.DayInterval));
                }
                TimeSpan timeSpan = nextExecuteTime - nowTime;
                nextExecuteTimeSpanMS = timeSpan.TotalMilliseconds;

                NextExecuteTime = nextExecuteTime;
                NextExecuteTimeSpan = timeSpan;

                break;

            case BackupTimingMode.FixedTimeInterval:
                // 模式二：按固定时间间隔执行，TimeInterval 格式为 "时:分:秒"
                string[] splitedTimeSpan = _config.BackupManagerConfig.TimeInterval.Split(':', StringSplitOptions.RemoveEmptyEntries);
                int[] splitedTimeSpanInt = [
                    Convert.ToInt32(splitedTimeSpan[0]),
                    Convert.ToInt32(splitedTimeSpan[1]),
                    Convert.ToInt32(splitedTimeSpan[2])];
                TimeSpan timeSpan1 = new(splitedTimeSpanInt[0], splitedTimeSpanInt[1], splitedTimeSpanInt[2]);
                nextExecuteTimeSpanMS = timeSpan1.TotalMilliseconds;

                NextExecuteTime = nowTime + timeSpan1;
                NextExecuteTimeSpan = timeSpan1;
                break;

            default:
                return;
        }

        CountDownTimer.Interval = nextExecuteTimeSpanMS;
        CountDownTimer.Start();
        OneSecondTimer.Start();
        IsWaitingForBackup = true;

        ReportServiceRunningStatue(true);
        ReportLog("成功", "MC服务端备份工具", "定时备份服务已启动");
    }

    /// <summary>
    /// 停止备份调度服务，停止两个计时器并上报停止状态。
    /// </summary>
    public void StopService()
    {
        IsWaitingForBackup = false;
        CountDownTimer.Stop();
        OneSecondTimer.Stop();

        ReportProgress("无", 0, "无", 0);
        ReportServiceRunningStatue(false);
        ReportLog("信息", "MC服务端备份工具", "定时备份服务已停止");
    }

    /// <summary>
    /// 立即触发一次备份。根据配置的备份模式选择对应的备份执行器。
    /// 当前仅实现全量备份，文件级/块级增量备份为预留扩展。
    /// </summary>
    public void StartBackup()
    {
        if (!CheckConfig())
        {
            return;
        }

        switch (_config.BackupManagerConfig.BackupMode)
        {
            case BackupMode.Full:
                // 全量备份：创建 FullBackupHelper 并绑定事件，运行后等待 TaskDone 回调复位状态
                FullBackupHelper fullBackupHelper = new(_config, CancellationTokenSource);
                fullBackupHelper.ReportLog += ReportLog;
                fullBackupHelper.TaskDone += () =>
                {
                    IsBackupHelperRunning = false;
                };
                IsBackupHelperRunning = true;
                fullBackupHelper.Run();
                break;

            case BackupMode.FileLevel_Incremental:
                ReportLog("信息", "MC服务端备份工具", "文件级增量备份功能暂未实现");
                break;

            case BackupMode.BlockLevel_Incremental:
                ReportLog("信息", "MC服务端备份工具", "块级增量备份功能暂未实现");
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 校验备份所需的关键配置项（MC 服务端目录、备份输出目录）是否有效。
    /// </summary>
    /// <returns>配置全部有效返回 true，否则返回 false 并上报错误日志。</returns>
    private bool CheckConfig()
    {
        bool isConfigValid = true;

        if (string.IsNullOrEmpty(_config.MCServerDirectory))
        {
            isConfigValid = false;
            ReportLog("错误", "MC服务端备份工具", "MC服务端目录未设置");
        }

        if (!Directory.Exists(_config.MCServerDirectory))
        {
            isConfigValid = false;
            ReportLog("错误", "MC服务端备份工具", "MC服务端目录不存在");
        }

        if (string.IsNullOrEmpty(_config.BackupManagerConfig.BackupFileOutputDirectory))
        {
            isConfigValid = false;
            ReportLog("错误", "MC服务端备份工具", "备份输出目录未设置");
        }

        return isConfigValid;
    }

    /// <summary>
    /// 释放计时器与取消令牌资源，并清空所有事件订阅以避免悬挂引用。
    /// </summary>
    public void Dispose()
    {
        CountDownTimer?.Dispose();
        OneSecondTimer?.Dispose();
        CancellationTokenSource?.Dispose();
        ReportServiceRunningStatue = delegate { };
        ReportLog = delegate { };
        ReportProgress = delegate { };
    }
}