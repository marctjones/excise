using System.CommandLine;
using System.Text.Json;
using Excise.ImageInspection;
using SkiaSharp;

var root = new RootCommand("Standalone raster image inspection utilities for renderer triage")
{
    CreateInfoCommand(),
    CreateDiffCommand(),
    CreateContactSheetCommand(),
};

return root.Parse(args).Invoke();

static Command CreateInfoCommand()
{
    var imagesArg = new Argument<FileInfo[]>("images")
    {
        Description = "Image files to inspect",
        Arity = ArgumentArity.OneOrMore,
    };
    var jsonOption = new Option<FileInfo?>("--json")
    {
        Description = "Optional JSON report output",
    };
    var rectOption = new Option<string[]>("--rect")
    {
        Description = "Optional analysis rectangle(s) as x,y,width,height",
        Arity = ArgumentArity.ZeroOrMore,
    };

    var command = new Command("info", "Print image dimensions and basic pixel statistics")
    {
        imagesArg,
        jsonOption,
        rectOption,
    };

    command.SetAction(parseResult =>
    {
        var images = parseResult.GetValue(imagesArg) ?? Array.Empty<FileInfo>();
        var jsonFile = parseResult.GetValue(jsonOption);
        var rectSpecs = parseResult.GetValue(rectOption) ?? Array.Empty<string>();
        var regions = new List<(string Spec, SKRectI? Rect)>();
        if (rectSpecs.Length == 0)
        {
            regions.Add(("full", null));
        }
        else
        {
            foreach (var spec in rectSpecs)
            {
                if (!TryParseRect(spec, out var rect))
                {
                    Console.Error.WriteLine($"Invalid --rect '{spec}'. Expected x,y,width,height.");
                    Environment.ExitCode = 1;
                    return;
                }

                regions.Add((spec, rect));
            }
        }

        var reports = new List<ImageInfoReport>();

        foreach (var file in images)
        {
            if (!TryLoadBitmap(file, out var bitmap))
                return;

            using (bitmap)
            {
                foreach (var (spec, rect) in regions)
                {
                    var report = ImageInfoAnalyzer.Analyze(bitmap, file.FullName, rect);
                    reports.Add(report);
                    Console.WriteLine(
                        $"{file.Name} [{spec}]: {report.width}x{report.height}, " +
                        $"region {report.regionX},{report.regionY},{report.regionWidth},{report.regionHeight}, " +
                        $"mean RGBA ({report.meanRed:F1}, {report.meanGreen:F1}, {report.meanBlue:F1}, {report.meanAlpha:F1}), " +
                        $"dark {report.darkPixels:N0}, transparent {report.transparentPixels:N0}, non-white {report.nonWhitePixels:N0}");
                }
            }
        }

        if (jsonFile != null)
        {
            EnsureParentDirectory(jsonFile);
            File.WriteAllText(
                jsonFile.FullName,
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Wrote JSON: {jsonFile.FullName}");
        }
    });

    return command;
}

static bool TryParseRect(string value, out SKRectI rect)
{
    rect = default;
    var parts = value.Split(',', StringSplitOptions.TrimEntries);
    if (parts.Length != 4 ||
        !int.TryParse(parts[0], out var x) ||
        !int.TryParse(parts[1], out var y) ||
        !int.TryParse(parts[2], out var width) ||
        !int.TryParse(parts[3], out var height) ||
        width <= 0 ||
        height <= 0)
    {
        return false;
    }

    rect = new SKRectI(x, y, checked(x + width), checked(y + height));
    return true;
}

static Command CreateDiffCommand()
{
    var actualArg = new Argument<FileInfo>("actual") { Description = "Actual image, usually excise output" };
    var referenceArg = new Argument<FileInfo>("reference") { Description = "Reference image" };
    var outputOption = new Option<FileInfo?>("--output", "-o")
    {
        Description = "Optional triptych PNG output: actual, reference, diff mask",
    };
    var jsonOption = new Option<FileInfo?>("--json")
    {
        Description = "Optional JSON report output",
    };
    var toleranceOption = new Option<int>("--tolerance")
    {
        Description = "Per-channel pixel tolerance used for diff masks",
        DefaultValueFactory = _ => VisualDiffAnalyzer.DefaultTolerance,
    };
    var noResizeOption = new Option<bool>("--no-resize")
    {
        Description = "Fail on dimension mismatch instead of resizing actual to reference dimensions",
    };

    var command = new Command("diff", "Analyze two rendered images and classify the visual difference")
    {
        actualArg,
        referenceArg,
        outputOption,
        jsonOption,
        toleranceOption,
        noResizeOption,
    };

    command.SetAction(parseResult =>
    {
        var actualFile = parseResult.GetValue(actualArg)!;
        var referenceFile = parseResult.GetValue(referenceArg)!;
        var outputFile = parseResult.GetValue(outputOption);
        var jsonFile = parseResult.GetValue(jsonOption);
        var tolerance = parseResult.GetValue(toleranceOption);
        var noResize = parseResult.GetValue(noResizeOption);

        if (tolerance < 0 || tolerance > 255)
        {
            Console.Error.WriteLine("--tolerance must be between 0 and 255.");
            Environment.ExitCode = 1;
            return;
        }

        if (!TryLoadBitmap(actualFile, out var actual) ||
            !TryLoadBitmap(referenceFile, out var reference))
        {
            return;
        }

        using (actual)
        using (reference)
        {
            if (noResize && (actual.Width != reference.Width || actual.Height != reference.Height))
            {
                Console.Error.WriteLine(
                    $"Dimension mismatch: actual {actual.Width}x{actual.Height}, " +
                    $"reference {reference.Width}x{reference.Height}.");
                Environment.ExitCode = 1;
                return;
            }

            using var comparableActual = actual.Width == reference.Width && actual.Height == reference.Height
                ? actual.Copy()
                : VisualDiffAnalyzer.ResizeMatch(actual, reference.Width, reference.Height);

            var report = VisualDiffAnalyzer.Analyze(
                comparableActual,
                reference,
                tolerance,
                actualFile.FullName,
                referenceFile.FullName,
                actual.Width,
                actual.Height);

            Console.WriteLine($"Category: {report.category}");
            Console.WriteLine($"Human impact: {report.humanImpact}");
            Console.WriteLine(
                $"Diff: {report.diffFraction:P2} ({report.differingPixels:N0}/{report.pixelCount:N0}), " +
                $"MAE {report.meanAbsoluteError:F2}, RMSE {report.rootMeanSquaredError:F2}, " +
                $"max {report.maxChannelDelta}");
            if (report.diffBounds != null)
                Console.WriteLine($"Bounds: {report.diffBounds}");

            if (outputFile != null)
            {
                EnsureParentDirectory(outputFile);
                using var diff = VisualDiffAnalyzer.BuildDiffOverlay(comparableActual, reference, tolerance);
                VisualDiffAnalyzer.SaveTriptych(outputFile.FullName, comparableActual, reference, diff);
                Console.WriteLine($"Wrote diff image: {outputFile.FullName}");
            }

            if (jsonFile != null)
            {
                EnsureParentDirectory(jsonFile);
                File.WriteAllText(
                    jsonFile.FullName,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Wrote JSON: {jsonFile.FullName}");
            }
        }
    });

    return command;
}

static Command CreateContactSheetCommand()
{
    var imagesArg = new Argument<string[]>("images")
    {
        Description = "Image files, optionally as label=path",
        Arity = ArgumentArity.OneOrMore,
    };
    var outputOption = new Option<FileInfo>("--output", "-o")
    {
        Description = "Output contact-sheet PNG",
        Required = true,
    };
    var columnsOption = new Option<int>("--columns")
    {
        Description = "Number of columns",
        DefaultValueFactory = _ => 3,
    };
    var cellWidthOption = new Option<int>("--cell-width")
    {
        Description = "Cell width in pixels",
        DefaultValueFactory = _ => 280,
    };
    var cellHeightOption = new Option<int>("--cell-height")
    {
        Description = "Cell height in pixels",
        DefaultValueFactory = _ => 260,
    };

    var command = new Command("contact-sheet", "Create a labeled side-by-side review PNG")
    {
        imagesArg,
        outputOption,
        columnsOption,
        cellWidthOption,
        cellHeightOption,
    };

    command.SetAction(parseResult =>
    {
        var specs = parseResult.GetValue(imagesArg) ?? Array.Empty<string>();
        var output = parseResult.GetValue(outputOption)!;
        var columns = parseResult.GetValue(columnsOption);
        var cellWidth = parseResult.GetValue(cellWidthOption);
        var cellHeight = parseResult.GetValue(cellHeightOption);

        if (columns < 1 || columns > 24)
        {
            Console.Error.WriteLine("--columns must be between 1 and 24.");
            Environment.ExitCode = 1;
            return;
        }

        if (cellWidth < 64 || cellHeight < 64)
        {
            Console.Error.WriteLine("--cell-width and --cell-height must be at least 64.");
            Environment.ExitCode = 1;
            return;
        }

        var items = new List<ContactSheetItem>();
        try
        {
            foreach (var spec in specs)
            {
                var (label, path) = ParseContactSheetSpec(spec);
                var file = new FileInfo(path);
                if (!TryLoadBitmap(file, out var bitmap))
                    return;

                items.Add(new ContactSheetItem(label, file.FullName, bitmap));
            }

            EnsureParentDirectory(output);
            using var sheet = ContactSheetBuilder.Build(items, columns, cellWidth, cellHeight);
            SavePng(sheet, output.FullName);
            Console.WriteLine($"Wrote contact sheet: {output.FullName}");
        }
        finally
        {
            foreach (var item in items)
                item.Bitmap.Dispose();
        }
    });

    return command;
}

static bool TryLoadBitmap(FileInfo file, out SKBitmap bitmap)
{
    bitmap = null!;
    if (!file.Exists)
    {
        Console.Error.WriteLine($"Image not found: {file.FullName}");
        Environment.ExitCode = 1;
        return false;
    }

    bitmap = SKBitmap.Decode(file.FullName);
    if (bitmap != null)
        return true;

    Console.Error.WriteLine($"Could not decode image: {file.FullName}");
    Environment.ExitCode = 1;
    return false;
}

static (string Label, string Path) ParseContactSheetSpec(string spec)
{
    var separator = spec.IndexOf('=');
    if (separator > 0 && separator + 1 < spec.Length)
        return (spec[..separator], spec[(separator + 1)..]);

    return (Path.GetFileNameWithoutExtension(spec), spec);
}

static void SavePng(SKBitmap bitmap, string path)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Open(path, FileMode.Create, FileAccess.Write);
    data.SaveTo(stream);
}

static void EnsureParentDirectory(FileInfo file)
{
    if (!string.IsNullOrWhiteSpace(file.DirectoryName))
        Directory.CreateDirectory(file.DirectoryName);
}
