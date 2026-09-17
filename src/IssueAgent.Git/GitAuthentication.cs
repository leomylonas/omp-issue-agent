namespace IssueAgent.Git;

public enum GitAuthenticationMode
{
    ProviderToken,
    Token,
    Ssh,
    Anonymous,
}

/// <summary>Resolved Git transport authentication. Secret values are already resolved from their
/// configured source; this type is never logged.</summary>
public sealed record GitAuthentication
{
    public required GitAuthenticationMode Mode { get; init; }

    /// <summary>HTTPS username (commonly "x-access-token" or similar) for token-based modes.</summary>
    public string? HttpsUsername { get; init; }

    /// <summary>HTTPS token/password for <see cref="GitAuthenticationMode.ProviderToken"/> or
    /// <see cref="GitAuthenticationMode.Token"/>.</summary>
    public string? HttpsToken { get; init; }

    /// <summary>SSH private key contents (PEM) for <see cref="GitAuthenticationMode.Ssh"/>.</summary>
    public string? SshPrivateKey { get; init; }

    public string? SshPrivateKeyPassphrase { get; init; }

    public string? SshUsername { get; init; }

    public SshTrust? SshTrust { get; init; }

    public TlsTrust TlsTrust { get; init; } = TlsTrust.System;

    public static GitAuthentication Anonymous(TlsTrust tlsTrust) => new() { Mode = GitAuthenticationMode.Anonymous, TlsTrust = tlsTrust };
}

public enum TlsTrustMode
{
    System,
    SystemPlusAdditionalCa,
    Pinned,
    None,
}

/// <summary>TLS trust configuration for HTTPS Git transport. <see cref="Fingerprints"/> is populated
/// only for <see cref="TlsTrustMode.Pinned"/>.</summary>
public sealed record TlsTrust
{
    public required TlsTrustMode Mode { get; init; }

    public IReadOnlyList<string> AdditionalCaCertificatePaths { get; init; } = [];

    public IReadOnlyList<string> Fingerprints { get; init; } = [];

    public static TlsTrust System { get; } = new() { Mode = TlsTrustMode.System };
}

public enum SshHostVerificationMode
{
    Pinned,
    None,
}

/// <summary>SSH host verification. V1 does not implement full OpenSSH known_hosts; selecting SSH
/// transport without an explicit policy is a fatal configuration error.</summary>
public sealed record SshTrust
{
    public required SshHostVerificationMode Mode { get; init; }

    public IReadOnlyList<string> Fingerprints { get; init; } = [];
}
