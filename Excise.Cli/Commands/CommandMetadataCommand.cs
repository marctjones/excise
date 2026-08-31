using System.CommandLine;
using System.Text.Json;
using Excise.Core.Automation;

namespace Excise.Cli.Commands;

/// <summary>
/// Builds the read-only command-metadata surface used by automation and accessibility clients.
/// </summary>
internal static class CommandMetadataCommand
{
    /// <summary>
    /// Creates <c>excise commands [id] [--json]</c> with its stable output and exit-code contract.
    /// </summary>
    internal static Command Create()
    {
        var idArg = new Argument<string?>("id")
        {
            Description = "Optional semantic command id to inspect",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Write command metadata as JSON",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("commands", "Show stable excise command metadata for automation and accessibility")
        {
            idArg,
            jsonOption
        };

        command.SetAction(parseResult =>
        {
            var id = parseResult.GetValue(idArg);
            var json = parseResult.GetValue(jsonOption);

            IReadOnlyList<PdfCommandMetadata> commands;
            if (string.IsNullOrWhiteSpace(id))
            {
                commands = PdfCommandRegistry.All;
            }
            else if (PdfCommandRegistry.TryGet(id, out var single))
            {
                commands = [single];
            }
            else
            {
                Console.Error.WriteLine($"Unknown command id: {id}");
                return 1;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(commands, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }));
                return 0;
            }

            foreach (var metadata in commands)
            {
                var shortcut = string.IsNullOrWhiteSpace(metadata.Shortcut)
                    ? string.Empty
                    : $" [{metadata.Shortcut}]";
                var cli = string.IsNullOrWhiteSpace(metadata.CliCommand)
                    ? string.Empty
                    : $" cli: {metadata.CliCommand}";
                Console.WriteLine($"{metadata.Id} - {metadata.Label}{shortcut}{cli}");
                Console.WriteLine($"  {metadata.Description}");
                if (metadata.IsSecuritySensitive)
                    Console.WriteLine("  Security-sensitive: true");
                if (metadata.IsDestructive)
                    Console.WriteLine("  Destructive: true");
            }

            return 0;
        });

        return command;
    }
}
