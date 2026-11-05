namespace Miu.Platform.Abstractions;

/// <summary>A network share that is connected now, with or without a drive letter.</summary>
public sealed record MappedDrive(string? Letter, string Unc, bool Available);

public sealed record NetResult(bool Ok, string Message)
{
    public static NetResult Success(string message) => new(true, message);
    public static NetResult Fail(string message) => new(false, message);
}

/// <summary>Connecting network shares (SMB). Windows maps drive letters; elsewhere the file manager mounts shares.</summary>
public interface INetworkDrives
{
    bool IsSupported { get; }

    /// <summary>Shown when <see cref="IsSupported"/> is false.</summary>
    string UnsupportedReason { get; }

    IReadOnlyList<MappedDrive> Mapped();

    /// <summary>Drive letters nobody uses, e.g. "Z:".</summary>
    IReadOnlyList<string> FreeLetters();

    /// <param name="letter">"Z:" or null to connect without a letter.</param>
    Task<NetResult> MapAsync(string unc, string? letter, string? user, string? password, bool reconnectAtSignIn, CancellationToken ct);

    /// <param name="letterOrUnc">"Z:" or the \\server\share it was connected as.</param>
    Task<NetResult> UnmapAsync(string letterOrUnc, CancellationToken ct);

    /// <summary>The shared folders on a computer (\\server), or an empty list when it can't be asked.</summary>
    Task<IReadOnlyList<string>> ListSharesAsync(string server, CancellationToken ct);
}

public sealed class UnsupportedNetworkDrives(string reason) : INetworkDrives
{
    public bool IsSupported => false;
    public string UnsupportedReason => reason;
    public IReadOnlyList<MappedDrive> Mapped() => [];
    public IReadOnlyList<string> FreeLetters() => [];
    public Task<NetResult> MapAsync(string unc, string? letter, string? user, string? password, bool reconnectAtSignIn, CancellationToken ct) =>
        Task.FromResult(NetResult.Fail(reason));
    public Task<NetResult> UnmapAsync(string letterOrUnc, CancellationToken ct) => Task.FromResult(NetResult.Fail(reason));
    public Task<IReadOnlyList<string>> ListSharesAsync(string server, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
}
