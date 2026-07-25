using Xunit;

// The automation-peer tests (#631) run through a shared
// HeadlessUnitTestSession dispatcher. Running other collections in parallel
// with session dispatch can deadlock the in-process runner (observed: full
// `dotnet run` hangs at "Starting"; `-parallel none` passes). The suite is
// sub-second, so serial execution costs nothing. Same precedent as
// Excise.App.Tests (#363).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
