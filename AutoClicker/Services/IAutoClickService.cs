using AutoClicker.Models;

namespace AutoClicker.Services;

public interface IAutoClickService
{
    event EventHandler<AutoClickFaultedEventArgs>? Faulted;

    bool IsRunning { get; }

    int CurrentRunVersion { get; }

    Task StartAsync(AutoClickConfiguration configuration);

    /// <summary>
    /// 不等待锁和后台任务，立即向当前连点任务发出停止信号。
    /// 关闭窗口时先调用此方法，再异步等待完整清理。
    /// </summary>
    void RequestStop();

    Task StopAsync();
}
