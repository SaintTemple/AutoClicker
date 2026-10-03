using AutoClicker.Commands;
using AutoClicker.Models;
using AutoClicker.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace AutoClicker.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private const int MinIntervalMilliseconds = 10;

    private readonly IAutoClickService _autoClickService;
    private readonly IHotkeyService _hotkeyService;

    // 统一串行化“开始/停止”及热键触发的启停，避免并发状态冲突。
    private readonly SemaphoreSlim _startStopLock = new(1, 1);

    private string _intervalValue = "100";
    private ClickIntervalUnit _selectedIntervalUnit = ClickIntervalUnit.Milliseconds;
    private TriggerMode _selectedTriggerMode = TriggerMode.Mouse;
    private MouseButtonType _selectedMouseButton = MouseButtonType.Left;
    private Key _selectedKeyboardKey = Key.A;
    private Key _hotkey = Key.F6;
    private Key _pendingHotkey = Key.F6;
    private AppStatus _status = AppStatus.Stopped;
    private string _statusMessage = "就绪。";
    private bool _isErrorMessage;
    private bool _isCapturingKey;
    private bool _isStartStopBusy;
    private int _activeRunVersion;
    private int _isShuttingDown;
    private CaptureTarget _captureTarget = CaptureTarget.None;

    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly RelayCommand _captureTriggerKeyCommand;
    private readonly RelayCommand _captureHotkeyCommand;
    private readonly AsyncRelayCommand _applyHotkeyCommand;

    public MainViewModel(IAutoClickService autoClickService, IHotkeyService hotkeyService)
    {
        _autoClickService = autoClickService;
        _hotkeyService = hotkeyService;

        _startCommand = new AsyncRelayCommand(
            _ => StartAsync(),
            _ => CanStart,
            OnStartCommandException);

        _stopCommand = new AsyncRelayCommand(
            _ => StopAsync(),
            _ => CanStop,
            OnStopCommandException);

        _captureTriggerKeyCommand = new RelayCommand(_ => BeginCapture(CaptureTarget.Trigger), _ => IsConfigurationEnabled);
        _captureHotkeyCommand = new RelayCommand(_ => BeginCapture(CaptureTarget.Hotkey), _ => IsConfigurationEnabled);

        _applyHotkeyCommand = new AsyncRelayCommand(
            _ => ApplyHotkeyAsync(),
            _ => CanApplyHotkey,
            OnApplyHotkeyCommandException);

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _autoClickService.Faulted += OnAutoClickFaulted;

        RefreshDerivedState();
    }

    public ICommand StartCommand => _startCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand CaptureTriggerKeyCommand => _captureTriggerKeyCommand;
    public ICommand CaptureHotkeyCommand => _captureHotkeyCommand;
    public ICommand ApplyHotkeyCommand => _applyHotkeyCommand;

    public string IntervalValue
    {
        get => _intervalValue;
        set
        {
            if (SetProperty(ref _intervalValue, value))
            {
                RefreshDerivedState();
            }
        }
    }

    public ClickIntervalUnit SelectedIntervalUnit
    {
        get => _selectedIntervalUnit;
        set
        {
            if (SetProperty(ref _selectedIntervalUnit, value))
            {
                RefreshDerivedState();
            }
        }
    }

    public TriggerMode SelectedTriggerMode
    {
        get => _selectedTriggerMode;
        set
        {
            if (SetProperty(ref _selectedTriggerMode, value))
            {
                OnPropertyChanged(nameof(IsMouseMode));
                OnPropertyChanged(nameof(IsKeyboardMode));
                RefreshDerivedState();
            }
        }
    }

    public MouseButtonType SelectedMouseButton
    {
        get => _selectedMouseButton;
        set
        {
            if (SetProperty(ref _selectedMouseButton, value))
            {
                RefreshDerivedState();
            }
        }
    }

    public Key SelectedKeyboardKey
    {
        get => _selectedKeyboardKey;
        set
        {
            if (SetProperty(ref _selectedKeyboardKey, value))
            {
                OnPropertyChanged(nameof(SelectedKeyboardKeyDisplay));
                RefreshDerivedState();
            }
        }
    }

    public Key Hotkey
    {
        get => _hotkey;
        private set
        {
            if (SetProperty(ref _hotkey, value))
            {
                OnPropertyChanged(nameof(HotkeyDisplay));
                RefreshDerivedState();
            }
        }
    }

    public Key PendingHotkey
    {
        get => _pendingHotkey;
        set
        {
            if (SetProperty(ref _pendingHotkey, value))
            {
                OnPropertyChanged(nameof(PendingHotkeyDisplay));
                RefreshDerivedState();
            }
        }
    }

    public bool IsMouseMode => SelectedTriggerMode == TriggerMode.Mouse;
    public bool IsKeyboardMode => SelectedTriggerMode == TriggerMode.Keyboard;

    public AppStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CurrentStatusText));
                OnPropertyChanged(nameof(IsConfigurationEnabled));
                RefreshDerivedState();
            }
        }
    }

    public bool IsRunning => Status == AppStatus.Running;

    public bool IsConfigurationEnabled =>
        Status == AppStatus.Stopped
        && !IsStartStopBusy
        && Volatile.Read(ref _isShuttingDown) == 0;

    public bool IsCapturingKey
    {
        get => _isCapturingKey;
        private set
        {
            if (SetProperty(ref _isCapturingKey, value))
            {
                RefreshDerivedState();
            }
        }
    }

    public bool IsStartStopBusy
    {
        get => _isStartStopBusy;
        private set
        {
            if (SetProperty(ref _isStartStopBusy, value))
            {
                OnPropertyChanged(nameof(IsConfigurationEnabled));
                RefreshDerivedState();
            }
        }
    }

    public string CurrentStatusText => Status switch
    {
        AppStatus.Starting => "启动中",
        AppStatus.Running => "运行中",
        AppStatus.Stopping => "停止中",
        _ => "未运行"
    };

    public string SelectedKeyboardKeyDisplay => GetKeyDisplayName(SelectedKeyboardKey);
    public string HotkeyDisplay => GetKeyDisplayName(Hotkey);
    public string PendingHotkeyDisplay => GetKeyDisplayName(PendingHotkey);
    public bool IsIntervalValid => TryGetIntervalMilliseconds(out _, out _);
    public string IntervalValidationText =>
        TryGetIntervalMilliseconds(out _, out var error) ? string.Empty : error;

    public string IntervalPreviewText
    {
        get
        {
            if (!TryGetIntervalMilliseconds(out var intervalMs, out _)
                || !TryParseIntervalValue(out var value, out _))
            {
                return "当前设置不可用";
            }

            if (SelectedIntervalUnit == ClickIntervalUnit.Milliseconds)
            {
                return $"每 {intervalMs} 毫秒触发一次";
            }

            var valueText = value.ToString(
                "0.############################",
                CultureInfo.InvariantCulture);
            var unitText = SelectedIntervalUnit == ClickIntervalUnit.Seconds
                ? "秒"
                : "分钟";

            return $"每 {valueText} {unitText}触发一次（{intervalMs} 毫秒）";
        }
    }

    public string PrecisionNotice => "提示：Windows 普通任务调度无法保证毫秒级绝对精确。";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsErrorMessage
    {
        get => _isErrorMessage;
        private set => SetProperty(ref _isErrorMessage, value);
    }

    public bool CanStart =>
        Status == AppStatus.Stopped
        && !IsCapturingKey
        && !IsStartStopBusy
        && Volatile.Read(ref _isShuttingDown) == 0
        && TryBuildConfiguration(out _, out _);

    public bool CanStop => IsRunning && !IsStartStopBusy;
    public bool CanApplyHotkey => IsConfigurationEnabled && !IsCapturingKey;

    public void Initialize(Window window)
    {
        try
        {
            _hotkeyService.Initialize(window);

            if (_hotkeyService.TryUpdateHotkey(Hotkey, out var error))
            {
                Hotkey = _hotkeyService.CurrentHotkey;
                PendingHotkey = Hotkey;
                SetStatus($"默认启停热键：{GetKeyDisplayName(Hotkey)}。");
                return;
            }

            Hotkey = _hotkeyService.CurrentHotkey;
            SetStatus($"默认热键注册失败：{error}", true);
        }
        catch (Exception ex)
        {
            Hotkey = Key.None;
            SetStatus($"全局热键初始化失败：{ex.Message}", true);
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
        {
            return;
        }

        IsCapturingKey = false;
        _captureTarget = CaptureTarget.None;
        RefreshDerivedState();

        // 必须在第一次 await 之前完成：窗口关闭时立即停止新热键消息，
        // 并非阻塞地取消正在运行的连点任务。
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _autoClickService.Faulted -= OnAutoClickFaulted;
        _hotkeyService.Dispose();
        _autoClickService.RequestStop();

        try
        {
            await ExecuteStartStopLockedAsync(async () =>
            {
                // 若关闭请求与启动操作竞争，第一次 RequestStop 可能早于任务创建；
                // 获得启停锁后再次取消，确保不会在窗口隐藏后留下新任务。
                _autoClickService.RequestStop();

                if (_autoClickService.IsRunning)
                {
                    Status = AppStatus.Stopping;
                }

                await _autoClickService.StopAsync();
                Status = AppStatus.Stopped;
            });
        }
        catch (Exception ex)
        {
            Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
            SetStatus($"关闭时停止任务失败：{ex.Message}", true);
        }
    }

    public bool HandleKeyCapture(Key key)
    {
        if (!IsCapturingKey)
        {
            return false;
        }

        if (key == Key.Escape)
        {
            IsCapturingKey = false;
            _captureTarget = CaptureTarget.None;
            SetStatus("已取消录入。");
            return true;
        }

        if (_captureTarget == CaptureTarget.Trigger)
        {
            if (!KeyRules.TryValidateTriggerKey(key, out var triggerError))
            {
                SetStatus(triggerError, true);
                return true;
            }

            if (key == Hotkey)
            {
                SetStatus("被连点按键不能与启停快捷键相同。", true);
                return true;
            }

            SelectedTriggerMode = TriggerMode.Keyboard;
            SelectedKeyboardKey = key;
            IsCapturingKey = false;
            _captureTarget = CaptureTarget.None;
            SetStatus($"已录入连点按键：{GetKeyDisplayName(key)}。");
            return true;
        }

        if (_captureTarget == CaptureTarget.Hotkey)
        {
            if (!KeyRules.TryValidateHotkeyKey(key, out var hotkeyError))
            {
                SetStatus(hotkeyError, true);
                return true;
            }

            if (SelectedTriggerMode == TriggerMode.Keyboard
                && SelectedKeyboardKey == key)
            {
                SetStatus("启停热键不能与被连点按键相同。", true);
                return true;
            }

            PendingHotkey = key;
            IsCapturingKey = false;
            _captureTarget = CaptureTarget.None;
            SetStatus($"已录入待应用热键：{GetKeyDisplayName(key)}。请点击“应用热键”。");
            return true;
        }

        return true;
    }

    private void BeginCapture(CaptureTarget target)
    {
        if (!IsConfigurationEnabled)
        {
            return;
        }

        IsCapturingKey = true;
        _captureTarget = target;
        SetStatus(target == CaptureTarget.Trigger
            ? "请按下要连点的键，按 Esc 可取消。"
            : "请按下新的启停热键，按 Esc 可取消。");
    }

    private async Task StartAsync()
    {
        await ExecuteStartStopLockedAsync(async () =>
        {
            if (Volatile.Read(ref _isShuttingDown) != 0)
            {
                return;
            }

            if (IsCapturingKey)
            {
                SetStatus("正在录入按键，无法启动。", true);
                return;
            }

            if (!TryBuildConfiguration(out var config, out var error))
            {
                SetStatus(error, true);
                return;
            }

            Status = AppStatus.Starting;
            await _autoClickService.StartAsync(config);
            _activeRunVersion = _autoClickService.CurrentRunVersion;
            Status = AppStatus.Running;
            SetStatus("连点已启动。");
        });
    }

    private async Task StopAsync()
    {
        await ExecuteStartStopLockedAsync(async () =>
        {
            if (!IsRunning)
            {
                SetStatus("当前未运行。");
                return;
            }

            Status = AppStatus.Stopping;
            await _autoClickService.StopAsync();
            Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
            SetStatus(Status == AppStatus.Stopped ? "连点已停止。" : "停止请求已发送。");
        });
    }

    private Task ApplyHotkeyAsync()
    {
        if (!IsConfigurationEnabled)
        {
            SetStatus("运行中无法修改热键。", true);
            return Task.CompletedTask;
        }

        if (!KeyRules.TryValidateHotkeyKey(PendingHotkey, out var keyError))
        {
            SetStatus(keyError, true);
            return Task.CompletedTask;
        }

        if (SelectedTriggerMode == TriggerMode.Keyboard && SelectedKeyboardKey == PendingHotkey)
        {
            SetStatus("启停热键不能与被连点按键相同。", true);
            return Task.CompletedTask;
        }

        var previousHotkey = Hotkey;
        if (_hotkeyService.TryUpdateHotkey(PendingHotkey, out var error))
        {
            Hotkey = _hotkeyService.CurrentHotkey;
            PendingHotkey = Hotkey;
            SetStatus($"启停热键已更新为：{GetKeyDisplayName(Hotkey)}。");
            return Task.CompletedTask;
        }

        Hotkey = _hotkeyService.CurrentHotkey;
        if (Hotkey != Key.None)
        {
            PendingHotkey = Hotkey;
        }
        else
        {
            PendingHotkey = previousHotkey;
        }

        SetStatus($"热键更新失败：{error}", true);
        return Task.CompletedTask;
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _isShuttingDown) != 0)
        {
            return;
        }

        _ = ToggleFromHotkeyAsync();
    }

    private async Task ToggleFromHotkeyAsync()
    {
        if (IsCapturingKey)
        {
            return;
        }

        try
        {
            await TryExecuteStartStopLockedAsync(async () =>
            {
                if (Volatile.Read(ref _isShuttingDown) != 0)
                {
                    return;
                }

                if (IsRunning)
                {
                    Status = AppStatus.Stopping;
                    await _autoClickService.StopAsync();
                    Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
                    SetStatus(Status == AppStatus.Stopped ? "已通过热键停止。" : "热键停止请求已发送。");
                }
                else
                {
                    if (!TryBuildConfiguration(out var config, out var error))
                    {
                        SetStatus(error, true);
                        return;
                    }

                    Status = AppStatus.Starting;
                    await _autoClickService.StartAsync(config);
                    _activeRunVersion = _autoClickService.CurrentRunVersion;
                    Status = AppStatus.Running;
                    SetStatus("已通过热键启动。");
                }
            });
        }
        catch (Exception ex)
        {
            Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
            SetStatus($"热键启停失败：{ex.Message}", true);
        }
    }

    private void OnAutoClickFaulted(object? sender, AutoClickFaultedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => ApplyFaultState(e));
            return;
        }

        ApplyFaultState(e);
    }

    private void ApplyFaultState(AutoClickFaultedEventArgs fault)
    {
        // 忽略旧任务异常，防止新任务已运行时被旧任务 fault 错误覆盖
        if (fault.RunVersion != _activeRunVersion)
        {
            return;
        }

        Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
        SetStatus($"后台连点任务异常：{fault.Exception.Message}", true);
    }

    private void OnStartCommandException(Exception ex)
    {
        Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
        SetStatus($"启动失败：{ex.Message}", true);
    }

    private void OnStopCommandException(Exception ex)
    {
        Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
        SetStatus($"停止失败：{ex.Message}", true);
    }

    private void OnApplyHotkeyCommandException(Exception ex)
    {
        SetStatus($"应用热键失败：{ex.Message}", true);
    }

    private async Task ExecuteStartStopLockedAsync(Func<Task> action)
    {
        await _startStopLock.WaitAsync();
        try
        {
            await ExecuteWithStartStopLockHeldAsync(action);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    /// <summary>
    /// 热键切换不排队：若启停转换正在进行，忽略本次重复热键，
    /// 防止“停止”完成后又被排队的第二次按键重新启动。
    /// </summary>
    private async Task<bool> TryExecuteStartStopLockedAsync(Func<Task> action)
    {
        if (!await _startStopLock.WaitAsync(0))
        {
            return false;
        }

        try
        {
            await ExecuteWithStartStopLockHeldAsync(action);
            return true;
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    private async Task ExecuteWithStartStopLockHeldAsync(Func<Task> action)
    {
        IsStartStopBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsStartStopBusy = false;
        }
    }

    private bool TryBuildConfiguration(out AutoClickConfiguration configuration, out string errorMessage)
    {
        configuration = default!;
        errorMessage = string.Empty;

        if (!TryGetIntervalMilliseconds(out var intervalMs, out errorMessage))
        {
            return false;
        }

        if (Hotkey == Key.None)
        {
            errorMessage = "请先设置一个可用的全局启停热键。";
            return false;
        }

        if (SelectedTriggerMode == TriggerMode.Keyboard)
        {
            if (!KeyRules.TryValidateTriggerKey(SelectedKeyboardKey, out errorMessage))
            {
                return false;
            }

            if (SelectedKeyboardKey == Hotkey)
            {
                errorMessage = "被连点按键不能与启停快捷键相同。";
                return false;
            }

            var vk = KeyInterop.VirtualKeyFromKey(SelectedKeyboardKey);
            if (vk <= 0 || vk > ushort.MaxValue)
            {
                errorMessage = "该按键无法转换为有效 Virtual-Key Code。";
                return false;
            }

            configuration = new AutoClickConfiguration(
                intervalMs,
                TriggerMode.Keyboard,
                SelectedMouseButton,
                (ushort)vk,
                SelectedKeyboardKey);

            return true;
        }

        configuration = new AutoClickConfiguration(
            intervalMs,
            TriggerMode.Mouse,
            SelectedMouseButton,
            0,
            null);

        return true;
    }

    private bool TryGetIntervalMilliseconds(out int intervalMs, out string errorMessage)
    {
        intervalMs = 0;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(IntervalValue))
        {
            errorMessage = "请输入连点间隔。";
            return false;
        }

        if (!TryParseIntervalValue(out var value, out errorMessage))
        {
            return false;
        }

        decimal multiplier;
        switch (SelectedIntervalUnit)
        {
            case ClickIntervalUnit.Milliseconds:
                multiplier = 1m;
                break;
            case ClickIntervalUnit.Seconds:
                multiplier = 1000m;
                break;
            case ClickIntervalUnit.Minutes:
                multiplier = 60000m;
                break;
            default:
                errorMessage = "未知的时间单位。";
                return false;
        }

        decimal rawMs;
        try
        {
            rawMs = value * multiplier;
        }
        catch (OverflowException)
        {
            errorMessage = "连点间隔超出范围。";
            return false;
        }

        if (rawMs > int.MaxValue)
        {
            errorMessage = "连点间隔过大，超出支持范围。";
            return false;
        }

        // 向上取整，保证实际间隔不会短于用户输入的时间。
        intervalMs = decimal.ToInt32(decimal.Ceiling(rawMs));

        if (intervalMs < MinIntervalMilliseconds)
        {
            errorMessage = $"最小间隔为 {MinIntervalMilliseconds} 毫秒。";
            return false;
        }

        return true;
    }

    private bool TryParseIntervalValue(
        out decimal value,
        out string errorMessage)
    {
        value = 0;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(IntervalValue))
        {
            errorMessage = "请输入连点间隔。";
            return false;
        }

        if (!decimal.TryParse(
                IntervalValue,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out value))
        {
            errorMessage = "连点间隔格式无效，请输入正数（小数点使用 .）。";
            return false;
        }

        if (value <= 0)
        {
            errorMessage = "连点间隔必须为正数。";
            return false;
        }

        return true;
    }

    private static string GetKeyDisplayName(Key key)
    {
        return key switch
        {
            Key.None => "未设置",
            Key.Back => "Backspace",
            Key.Return => "Enter",
            Key.Capital => "Caps Lock",
            Key.Prior => "Page Up",
            Key.Next => "Page Down",
            Key.Snapshot => "Print Screen",
            Key.Scroll => "Scroll Lock",
            Key.LeftCtrl => "Left Ctrl",
            Key.RightCtrl => "Right Ctrl",
            Key.LeftShift => "Left Shift",
            Key.RightShift => "Right Shift",
            Key.LeftAlt => "Left Alt",
            Key.RightAlt => "Right Alt",
            Key.LWin => "Left Windows",
            Key.RWin => "Right Windows",
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(
                CultureInfo.InvariantCulture),
            _ => key.ToString()
        };
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsErrorMessage = isError;
    }

    private void RefreshDerivedState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanApplyHotkey));
        OnPropertyChanged(nameof(IsConfigurationEnabled));
        OnPropertyChanged(nameof(IntervalPreviewText));
        OnPropertyChanged(nameof(IsIntervalValid));
        OnPropertyChanged(nameof(IntervalValidationText));

        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _captureTriggerKeyCommand.RaiseCanExecuteChanged();
        _captureHotkeyCommand.RaiseCanExecuteChanged();
        _applyHotkeyCommand.RaiseCanExecuteChanged();
    }

    private enum CaptureTarget
    {
        None = 0,
        Trigger = 1,
        Hotkey = 2
    }
}
