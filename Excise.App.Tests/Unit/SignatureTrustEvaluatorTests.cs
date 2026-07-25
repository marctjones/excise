using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Deterministic trust-chain tests for issue #466. Every trust anchor is
/// generated in-process and injected via the custom trust store — no network,
/// no dependence on the machine's OS trust store.
/// </summary>
public class SignatureTrustEvaluatorTests
{
    private static X509Certificate2 ToX509Certificate2(Org.BouncyCastle.X509.X509Certificate certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.GetEncoded());

    [Fact]
    public void Evaluate_SelfSignedCertificate_EmptyCustomTrustStore_IsUntrusted()
    {
        var identity = TestCertificateFactory.CreateSelfSigned();
        var evaluator = new SignatureTrustEvaluator(Array.Empty<X509Certificate2>());

        var result = evaluator.Evaluate(identity.Certificate.GetEncoded(), Array.Empty<byte[]>());

        result.Status.Should().Be(SignatureTrustStatus.Untrusted);
        result.Details.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Evaluate_LeafChainedToConfiguredRoot_IsTrusted()
    {
        var identity = TestCertificateFactory.CreateChainedToRoot();
        var evaluator = new SignatureTrustEvaluator(
            new List<X509Certificate2> { ToX509Certificate2(identity.TrustAnchor!) });

        var result = evaluator.Evaluate(
            identity.Certificate.GetEncoded(),
            identity.ChainCertificates.Select(c => c.GetEncoded()).ToList());

        result.Status.Should().Be(SignatureTrustStatus.Trusted);
        result.Details.Should().Contain("PDFe Test Root CA");
    }

    [Fact]
    public void Evaluate_LeafWithUnrelatedRootInTrustStore_IsUntrusted()
    {
        var identity = TestCertificateFactory.CreateChainedToRoot();
        var unrelated = TestCertificateFactory.CreateChainedToRoot("CN=Some Other Root");
        var evaluator = new SignatureTrustEvaluator(
            new List<X509Certificate2> { ToX509Certificate2(unrelated.TrustAnchor!) });

        var result = evaluator.Evaluate(
            identity.Certificate.GetEncoded(),
            identity.ChainCertificates.Select(c => c.GetEncoded()).ToList());

        result.Status.Should().Be(SignatureTrustStatus.Untrusted);
    }

    [Fact]
    public void Evaluate_SelfSignedAnchoredToItself_IsTrusted()
    {
        // A user can explicitly pin a self-signed certificate as its own anchor.
        var identity = TestCertificateFactory.CreateSelfSigned();
        var evaluator = new SignatureTrustEvaluator(
            new List<X509Certificate2> { ToX509Certificate2(identity.Certificate) });

        var result = evaluator.Evaluate(identity.Certificate.GetEncoded(), Array.Empty<byte[]>());

        result.Status.Should().Be(SignatureTrustStatus.Trusted);
    }

    [Fact]
    public void Evaluate_GarbageCertificateBytes_IsIndeterminate()
    {
        var evaluator = new SignatureTrustEvaluator(Array.Empty<X509Certificate2>());

        var result = evaluator.Evaluate(new byte[] { 0x01, 0x02, 0x03 }, Array.Empty<byte[]>());

        result.Status.Should().Be(SignatureTrustStatus.Indeterminate);
        result.Details.Should().Contain("trust evaluation failed");
    }

    [Fact]
    public void Evaluate_NullArguments_Throw()
    {
        var evaluator = new SignatureTrustEvaluator();

        var nullSigner = () => evaluator.Evaluate(null!, Array.Empty<byte[]>());
        var nullCandidates = () => evaluator.Evaluate(new byte[] { 0x30 }, null!);

        nullSigner.Should().Throw<ArgumentNullException>();
        nullCandidates.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullCustomAnchors_Throws()
    {
        var action = () => new SignatureTrustEvaluator(null!);
        action.Should().Throw<ArgumentNullException>();
    }
}
