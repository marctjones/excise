using System;
using System.IO;
using Excise.Core.Document;

namespace Excise.App.Tests.Fixtures;

/// <summary>
/// Class fixture that loads the Pragmatic book PDF once per Avalonia test class
/// instead of once per test. Designed for use with [FixedAvaloniaFact] tests that
/// otherwise would load the 455-page PDF independently in each test method.
///
/// Unlike the standard PragmaticBookFixture, this one is used in test classes
/// marked with [Collection("AvaloniaTests")] where all tests run serially anyway.
/// </summary>
public class PragmaticBookHeadlessFixture : IDisposable
{
    private const string PragmaticBookPath =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";

    public PdfDocument? Document { get; }
    public bool IsAvailable { get; }

    public PragmaticBookHeadlessFixture()
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
