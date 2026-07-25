using System.Windows.Input;

namespace AutoClicker.Services;

/// <summary>
/// 按键有效性规则。连点目标允许普通实体键和纯修饰键；
/// 全局热键则必须是一个非修饰键，避免 RegisterHotKey 的歧义行为。
/// </summary>
public static class KeyRules
{
    private static readonly HashSet<Key> ModifierKeys =
    [
        Key.LeftCtrl, Key.RightCtrl,
        Key.LeftShift, Key.RightShift,
        Key.LeftAlt, Key.RightAlt,
        Key.LWin, Key.RWin
    ];

    private static readonly HashSet<Key> UnsupportedHotkeys =
    [
        Key.Apps,
        Key.Sleep
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
        if (!TryValidateCommon(key, out errorMessage))
        {
            return false;
        }

        if (key == Key.Sleep)
        {
            errorMessage = "为避免意外使电脑进入睡眠状态，不支持将 Sleep 设为连点按键。";
            return false;
        }

        return true;
    }

    public static bool TryValidateHotkeyKey(Key key, out string errorMessage)
    {
        if (!TryValidateCommon(key, out errorMessage))
        {
            return false;
        }

        if (ModifierKeys.Contains(key))
        {
            errorMessage = "启停热键不能是单独的 Ctrl、Shift、Alt 或 Windows 键。";
            return false;
        }

        if (UnsupportedHotkeys.Contains(key))
        {
            errorMessage = "该按键不能注册为全局启停热键，请选择其他按键。";
            return false;
        }

        return true;
    }

    private static bool TryValidateCommon(Key key, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (UnsupportedKeys.Contains(key))
        {
            errorMessage = "该按键无法使用，请选择其他按键。";
            return false;
        }

        return true;
    }
}
