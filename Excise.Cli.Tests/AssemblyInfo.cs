using Xunit;

// The CLI command tests redirect the process-wide Console.Out to capture
// program output (Console.SetOut). Under xUnit's default parallel execution
// that global redirection races across collections — one test's output bleeds
// into another's captured stream (e.g. a form-flatten test's "Set 1 field
// value(s) (flattened)" appearing in the redact test's capture), causing
// flaky failures in CI. These tests are few and fast, so run them serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
