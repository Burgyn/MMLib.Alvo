using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Security.Cryptography;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The cipher every stored secret goes through, and the four ways it must fail.
/// </summary>
/// <remarks>
/// Each fact here is one a plausible implementation gets wrong silently: a deterministic nonce passes a
/// round-trip test and leaks equality; an unauthenticated mode decrypts a tampered row into a value nobody
/// wrote; a 16-byte key is accepted by <see cref="AesGcm"/> and halves the strength the design claims.
/// </remarks>
public sealed class SecretCipherTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void A_value_survives_a_round_trip()
    {
        var cipher = SecretCipher.FromKeyFile(WriteKeyFile())!;

        cipher.Unprotect(cipher.Protect("  sk-secret with spaces  ")).ShouldBe("  sk-secret with spaces  ");
    }

    /// <summary>Two encryptions of one value differ, which is what a fresh nonce per write buys.</summary>
    [Fact]
    public void Two_encryptions_of_one_value_differ()
    {
        var cipher = SecretCipher.FromKeyFile(WriteKeyFile())!;

        cipher.Protect("same").ShouldNotBe(cipher.Protect("same"));
    }

    /// <summary>A row somebody edited is refused rather than decrypted into something nobody wrote.</summary>
    [Fact]
    public void A_tampered_ciphertext_is_refused_rather_than_decrypted()
    {
        var cipher = SecretCipher.FromKeyFile(WriteKeyFile())!;
        var stored = cipher.Protect("value").ToCharArray();
        stored[^2] = stored[^2] == 'A' ? 'B' : 'A';

        Should.Throw<CryptographicException>(() => cipher.Unprotect(new string(stored)));
    }

    /// <summary>A row that is not one of ours is the same refusal, not a format error a caller must learn.</summary>
    [Fact]
    public void A_value_that_was_never_ours_is_refused_the_same_way()
    {
        var cipher = SecretCipher.FromKeyFile(WriteKeyFile())!;

        Should.Throw<CryptographicException>(() => cipher.Unprotect("not base64 at all"));
        Should.Throw<CryptographicException>(() => cipher.Unprotect(Convert.ToBase64String([1, 2, 3])));
    }

    /// <summary>A value encrypted under one key does not decrypt under another.</summary>
    [Fact]
    public void Another_deployments_key_does_not_read_this_ones_secrets()
    {
        var stored = SecretCipher.FromKeyFile(WriteKeyFile())!.Protect("value");

        Should.Throw<CryptographicException>(
            () => SecretCipher.FromKeyFile(WriteKeyFile())!.Unprotect(stored));
    }

    /// <summary>No key file is no cipher — which is how a deployment ends up with no writable store.</summary>
    [Fact]
    public void A_missing_key_file_yields_no_cipher() =>
        SecretCipher.FromKeyFile(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}"))
            .ShouldBeNull();

    /// <summary>And an unset path is the same answer, without touching the file system.</summary>
    [Fact]
    public void An_unset_path_yields_no_cipher() => SecretCipher.FromKeyFile(null).ShouldBeNull();

    /// <summary>A file that is there and wrong is a mount that went wrong, so it is refused by message.</summary>
    [Fact]
    public void A_key_of_the_wrong_length_is_refused_by_message() =>
        Should.Throw<InvalidOperationException>(() => SecretCipher.FromKeyFile(WriteKeyFile(bytes: 16)))
            .Message.ShouldContain("32");

    /// <summary>
    /// A mounted secret's trailing newline is not a broken key.
    /// </summary>
    /// <remarks>
    /// Kubernetes, Docker and <c>openssl rand -base64 32 &gt; key</c> all produce one. Refusing it would be a
    /// footgun with no upside, and the operator would have no way to tell a whitespace refusal from a wrong
    /// key.
    /// </remarks>
    [Fact]
    public void A_key_file_that_ends_with_a_newline_is_read()
    {
        var path = WriteKeyFile(trailingNewline: true);

        SecretCipher.FromKeyFile(path).ShouldNotBeNull();
    }

    private string WriteKeyFile(int bytes = 32, bool trailingNewline = false)
    {
        var key = new byte[bytes];
        RandomNumberGenerator.Fill(key);

        var path = Path.Combine(Path.GetTempPath(), $"alvo-secret-key-{Guid.NewGuid():N}");
        File.WriteAllText(path, Convert.ToBase64String(key) + (trailingNewline ? Environment.NewLine : string.Empty));
        _files.Add(path);

        return path;
    }

    public void Dispose()
    {
        foreach (var path in _files)
        {
            // Best-effort: these are temp files, so a stray lock must not fail the test.
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
