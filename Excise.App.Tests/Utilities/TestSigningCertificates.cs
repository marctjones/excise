using System;
using System.Collections.Concurrent;
using Excise.Core.Signatures;

namespace Excise.App.Tests.Utilities;

/// <summary>
/// <see cref="SigningCertificateFactory.CreateSelfSigned"/> identities for the
/// signing tests, generated once per subject. Each call returns a FRESH
/// <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/> loaded from the cached PKCS#12 bytes, so a
/// test can still dispose what it was handed.
/// </summary>
internal static class TestSigningCertificates
{
    private const string Pkcs12Password = "excise-test-only";

    private static readonly ConcurrentDictionary<string, Lazy<byte[]>> Pkcs12ByName = new();

    public static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateSelfSigned(string subjectName)
    {
        var pkcs12 = Pkcs12ByName.GetOrAdd(subjectName, name => new Lazy<byte[]>(() =>
        {
            using var generated = SigningCertificateFactory.CreateSelfSigned(name);
            return generated.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, Pkcs12Password);
        })).Value;

        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            pkcs12, Pkcs12Password, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
    }
}
