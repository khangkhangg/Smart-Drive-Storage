namespace Miu.Platform.Abstractions;

/// <summary>Starting Miu when you sign in (background mode).</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }
    void Enable(string executablePath, string arguments);
    void Disable();
}

public interface IPowerState
{
    bool IsOnBattery { get; }
}

public sealed class NoStartupRegistration : IStartupRegistration
{
    public bool IsEnabled => false;
    public void Enable(string executablePath, string arguments) { }
    public void Disable() { }
}

public sealed class AlwaysPlugged : IPowerState
{
    public bool IsOnBattery => false;
}
