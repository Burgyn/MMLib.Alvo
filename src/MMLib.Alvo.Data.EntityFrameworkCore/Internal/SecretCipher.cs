using MMLib.Alvo.Secrets;

using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Encrypts a secret's value with AES-GCM under a key-encryption key the platform mounts as a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key comes from a file, and there is no fallback.</b> §7.1's answer to the bootstrap paradox is that
/// the credential for the secret store comes from the platform — a mounted Kubernetes secret, a Docker
/// secret, a workload identity's file — never from another secret store and never from configuration, which
/// is readable from a process listing, a crash dump and <c>docker inspect</c>. With no readable key file
/// there is no cipher, so there is no writable store at all; a generated or derived key would mean a
/// deployment that lost its mount silently re-encrypting under a key nobody can restore from.
/// </para>
/// <para>
/// <b>AES-GCM rather than AES-CBC plus an HMAC.</b> The stored value must be tamper-evident, because a row
/// an operator can edit is a row an attacker who reached the database can edit, and a cipher without
/// authentication would decrypt a swapped ciphertext into a key that points somewhere else. GCM
/// authenticates as part of decrypting, so <see cref="Unprotect"/> throws rather than returning a value
/// nobody wrote.
/// </para>
/// <para>
/// <b>A fresh 12-byte nonce per encryption</b>, which is what makes two encryptions of one value differ. A
/// deterministic nonce under one key is the failure that turns GCM into a cipher that leaks equality and, on
/// a repeat, the key stream itself.
/// </para>
/// </remarks>
internal sealed class SecretCipher
{
    /// <summary>The nonce length AES-GCM is specified for, and the only one worth using.</summary>
    private const int NonceBytes = 12;

    /// <summary>The full-length authentication tag.</summary>
    private const int TagBytes = 16;

    /// <summary>The key length this build accepts — AES-256, spelled in bytes.</summary>
    private const int KeyBytes = 32;

    private readonly byte[] _key;

    private SecretCipher(byte[] key) => _key = key;

    /// <summary>
    /// The cipher for the key file at <paramref name="path"/>, or <see langword="null"/> when this
    /// deployment mounted none.
    /// </summary>
    /// <param name="path">The configured path, which may be absent or point at nothing.</param>
    /// <returns>The cipher, or <see langword="null"/> when there is no key to build one from.</returns>
    /// <exception cref="InvalidOperationException">The file exists and is not a 32-byte base64 key.</exception>
    /// <remarks>
    /// <b>Absent is null; present and wrong is a refusal.</b> A deployment that mounted nothing has made a
    /// choice — its secrets come from configuration — and turning that into a startup failure would refuse
    /// every GitOps deployment. A file that is there and unusable is a mount that went wrong, and starting
    /// without a writable store would hide it until the first save.
    /// </remarks>
    internal static SecretCipher? FromKeyFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new SecretCipher(KeyFrom(path, File.ReadAllText(path)));
    }

    /// <summary>The key the file holds, refused by message when it is not one.</summary>
    /// <remarks>
    /// The content is trimmed before decoding: a mounted secret usually ends with a newline, and refusing one
    /// would be a footgun with no upside.
    /// </remarks>
    private static byte[] KeyFrom(string path, string content)
    {
        if (!Convert.TryFromBase64String(content.Trim(), new byte[KeyBytes + 1], out var written)
            || written != KeyBytes)
        {
            throw new InvalidOperationException(
                $"The secret encryption key at '{path}' is not {KeyBytes} bytes of base64. Write one with "
                + $"`openssl rand -base64 {KeyBytes}` and mount that file, or unset "
                + $"{AlvoSecretOptions.ConfigurationSection}:EncryptionKeyFile to run "
                + "with configuration-supplied secrets only.");
        }

        return Convert.FromBase64String(content.Trim());
    }

    /// <summary>Encrypts <paramref name="plaintext"/> into the form a row stores.</summary>
    /// <param name="plaintext">The secret's value, exactly as the caller wrote it.</param>
    /// <returns>Base64 of <c>nonce || ciphertext || tag</c>.</returns>
    internal string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var stored = new byte[NonceBytes + bytes.Length + TagBytes];
        var nonce = stored.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(
            nonce,
            bytes,
            stored.AsSpan(NonceBytes, bytes.Length),
            stored.AsSpan(NonceBytes + bytes.Length, TagBytes));

        return Convert.ToBase64String(stored);
    }

    /// <summary>Decrypts what <see cref="Protect"/> stored.</summary>
    /// <param name="stored">The stored form.</param>
    /// <returns>The original value, byte for byte.</returns>
    /// <exception cref="CryptographicException">The stored value was tampered with, or is not one of ours.</exception>
    internal string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var bytes = Decode(stored);
        var plaintext = new byte[bytes.Length - NonceBytes - TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(
            bytes.AsSpan(0, NonceBytes),
            bytes.AsSpan(NonceBytes, plaintext.Length),
            bytes.AsSpan(NonceBytes + plaintext.Length, TagBytes),
            plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// The stored bytes, refusing anything too short to hold a nonce and a tag.
    /// </summary>
    /// <remarks>
    /// As a <see cref="CryptographicException"/> rather than a format error: a row that is not one of ours is
    /// a row we must not decrypt, and one exception type is what lets a caller treat "tampered" and
    /// "truncated" as the one thing they are.
    /// </remarks>
    private static byte[] Decode(string stored)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            throw new CryptographicException("The stored secret is not base64, so it was not written by Alvo.");
        }

        return bytes.Length >= NonceBytes + TagBytes
            ? bytes
            : throw new CryptographicException(
                "The stored secret is too short to carry a nonce and an authentication tag.");
    }
}
