using AutoClicker.Interop;
using AutoClicker.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoClicker.Services;

/// <summary>
/// 使用 SendInput 模拟一组完整的按下和松开事件。
/// 鼠标输入不携带坐标，因此作用于当前鼠标指针所在位置。
/// </summary>
public sealed class SendInputSimulationService : IInputSimulationService
{
    public void ClickMouse(MouseButtonType buttonType)
    {
        var (downFlag, upFlag) = buttonType switch
        {
            MouseButtonType.Left => (
                NativeConstants.MOUSEEVENTF_LEFTDOWN,
                NativeConstants.MOUSEEVENTF_LEFTUP),
            MouseButtonType.Right => (
                NativeConstants.MOUSEEVENTF_RIGHTDOWN,
                NativeConstants.MOUSEEVENTF_RIGHTUP),
            MouseButtonType.Middle => (
                NativeConstants.MOUSEEVENTF_MIDDLEDOWN,
                NativeConstants.MOUSEEVENTF_MIDDLEUP),
            _ => throw new ArgumentOutOfRangeException(
                nameof(buttonType),
                buttonType,
                "未知鼠标按键类型。")
        };

        var releaseInput = CreateMouseInput(upFlag);
        INPUT[] inputs =
        [
            CreateMouseInput(downFlag),
            releaseInput
        ];

        SendInputs(inputs, releaseInput);
    }

    public void PressKey(ushort virtualKey)
    {
        if (virtualKey == 0)
        {
            throw new ArgumentException(
                "Virtual-Key Code 无效。",
                nameof(virtualKey));
        }

        var extendedFlag = IsExtendedKey(virtualKey)
            ? NativeConstants.KEYEVENTF_EXTENDEDKEY
            : 0u;

        var releaseInput = CreateKeyboardInput(
            virtualKey,
            extendedFlag | NativeConstants.KEYEVENTF_KEYUP);

        INPUT[] inputs =
        [
            CreateKeyboardInput(virtualKey, extendedFlag),
            releaseInput
        ];

        SendInputs(inputs, releaseInput);
    }

    private static INPUT CreateMouseInput(uint flags)
    {
        return new INPUT
        {
            type = NativeConstants.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flags
                }
            }
        };
    }

    private static INPUT CreateKeyboardInput(
        ushort virtualKey,
        uint flags)
    {
        return new INPUT
        {
            type = NativeConstants.INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = flags
                }
            }
        };
    }

    private static void SendInputs(
        INPUT[] inputs,
        INPUT releaseInput)
    {
        var inputSize = Marshal.SizeOf<INPUT>();
        Marshal.SetLastPInvokeError(0);

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            inputSize);

        if (sent == (uint)inputs.Length)
        {
            return;
        }

        var errorCode = Marshal.GetLastPInvokeError();

        // 极少数部分成功的情况下，尽力补发 UP，避免按键保持按下。
        if (sent > 0)
        {
            NativeMethods.SendInput(
                1,
                [releaseInput],
                inputSize);
        }

        var detail = errorCode == 0
            ? "Windows 可能因目标窗口权限高于本程序而拒绝输入。"
            : new Win32Exception(errorCode).Message;

        throw new InvalidOperationException(
            $"SendInput 失败，已发送 {sent}/{inputs.Length} 个事件。{detail}");
    }

    private static bool IsExtendedKey(ushort virtualKey)
    {
        return virtualKey is
            0x21 or 0x22 or 0x23 or 0x24 or // PageUp/PageDown/End/Home
            0x25 or 0x26 or 0x27 or 0x28 or // 方向键
            0x2C or 0x2D or 0x2E or         // PrintScreen/Insert/Delete
            0x5B or 0x5C or                 // 左/右 Windows
            0x6F or 0x90 or                 // 小键盘除号/NumLock
            0xA3 or 0xA5;                   // 右 Ctrl/右 Alt
    }
}
