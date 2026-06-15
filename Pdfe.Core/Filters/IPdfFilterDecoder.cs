namespace Pdfe.Core.Filters;

internal interface IPdfFilterDecoder
{
    bool CanDecode(string filterName);

    byte[] Decode(byte[] data, PdfFilterDecodeContext context);
}
