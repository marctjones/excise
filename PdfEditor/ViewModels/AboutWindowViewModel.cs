using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfEditor.Services;

namespace PdfEditor.ViewModels;

/// <summary>
/// ViewModel for the About window. Loads the embedded
/// third-party-licenses.json manifest produced by
/// <c>scripts/generate-license-manifest.sh</c> and exposes it as a
/// strongly-typed list the dialog can bind to.
/// </summary>
public sealed class AboutWindowViewModel
{
    public string AppName { get; } = "pdfe";

    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";

    public string Tagline { get; } =
        "Cross-platform PDF editor with true content-level redaction, " +
        "AcroForm authoring, and PDF 2.0 conformance.";

    public string Copyright { get; } =
        "Copyright © 2024–2026 Marc Jones · MIT License";

    public string ProjectUrl { get; } = "https://github.com/marctjones/pdfe";

    public LicenseManifest Manifest { get; }

    public IReadOnlyList<ThirdPartyPackage> Packages => Manifest.Packages;

    public AboutWindowViewModel()
    {
        Manifest = LoadManifest() ?? new LicenseManifest();
    }

    private static LicenseManifest? LoadManifest()
    {
        // Same name we set as <EmbeddedResource> in the csproj. Avalonia
        // doesn't transform resource names, so the value matches the file
        // name with the project's default namespace as the prefix.
        var asm = typeof(AboutWindowViewModel).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("third-party-licenses.json", System.StringComparison.OrdinalIgnoreCase));
        if (resourceName == null) return null;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        // The manifest's property names are mapped explicitly with
        // [JsonPropertyName], so the source-generated context matches them
        // without the reflection-based case-insensitive fallback. (The old
        // WhenWritingNull option only affected writes; this path is read-only.)
        return JsonSerializer.Deserialize(json, PdfeJsonContext.Default.LicenseManifest);
    }
}

public sealed class LicenseManifest
{
    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("packages")]
    public List<ThirdPartyPackage> Packages { get; set; } = new();
}

public sealed class ThirdPartyPackage
{
    [JsonPropertyName("nugetId")]       public string? Id { get; set; }
    [JsonPropertyName("nugetVersion")]  public string? Version { get; set; }
    [JsonPropertyName("authors")]       public string? Authors { get; set; }
    [JsonPropertyName("copyright")]     public string? Copyright { get; set; }
    [JsonPropertyName("description")]   public string? Description { get; set; }
    [JsonPropertyName("projectUrl")]    public string? ProjectUrl { get; set; }
    [JsonPropertyName("repositoryUrl")] public string? RepositoryUrl { get; set; }

    [JsonPropertyName("licenseKind")]   public string? LicenseKind { get; set; }
    [JsonPropertyName("licenseValue")]  public string? LicenseValue { get; set; }
    [JsonPropertyName("licenseName")]   public string? LicenseName { get; set; }
    [JsonPropertyName("licenseUrl")]    public string? LicenseUrl { get; set; }
    [JsonPropertyName("licenseSpdxUrl")]public string? LicenseSpdxUrl { get; set; }
    [JsonPropertyName("licenseFileName")]public string? LicenseFileName { get; set; }
    [JsonPropertyName("licenseText")]   public string? LicenseText { get; set; }
    [JsonPropertyName("spdx")]          public string? Spdx { get; set; }

    [JsonPropertyName("scancodeDetectedSpdx")] public List<string>? ScancodeDetectedSpdx { get; set; }
    [JsonPropertyName("scancodeMismatch")]     public bool ScancodeMismatch { get; set; }

    /// <summary>"Avalonia 11.2.5 — MIT License" for the master list.</summary>
    public string DisplayName => $"{Id} {Version} — {LicenseName ?? Spdx ?? "(unknown)"}";

    /// <summary>Whichever URL is most useful: project, repo, or SPDX.</summary>
    public string? PrimaryUrl => ProjectUrl ?? RepositoryUrl ?? LicenseSpdxUrl ?? LicenseUrl;
}
