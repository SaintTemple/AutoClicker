using AutoClicker.Interop;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AutoClicker.Services;

public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int HotkeyId = 0xA11C;

    private IntPtr _windowHandle = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private bool _isRegistered;

    public event EventHandler? HotkeyPressed;

    public Key CurrentHotkey { get; private set; } = Key.None;

    /// <summary>
    /// 绑定窗口句柄并挂载 WM_HOTKEY 消息钩子。
    /// </summary>
    public void Initialize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(window).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        if (_windowHandle == IntPtr.Zero || _hwndSource is null)
        {
            _windowHandle = IntPtr.Zero;
            throw new InvalidOperationException("无法获取主窗口句柄，不能注册全局热键。");
        }

        _hwndSource.AddHook(WndProc);
    }

    /// <summary>
    /// 更新全局热键。失败时自动恢复旧热键。
    /// </summary>
    public bool TryUpdateHotkey(Key key, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (_windowHandle == IntPtr.Zero)
        {
            errorMessage = "窗口句柄尚未初始化。";
            return false;
        }

        if (!KeyRules.TryValidateHotkeyKey(key, out errorMessage))
        {
            return false;
        }

        if (_isRegistered && key == CurrentHotkey)
        {
            return true;
        }

        var oldKey = CurrentHotkey;
        var hadOldKey = _isRegistered;

        if (hadOldKey)
        {
            var unregistered = NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
            if (!unregistered)
            {
                var win32Error = Marshal.GetLastWin32Error();
                var error = new Win32Exception(win32Error);
                errorMessage = $"UnregisterHotKey 失败：{error.Message} (错误码 {win32Error})";
                // 注销失败时，保持原状态，避免服务状态与系统状态不一致
                return false;
            }

            _isRegistered = false;
        }

        if (TryRegisterInternal(key, out errorMessage))
        {
            CurrentHotkey = key;
            _isRegistered = true;
            return true;
        }

        if (hadOldKey)
        {
            if (TryRegisterInternal(oldKey, out var rollbackError))
            {
                CurrentHotkey = oldKey;
                _isRegistered = true;
            }
            else
            {
                // 回滚失败时，明确处于未注册状态
                _isRegistered = false;
                CurrentHotkey = Key.None;
                errorMessage =
                    $"{errorMessage} 同时无法恢复原热键：{rollbackError}";
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero && _isRegistered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
            _isRegistered = false;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _windowHandle = IntPtr.Zero;
        CurrentHotkey = Key.None;
    }

    private bool TryRegisterInternal(Key key, out string errorMessage)
    {
        errorMessage = string.Empty;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);

        if (virtualKey <= 0 || virtualKey > ushort.MaxValue)
        {
            errorMessage = "热键无法转换为有效 Virtual-Key Code。";
            return false;
        }

        var success = NativeMethods.RegisterHotKey(
            _windowHandle,
            HotkeyId,
            NativeConstants.MOD_NONE | NativeConstants.MOD_NOREPEAT,
            (uint)virtualKey);

        if (!success)
        {
            var errorCode = Marshal.GetLastWin32Error();
            var error = new Win32Exception(errorCode);
            errorMessage = errorCode == 1409
                ? "该按键已被系统或其他程序注册为全局热键，请选择其他按键。"
                : $"RegisterHotKey 失败：{error.Message}（错误码 {errorCode}）。";
            return false;
        }

        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeConstants.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }
}
