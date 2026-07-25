using AutoClicker.Models;

namespace AutoClicker.Services;

public interface IInputSimulationService
{
    void ClickMouse(MouseButtonType buttonType);
    void PressKey(ushort virtualKey);
}