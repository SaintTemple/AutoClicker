using AutoClicker.Models;

namespace AutoClicker.Services;

public sealed class AutoClickService : IAutoClickService
{
    private readonly IInputSimulationService _inputSimulationService;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private int _runVersionSeed;

    public AutoClickService(IInputSimulationService inputSimulationService)
    {
        _inputSimulationService = inputSimulationService;
    }

    public event EventHandler<AutoClickFaultedEventArgs>? Faulted;

    public bool IsRunning { get; private set; }

    public int CurrentRunVersion { get; private set; }

    /// <summary>
    /// 启动连点循环。该方法会防止重复启动。
    /// </summary>
    public async Task StartAsync(AutoClickConfiguration configuration)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("连点任务已在运行。");
            }

            var cts = new CancellationTokenSource();
            var runVersion = NextRunVersion();
            var workerTask = RunLoopAsync(configuration, cts.Token);

            _cts = cts;
            _workerTask = workerTask;
            CurrentRunVersion = runVersion;
            IsRunning = true;

            _ = ObserveWorkerAsync(workerTask, cts, runVersion);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// 安全停止连点循环。可等待后台任务完整退出。
    /// 清理所有权：只有成功从当前状态中“摘除” _workerTask/_cts 的路径才负责 Dispose CTS。
    /// </summary>
    public async Task StopAsync()
    {
        Task? workerTask;
        CancellationTokenSource? cts;
        bool ownsCleanup;

        await _stateLock.WaitAsync();
        try
        {
            ownsCleanup = TryDetachCurrentRun(out workerTask, out cts);
        }
        finally
        {
            _stateLock.Release();
        }

        if (!ownsCleanup || workerTask is null || cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            await workerTask;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 正常取消路径
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunLoopAsync(AutoClickConfiguration configuration, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (configuration.TriggerMode == TriggerMode.Mouse)
            {
                _inputSimulationService.ClickMouse(configuration.MouseButton);
            }
            else
            {
                _inputSimulationService.PressKey(configuration.KeyboardVirtualKey);
            }

            await Task.Delay(configuration.IntervalMilliseconds, token);
        }
    }

    private async Task ObserveWorkerAsync(Task workerTask, CancellationTokenSource cts, int runVersion)
    {
        Exception? fault = null;

        try
        {
            await workerTask;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        bool ownsCleanup;

        await _stateLock.WaitAsync();
        try
        {
            ownsCleanup = ReferenceEquals(_workerTask, workerTask) && ReferenceEquals(_cts, cts);
            if (ownsCleanup)
            {
                _workerTask = null;
                _cts = null;
                IsRunning = false;
            }
        }
        finally
        {
            _stateLock.Release();
        }

        if (ownsCleanup)
        {
            cts.Dispose();
        }

        if (fault is not null)
        {
            Faulted?.Invoke(this, new AutoClickFaultedEventArgs(fault, runVersion));
        }
    }

    /// <summary>
    /// 尝试从服务当前状态中摘除当前运行实例。成功摘除的一方拥有后续清理责任。
    /// </summary>
    private bool TryDetachCurrentRun(out Task? workerTask, out CancellationTokenSource? cts)
    {
        workerTask = _workerTask;
        cts = _cts;

        if (workerTask is null || cts is null)
        {
            IsRunning = false;
            return false;
        }

        _workerTask = null;
        _cts = null;
        IsRunning = false;
        return true;
    }

    private int NextRunVersion()
    {
        if (_runVersionSeed == int.MaxValue)
        {
            _runVersionSeed = 0;
        }

        _runVersionSeed++;
        return _runVersionSeed;
    }
}