using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: DebugBundle.CoverageGate <coverage-directory> [minimum-percent]");
    return 2;
}

var coverageDirectory = Path.GetFullPath(args[0]);
var minimum = args.Length == 2
    ? double.Parse(args[1], CultureInfo.InvariantCulture)
    : 80d;
var reports = Directory.Exists(coverageDirectory)
    ? Directory.EnumerateFiles(coverageDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories).ToArray()
    : [];
if (reports.Length == 0)
{
    Console.Error.WriteLine($"No Cobertura coverage reports were found under {coverageDirectory}.");
    return 2;
}

var files = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
foreach (var report in reports)
{
    var document = XDocument.Load(report);
    var sourceRoot = (string?)document.Descendants("source").FirstOrDefault();
    foreach (var classElement in document.Descendants("class"))
    {
        var filename = NormalizePath(sourceRoot, (string?)classElement.Attribute("filename"));
        if (filename is null)
        {
            continue;
        }

        var file = GetOrAdd(files, filename);
        MergeLines(file, classElement.Element("lines")?.Elements("line") ?? []);
        foreach (var method in classElement.Element("methods")?.Elements("method") ?? [])
        {
            var methodKey = string.Join(
                "|",
                (string?)classElement.Attribute("name") ?? "",
                (string?)method.Attribute("name") ?? "",
                (string?)method.Attribute("signature") ?? "");
            var covered = method
                .Element("lines")?
                .Elements("line")
                .Any(line => ParseInt(line.Attribute("hits")) > 0) == true;
            file.Methods[methodKey] = file.Methods.GetValueOrDefault(methodKey) || covered;
        }
    }
}

var failed = false;
foreach (var (filename, coverage) in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
{
    var lines = Percentage(coverage.Lines.Values.Count(value => value > 0), coverage.Lines.Count);
    var branches = Percentage(
        coverage.Branches.Values.Sum(value => value.Covered),
        coverage.Branches.Values.Sum(value => value.Total));
    var methods = Percentage(coverage.Methods.Values.Count(value => value), coverage.Methods.Count);
    var metrics = new[]
    {
        ("lines/statements", lines),
        ("branches (informational)", branches),
        ("methods (informational)", methods),
    };
    var fileFailed = lines + 0.000001 < minimum;
    failed |= fileFailed;
    Console.WriteLine(
        "{0} {1}: {2}",
        fileFailed ? "FAIL" : "PASS",
        filename,
        string.Join(", ", metrics.Select(metric => $"{metric.Item1} {metric.Item2:F2}%")));
}

if (failed)
{
    Console.Error.WriteLine($"Every production C# file must have at least {minimum:F2}% line/statement coverage.");
    return 1;
}

Console.WriteLine($"Coverage gate passed for {files.Count} production C# files across {reports.Length} test reports.");
return 0;

static FileCoverage GetOrAdd(Dictionary<string, FileCoverage> files, string filename)
{
    if (!files.TryGetValue(filename, out var coverage))
    {
        coverage = new FileCoverage();
        files.Add(filename, coverage);
    }
    return coverage;
}

static void MergeLines(FileCoverage file, IEnumerable<XElement> lines)
{
    foreach (var line in lines)
    {
        var number = ParseInt(line.Attribute("number"));
        file.Lines[number] = Math.Max(file.Lines.GetValueOrDefault(number), ParseInt(line.Attribute("hits")));
        var conditionCoverage = (string?)line.Attribute("condition-coverage");
        if (conditionCoverage is null)
        {
            continue;
        }
        var match = Regex.Match(conditionCoverage, @"\((\d+)/(\d+)\)");
        if (!match.Success)
        {
            continue;
        }
        var branch = new BranchCoverage(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        var existing = file.Branches.GetValueOrDefault(number);
        file.Branches[number] = new BranchCoverage(
            Math.Max(existing.Covered, branch.Covered),
            Math.Max(existing.Total, branch.Total));
    }
}

static int ParseInt(XAttribute? attribute) =>
    int.TryParse((string?)attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : 0;

static double Percentage(int covered, int total) => total == 0 ? 100d : covered * 100d / total;

static string? NormalizePath(string? sourceRoot, string? filename)
{
    if (string.IsNullOrWhiteSpace(filename))
    {
        return null;
    }
    var normalizedSource = sourceRoot?.Replace('\\', '/').TrimEnd('/') ?? "";
    var normalizedFilename = filename.Replace('\\', '/').TrimStart('/');
    var sourceDirectory = normalizedSource.LastIndexOf("/src", StringComparison.Ordinal);
    var sourcePrefix = sourceDirectory >= 0
        ? normalizedSource[(sourceDirectory + "/src".Length)..].Trim('/')
        : "";
    var normalized = string.IsNullOrEmpty(sourcePrefix)
        ? normalizedFilename
        : $"{sourcePrefix}/{normalizedFilename}";
    return normalized.Contains("/obj/", StringComparison.Ordinal)
        || normalized.EndsWith(".g.cs", StringComparison.Ordinal)
        || normalized.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal)
        ? null
        : normalized;
}

sealed class FileCoverage
{
    public Dictionary<int, int> Lines { get; } = [];
    public Dictionary<int, BranchCoverage> Branches { get; } = [];
    public Dictionary<string, bool> Methods { get; } = new(StringComparer.Ordinal);
}

readonly record struct BranchCoverage(int Covered, int Total);
