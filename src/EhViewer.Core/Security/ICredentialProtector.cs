namespace EhViewer.Core.Security;

/// <summary>
/// Encrypts and decrypts sensitive values (such as session cookies) before persisting to disk.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into an envelope string suitable for storage.
    /// </summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts <paramref name="protectedValue"/> previously returned by <see cref="Protect"/>.
    /// </summary>
    string Unprotect(string protectedValue);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> carries this protector's envelope prefix.
    /// </summary>
    bool IsProtected(string? value);
}
