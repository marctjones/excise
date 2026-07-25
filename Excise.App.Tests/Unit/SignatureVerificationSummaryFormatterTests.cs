using AwesomeAssertions;
using Excise.App.Services;
using System;
using System.Collections.Generic;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// UI-text tests for the signature summary (#466). The key invariant: wording
/// that claims a trusted signer may only appear for ValidTrusted — no other
/// state is allowed to overclaim trust.
/// </summary>
public class SignatureVerificationSummaryFormatterTests
{
    private readonly SignatureVerificationSummaryFormatter _formatter = new();

    private static SignatureVerificationResult ValidResult() => new()
    {
        SignatureName = "Sig1",
        IsValid = true,
        SignedBy = "CN=Signer",
        ByteRangeStructureChecked = true,
        ByteRangeStructureValid = true,
        ByteRangeIntegrityChecked = true,
        ByteRangeIntegrityValid = true,
        CoversWholeDocument = true
    };

    [Fact]
    public void Format_ValidTrusted_ReportsTrustedSigner()
    {
        var result = ValidResult();
        result.TrustStatus = SignatureTrustStatus.Trusted;
        result.TrustDetails = "signer chains to trusted root: CN=Root";

        var text = _formatter.Format(new[] { result });

        result.State.Should().Be(SignatureVerificationState.ValidTrusted);
        text.Should().Contain("trusted signer");
        text.Should().Contain("Certificate trust chain: trusted");
        text.Should().Contain("CN=Root");
    }

    [Fact]
    public void Format_ValidUntrusted_ReportsUntrustedWithoutOverclaiming()
    {
        var result = ValidResult();
        result.TrustStatus = SignatureTrustStatus.Untrusted;
        result.TrustDetails = "self-signed certificate is not a configured trust anchor";

        var text = _formatter.Format(new[] { result });

        result.State.Should().Be(SignatureVerificationState.ValidUntrusted);
        text.Should().Contain("UNTRUSTED");
        text.Should().Contain("valid signature");
        text.Should().NotContain("trusted signer");
    }

    [Fact]
    public void Format_ValidTrustUnknown_SaysTrustNotDetermined()
    {
        var result = ValidResult();
        result.TrustStatus = SignatureTrustStatus.Indeterminate;
        result.TrustDetails = "trust evaluation failed: platform error";

        var text = _formatter.Format(new[] { result });

        result.State.Should().Be(SignatureVerificationState.ValidTrustUnknown);
        text.Should().Contain("signer trust could not be determined");
        text.Should().Contain("Certificate trust chain: could not be evaluated");
        text.Should().NotContain("trusted signer");
    }

    [Fact]
    public void Format_InvalidSignature_ReportsModificationClearly()
    {
        var result = new SignatureVerificationResult
        {
            SignatureName = "Sig1",
            IsValid = false,
            ByteRangeStructureChecked = true,
            ByteRangeStructureValid = true,
            ByteRangeIntegrityChecked = true,
            ByteRangeIntegrityValid = false,
            StatusMessage = "Signature verification failed or ByteRange digest mismatch"
        };

        var text = _formatter.Format(new[] { result });

        result.State.Should().Be(SignatureVerificationState.Invalid);
        text.Should().Contain("INVALID");
        text.Should().Contain("modified after signing");
        text.Should().Contain("Certificate trust chain: not evaluated");
        text.Should().NotContain("trusted signer");
    }

    [Fact]
    public void Format_IndeterminateSignature_SaysCouldNotBeVerified()
    {
        var result = new SignatureVerificationResult
        {
            SignatureName = "Sig1",
            IsValid = false,
            StatusMessage = "Verification failed: BouncyCastle verification failed"
        };

        var text = _formatter.Format(new[] { result });

        result.State.Should().Be(SignatureVerificationState.Indeterminate);
        text.Should().Contain("could not be verified");
        text.Should().NotContain("trusted signer");
    }

    [Fact]
    public void Format_TrustedSignerWording_OnlyAppearsForValidTrusted()
    {
        // Enumerate every reachable combination and assert the no-overclaim
        // invariant structurally rather than per-case.
        foreach (var isValid in new[] { true, false })
        {
            foreach (SignatureTrustStatus trust in Enum.GetValues<SignatureTrustStatus>())
            {
                var result = new SignatureVerificationResult
                {
                    SignatureName = "Sig",
                    IsValid = isValid,
                    ByteRangeStructureChecked = true,
                    ByteRangeStructureValid = true,
                    ByteRangeIntegrityChecked = true,
                    ByteRangeIntegrityValid = isValid,
                    TrustStatus = trust
                };

                var text = _formatter.Format(new[] { result });
                var claimsTrust = text.Contains("trusted signer", StringComparison.OrdinalIgnoreCase);

                claimsTrust.Should().Be(
                    result.State == SignatureVerificationState.ValidTrusted,
                    $"IsValid={isValid}, TrustStatus={trust} must not overclaim trust");
            }
        }
    }

    [Fact]
    public void Format_AlwaysDisclosesRevocationLimitation()
    {
        var text = _formatter.Format(new List<SignatureVerificationResult> { ValidResult() });
        text.Should().Contain("revocation (CRL/OCSP) is not checked");

        var emptyText = _formatter.Format(new List<SignatureVerificationResult>());
        emptyText.Should().Contain("revocation (CRL/OCSP) is not checked");
    }
}
