using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

// Direct references and observations are evidence, never release proof. See #1943.
if (args.Length == 1 && args[0] == "self-test") { SelfTest(); return; }
if (args.Length < 4 || args[0] != "capture")
    throw new ArgumentException("capture ROOT INVENTORY OUTPUT [--changed DELTA] [--execution MANIFEST] [--coverage MANIFEST] | self-test");
var root = Path.GetFullPath(args[1]);
var inventoryPath = Path.GetFullPath(args[2]);
var inventory = JsonSerializer.Deserialize<Inventory>(File.ReadAllText(inventoryPath))!;
var inventoryHash = FileHash(inventoryPath);
ValidateInventory(root, inventory);
string? Option(string name) => Array.IndexOf(args, name) is var i && i >= 0 ? args[i + 1] : null;
var selected = Option("--changed") is { } deltaPath ? ChangedKeys(deltaPath) : null;
var oraclePath = Path.Combine(root, "tests/code-health/oracles.json");
var oracles = JsonSerializer.Deserialize<OraclePolicy>(File.ReadAllText(oraclePath))!;
if (oracles.SchemaVersion != 1) throw new InvalidOperationException("Unsupported oracle policy schema.");
var buildPaths = new[] { "excise.sln", "global.json", "Directory.Build.props", "Directory.Build.targets", "architecture/repository-scope.json", "tools/Excise.TestEvidence/Excise.TestEvidence.csproj" };
var buildInputs = buildPaths.Where(p => File.Exists(Path.Combine(root, p))).Select(p => new Artifact(p, FileHash(Path.Combine(root, p)))).ToArray();
var inputs = inventory.Inputs.Where(x => x.Classification is "shipping" or "test")
    .Where(x => !inventory.Exclusions.Any(e => e.Path == x.Path)).ToArray();
var inputIndex = inputs.ToDictionary(x => x.Path, StringComparer.Ordinal);
var declarationIndex = inventory.Symbols.GroupBy(s => (s.Path, s.Line, s.Kind)).ToDictionary(g => g.Key, g => g.ToArray());
foreach (var input in inputs)
    if (Hash(Normalize(File.ReadAllText(Path.Combine(root, input.Path)))) != input.SourceHash)
        throw new InvalidOperationException($"Stale inventory: {input.Path}. Regenerate #1939 inventory before attribution.");
var executions = LoadExecution(Option("--execution"), inventoryHash);
var coverage = LoadCoverage(Option("--coverage"), inventoryHash, root);
MSBuildLocator.RegisterDefaults();
using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["NuGetAudit"] = "false" });
var failures = new List<string>();
workspace.RegisterWorkspaceFailedHandler(e => { if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) failures.Add(e.Diagnostic.Message); });
var solution = await workspace.OpenSolutionAsync(Path.Combine(root, "excise.sln"));
if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));
var semanticDeclarations = new Dictionary<string, List<Production>>(StringComparer.Ordinal);
var tests = new List<Test>();
var projectObservations = new List<object>();
var referenceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
var omitted = new SortedSet<string>(StringComparer.Ordinal);
foreach (var project in solution.Projects.OrderBy(p => p.FilePath, StringComparer.Ordinal))
{
    var projectPath = Relative(root, project.FilePath!);
    if (!inputs.Any(x => x.Project == projectPath)) continue;
    var compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException($"No compilation for {projectPath}");
    var parse = (CSharpParseOptions?)project.ParseOptions;
    projectObservations.Add(new { Project = projectPath, ProjectSha256 = FileHash(project.FilePath!),
        CompilationErrorCount = compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error),
        LanguageVersion = parse?.LanguageVersion.ToString(), PreprocessorSymbols = parse?.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(), AssemblyName = project.AssemblyName,
        References = project.MetadataReferences.Select(r =>
        {
            var path = r.Display ?? throw new InvalidOperationException("Metadata reference has no path");
            if (!referenceHashes.TryGetValue(path, out var hash)) referenceHashes[path] = hash = FileHash(path);
            return new { Assembly = Path.GetFileName(path), Sha256 = hash };
        }).OrderBy(r => r.Assembly, StringComparer.Ordinal).ThenBy(r => r.Sha256, StringComparer.Ordinal).ToArray() });
    foreach (var document in project.Documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
    {
        if (document.FilePath is null) continue;
        var path = Relative(root, document.FilePath);
        var input = inputIndex.GetValueOrDefault(path);
        if (input is null) { omitted.Add(path); continue; }
        var tree = await document.GetSyntaxTreeAsync();
        if (tree is null) continue;
        if (Hash(Normalize(tree.GetText().ToString())) != input.SourceHash)
            throw new InvalidOperationException($"Workspace source differs from inventory: {path}");
        var model = compilation.GetSemanticModel(tree);
        foreach (var node in tree.GetRoot().DescendantNodes().Where(IsDeclaration))
        {
            var symbol = model.GetDeclaredSymbol(node);
            if (symbol is null) continue;
            var span = node.GetLocation().GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            if (input.Classification == "shipping")
            {
                var id = Canonical(symbol);
                var rows = declarationIndex.GetValueOrDefault((path, line, symbol.Kind.ToString())) ?? [];
                if (rows.Length > 1) rows = rows.Where(s => s.Key.EndsWith("::" + id, StringComparison.Ordinal)).ToArray();
                if (id is null || rows.Length == 0) continue;
                if (!semanticDeclarations.TryGetValue(id, out var declarations)) semanticDeclarations[id] = declarations = [];
                declarations.AddRange(rows.Select(s => new Production(s.Key, path, line, span.EndLinePosition.Line + 1)));
            }
            else if (node is MethodDeclarationSyntax method && symbol is IMethodSymbol testSymbol && IsTest(testSymbol))
                tests.Add(InspectTest(method, testSymbol, model, projectPath, path, oracles.Types));
        }
    }
}
var links = tests.SelectMany(t => t.References.Select(r => (Test: t, Target: r)))
    .Where(x => semanticDeclarations.ContainsKey(x.Target)).GroupBy(x => x.Target).ToDictionary(g => g.Key, g => g.Select(x => x.Test).DistinctBy(t => t.Key).OrderBy(t => t.Key, StringComparer.Ordinal).ToArray());
var symbols = semanticDeclarations.OrderBy(x => x.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Select(p =>
{
    var attributed = links.GetValueOrDefault(pair.Key) ?? [];
    return new SymbolEvidence(p.Key, pair.Key, p.Path, p.StartLine, p.EndLine,
        attributed.Length == 0 ? "no-direct-attributed-tests" : "direct-attributed-tests",
        attributed.Count(t => executions.Results.Any(e => e.Project == t.Project && e.FullyQualifiedName == t.FullyQualifiedName && e.Outcome is "Passed" or "Failed")),
        attributed.Count(t => t.OracleCalls.Length != 0),
        attributed.Select(t => new Attribution(t.Key, t.FullyQualifiedName, t.Project, t.Path, t.Line,
            t.OracleCalls, t.UnresolvedInvocations, executions.Results.Where(e => e.Project == t.Project && e.FullyQualifiedName == t.FullyQualifiedName).ToArray())).ToArray(),
        coverage.Lines.Where(l => l.Path == p.Path && l.Line >= p.StartLine && l.Line <= p.EndLine).OrderBy(l => l.Line).ToArray());
})).Where(s => selected is null || selected.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal).ThenBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine).ToArray();
var present = symbols.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
var unmatched = inventory.Symbols.Where(s => s.Classification == "shipping" && (selected is null || selected.Contains(s.Key)) && !present.Contains(s.Key))
    .Select(s => s.Key).Distinct().Order(StringComparer.Ordinal).ToArray();
var sdk = Run("dotnet", root, "--version").Trim();
foreach (var input in inputs)
    if (Hash(Normalize(File.ReadAllText(Path.Combine(root, input.Path)))) != input.SourceHash)
        throw new InvalidOperationException($"Source changed during capture: {input.Path}");
foreach (var input in buildInputs)
    if (FileHash(Path.Combine(root, input.Label)) != input.Sha256)
        throw new InvalidOperationException($"Build input changed during capture: {input.Label}");
ValidateInventory(root, inventory);
var report = new { SchemaVersion = 1, ToolVersion = "Excise.TestEvidence/1", ToolSourceHash = FileHash(Path.Combine(root, "tools/Excise.TestEvidence/Program.cs")),
    RoslynVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(), DotnetSdkVersion = sdk,
    InventorySha256 = inventoryHash, OraclePolicySha256 = FileHash(oraclePath),
    BuildInputs = buildInputs,
    InputContract = "#1939 inventory hashes must match current shipping/test source. MSBuild evaluates excise.sln; only inventory source participates. References are resolved symbols; candidate/unresolved calls are never guessed by text.",
    EvidenceContract = "Direct attributed xUnit Fact/Theory references only, including constructor/property/type uses. Helpers, dynamic calls, reflection, inheritance, and transitive call paths are not attributed. no-direct-attributed-tests does not prove no tests. Oracle calls are static candidates, not proof their result was asserted or executed. TRX results prove an attributed test ran, not that a production symbol/oracle executed. Coverage lines are file-level observations, never a strength score or sole release proof. No coverage thresholds are altered.",
    Reproduce = "dotnet run --project tools/Excise.TestEvidence -- capture . INVENTORY.json OUTPUT.json [--changed DELTA.json] [--execution MANIFEST.json] [--coverage MANIFEST.json]",
    Inputs = inputs, WorkspaceOmittedInputs = omitted.ToArray(), Projects = projectObservations, UnmatchedInventorySymbols = unmatched,
    UnmatchedSelectedKeys = selected?.Except(present).Order(StringComparer.Ordinal).ToArray() ?? [],
    UnresolvedTestInvocationCount = tests.Sum(t => t.UnresolvedInvocations),
    Execution = new { Status = executions.Status, executions.Artifacts }, Coverage = new { Status = coverage.Status, coverage.Artifacts }, Symbols = symbols };
File.WriteAllText(args[3], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
Console.WriteLine($"Captured {symbols.Length} production declarations; {symbols.Count(s => s.Tests.Length != 0)} directly attributed; {unmatched.Length} inventory symbols unbound (reported unknown).");

static bool IsDeclaration(SyntaxNode n) => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or VariableDeclaratorSyntax or EnumMemberDeclarationSyntax;
static bool IsTest(IMethodSymbol symbol) => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() is "Xunit.FactAttribute" or "Xunit.TheoryAttribute");
static string? Canonical(ISymbol symbol) => symbol switch
{
    IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition.GetDocumentationCommentId(),
    _ => symbol.OriginalDefinition.GetDocumentationCommentId()
};
static Test InspectTest(MethodDeclarationSyntax method, IMethodSymbol symbol, SemanticModel model, string project, string path, OracleType[] oracles)
{
    var refs = new SortedSet<string>(StringComparer.Ordinal);
    var oracleCalls = new SortedSet<string>(StringComparer.Ordinal);
    var unresolved = 0;
    foreach (var node in method.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax))
    {
        var info = model.GetSymbolInfo(node);
        if (info.Symbol is not { } target) { if (node is InvocationExpressionSyntax) unresolved++; continue; }
        if (Canonical(target) is { } id) refs.Add(id);
        if (node is InvocationExpressionSyntax && target is IMethodSymbol called)
            foreach (var oracle in oracles.Where(o => o.Type == called.ContainingType.ToDisplayString()))
                oracleCalls.Add(oracle.Kind + ":" + Canonical(called));
    }
    var fullname = symbol.ContainingType.ToDisplayString() + "." + symbol.Name;
    return new(project + "::" + symbol.GetDocumentationCommentId(), fullname, project, path,
        method.GetLocation().GetLineSpan().StartLinePosition.Line + 1, refs.ToArray(), oracleCalls.ToArray(), unresolved);
}
static HashSet<string> ChangedKeys(string path)
{
    using var json = JsonDocument.Parse(File.ReadAllText(path));
    return new[] { "Added", "Removed", "Changed" }.SelectMany(p => json.RootElement.GetProperty(p).EnumerateArray()).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
}
static ExecutionData LoadExecution(string? path, string inventoryHash)
{
    if (path is null) return new("not-supplied", [], []);
    var manifest = ReadManifest(path, inventoryHash);
    var results = new List<ExecutionResult>(); var artifacts = new List<Artifact> { new("manifest", FileHash(path)) };
    foreach (var run in manifest.Runs)
    {
        var trxPath = Path.GetFullPath(run.Artifact, Path.GetDirectoryName(Path.GetFullPath(path))!);
        artifacts.Add(new(run.Project + ":trx", FileHash(trxPath)));
        results.AddRange(ParseTrx(XDocument.Load(trxPath), run.Project));
    }
    if (results.Count == 0) throw new InvalidOperationException("Execution manifest supplied no test results; zero-test evidence is invalid.");
    return new("supplied-inventory-attestation", artifacts.OrderBy(a => a.Label, StringComparer.Ordinal).ToArray(), results.OrderBy(r => r.Project, StringComparer.Ordinal).ThenBy(r => r.FullyQualifiedName, StringComparer.Ordinal).ThenBy(r => r.Outcome, StringComparer.Ordinal).ToArray());
}
static ExecutionResult[] ParseTrx(XDocument doc, string project)
{
    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    var definitions = doc.Descendants(ns + "UnitTest").ToDictionary(x => (string)x.Attribute("id")!, x => x.Element(ns + "TestMethod")!);
    return doc.Descendants(ns + "UnitTestResult").Select(result =>
    {
        var method = definitions[(string)result.Attribute("testId")!];
        var className = ((string)method.Attribute("className")!).Split(',')[0];
        var methodName = (string)method.Attribute("name")!;
        return new ExecutionResult(project, className + "." + methodName.Split('(')[0], (string)result.Attribute("outcome")!);
    }).ToArray();
}
static CoverageData LoadCoverage(string? path, string inventoryHash, string root)
{
    if (path is null) return new("not-supplied", [], []);
    var manifest = ReadManifest(path, inventoryHash); var artifacts = new List<Artifact> { new("manifest", FileHash(path)) }; var lines = new List<CoverageLine>();
    foreach (var run in manifest.Runs)
    {
        var full = Path.GetFullPath(run.Artifact, Path.GetDirectoryName(Path.GetFullPath(path))!);
        artifacts.Add(new(run.Project + ":cobertura", FileHash(full)));
        var doc = XDocument.Load(full);
        foreach (var cls in doc.Descendants("class"))
        {
            var filename = (string)cls.Attribute("filename")!;
            var relative = Path.IsPathRooted(filename) ? Relative(root, filename) : filename.Replace('\\', '/');
            foreach (var line in cls.Element("lines")?.Elements("line") ?? [])
                lines.Add(new(relative, (int)line.Attribute("number")!, (long)line.Attribute("hits")!));
        }
    }
    return new("supplied-inventory-attestation", artifacts.OrderBy(a => a.Label, StringComparer.Ordinal).ToArray(),
        lines.GroupBy(l => (l.Path, l.Line)).Select(g => new CoverageLine(g.Key.Path, g.Key.Line, g.Sum(l => l.Hits))).OrderBy(l => l.Path, StringComparer.Ordinal).ThenBy(l => l.Line).ToArray());
}
static EvidenceManifest ReadManifest(string path, string hash)
{
    var manifest = JsonSerializer.Deserialize<EvidenceManifest>(File.ReadAllText(path))!;
    if (manifest.SchemaVersion != 1 || manifest.InventorySha256 != hash || manifest.Runs.Length == 0)
        throw new InvalidOperationException("Execution/coverage manifest must attest this exact inventory SHA-256 and contain at least one run.");
    return manifest;
}
static void ValidateInventory(string root, Inventory inventory)
{
    if (inventory.SchemaVersion != 1 || inventory.Inputs.Length == 0)
        throw new InvalidOperationException("Inventory requires schema 1 and nonempty source inputs.");
    var tracked = Run("git", root, "ls-files", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
    if (inventory.Inputs.Select(i => i.Path).Distinct(StringComparer.Ordinal).Count() != inventory.Inputs.Length
        || !tracked.SetEquals(inventory.Inputs.Select(i => i.Path)))
        throw new InvalidOperationException("Stale inventory: tracked C# input set changed.");
    foreach (var input in inventory.Inputs)
        if (Hash(Normalize(File.ReadAllText(Path.Combine(root, input.Path)))) != input.SourceHash)
            throw new InvalidOperationException($"Stale inventory source: {input.Path}");
    if (Hash(Normalize(File.ReadAllText(Path.Combine(root, "architecture/repository-scope.json")))) != inventory.ScopeHash)
        throw new InvalidOperationException("Stale inventory scope hash.");
}
static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
static string Run(string command, string root, params string[] arguments)
{
    var info = new ProcessStartInfo(command) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var result = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
    return result;
}
static void SelfTest()
{
    const string source = """
        namespace Xunit { class FactAttribute : System.Attribute {} }
        namespace Engine { class Subject { public static int Compute(int x) => x; public static int Compute(string x) => x.Length; } }
        namespace Independent { class Oracle { public static int Read() => 1; } }
        namespace Tests { class Sample {
          [Xunit.Fact] public void Bound() { Engine.Subject.Compute(1); Independent.Oracle.Read(); }
          public void Helper() { Engine.Subject.Compute("text"); }
          [Xunit.Fact] public void Unresolved() { Missing.Compute(); }
        } }
        """;
    var tree = CSharpSyntaxTree.ParseText(source, path: "Fixture.cs");
    var compilation = CSharpCompilation.Create("Fixture", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    var model = compilation.GetSemanticModel(tree);
    var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => model.GetDeclaredSymbol(m) is { } s && IsTest(s)).ToArray();
    var tests = methods.Select(m => InspectTest(m, model.GetDeclaredSymbol(m)!, model, "Tests.csproj", "Fixture.cs", [new("Independent.Oracle", "external-tool")])).ToArray();
    if (tests.Length != 2 || !tests[0].References.Contains("M:Engine.Subject.Compute(System.Int32)") || tests[0].References.Contains("M:Engine.Subject.Compute(System.String)") || tests[0].OracleCalls.Length != 1 || tests[1].UnresolvedInvocations != 1 || tests[1].References.Any(r => r.Contains("Missing", StringComparison.Ordinal)))
        throw new Exception("Direct attribution, overload identity, test/helper distinction, oracle, or unresolved-call handling failed.");
    if (JsonSerializer.Serialize(tests) != JsonSerializer.Serialize(methods.Select(m => InspectTest(m, model.GetDeclaredSymbol(m)!, model, "Tests.csproj", "Fixture.cs", [new("Independent.Oracle", "external-tool")])).ToArray()))
        throw new Exception("Non-deterministic static evidence.");
    var trx = XDocument.Parse("""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="a"><TestMethod className="Tests.Sample, Tests" name="Bound(x: 1)" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="a" outcome="Passed" /></Results></TestRun>
        """);
    if (ParseTrx(trx, "Tests.csproj").Single() != new ExecutionResult("Tests.csproj", "Tests.Sample.Bound", "Passed")) throw new Exception("TRX matching failed.");
    if (LoadExecution(null, "").Status != "not-supplied" || LoadCoverage(null, "", ".").Status != "not-supplied") throw new Exception("Absent evidence misrepresented.");
    var temp = Path.Combine(Path.GetTempPath(), "excise-test-evidence-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var manifest = Path.Combine(temp, "manifest.json");
        var trxPath = Path.Combine(temp, "run.trx");
        trx.Save(trxPath);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new EvidenceManifest(1, "inventory", [new("Tests.csproj", "run.trx")])));
        if (LoadExecution(manifest, "inventory").Results.Single().Outcome != "Passed") throw new Exception("Execution import failed.");
        try { ReadManifest(manifest, "stale"); throw new Exception("Stale attestation accepted."); }
        catch (InvalidOperationException) { }
        File.WriteAllText(trxPath, "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010' />");
        try { LoadExecution(manifest, "inventory"); throw new Exception("Zero-test execution evidence accepted."); }
        catch (InvalidOperationException) { }
        File.WriteAllText(Path.Combine(temp, "coverage.xml"), "<coverage><packages><package><classes><class filename='Engine.cs'><lines><line number='12' hits='2'/><line number='13' hits='0'/></lines></class></classes></package></packages></coverage>");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new EvidenceManifest(1, "inventory", [new("Tests.csproj", "coverage.xml")])));
        var observation = LoadCoverage(manifest, "inventory", temp);
        if (observation.Lines.Length != 2 || observation.Lines[0].Hits != 2 || observation.Lines[1].Hits != 0) throw new Exception("Coverage observations altered.");
        var delta = Path.Combine(temp, "delta.json");
        File.WriteAllText(delta, "{\"Added\":[\"new\"],\"Removed\":[\"old\"],\"Changed\":[\"same\"]}");
        if (!ChangedKeys(delta).SetEquals(["new", "old", "same"])) throw new Exception("Changed symbol scope dropped additions/removals.");
        Run("git", temp, "init", "--quiet");
        Directory.CreateDirectory(Path.Combine(temp, "architecture"));
        File.WriteAllText(Path.Combine(temp, "architecture/repository-scope.json"), "{}");
        File.WriteAllText(Path.Combine(temp, "Source.cs"), "class Source {}");
        Run("git", temp, "add", "Source.cs");
        var inventory = new Inventory(1, Hash("{}"), [new("Source.cs", "Source.csproj", "shipping", Hash("class Source {}"))], [], []);
        ValidateInventory(temp, inventory);
        File.WriteAllText(Path.Combine(temp, "Source.cs"), "class Changed {}");
        try { ValidateInventory(temp, inventory); throw new Exception("Stale inventory source accepted."); }
        catch (InvalidOperationException) { }
        File.WriteAllText(Path.Combine(temp, "Source.cs"), "class Source {}");
        File.WriteAllText(Path.Combine(temp, "Added.cs"), "class Added {}");
        Run("git", temp, "add", "Added.cs");
        try { ValidateInventory(temp, inventory); throw new Exception("Stale inventory membership accepted."); }
        catch (InvalidOperationException) { }
    }
    finally { Directory.Delete(temp, true); }
    Console.WriteLine("PASS: semantic overload attribution; helper exclusion; unresolved calls; oracle candidates; deterministic bytes; TRX theory import; stale attestation/source/membership and zero-test evidence rejected; coverage import; changed scope; absent evidence remains unknown.");
}
record Input(string Path, string? Project, string Classification, string SourceHash);
record Exclusion(string Path, string Classification, string Reason);
record Declaration(string Key, string Kind, string Classification, string Path, int Line);
record Inventory(int SchemaVersion, string ScopeHash, Input[] Inputs, Exclusion[] Exclusions, Declaration[] Symbols);
record OracleType(string Type, string Kind);
record OraclePolicy(int SchemaVersion, OracleType[] Types);
record Production(string Key, string Path, int StartLine, int EndLine);
record Test(string Key, string FullyQualifiedName, string Project, string Path, int Line, string[] References, string[] OracleCalls, int UnresolvedInvocations);
record Attribution(string Key, string FullyQualifiedName, string Project, string Path, int Line, string[] OracleCandidates, int UnresolvedInvocations, ExecutionResult[] Execution);
record SymbolEvidence(string Key, string BoundSymbol, string Path, int StartLine, int EndLine, string AttributionStatus, int ExecutedAttributedTestCount, int StaticOracleCandidateTestCount, Attribution[] Tests, CoverageLine[] ObservedCoverageLines);
record ExecutionResult(string Project, string FullyQualifiedName, string Outcome);
record Artifact(string Label, string Sha256);
record ExecutionData(string Status, Artifact[] Artifacts, ExecutionResult[] Results);
record CoverageLine(string Path, int Line, long Hits);
record CoverageData(string Status, Artifact[] Artifacts, CoverageLine[] Lines);
record EvidenceRun(string Project, string Artifact);
record EvidenceManifest(int SchemaVersion, string InventorySha256, EvidenceRun[] Runs);
