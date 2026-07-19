// Theory counterpart of [FixedAvaloniaFact] (see FixedAvaloniaFact.cs for the
// upstream Avalonia.Headless.XUnit bug this works around). Until now the suite
// had no way to run PARAMETERIZED tests on the headless Avalonia dispatcher —
// every UI test was a Fact, so batteries like "every interaction mode × every
// device-pixel-ratio" (ModeSwitchDisplayTests) had to either loop inside one
// Fact (losing per-case reporting) or not exist. This discoverer reuses
// FixedAvaloniaTestCase, which already carries testMethodArguments.
//
// Tracking: #337 — delete alongside FixedAvaloniaFact when upstream ships.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Excise.App.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer(typeof(FixedAvaloniaTheoryDiscoverer))]
public sealed class FixedAvaloniaTheoryAttribute : TheoryAttribute
{
    public FixedAvaloniaTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) { }
}

public class FixedAvaloniaTheoryDiscoverer : TheoryDiscoverer
{
    protected override ValueTask<IReadOnlyCollection<IXunitTestCase>> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        ITheoryAttribute theoryAttribute,
        ITheoryDataRow dataRow,
        object?[] testMethodArguments)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetailsForTheoryDataRow(
            discoveryOptions, testMethod, theoryAttribute, dataRow, testMethodArguments);

        // Same trait projection as FixedAvaloniaFactDiscoverer, plus any
        // row-level traits the data row contributes.
        var traits = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in testMethod.Traits)
            traits[key] = new HashSet<string>(values);
        if (dataRow.Traits != null)
            foreach (var (key, values) in dataRow.Traits)
            {
                if (!traits.TryGetValue(key, out var set))
                    traits[key] = set = new HashSet<string>();
                foreach (var v in values) set.Add(v);
            }

        IReadOnlyCollection<IXunitTestCase> cases = new IXunitTestCase[]
        {
            new FixedAvaloniaTestCase(
                details.ResolvedTestMethod,
                details.TestCaseDisplayName,
                details.UniqueID,
                details.Explicit,
                details.SkipExceptions,
                details.SkipReason,
                details.SkipType,
                details.SkipUnless,
                details.SkipWhen,
                traits,
                testMethodArguments,
                details.SourceFilePath,
                details.SourceLineNumber,
                details.Timeout),
        };
        return new(cases);
    }
}
