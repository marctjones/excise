using Excise.Cli.Commands;

namespace Excise.Cli.Tests;

internal static class FormMutationTestDriver
{
    internal static int RunFillForm(
        string inputPath,
        string outputPath,
        string[] fields,
        bool flatten,
        bool ignorePermissions = false)
        => FormMutationHandler.Fill(new FillFormRequest(
            inputPath,
            outputPath,
            fields,
            flatten,
            ignorePermissions)).UpdatedFieldCount;

    internal static void RunAddField(
        string inputPath,
        string outputPath,
        string type,
        string name,
        int page,
        string rectStr,
        string? value,
        string[] options,
        bool ignorePermissions = false)
        => FormMutationHandler.AddField(new AddFieldRequest(
            inputPath,
            outputPath,
            type,
            name,
            page,
            rectStr,
            value,
            options,
            ignorePermissions));
}
