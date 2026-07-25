using System.Windows.Input;

namespace AutoClicker.Models;

public sealed record AutoClickConfiguration(
    int IntervalMilliseconds,
    TriggerMode TriggerMode,
    MouseButtonType MouseButton,
    ushort KeyboardVirtualKey,
    Key? KeyboardKey);