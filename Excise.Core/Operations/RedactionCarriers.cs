using System;

namespace Excise.Core.Operations;

/// <summary>
/// The document-level text carriers a term scrub may touch (#1188). A PDF
/// restates the same string in many places outside page content; each of these
/// is one of them. Used to give the caller per-carrier control over the
/// <see cref="PdfDocumentSanitizer.ScrubTerms(Excise.Core.Document.PdfDocument, System.Collections.Generic.IEnumerable{string}, bool, RedactionCarriers)"/>
/// pass — the default (<see cref="All"/>) reproduces the pre-#1188 behaviour.
/// </summary>
/// <remarks>
/// ⚠️ Disabling a carrier can LEAVE the redacted term in the document. Scoping
/// exists for the inverse case #1169 raises — a carrier where stripping the term
/// REVEALS it (a URL, a known filename) — but the safe default is to scrub
/// everything. Turn a carrier off deliberately, not casually.
/// </remarks>
[Flags]
public enum RedactionCarriers
{
    /// <summary>Scrub no document-level carriers.</summary>
    None = 0,

    /// <summary>The <c>/Info</c> dictionary (/Title, /Author, /Subject, …).</summary>
    Info = 1 << 0,

    /// <summary>The XMP <c>/Metadata</c> packet.</summary>
    Xmp = 1 << 1,

    /// <summary>The XFA form XML.</summary>
    Xfa = 1 << 2,

    /// <summary>Outline (bookmark) titles.</summary>
    Outlines = 1 << 3,

    /// <summary>Annotation <c>/Contents</c> and rich-text <c>/RC</c>.</summary>
    Annotations = 1 << 4,

    /// <summary>AcroForm field names.</summary>
    FormFields = 1 << 5,

    /// <summary>Structure-tree <c>/ActualText</c> / <c>/Alt</c> / <c>/E</c>.</summary>
    StructTree = 1 << 6,

    /// <summary>Document/annotation JavaScript.</summary>
    JavaScript = 1 << 7,

    /// <summary>Embedded-file names and descriptions.</summary>
    EmbeddedFiles = 1 << 8,

    /// <summary>Link/action <c>/URI</c> targets.</summary>
    ActionUris = 1 << 9,

    /// <summary>Every carrier — the default and the safe choice.</summary>
    All = Info | Xmp | Xfa | Outlines | Annotations | FormFields
        | StructTree | JavaScript | EmbeddedFiles | ActionUris,
}
