using Excise.Core.Primitives;

namespace Excise.Core.Filters;

internal readonly record struct PdfFilterDecodeContext(PdfDictionary? DecodeParms, PdfStream? Stream);
