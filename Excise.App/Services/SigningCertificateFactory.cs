using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Excise.App.Services;

/// <summary>
/// Creates and loads the signing identities <see cref="SignatureApplicationService"/>
/// consumes — deliberately limited to what issue #623 allows: a locally
/// generated self-signed certificate, or a PKCS#12 (.p12/.pfx) file already on
/// disk. No CA enrollment, no paid service, no network access.
/// </summary>
public static class SigningCertificateFactory
{
    /// <summary>
    /// Generate a fresh self-signed RSA-2048 signing certificate entirely
    /// in-process. <paramref name="subjectName"/> may be a bare common name
    /// (<c>"Jane Doe"</c>) or a full distinguished name (<c>"CN=Jane Doe, O=…"</c>).
    /// Such a signature verifies as cryptographically valid but untrusted in
    /// third-party readers (unknown issuer) — the correct, expected result
    /// for a self-signed identity.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(string subjectName, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectName);

        var distinguishedName = subjectName.Contains('=', StringComparison.Ordinal)
            ? subjectName
            : $"CN={subjectName}";

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName(distinguishedName),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
            critical: true));

        // Backdate slightly so clock skew between signer and verifier can't
        // make a just-created certificate "not yet valid".
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore + (lifetime ?? TimeSpan.FromDays(3 * 365));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>
    /// Load a signing identity from a PKCS#12 (.p12/.pfx) file on disk. The
    /// private key is loaded exportable so the CMS signer can use it.
    /// </summary>
    public static X509Certificate2 LoadFromPkcs12(string path, string? password)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Exportable only — EphemeralKeySet is not supported on all platforms
        // (notably macOS keychain-backed stores).
        return X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.Exportable);
    }
}
