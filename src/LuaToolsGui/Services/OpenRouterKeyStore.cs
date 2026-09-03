using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LuaToolsGui.Services;

/// <summary>
/// Stores the optional OpenRouter key encrypted for the current Windows user. An environment variable
/// always wins, which keeps managed/development installs secret-free on disk.
/// </summary>
public sealed class OpenRouterKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LuaTools.CanIRunIt.OpenRouter.v1");
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "openrouter-key.dat");

    public bool IsManagedByEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));

    public string? Load()
    {
        string? environmentKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey.Trim();

        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] encrypted = File.ReadAllBytes(FilePath);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            string value = Encoding.UTF8.GetString(clear).Trim();
            CryptographicOperations.ZeroMemory(clear);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("API key cannot be empty.", nameof(key));

        byte[] clear = Encoding.UTF8.GetBytes(key);
        try
        {
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(DirectoryPath);
            string temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best effort; an environment-provided key is unaffected */ }
    }
}
