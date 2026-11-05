using System.ComponentModel;
using System.Diagnostics;

namespace Miu.Dev.Docker;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Runs a command-line tool and captures its output. A missing tool returns exit code 127.</summary>
public sealed class ProcessRunner(TimeSpan? timeout = null) : IProcessRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);

    public async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new ProcessResult(127, "", "Could not start " + file);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
            return new ProcessResult(p.ExitCode, await stdout, await stderr);
        }
        catch (Win32Exception ex) { return new ProcessResult(127, "", ex.Message); }
    }
}
