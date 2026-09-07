using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Excise.Cli.Commands;

/// <summary>
/// The CLI's JSON writer. Every caller passes the <see cref="JsonTypeInfo{T}"/> the source
/// generator produced for its report type (see <c>CliJsonContexts.cs</c>) rather than letting
/// the serializer discover the shape by reflection — reflection is invisible to the trimmer,
/// so under Native AOT it yields <c>{}</c> at runtime with no build-time complaint (#1389).
/// </summary>
internal static class CliJson
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);
}
