using System;
using System.IO;
using Excise.Core.Document;

namespace Excise.App.Tests.Fixtures;

/// <summary>
/// Class fixture that loads the Pragmatic book PDF once per test class
/// instead of once per test, eliminating redundant PDF I/O and parsing.
/// This fixture is shared across all tests in the class that use it via
/// IClassFixture&lt;PragmaticBookFixture&gt;.
/// </summary>
public class PragmaticBookFixture : IDisposable
{
    private const string PragmaticBookPath =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";

    public PdfDocument? Document { get; }
    public bool IsAvailable { get; }

    public PragmaticBookFixture()
    {
        if (File.Exists(PragmaticBookPath))
        {
            try
            {
                Document = PdfDocument.Open(PragmaticBookPath);
                IsAvailable = true;
            }
            catch
            {
                Document = null;
                IsAvailable = false;
            }
        }
        else
        {
            Document = null;
            IsAvailable = false;
        }
    }

    public void Dispose()
    {
        Document?.Dispose();
    }
}
