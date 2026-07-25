namespace AutoClicker.Services;

public sealed class AutoClickFaultedEventArgs : EventArgs
{
    public AutoClickFaultedEventArgs(Exception exception, int runVersion)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        RunVersion = runVersion;
    }

    public Exception Exception { get; }

    public int RunVersion { get; }
}