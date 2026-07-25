using AutoClicker.Models;

namespace AutoClicker.Services;

public interface IAutoClickService
{
    event EventHandler<AutoClickFaultedEventArgs>? Faulted;

    bool IsRunning { get; }

    int CurrentRunVersion { get; }

    Task StartAsync(AutoClickConfiguration configuration);

    Task StopAsync();
}