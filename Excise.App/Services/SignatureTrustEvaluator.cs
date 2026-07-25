using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Excise.App.Services;

/// <summary>
/// Whether the signer certificate chains to a trusted root. Kept separate from
/// cryptographic signature validity: a signature can be cryptographically valid
/// while the signer is untrusted, and the two must never collapse into one
/// state. See issue #466.
/// </summary>
public enum SignatureTrustStatus
{
    /// <summary>Trust was not evaluated (e.g. the signature itself was not cryptographically valid).</summary>
    NotEvaluated = 0,

    /// <summary>The signer certificate chains to a configured trust anchor (OS trust store by default).</summary>
    Trusted,

    /// <summary>Chain building completed but the signer does not chain to any trusted root, or the chain has errors.</summary>
    Untrusted,

    /// <summary>Trust evaluation was attempted but could not produce an answer (malformed certificate, platform error).</summary>
    Indeterminate
}

/// <summary>Outcome of a trust-chain evaluation for one signer certificate.</summary>
public sealed class SignatureTrustResult
{
    public SignatureTrustStatus Status { get; init; }
    public string Details { get; init; } = string.Empty;
}

/// <summary>
/// Builds and evaluates the signer certificate chain for a PDF signature.
/// By default the chain is validated against the OS/.NET system trust store;
/// tests (and callers wanting a pinned trust policy) can inject explicit trust
/// anchors so results are deterministic and machine-independent.
///
/// Revocation (CRL/OCSP) is deliberately NOT checked: it requires network
/// access and would make verification non-deterministic. The formatter states
/// this limitation to the user. See issue #466.
/// </summary>
public class SignatureTrustEvaluator
{
    private readonly IReadOnlyList<X509Certificate2>? _customTrustAnchors;

    /// <summary>Creates an evaluator that trusts the OS/.NET system root store.</summary>
    public SignatureTrustEvaluator()
    {
    }

    /// <summary>
    /// Creates an evaluator that trusts ONLY the supplied anchors
    /// (X509ChainTrustMode.CustomRootTrust). An empty list means nothing is
    /// trusted — useful for deterministic tests.
    /// </summary>
    public SignatureTrustEvaluator(IReadOnlyList<X509Certificate2> customTrustAnchors)
    {
        ArgumentNullException.ThrowIfNull(customTrustAnchors);
        _customTrustAnchors = customTrustAnchors;
    }

    /// <summary>
    /// Evaluates whether the DER-encoded signer certificate chains to a trusted
    /// root. <paramref name="candidateChainCertificates"/> carries the other
    /// DER-encoded certificates embedded in the CMS bundle so intermediates can
    /// be resolved without touching the network.
    /// </summary>
    public virtual SignatureTrustResult Evaluate(
        byte[] signerCertificate,
        IReadOnlyList<byte[]> candidateChainCertificates)
    {
        ArgumentNullException.ThrowIfNull(signerCertificate);
        ArgumentNullException.ThrowIfNull(candidateChainCertificates);

        var extraCertificates = new List<X509Certificate2>();
        try
        {
            using var signer = X509CertificateLoader.LoadCertificate(signerCertificate);
            using var chain = new X509Chain();

            // Offline and deterministic by design; the limitation is reported
            // to the user by SignatureVerificationSummaryFormatter (#466).
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            foreach (var der in candidateChainCertificates)
            {
                var candidate = X509CertificateLoader.LoadCertificate(der);
                extraCertificates.Add(candidate);
                chain.ChainPolicy.ExtraStore.Add(candidate);
            }

            if (_customTrustAnchors is not null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                foreach (var anchor in _customTrustAnchors)
                {
                    chain.ChainPolicy.CustomTrustStore.Add(anchor);
                }
            }

            if (chain.Build(signer))
            {
                var root = chain.ChainElements.Count > 0
                    ? chain.ChainElements[^1].Certificate.Subject
                    : signer.Subject;
                return new SignatureTrustResult
                {
                    Status = SignatureTrustStatus.Trusted,
                    Details = $"signer chains to trusted root: {root}"
                };
            }

            var statusText = string.Join("; ", chain.ChainStatus
                .Select(s => string.IsNullOrWhiteSpace(s.StatusInformation)
                    ? s.Status.ToString()
                    : s.StatusInformation.Trim())
                .Distinct());
            return new SignatureTrustResult
            {
                Status = SignatureTrustStatus.Untrusted,
                Details = string.IsNullOrWhiteSpace(statusText)
                    ? "signer does not chain to a trusted root"
                    : statusText
            };
        }
        catch (Exception ex)
        {
            return new SignatureTrustResult
            {
                Status = SignatureTrustStatus.Indeterminate,
                Details = $"trust evaluation failed: {ex.Message}"
            };
        }
        finally
        {
            foreach (var candidate in extraCertificates)
            {
                candidate.Dispose();
            }
        }
    }
}
