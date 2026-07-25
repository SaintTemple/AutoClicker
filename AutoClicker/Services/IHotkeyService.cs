using System.Windows;
using System.Windows.Input;

namespace AutoClicker.Services;

public interface IHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;
    Key CurrentHotkey { get; }
    void Initialize(Window window);
    bool TryUpdateHotkey(Key key, out string errorMessage);
}