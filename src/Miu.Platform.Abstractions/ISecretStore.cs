namespace Miu.Platform.Abstractions;

/// <summary>Where Miu keeps secrets (bot tokens, cloud sign-ins): never in the database.</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}
