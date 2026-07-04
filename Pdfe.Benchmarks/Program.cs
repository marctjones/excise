using BenchmarkDotNet.Running;
using Pdfe.Benchmarks;

// `dotnet run -c Release` runs all microbenchmarks. The maintained wrapper is:
//   scripts/run-benchmarks.sh benchmarkdotnet --filter '*Render*'
BenchmarkSwitcher.FromAssembly(typeof(PdfeBenchmarks).Assembly).Run(args);
