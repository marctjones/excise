using System.Text.Json;
using AwesomeAssertions;

namespace Excise.Core.Tests.Conformance;

public class Pdf20RendererRequirementMatrixTests
{
    private static readonly string[] HardGateProfiles = ["RendererCore", "ParserSupport", "SecurityPolicy"];

    [Fact]
    public void Matrix_HasRequiredSchemaForEveryRequirement()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MatrixPath()));
        var root = document.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("sources").GetArrayLength().Should().BeGreaterThan(0);

        var requirements = root.GetProperty("requirements").EnumerateArray().ToList();
        requirements.Should().NotBeEmpty();
        requirements.Select(r => r.GetProperty("id").GetString())
            .Should().OnlyHaveUniqueItems("requirement IDs are stable trace keys");

        foreach (var requirement in requirements)
        {
            foreach (var property in new[]
            {
                "id",
                "clause",
                "area",
                "profile",
                "obligation",
                "detectFeatures",
                "requiredEvidence",
                "oracle",
                "status",
                "notes",
            })
            {
                requirement.TryGetProperty(property, out _)
                    .Should().BeTrue("{0} must be present on {1}", property, requirement.GetProperty("id").GetString());
            }

            requirement.GetProperty("detectFeatures").ValueKind.Should().Be(JsonValueKind.Array);
            requirement.GetProperty("requiredEvidence").GetArrayLength().Should().BeGreaterThan(0);
            requirement.GetProperty("obligation").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Matrix_HardGateRequirementsHaveActionableEvidenceAndOracles()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MatrixPath()));
        var requirements = document.RootElement.GetProperty("requirements").EnumerateArray();

        foreach (var requirement in requirements)
        {
            var profile = requirement.GetProperty("profile").GetString();
            if (!HardGateProfiles.Contains(profile))
            {
                continue;
            }

            requirement.GetProperty("clause").GetString().Should().NotBeNullOrWhiteSpace();
            requirement.GetProperty("obligation").GetString().Should().NotBeNullOrWhiteSpace();
            requirement.GetProperty("requiredEvidence").GetArrayLength().Should().BeGreaterThan(0);
            requirement.GetProperty("oracle").GetString().Should().NotBe("tracked-only");
            requirement.GetProperty("status").GetString().Should().NotBe("TRACKED_NON_BLOCKING");
        }
    }

    [Fact]
    public void Matrix_ContainsEachAnnexAOperatorExactlyOnce()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MatrixPath()));
        var root = document.RootElement;

        var expected = root.GetProperty("annexAOperators").EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        var actual = root.GetProperty("requirements").EnumerateArray()
            .Where(row => row.TryGetProperty("kind", out var kind) && kind.GetString() == "content-operator")
            .Select(row => row.GetProperty("operator").GetString())
            .ToArray();

        actual.Should().BeEquivalentTo(expected);
        actual.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void OperatorEvidence_MapsEveryAnnexAOperatorRequirementToUnitAndAtomicEvidence()
    {
        using var matrix = JsonDocument.Parse(File.ReadAllText(MatrixPath()));
        using var evidence = JsonDocument.Parse(File.ReadAllText(OperatorEvidencePath()));

        var operatorRows = matrix.RootElement.GetProperty("requirements").EnumerateArray()
            .Where(row => row.TryGetProperty("kind", out var kind) && kind.GetString() == "content-operator")
            .ToDictionary(
                row => row.GetProperty("id").GetString()!,
                row => row.GetProperty("operator").GetString());
        var evidenceRows = evidence.RootElement.GetProperty("operatorEvidence").EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("id").GetString()!,
                row => row);

        evidenceRows.Keys.Should().BeEquivalentTo(operatorRows.Keys);

        foreach (var (id, row) in evidenceRows)
        {
            row.GetProperty("operator").GetString().Should().Be(operatorRows[id]);
            row.GetProperty("unitEvidence").GetArrayLength().Should().BeGreaterThan(0);
            row.GetProperty("atomicEvidence").GetArrayLength().Should().BeGreaterThan(0);

            AssertEvidenceFilesExist(row.GetProperty("unitEvidence"));
            AssertEvidenceFilesExist(row.GetProperty("atomicEvidence"));
        }
    }


    [Fact]
    public void Matrix_ExtendsExistingImageFilterMatrix()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MatrixPath()));
        var sourceMatrices = document.RootElement.GetProperty("sourceMatrices").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString())
            .ToArray();

        sourceMatrices.Should().Contain("pdf-image-feature-matrix");
        File.Exists(Path.Combine(RepoRoot(), "test-pdfs", "manifests", "pdf-image-feature-matrix.json"))
            .Should().BeTrue("image/filter obligations must remain represented in the PDF 2.0 renderer gate");
    }

    [Fact]
    public void ImageFilterCoverageReport_HasNoMissingRequirementsWhenPresent()
    {
        var reportPath = Path.Combine(RepoRoot(), "logs", "image-conformance", "normative", "coverage-audit.json");
        if (!File.Exists(reportPath))
        {
            return;
        }

        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        report.RootElement.GetProperty("missingAtomicRequirements").GetInt32().Should().Be(0);
        report.RootElement.GetProperty("missingCorpusRequirements").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Matrix_HardGateCorpusRowsHaveExplicitRenderingContractEvidence()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(MatrixPath()));
        var requiredCorpusIds = document.RootElement.GetProperty("requirements").EnumerateArray()
            .Where(row => HardGateProfiles.Contains(row.GetProperty("profile").GetString()))
            .Where(row => row.GetProperty("requiredEvidence").EnumerateArray()
                .Any(value => value.GetString() == "corpus"))
            .Select(row => row.GetProperty("id").GetString())
            .ToArray();

        var contractIds = new HashSet<string>();
        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "test-pdfs", "rendering-contracts"),
            "*.json",
            SearchOption.AllDirectories))
        {
            using var contract = JsonDocument.Parse(File.ReadAllText(path));
            AddRequirementIds(contract.RootElement, contractIds);
        }

        foreach (var id in requiredCorpusIds)
        {
            contractIds.Should().Contain(id, "{0} requires corpus evidence", id);
        }
    }

    private static string MatrixPath() =>
        Path.Combine(RepoRoot(), "test-pdfs", "manifests", "pdf20-renderer-requirements.json");

    private static string OperatorEvidencePath() =>
        Path.Combine(RepoRoot(), "test-pdfs", "manifests", "pdf20-operator-evidence.json");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }

    private static void AddRequirementIds(JsonElement element, HashSet<string> ids)
    {
        foreach (var propertyName in new[] { "Pdf20Requirements", "pdf20Requirements" })
        {
            if (!element.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        if (!element.TryGetProperty("Pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.ValueKind == JsonValueKind.Object)
            {
                AddRequirementIds(page.Value, ids);
            }
        }
    }

    private static void AssertEvidenceFilesExist(JsonElement evidenceItems)
    {
        foreach (var evidence in evidenceItems.EnumerateArray())
        {
            var file = evidence.GetProperty("file").GetString();
            File.Exists(Path.Combine(RepoRoot(), file!))
                .Should().BeTrue("{0} should point to a real evidence file", file);
        }
    }
}
