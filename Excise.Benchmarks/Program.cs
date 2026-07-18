using BenchmarkDotNet.Running;
using Excise.Benchmarks;

// `dotnet run -c Release` runs all microbenchmarks. The maintained wrapper is:
//   scripts/run-benchmarks.sh benchmarkdotnet --filter '*Render*'
BenchmarkSwitcher.FromAssembly(typeof(ExciseBenchmarks).Assembly).Run(args);
