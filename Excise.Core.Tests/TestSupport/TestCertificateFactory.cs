using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using System;
using System.Collections.Concurrent;
using Excise.Core.Signatures;

namespace Excise.Core.Tests.Signatures;

/// <summary>
/// A signing identity for signature tests: the leaf certificate + key that
/// signs, the extra certificates to embed in the CMS bundle, and (for chained
/// identities) the root that a test can install as an explicit trust anchor.
/// All material is generated in-process so trust assertions are deterministic
/// and never depend on the machine trust store or the network (#466).
/// </summary>
internal sealed record TestSigningIdentity(
    AsymmetricCipherKeyPair KeyPair,
    X509Certificate Certificate,
    X509Certificate[] ChainCertificates,
    X509Certificate? TrustAnchor);

internal static class TestCertificateFactory
{
    /// <param name="notBefore">
    /// Overrides the default validity start (one day ago). Used to build a certificate that
    /// was not yet valid at the CMS signingTime attribute, which BouncyCastle rejects before
    /// it computes any digest (#1494).
    /// </param>
    public static TestSigningIdentity CreateSelfSigned(
        string subject = "CN=PDFe Test Signer", DateTime? notBefore = null)
    {
        // RSA-2048 generation dominates the signature classes' runtime, and the
        // identities are immutable, so equal requests share one. A caller that
        // pins notBefore wants a specific validity window and gets its own.
        if (notBefore is not null)
            return GenerateSelfSigned(subject, notBefore);

        return SelfSigned.GetOrAdd(
            subject, s => new Lazy<TestSigningIdentity>(() => GenerateSelfSigned(s, null))).Value;
    }

    private static readonly ConcurrentDictionary<string, Lazy<TestSigningIdentity>> SelfSigned = new();
    private static readonly ConcurrentDictionary<(string Root, string Leaf), Lazy<TestSigningIdentity>> Chained = new();

    private static TestSigningIdentity GenerateSelfSigned(string subject, DateTime? notBefore)
    {
        var random = new SecureRandom();
        var keyPair = GenerateKeyPair(random);

        var generator = CreateGenerator(
            new X509Name(subject), new X509Name(subject), keyPair.Public, random, notBefore);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        generator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        var certificate = generator.Generate(
            new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random));

        return new TestSigningIdentity(keyPair, certificate, Array.Empty<X509Certificate>(), TrustAnchor: null);
    }

    /// <summary>
    /// Root CA + leaf signer. The leaf signs; the root travels in
    /// <see cref="TestSigningIdentity.ChainCertificates"/> and is exposed as
    /// <see cref="TestSigningIdentity.TrustAnchor"/> for a custom trust store.
    /// </summary>
    public static TestSigningIdentity CreateChainedToRoot(
        string rootSubject = "CN=PDFe Test Root CA",
        string leafSubject = "CN=PDFe Test Chained Signer") =>
        Chained.GetOrAdd(
            (rootSubject, leafSubject),
            key => new Lazy<TestSigningIdentity>(() => GenerateChainedToRoot(key.Root, key.Leaf))).Value;

    private static TestSigningIdentity GenerateChainedToRoot(string rootSubject, string leafSubject)
    {
        var random = new SecureRandom();

        var rootKeyPair = GenerateKeyPair(random);
        var rootName = new X509Name(rootSubject);
        var rootGenerator = CreateGenerator(rootName, rootName, rootKeyPair.Public, random);
        rootGenerator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        rootGenerator.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
        var rootCertificate = rootGenerator.Generate(
            new Asn1SignatureFactory("SHA256WITHRSA", rootKeyPair.Private, random));

        var leafKeyPair = GenerateKeyPair(random);
        var leafGenerator = CreateGenerator(rootName, new X509Name(leafSubject), leafKeyPair.Public, random);
        leafGenerator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        leafGenerator.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        var leafCertificate = leafGenerator.Generate(
            new Asn1SignatureFactory("SHA256WITHRSA", rootKeyPair.Private, random));

        return new TestSigningIdentity(
            leafKeyPair,
            leafCertificate,
            new[] { rootCertificate },
            TrustAnchor: rootCertificate);
    }

    private static X509V3CertificateGenerator CreateGenerator(
        X509Name issuer,
        X509Name subject,
        AsymmetricKeyParameter publicKey,
        SecureRandom random,
        DateTime? notBefore = null)
    {
        var start = notBefore ?? DateTime.UtcNow.AddDays(-1);
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(128, random));
        generator.SetIssuerDN(issuer);
        generator.SetSubjectDN(subject);
        generator.SetNotBefore(start);
        generator.SetNotAfter(start.AddDays(2));
        generator.SetPublicKey(publicKey);
        return generator;
    }

    private static AsymmetricCipherKeyPair GenerateKeyPair(SecureRandom random)
    {
        var keyGenerator = new RsaKeyPairGenerator();
        keyGenerator.Init(new KeyGenerationParameters(random, 2048));
        return keyGenerator.GenerateKeyPair();
    }
}
