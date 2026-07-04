using System.Text.Json.Serialization;
using PdfEditor.Models;
using PdfEditor.ViewModels;

namespace PdfEditor.Services;

/// <summary>
/// Source-generated JSON type metadata for the app's persisted/embedded JSON
/// (window settings, recent files, third-party license manifest).
///
/// Using these generated <c>JsonTypeInfo</c>s instead of the reflection-based
/// <c>JsonSerializer.Serialize/Deserialize&lt;T&gt;</c> overloads makes
/// (de)serialization trim- and NativeAOT-safe: it removes the IL2026
/// (RequiresUnreferencedCode) and IL3050 (RequiresDynamicCode) warnings those
/// overloads raise, and avoids the runtime <c>NotSupportedException</c> the
/// reflection path can throw once the metadata is trimmed.
///
/// WriteIndented matches the previous hand-rolled options for the two types we
/// write (WindowSettings, RecentFilesData); the license manifest is read-only.
/// See docs/NATIVE_AOT_INVESTIGATION.md.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowSettings))]
[JsonSerializable(typeof(RecentFilesService.RecentFilesData))]
[JsonSerializable(typeof(LicenseManifest))]
[JsonSerializable(typeof(DocumentOpenResponsivenessReport))]
internal partial class PdfeJsonContext : JsonSerializerContext
{
}
