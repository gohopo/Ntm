using Ntm.Gateway.Security;

namespace Ntm.Gateway.Tests.Security;

public sealed class SecretFingerprintTests
{
    [Fact]
    public void ALongSecretKeepsTheFullPrefixLength()
    {
        string secret = "sk-" + new string('a', 60);

        Assert.Equal(secret[..SecretFingerprint.PrefixLength], SecretFingerprint.Prefix(secret));
    }

    [Theory]
    [InlineData("12345678", "12")]
    [InlineData("abcdefghijkl", "abcd")]
    [InlineData("ab", "")]
    public void AShortSecretNeverShowsMoreThanAThird(string secret, string expected)
    {
        string prefix = SecretFingerprint.Prefix(secret);

        Assert.Equal(expected, prefix);
        Assert.True(prefix.Length * 3 <= secret.Length);
    }

    [Fact]
    public void AGeneratedApiKeyHasAUsefulPrefix()
    {
        string token = SecretFingerprint.GenerateApiKey();

        Assert.StartsWith("ntm-", token, StringComparison.Ordinal);
        Assert.Equal(52, token.Length);
        Assert.Equal(token[..SecretFingerprint.PrefixLength], SecretFingerprint.Prefix(token));
    }

    [Fact]
    public void TheFingerprintIsStableAndDoesNotContainTheSecret()
    {
        const string secret = "sk-test-secret-value";

        string fingerprint = SecretFingerprint.Compute(secret);

        Assert.Equal(fingerprint, SecretFingerprint.Compute(secret));
        Assert.Equal(64, fingerprint.Length);
        Assert.DoesNotContain(secret, fingerprint, StringComparison.Ordinal);
        Assert.True(SecretFingerprint.Matches(fingerprint, SecretFingerprint.Compute(secret)));
        Assert.False(SecretFingerprint.Matches(fingerprint, SecretFingerprint.Compute(secret + "x")));
    }

    [Fact]
    public void AnEmptySecretIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SecretFingerprint.Prefix(""));
        Assert.Throws<ArgumentException>(() => SecretFingerprint.Compute(""));
    }
}
