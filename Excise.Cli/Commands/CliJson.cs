using System.Text.Json;
using System.Text.Json.Serialization;

namespace Excise.Cli.Commands;

internal static class CliJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);
}
