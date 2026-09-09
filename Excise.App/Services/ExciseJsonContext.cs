using System.Text.Json.Serialization;
using Excise.App.Models;
using Excise.App.ViewModels;

namespace Excise.App.Services;

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
/// WriteIndented matches the previous hand-rolled options for the type we
/// write (WindowSettings); the license manifest is read-only.
/// See docs/NATIVE_AOT_INVESTIGATION.md.
///
/// RecentFilesData was registered here until #1307 retired
/// RecentFilesService. Recent files are NOT stored as JSON: the shipping
/// implementation lives in MainWindowViewModel and writes newline-delimited
/// text to AppPaths.RecentFilesPath.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowSettings))]
[JsonSerializable(typeof(LicenseManifest))]
[JsonSerializable(typeof(DocumentOpenResponsivenessReport))]
internal partial class ExciseJsonContext : JsonSerializerContext
{
}
