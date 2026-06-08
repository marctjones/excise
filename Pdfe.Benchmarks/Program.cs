using BenchmarkDotNet.Running;
using Pdfe.Benchmarks;

// `dotnet run -c Release` runs all benchmarks; pass a filter to narrow, e.g.
//   scripts/run-benchmarks.sh --filter '*Render*'
BenchmarkSwitcher.FromAssembly(typeof(PdfeBenchmarks).Assembly).Run(args);
