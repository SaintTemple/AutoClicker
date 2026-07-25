using AutoClicker.Interop;
using AutoClicker.Models;
using System.Runtime.InteropServices;

namespace AutoClicker.Services;

public sealed class SendInputSimulationService : IInputSimulationService
{
    public void ClickMouse(MouseButtonType buttonType)
    {
        uint downFlag = buttonType switch
        {
            MouseButtonType.Left => NativeConstants.MOUSEEVENTF_LEFTDOWN,
            MouseButtonType.Right => NativeConstants.MOUSEEVENTF_RIGHTDOWN,
            MouseButtonType.Middle => NativeConstants.MOUSEEVENTF_MIDDLEDOWN,
            _ => throw new ArgumentOutOfRangeException(nameof(buttonType), buttonType, "未知鼠标按键类型。")
        };

        uint upFlag = buttonType switch
        {
            MouseButtonType.Left => NativeConstants.MOUSEEVENTF_LEFTUP,
            MouseButtonType.Right => NativeConstants.MOUSEEVENTF_RIGHTUP,
            MouseButtonType.Middle => NativeConstants.MOUSEEVENTF_MIDDLEUP,
            _ => throw new ArgumentOutOfRangeException(nameof(buttonType), buttonType, "未知鼠标按键类型。")
        };

        var inputs = new INPUT[2];

        inputs[0] = new INPUT
        {
            type = NativeConstants.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = downFlag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        inputs[1] = new INPUT
        {
            type = NativeConstants.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = upFlag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputs(inputs);
    }

    public void PressKey(ushort virtualKey)
    {
        if (virtualKey == 0)
        {
            throw new ArgumentException("Virtual-Key Code 无效。", nameof(virtualKey));
        }

        var extendedFlag = IsExtendedKey(virtualKey) ? NativeConstants.KEYEVENTF_EXTENDEDKEY : 0u;

        var inputs = new INPUT[2];

        inputs[0] = new INPUT
        {
            type = NativeConstants.INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = extendedFlag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        inputs[1] = new INPUT
        {
            type = NativeConstants.INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = extendedFlag | NativeConstants.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInputs(inputs);
    }

    private static void SendInputs(INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput 调用失败，已发送 {sent}/{inputs.Length}，错误码：{errorCode}。");
        }
    }

    private static bool IsExtendedKey(ushort virtualKey)
    {
        return virtualKey is
            0x21 or 0x22 or 0x23 or 0x24 or
            0x25 or 0x26 or 0x27 or 0x28 or
            0x2D or 0x2E or 0x6F or 0x90 or
            0xA3 or 0xA5;
    }
}