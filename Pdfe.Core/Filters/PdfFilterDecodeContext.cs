using Pdfe.Core.Primitives;

namespace Pdfe.Core.Filters;

internal readonly record struct PdfFilterDecodeContext(PdfDictionary? DecodeParms, PdfStream? Stream);
