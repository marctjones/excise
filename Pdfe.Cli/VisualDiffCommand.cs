using System.CommandLine;
using System.Text.Json;
using Pdfe.Rendering.Differential;
using SkiaSharp;

namespace Pdfe.Cli;

partial class Program
{
    private const int DefaultVisualDiffTolerance = 64;

    static Command CreateVisualDiffCommand()
    {
        var actualArg = new Argument<FileInfo>("actual") { Description = "Actual PNG, usually pdfe output" };
        var referenceArg = new Argument<FileInfo>("reference") { Description = "Reference PNG" };
        var outputOption = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Optional side-by-side diff PNG output",
        };
        var jsonOption = new Option<FileInfo?>("--json")
        {
            Description = "Optional JSON report output",
        };
        var toleranceOption = new Option<int>("--tolerance")
        {
            Description = "Per-channel pixel tolerance used for diff masks",
            DefaultValueFactory = _ => DefaultVisualDiffTolerance,
        };

        var command = new Command("visual-diff",
            "Analyze two rendered PNGs and classify the visual difference")
        {
            actualArg,
            referenceArg,
            outputOption,
            jsonOption,
            toleranceOption,
        };

        command.SetAction(parseResult =>
        {
            var actualFile = parseResult.GetValue(actualArg)!;
            var referenceFile = parseResult.GetValue(referenceArg)!;
            var outputFile = parseResult.GetValue(outputOption);
            var jsonFile = parseResult.GetValue(jsonOption);
            var tolerance = parseResult.GetValue(toleranceOption);

            if (!actualFile.Exists)
            {
                Console.Error.WriteLine($"Actual image not found: {actualFile.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            if (!referenceFile.Exists)
            {
                Console.Error.WriteLine($"Reference image not found: {referenceFile.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            if (tolerance < 0 || tolerance > 255)
            {
                Console.Error.WriteLine("--tolerance must be between 0 and 255.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                using var actual = LoadBitmapOrThrow(actualFile);
                using var reference = LoadBitmapOrThrow(referenceFile);
                using var comparableActual = actual.Width == reference.Width && actual.Height == reference.Height
                    ? actual.Copy()
                    : DifferentialMetrics.ResizeMatch(actual, reference.Width, reference.Height);

                var report = AnalyzeVisualDiff(
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
                    using var diff = BuildVisualDiffOverlay(comparableActual, reference, tolerance);
                    DifferentialMetrics.SaveTriptych(outputFile.FullName, comparableActual, reference, diff);
                    Console.WriteLine($"Wrote diff image: {outputFile.FullName}");
                }

                if (jsonFile != null)
                {
                    EnsureParentDirectory(jsonFile);
                    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(jsonFile.FullName, json);
                    Console.WriteLine($"Wrote JSON: {jsonFile.FullName}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    internal static VisualDiffReport AnalyzeVisualDiff(
        SKBitmap actual,
        SKBitmap reference,
        int tolerance = DefaultVisualDiffTolerance,
        string? actualPath = null,
        string? referencePath = null,
        int? originalActualWidth = null,
        int? originalActualHeight = null)
    {
        if (actual.Width != reference.Width || actual.Height != reference.Height)
            throw new ArgumentException("Images must have matching dimensions before visual diff analysis.");

        var width = actual.Width;
        var height = actual.Height;
        var pixelCount = checked(width * height);
        var diffMask = new bool[pixelCount];
        var deltas = new byte[pixelCount];

        long differingPixels = 0;
        long sumAbs = 0;
        double sumSquared = 0;
        int maxChannelDelta = 0;
        double diffLuminance = 0;
        double diffChroma = 0;
        long actualDarkPixels = 0;
        long referenceDarkPixels = 0;
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;

        for (int y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                var pa = actual.GetPixel(x, y);
                var pr = reference.GetPixel(x, y);
                if (IsDarkTextPixel(pa))
                    actualDarkPixels++;
                if (IsDarkTextPixel(pr))
                    referenceDarkPixels++;
                var dR = Math.Abs(pa.Red - pr.Red);
                var dG = Math.Abs(pa.Green - pr.Green);
                var dB = Math.Abs(pa.Blue - pr.Blue);
                var worst = Math.Max(dR, Math.Max(dG, dB));
                var index = rowOffset + x;

                sumAbs += dR + dG + dB;
                sumSquared += dR * dR + dG * dG + dB * dB;
                maxChannelDelta = Math.Max(maxChannelDelta, worst);
                deltas[index] = (byte)worst;

                if (worst <= tolerance)
                    continue;

                diffMask[index] = true;
                differingPixels++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
                diffLuminance += Math.Abs(Luminance(pa) - Luminance(pr));
                diffChroma += ChromaDistance(pa, pr);
            }
        }

        var regions = FindDiffRegions(diffMask, deltas, width, height, maxRegions: 10);
        var diffFraction = (double)differingPixels / pixelCount;
        var meanAbsoluteError = (double)sumAbs / (pixelCount * 3.0);
        var rootMeanSquaredError = Math.Sqrt(sumSquared / (pixelCount * 3.0));
        var meanDiffLuminance = differingPixels == 0 ? 0 : diffLuminance / differingPixels;
        var meanDiffChroma = differingPixels == 0 ? 0 : diffChroma / differingPixels;
        var darkPixelBalance = ComputeBalance(actualDarkPixels, referenceDarkPixels);
        var bounds = differingPixels == 0
            ? null
            : new VisualDiffBounds(left, top, right - left, bottom - top);
        var category = ClassifyVisualDiff(
            diffFraction,
            meanAbsoluteError,
            meanDiffLuminance,
            meanDiffChroma,
            regions.FirstOrDefault()?.pixelCount ?? 0,
            regions.FirstOrDefault()?.width ?? 0,
            regions.FirstOrDefault()?.height ?? 0,
            regions.FirstOrDefault()?.density ?? 0,
            darkPixelBalance,
            pixelCount);
        var humanImpact = ClassifyHumanImpact(category, diffFraction, meanAbsoluteError);

        return new VisualDiffReport
        {
            actualPath = actualPath,
            referencePath = referencePath,
            actualWidth = originalActualWidth ?? actual.Width,
            actualHeight = originalActualHeight ?? actual.Height,
            referenceWidth = reference.Width,
            referenceHeight = reference.Height,
            comparedWidth = width,
            comparedHeight = height,
            pixelCount = pixelCount,
            tolerance = tolerance,
            differingPixels = (int)differingPixels,
            diffFraction = diffFraction,
            meanAbsoluteError = meanAbsoluteError,
            rootMeanSquaredError = rootMeanSquaredError,
            maxChannelDelta = maxChannelDelta,
            actualDarkPixels = (int)actualDarkPixels,
            referenceDarkPixels = (int)referenceDarkPixels,
            darkPixelBalance = darkPixelBalance,
            meanDiffLuminance = meanDiffLuminance,
            meanDiffChroma = meanDiffChroma,
            diffBounds = bounds,
            topRegions = regions,
            category = category,
            humanImpact = humanImpact,
        };
    }

    internal static void ApplyCorpusVisualDiffClassification(
        CorpusScanEntry entry,
        SKBitmap pdfe,
        SKBitmap reference)
    {
        using var comparablePdfe = pdfe.Width == reference.Width && pdfe.Height == reference.Height
            ? pdfe.Copy()
            : DifferentialMetrics.ResizeMatch(pdfe, reference.Width, reference.Height);
        var report = AnalyzeVisualDiff(
            comparablePdfe,
            reference,
            DefaultVisualDiffTolerance,
            originalActualWidth: pdfe.Width,
            originalActualHeight: pdfe.Height);

        entry.visualCategory = report.category;
        entry.visualHumanImpact = report.humanImpact;
        entry.visualDiffBounds = report.diffBounds;
        entry.visualTopRegions = report.topRegions.Take(5).ToArray();
    }

    private static SKBitmap LoadBitmapOrThrow(FileInfo file)
    {
        var bitmap = SKBitmap.Decode(file.FullName);
        return bitmap ?? throw new InvalidDataException($"Could not decode image: {file.FullName}");
    }

    private static SKBitmap BuildVisualDiffOverlay(SKBitmap actual, SKBitmap reference, int tolerance)
    {
        if (actual.Width != reference.Width || actual.Height != reference.Height)
            throw new ArgumentException("Dimension mismatch");

        var diff = new SKBitmap(actual.Width, actual.Height);
        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                var pa = actual.GetPixel(x, y);
                var pr = reference.GetPixel(x, y);
                var worst = Math.Max(
                    Math.Abs(pa.Red - pr.Red),
                    Math.Max(Math.Abs(pa.Green - pr.Green), Math.Abs(pa.Blue - pr.Blue)));
                diff.SetPixel(x, y, worst > tolerance
                    ? new SKColor(255, 32, 32)
                    : new SKColor(240, 240, 240));
            }
        }

        return diff;
    }

    private static IReadOnlyList<VisualDiffRegion> FindDiffRegions(
        bool[] diffMask,
        byte[] deltas,
        int width,
        int height,
        int maxRegions)
    {
        var visited = new bool[diffMask.Length];
        var regions = new List<VisualDiffRegion>();
        var stack = new Stack<int>();

        for (int index = 0; index < diffMask.Length; index++)
        {
            if (!diffMask[index] || visited[index])
                continue;

            var count = 0;
            long sumDelta = 0;
            var left = width;
            var top = height;
            var right = -1;
            var bottom = -1;

            visited[index] = true;
            stack.Push(index);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var x = current % width;
                var y = current / width;
                count++;
                sumDelta += deltas[current];
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);

                PushNeighbor(current - 1, x > 0);
                PushNeighbor(current + 1, x + 1 < width);
                PushNeighbor(current - width, y > 0);
                PushNeighbor(current + width, y + 1 < height);
            }

            regions.Add(new VisualDiffRegion
            {
                x = left,
                y = top,
                width = right - left,
                height = bottom - top,
                pixelCount = count,
                density = (double)count / Math.Max(1, (right - left) * (bottom - top)),
                meanChannelDelta = (double)sumDelta / count,
            });
        }

        return regions
            .OrderByDescending(r => r.pixelCount)
            .ThenByDescending(r => r.meanChannelDelta)
            .Take(maxRegions)
            .ToArray();

        void PushNeighbor(int neighbor, bool inBounds)
        {
            if (!inBounds || visited[neighbor] || !diffMask[neighbor])
                return;

            visited[neighbor] = true;
            stack.Push(neighbor);
        }
    }

    private static double Luminance(SKColor color)
        => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;

    private static bool IsDarkTextPixel(SKColor color)
        => Luminance(color) < 200;

    private static double ComputeBalance(long a, long b)
    {
        var max = Math.Max(a, b);
        return max == 0 ? 1 : (double)Math.Min(a, b) / max;
    }

    private static double ChromaDistance(SKColor a, SKColor b)
    {
        var cbA = -0.168736 * a.Red - 0.331264 * a.Green + 0.5 * a.Blue;
        var crA = 0.5 * a.Red - 0.418688 * a.Green - 0.081312 * a.Blue;
        var cbB = -0.168736 * b.Red - 0.331264 * b.Green + 0.5 * b.Blue;
        var crB = 0.5 * b.Red - 0.418688 * b.Green - 0.081312 * b.Blue;
        return (Math.Abs(cbA - cbB) + Math.Abs(crA - crB)) / 2.0;
    }

    private static string ClassifyVisualDiff(
        double diffFraction,
        double meanAbsoluteError,
        double meanDiffLuminance,
        double meanDiffChroma,
        int largestRegionPixels,
        int largestRegionWidth,
        int largestRegionHeight,
        double largestRegionDensity,
        double darkPixelBalance,
        int pixelCount)
    {
        if (diffFraction == 0)
            return "none";

        var largestRegionFraction = (double)largestRegionPixels / pixelCount;
        if (diffFraction <= 0.002 && meanAbsoluteError <= 2)
            return "minor-noise-or-antialiasing";

        if (meanDiffLuminance <= 18 && meanAbsoluteError <= 24)
            return "color-tone-or-texture";

        var largestRegionMinSide = Math.Min(largestRegionWidth, largestRegionHeight);
        var largestRegionMaxSide = Math.Max(largestRegionWidth, largestRegionHeight);
        var largestRegionAspect = largestRegionMinSide == 0
            ? 0
            : (double)largestRegionMaxSide / largestRegionMinSide;

        if (largestRegionFraction >= 0.01 &&
            (largestRegionDensity < 0.30 || largestRegionAspect >= 8) &&
            darkPixelBalance >= 0.75 &&
            meanAbsoluteError <= 20)
        {
            return "localized-linework-or-texture";
        }

        if (largestRegionFraction >= 0.01 &&
            largestRegionDensity >= 0.60 &&
            meanDiffChroma >= 12 &&
            diffFraction <= 0.35)
        {
            return "localized-color-or-image-content";
        }

        if (largestRegionFraction >= 0.01 &&
            largestRegionDensity < 0.50 &&
            meanAbsoluteError >= 8)
        {
            if (pixelCount <= 10_000 && largestRegionPixels < 100 && darkPixelBalance >= 0.50)
                return "small-text-antialiasing";

            return "localized-text-or-geometry";
        }

        if (pixelCount <= 10_000 &&
            largestRegionPixels < 100 &&
            diffFraction >= 0.005 &&
            darkPixelBalance >= 0.50)
        {
            return "small-text-antialiasing";
        }

        if (largestRegionFraction >= 0.01 && meanAbsoluteError >= 12)
            return "localized-content-or-geometry";

        if (diffFraction >= 0.08 && meanAbsoluteError >= 12)
            return "structural-or-missing-content-candidate";

        return "mixed";
    }

    private static string ClassifyHumanImpact(string category, double diffFraction, double meanAbsoluteError)
        => category switch
        {
            "none" => "none",
            "minor-noise-or-antialiasing" => "low",
            "color-tone-or-texture" when meanAbsoluteError <= 16 => "low",
            "color-tone-or-texture" => "medium",
            "localized-color-or-image-content" => "medium",
            "small-text-antialiasing" => "low",
            "localized-linework-or-texture" => "medium",
            "structural-or-missing-content-candidate" => "high",
            "localized-text-or-geometry" when diffFraction >= 0.03 => "high",
            "localized-text-or-geometry" => "medium",
            "localized-content-or-geometry" when diffFraction >= 0.03 => "high",
            "localized-content-or-geometry" => "medium",
            _ when diffFraction <= 0.01 && meanAbsoluteError <= 5 => "low",
            _ when diffFraction >= 0.08 && meanAbsoluteError >= 16 => "high",
            _ => "medium",
        };

    private static void EnsureParentDirectory(FileInfo file)
    {
        if (!string.IsNullOrWhiteSpace(file.DirectoryName))
            Directory.CreateDirectory(file.DirectoryName);
    }

    internal sealed class VisualDiffReport
    {
        public string? actualPath { get; set; }
        public string? referencePath { get; set; }
        public int actualWidth { get; set; }
        public int actualHeight { get; set; }
        public int referenceWidth { get; set; }
        public int referenceHeight { get; set; }
        public int comparedWidth { get; set; }
        public int comparedHeight { get; set; }
        public int pixelCount { get; set; }
        public int tolerance { get; set; }
        public int differingPixels { get; set; }
        public double diffFraction { get; set; }
        public double meanAbsoluteError { get; set; }
        public double rootMeanSquaredError { get; set; }
        public int maxChannelDelta { get; set; }
        public int actualDarkPixels { get; set; }
        public int referenceDarkPixels { get; set; }
        public double darkPixelBalance { get; set; }
        public double meanDiffLuminance { get; set; }
        public double meanDiffChroma { get; set; }
        public VisualDiffBounds? diffBounds { get; set; }
        public IReadOnlyList<VisualDiffRegion> topRegions { get; set; } = Array.Empty<VisualDiffRegion>();
        public string category { get; set; } = "";
        public string humanImpact { get; set; } = "";
    }

    internal sealed record VisualDiffBounds(int x, int y, int width, int height);

    internal sealed class VisualDiffRegion
    {
        public int x { get; set; }
        public int y { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public int pixelCount { get; set; }
        public double density { get; set; }
        public double meanChannelDelta { get; set; }
    }
}
