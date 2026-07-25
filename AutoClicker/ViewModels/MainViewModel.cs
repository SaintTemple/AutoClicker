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

    // 统一串行化“开始/停止”及热键触发的启停，避免并发状态冲突
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

    public bool IsConfigurationEnabled => !IsRunning && !IsStartStopBusy;

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

    public string CurrentStatusText => IsRunning ? "运行中" : "未运行";
    public string SelectedKeyboardKeyDisplay => GetKeyDisplayName(SelectedKeyboardKey);
    public string HotkeyDisplay => GetKeyDisplayName(Hotkey);
    public string PendingHotkeyDisplay => GetKeyDisplayName(PendingHotkey);

    public string IntervalPreviewText
    {
        get
        {
            if (!TryGetIntervalMilliseconds(out var intervalMs, out _))
            {
                return "间隔设置无效";
            }

            return $"每 {intervalMs} 毫秒触发一次";
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

    public bool CanStart => !IsRunning && !IsCapturingKey && !IsStartStopBusy && TryBuildConfiguration(out _, out _);
    public bool CanStop => IsRunning && !IsStartStopBusy;
    public bool CanApplyHotkey => IsConfigurationEnabled && !IsCapturingKey;

    public void Initialize(Window window)
    {
        _hotkeyService.Initialize(window);

        if (_hotkeyService.TryUpdateHotkey(Hotkey, out var error))
        {
            SetStatus($"默认启停热键：{GetKeyDisplayName(Hotkey)}。");
        }
        else
        {
            SetStatus($"默认热键注册失败：{error}", true);
        }
    }

    public async Task ShutdownAsync()
    {
        IsCapturingKey = false;
        _captureTarget = CaptureTarget.None;

        try
        {
            await ExecuteStartStopLockedAsync(async () =>
            {
                await _autoClickService.StopAsync();
                Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
            });
        }
        catch (Exception ex)
        {
            Status = _autoClickService.IsRunning ? AppStatus.Running : AppStatus.Stopped;
            SetStatus($"关闭时停止任务失败：{ex.Message}", true);
        }
        finally
        {
            _hotkeyService.Dispose();
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

    private Task StartAsync()
    {
        return ExecuteStartStopLockedAsync(async () =>
        {
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

            await _autoClickService.StartAsync(config);
            _activeRunVersion = _autoClickService.CurrentRunVersion;
            Status = AppStatus.Running;
            SetStatus("连点已启动。");
        });
    }

    private Task StopAsync()
    {
        return ExecuteStartStopLockedAsync(async () =>
        {
            if (!IsRunning)
            {
                SetStatus("当前未运行。");
                return;
            }

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
            Hotkey = PendingHotkey;
            SetStatus($"启停热键已更新为：{GetKeyDisplayName(Hotkey)}。");
            return Task.CompletedTask;
        }

        PendingHotkey = previousHotkey;
        SetStatus($"热键更新失败：{error}", true);
        return Task.CompletedTask;
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
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
            await ExecuteStartStopLockedAsync(async () =>
            {
                if (IsRunning)
                {
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
            IsStartStopBusy = true;
            await action();
        }
        finally
        {
            IsStartStopBusy = false;
            _startStopLock.Release();
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

        if (!double.TryParse(IntervalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            errorMessage = "连点间隔格式无效，请输入正数（小数点请使用 .）。";
            return false;
        }

        if (value <= 0)
        {
            errorMessage = "连点间隔必须为正数。";
            return false;
        }

        decimal multiplier = SelectedIntervalUnit switch
        {
            ClickIntervalUnit.Milliseconds => 1m,
            ClickIntervalUnit.Seconds => 1000m,
            ClickIntervalUnit.Minutes => 60000m,
            _ => 1m
        };

        decimal rawMs;
        try
        {
            rawMs = (decimal)value * multiplier;
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

        intervalMs = (int)Math.Round(rawMs, MidpointRounding.AwayFromZero);

        if (intervalMs < MinIntervalMilliseconds)
        {
            errorMessage = $"最小间隔为 {MinIntervalMilliseconds} 毫秒。";
            return false;
        }

        return true;
    }

    private static string GetKeyDisplayName(Key key)
    {
        return key == Key.None ? "未设置" : key.ToString();
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