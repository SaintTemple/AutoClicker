using AutoClicker.Models;

namespace AutoClicker.Services;

/// <summary>
/// 在后台线程上执行连点循环，并集中管理每次运行的取消与清理。
/// </summary>
public sealed class AutoClickService : IAutoClickService
{
    private readonly IInputSimulationService _inputSimulationService;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private RunContext? _currentRun;
    private CancellationTokenSource? _currentCancellation;
    private int _isRunning;
    private int _currentRunVersion;
    private int _runVersionSeed;

    public AutoClickService(IInputSimulationService inputSimulationService)
    {
        _inputSimulationService = inputSimulationService
            ?? throw new ArgumentNullException(nameof(inputSimulationService));
    }

    public event EventHandler<AutoClickFaultedEventArgs>? Faulted;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public int CurrentRunVersion => Volatile.Read(ref _currentRunVersion);

    /// <summary>
    /// 创建一次独立运行。Task.Run 保证 SendInput 不会占用 WPF UI 线程。
    /// </summary>
    public async Task StartAsync(AutoClickConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.IntervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "连点间隔必须大于零。");
        }

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_currentRun is not null)
            {
                throw new InvalidOperationException("连点任务已在运行。");
            }

            var context = new RunContext(
                configuration,
                new CancellationTokenSource(),
                NextRunVersion());

            _currentRun = context;
            Volatile.Write(ref _currentCancellation, context.Cancellation);
            Volatile.Write(ref _currentRunVersion, context.RunVersion);
            Volatile.Write(ref _isRunning, 1);

            try
            {
                context.WorkerTask = Task.Run(
                    () => RunWorkerAsync(context),
                    CancellationToken.None);
            }
            catch
            {
                _currentRun = null;
                Interlocked.CompareExchange(
                    ref _currentCancellation,
                    null,
                    context.Cancellation);
                Volatile.Write(ref _isRunning, 0);
                context.Cancellation.Dispose();
                throw;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// 关闭程序时使用的非阻塞停止信号。
    /// CancellationTokenSource.Cancel 是线程安全的；即使清理线程刚好已释放
    /// CancellationTokenSource，也只需忽略 ObjectDisposedException。
    /// </summary>
    public void RequestStop()
    {
        var cancellation = Volatile.Read(ref _currentCancellation);
        TryCancel(cancellation);
    }

    /// <summary>
    /// 立即发出取消信号，并等待当前后台任务结束。
    /// </summary>
    public async Task StopAsync()
    {
        RunContext? context;

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            context = _currentRun;
            if (context is null)
            {
                Volatile.Write(ref _isRunning, 0);
                return;
            }

            // Cancel 在等待 WorkerTask 前执行，因此可立即中断 Task.Delay。
            TryCancel(context.Cancellation);
        }
        finally
        {
            _stateLock.Release();
        }

        if (context.WorkerTask is not null)
        {
            await context.WorkerTask.ConfigureAwait(false);
        }
    }

    private async Task RunWorkerAsync(RunContext context)
    {
        Exception? fault = null;

        try
        {
            await RunLoopAsync(
                    context.Configuration,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            // 用户主动停止，是正常退出路径。
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        var wasCurrentRun = false;

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            wasCurrentRun = ReferenceEquals(_currentRun, context);
            if (wasCurrentRun)
            {
                _currentRun = null;
                Volatile.Write(ref _isRunning, 0);
            }
        }
        finally
        {
            _stateLock.Release();
            Interlocked.CompareExchange(
                ref _currentCancellation,
                null,
                context.Cancellation);
            context.Cancellation.Dispose();
        }

        if (fault is not null && wasCurrentRun)
        {
            Faulted?.Invoke(
                this,
                new AutoClickFaultedEventArgs(fault, context.RunVersion));
        }
    }

    private async Task RunLoopAsync(
        AutoClickConfiguration configuration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // 在每次 SendInput 之前检查，停止后不会开始下一次输入。
            cancellationToken.ThrowIfCancellationRequested();

            if (configuration.TriggerMode == TriggerMode.Mouse)
            {
                _inputSimulationService.ClickMouse(configuration.MouseButton);
            }
            else
            {
                _inputSimulationService.PressKey(configuration.KeyboardVirtualKey);
            }

            await Task.Delay(
                    configuration.IntervalMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 后台任务可能刚好完成并释放资源，视为已经停止。
        }
    }

    private sealed class RunContext
    {
        public RunContext(
            AutoClickConfiguration configuration,
            CancellationTokenSource cancellation,
            int runVersion)
        {
            Configuration = configuration;
            Cancellation = cancellation;
            RunVersion = runVersion;
        }

        public AutoClickConfiguration Configuration { get; }

        public CancellationTokenSource Cancellation { get; }

        public int RunVersion { get; }

        public Task? WorkerTask { get; set; }
    }
}
