using Xunit;

namespace Excise.App.Tests.Integration;

/// <summary>
/// Collection definition for tests that use PDF rendering.
/// Tests in this collection run sequentially to avoid PDFium resource contention.
/// </summary>
[CollectionDefinition("PdfRenderingTests", DisableParallelization = true)]
public class PdfRenderingTestCollection : ICollectionFixture<PdfRenderingTestFixture>
{
}

/// <summary>
/// Shared fixture for PDF rendering tests.
/// Ensures proper initialization and cleanup of rendering resources.
/// </summary>
public class PdfRenderingTestFixture : IDisposable
{
    private static readonly object _lock = new object();
    private static bool _initialized = false;

    public PdfRenderingTestFixture()
    {
        lock (_lock)
        {
            if (!_initialized)
            {
                // Allow time for any previous PDFium operations to complete
                Thread.Sleep(100);
                _initialized = true;
            }
        }
    }

    public void Dispose()
    {
        // Allow time for PDFium cleanup between test classes
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
