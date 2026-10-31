using Ntilde.Platform.Security;
namespace Ntilde.Shell.Secrets
{
    /// <summary>
    /// Abstracts per-user secret storage. Implementations back onto OS keychains
    /// (Windows Credential Manager, macOS Keychain, Linux Secret Service) or, for
    /// tests, an in-memory dictionary. There is intentionally no file-based,
    /// machine-derived-key implementation (see issue #100).
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>True when this store can persist secrets right now.</summary>
        bool IsAvailable { get; }

        /// <summary>Returns the stored value, or null if absent.</summary>
        string? Read(string key);

        /// <summary>Creates or overwrites the value for <paramref name="key"/>.</summary>
        void Write(string key, string value);

        /// <summary>Removes the value; returns true if something was removed.</summary>
        bool Delete(string key);

        // String adapters remain for OpenSSH askpass and existing store implementations. Production
        // platform stores override these so native SSH never decodes a credential to a managed string.
        /// <summary>Returns an owned buffer, or null if absent/unavailable; an available store must throw on lookup errors.</summary>
        SecretBuffer? ReadBuffer(string key)
        {
            string? value = Read(key);
            return value is null ? null : SecretBuffer.FromText(value);
        }
        void WriteBuffer(string key, SecretBuffer value) => Write(key, value.DangerousGetString());
    }
}
