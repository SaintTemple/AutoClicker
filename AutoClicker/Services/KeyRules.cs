using System.Windows.Input;

namespace AutoClicker.Services;

/// <summary>
/// 按键有效性规则：用于过滤纯修饰键、系统保留键和不可用按键。
/// </summary>
public static class KeyRules
{
    private static readonly HashSet<Key> ModifierKeys =
    [
        Key.LeftCtrl, Key.RightCtrl,
        Key.LeftShift, Key.RightShift,
        Key.LeftAlt, Key.RightAlt
    ];

    private static readonly HashSet<Key> ReservedSystemKeys =
    [
        Key.LWin, Key.RWin, Key.Apps, Key.Sleep
    ];

    private static readonly HashSet<Key> UnsupportedKeys =
    [
        Key.None,
        Key.System,
        Key.DeadCharProcessed,
        Key.ImeProcessed,
        Key.KanaMode,
        Key.JunjaMode,
        Key.FinalMode,
        Key.HanjaMode,
        Key.KanjiMode,
        Key.ImeConvert,
        Key.ImeNonConvert
    ];

    public static bool TryValidateTriggerKey(Key key, out string errorMessage)
    {
        return TryValidateCommon(key, out errorMessage);
    }

    public static bool TryValidateHotkeyKey(Key key, out string errorMessage)
    {
        return TryValidateCommon(key, out errorMessage);
    }

    private static bool TryValidateCommon(Key key, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (UnsupportedKeys.Contains(key))
        {
            errorMessage = "该按键无法使用，请选择其他按键。";
            return false;
        }

        if (ModifierKeys.Contains(key))
        {
            errorMessage = "不支持纯修饰键（Ctrl/Shift/Alt）作为目标键或热键。";
            return false;
        }

        if (ReservedSystemKeys.Contains(key))
        {
            errorMessage = "该按键为系统保留键，不支持使用。";
            return false;
        }

        return true;
    }
}