#!/usr/bin/env -S dotnet
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false
#:package Microsoft.CodeAnalysis.CSharp@4.14.0
#:package Microsoft.Extensions.FileSystemGlobbing@9.0.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.FileSystemGlobbing;

// Arguments and reports use a dot as decimal separator, whatever the system culture.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const double DefaultThreshold = 30.0;
const string DefaultFormat = "text";

var argsCopy = args.ToList();
var sourcePaths = new List<string>();
var coveragePatterns = new List<string>();
var excludePatterns = new List<string>();
var runTests = true;
var failAbove = false;
var changedSince = (string?)null;
var missingCoverage = "show";
var affectedSince = (string?)null;
var listAffected = false;
var testSettings = (string?)null;
var threshold = DefaultThreshold;
var format = DefaultFormat;

for (var i = 0; i < argsCopy.Count; i++)
{
    switch (argsCopy[i].ToLowerInvariant())
    {
        case "--help":
        case "-h":
            Console.WriteLine(Usage());
            return 0;

        case "--threshold":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--threshold requires a value"); return 1; }
            if (!double.TryParse(argsCopy[i], out threshold)) { Console.Error.WriteLine($"Invalid threshold: {argsCopy[i]}"); return 1; }
            break;

        case "--coverage":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--coverage requires a path"); return 1; }
            coveragePatterns.Add(argsCopy[i]);
            runTests = false;
            break;

        case "--exclude":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--exclude requires a glob pattern"); return 1; }
            excludePatterns.Add(argsCopy[i]);
            break;

        case "--changed-since":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--changed-since requires a git ref"); return 1; }
            changedSince = argsCopy[i];
            break;

        case "--missing-coverage":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--missing-coverage requires a mode"); return 1; }
            missingCoverage = argsCopy[i].ToLowerInvariant();
            if (missingCoverage is not ("show" or "zero" or "skip")) { Console.Error.WriteLine($"Unknown missing-coverage mode: {missingCoverage}"); return 1; }
            break;

        case "--affected-since":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--affected-since requires a git ref"); return 1; }
            affectedSince = argsCopy[i];
            break;

        case "--list-affected":
            listAffected = true;
            break;

        case "--test-settings":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--test-settings requires a path"); return 1; }
            testSettings = Path.GetFullPath(argsCopy[i]);
            break;

        case "--fail-above":
            failAbove = true;
            break;

        case "--format":
            if (++i >= argsCopy.Count) { Console.Error.WriteLine("--format requires a value"); return 1; }
            format = argsCopy[i].ToLowerInvariant();
            if (format is not ("text" or "json")) { Console.Error.WriteLine($"Unknown format: {format}"); return 1; }
            break;

        case "--json":
            format = "json";
            break;

        case "--text":
            format = "text";
            break;

        default:
            sourcePaths.Add(argsCopy[i]);
            break;
    }
}

if (sourcePaths.Count == 0)
    sourcePaths.Add("src");

// --affected-since judges the changed methods on the tests that can reach them.
changedSince ??= affectedSince;
Dictionary<string, HashSet<int>>? changedLines = null;
if (changedSince != null)
{
    changedLines = GetChangedLines(sourcePaths, changedSince);
    if (changedLines == null) return 1;
}

Dictionary<string, Dictionary<int, int>>? coverage = null;

if (affectedSince != null && coveragePatterns.Count == 0)
{
    var affectedTests = FindAffectedTestProjects(sourcePaths, changedLines!.Keys);
    if (listAffected)
    {
        foreach (var project in affectedTests)
            Console.WriteLine(Path.GetRelativePath(Directory.GetCurrentDirectory(), project));
        return 0;
    }

    coverage = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
    var resultsRoot = Path.Combine(Path.GetTempPath(), $"crap4cs-{Guid.NewGuid():N}");
    try
    {
        foreach (var project in affectedTests)
        {
            Console.Error.WriteLine($"Running {Path.GetFileNameWithoutExtension(project)} with coverage...");
            var resultsDir = Path.Combine(resultsRoot, Path.GetFileNameWithoutExtension(project));
            var testArgs = new List<string> { "test", project, "--collect", "XPlat Code Coverage", "--results-directory", resultsDir };
            if (testSettings != null) testArgs.AddRange(["--settings", testSettings]);

            var (testExit, testOutput, testError) = RunProcess("dotnet", Directory.GetCurrentDirectory(), [.. testArgs]);
            if (testExit != 0)
            {
                // Coverage of a failing run would hide the failure behind a score: stop here.
                Console.Error.WriteLine(testOutput);
                Console.Error.WriteLine(testError);
                Console.Error.WriteLine($"Tests failed in {project} (exit code {testExit}).");
                return 1;
            }

            foreach (var coverageFile in Directory.EnumerateFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories))
                MergeCobertura(coverageFile, coverage);
        }
    }
    finally
    {
        try { Directory.Delete(resultsRoot, true); } catch { }
    }
}
else if (coveragePatterns.Count > 0)
{
    coverage = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
    foreach (var pattern in coveragePatterns)
    {
        var coverageFiles = ResolveCoverageFiles(pattern);
        if (coverageFiles.Count == 0)
        { Console.Error.WriteLine($"Coverage file not found: {pattern}"); return 1; }

        foreach (var coverageFile in coverageFiles)
        {
            if (!MergeCobertura(coverageFile, coverage))
            { Console.Error.WriteLine($"Could not parse coverage file: {coverageFile}"); return 1; }
        }
    }
}
else if (runTests)
{
    var testDir = FindTestProject(sourcePaths);
    if (testDir != null)
    {
        var exitCode = RunTests(testDir);
        if (exitCode == 0)
        {
            var covFile = FindCoverageXml(testDir);
            if (covFile != null)
            {
                coverage = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
                if (!MergeCobertura(covFile, coverage))
                    coverage = null;
            }
            else
                Console.Error.WriteLine("Warning: tests passed but no coverage XML found in TestResults/.");
        }
        else
        {
            Console.Error.WriteLine($"Warning: dotnet test exited with code {exitCode}. Proceeding without coverage data.");
        }
    }
    else
    {
        Console.Error.WriteLine("Warning: no test project found. Proceeding without coverage data.");
    }
}

var files = GetFiles(sourcePaths, excludePatterns).ToList();
var allEntries = new List<MethodEntry>();

foreach (var file in files)
{
    var text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithKind(SourceCodeKind.Regular));
    var root = tree.GetRoot();
    var entries = ExtractMethods(file, root, coverage);
    allEntries.AddRange(entries);
}

// A method without coverage data belongs to code no test loads (or to lines Coverlet does not
// instrument): "zero" counts it as untested, "skip" hides it, "show" reports it as N/A.
if (missingCoverage == "zero")
{
    for (var i = 0; i < allEntries.Count; i++)
        if (allEntries[i].Coverage == null)
            allEntries[i] = allEntries[i] with { Coverage = 0.0, CrapScore = MemberExtractorWalker.ComputeCrapScore(allEntries[i].Complexity, 0.0) };
}
else if (missingCoverage == "skip")
{
    allEntries.RemoveAll(e => e.Coverage == null);
}

if (changedLines != null)
    allEntries.RemoveAll(e => !TouchesChangedLines(e, changedLines));

allEntries.Sort((a, b) =>
{
    var aScore = a.CrapScore;
    var bScore = b.CrapScore;
    var aHas = aScore.HasValue;
    var bHas = bScore.HasValue;
    if (aHas != bHas) return aHas ? -1 : 1;
    if (aHas) return -bScore!.Value.CompareTo(aScore!.Value);
    return string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal);
});

Console.WriteLine(FormatOutput(allEntries, threshold, format));

// 1 is kept for usage and input errors, so a caller can tell them apart from a risky method.
return failAbove && allEntries.Any(e => e.CrapScore >= threshold) ? 2 : 0;

// ---- helpers -----------------------------------------------------------

static string Usage() => """
CRAP4CS - Change Risk Anti-Pattern analysis for C#.

Usage: dotnet scripts/crap4cs.cs -- [options] [source-path ...]

Options:
  --threshold N    CRAP score threshold (default 30). Methods below are hidden.
  --coverage PATH  Cobertura XML coverage file, or a glob pattern such as
                   TestResults/**/coverage.cobertura.xml. Repeatable: every matching
                   file is merged, keeping the highest hit count of each line.
                   If omitted, auto-runs dotnet test with XPlat Code Coverage on the
                   first test project found.
  --exclude GLOB   Skip source files matching the pattern, relative to the source
                   path (e.g. **/Migrations/**, **/obj/**). Repeatable.
  --changed-since REF
                   Keep only the methods with a line added or modified since the
                   merge base of REF and HEAD (git diff REF...HEAD).
  --missing-coverage MODE
                   What to do with a method that has no coverage data: show it as
                   N/A (default), count it as untested with zero, or skip it.
  --affected-since REF
                   Run, with coverage, only the test projects that contain or reference
                   (transitively) a project changed since REF, then keep only the
                   changed methods, as --changed-since REF does.
  --list-affected  With --affected-since: print the selected test projects and exit.
  --test-settings PATH
                   .runsettings file passed to dotnet test by --affected-since.
  --fail-above     Exit with 2 when a method reaches the threshold (1 is kept
                   for usage and input errors).
  --format F       Output format: text or json (default text).
  --json           Short for --format json.
  --help           Show this help message.

If no source paths are given, defaults to 'src'.

CRAP formula: CRAP = CC^2 * (1 - cov)^3 + CC
  CC   = cyclomatic complexity (decision points + 1)
  cov  = line coverage fraction (0.0 to 1.0)

Score interpretation:
  1-5    Low risk
  5-30   Moderate risk - consider refactoring or adding tests
  30+    High risk - complex and under-tested code
""";

// A plain path is returned as is when it exists; a pattern with * or ? is matched from
// the longest leading directory that has no wildcard.
static List<string> ResolveCoverageFiles(string pattern)
{
    if (pattern.IndexOfAny(['*', '?']) < 0)
        return File.Exists(pattern) ? [Path.GetFullPath(pattern)] : [];

    var segments = pattern.Replace('\\', '/').Split('/');
    var wildcardIndex = Array.FindIndex(segments, s => s.IndexOfAny(['*', '?']) >= 0);
    var baseDir = string.Join('/', segments.Take(wildcardIndex));
    if (baseDir.Length == 0)
        baseDir = pattern.StartsWith('/') ? "/" : ".";
    if (!Directory.Exists(baseDir))
        return [];

    var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
    matcher.AddInclude(string.Join('/', segments.Skip(wildcardIndex)));
    return matcher.GetResultsInFullPath(baseDir).OrderBy(f => f, StringComparer.Ordinal).ToList();
}

// Adds the lines of a Cobertura file to the coverage map. A line can be listed several times,
// by several classes (async state machines, lambdas) or several reports: the highest hit count wins.
static bool MergeCobertura(string path, Dictionary<string, Dictionary<int, int>> result)
{
    XDocument doc;
    try { doc = XDocument.Load(path); }
    catch { return false; }
    var root = doc.Root;
    if (root == null) return false;

    // Cobertura's <class filename="..."> may be relative to <sources><source>, some XML
    // file's own location or the process cwd (dotnet's Coverlet writer emits e.g.
    // "Modules\Workspace\...\Foo.cs" alongside a <source>...\Src\</source> base). Resolving
    // filename alone with Path.GetFullPath silently resolves against cwd instead, so every
    // class permanently fails to match real source paths and every score reports as N/A.
    var sourceBase = root.Descendants("source").Select(s => s.Value?.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    foreach (var cls in root.Descendants("class"))
    {
        var filename = cls.Attribute("filename")?.Value;
        if (string.IsNullOrWhiteSpace(filename)) continue;

        var resolved = sourceBase is not null && !Path.IsPathRooted(filename)
            ? Path.Combine(sourceBase, filename)
            : filename;
        var fileKey = NormalizePath(resolved);
        if (!result.TryGetValue(fileKey, out var lineMap))
        {
            lineMap = new Dictionary<int, int>();
            result[fileKey] = lineMap;
        }

        foreach (var line in cls.Descendants("line"))
        {
            var numStr = line.Attribute("number")?.Value;
            var hitsStr = line.Attribute("hits")?.Value;
            if (numStr == null || hitsStr == null) continue;
            if (int.TryParse(numStr, out var num) && int.TryParse(hitsStr, out var hits))
                lineMap[num] = lineMap.TryGetValue(num, out var known) ? Math.Max(known, hits) : hits;
        }
    }

    return true;
}

static string NormalizePath(string path) => PathNormalizer.Normalize(path);

// Exclude patterns are matched against the path relative to the source directory, or to the
// current directory for a file given by name.
static IEnumerable<string> GetFiles(IReadOnlyList<string> paths, IReadOnlyList<string> excludePatterns)
{
    foreach (var path in paths)
    {
        if (File.Exists(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var excluded = new Matcher(StringComparison.OrdinalIgnoreCase);
            excluded.AddIncludePatterns(excludePatterns);
            if (!excluded.Match(Directory.GetCurrentDirectory(), Path.GetFullPath(path)).HasMatches)
                yield return Path.GetFullPath(path);
        }
        else if (Directory.Exists(path))
        {
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude("**/*.cs");
            matcher.AddExcludePatterns(excludePatterns);
            foreach (var file in matcher.GetResultsInFullPath(path).OrderBy(f => f, StringComparer.Ordinal))
                yield return Path.GetFullPath(file);
        }
    }
}

static List<MethodEntry> ExtractMethods(string filePath, SyntaxNode root, Dictionary<string, Dictionary<int, int>>? coverage)
{
    var entries = new List<MethodEntry>();
    ExtractMethodsFromNode(filePath, root, coverage, entries);
    return entries;
}

static void ExtractMethodsFromNode(string filePath, SyntaxNode node,
    Dictionary<string, Dictionary<int, int>>? coverage, List<MethodEntry> entries)
{
    foreach (var child in node.ChildNodes())
    {
        if (child is TypeDeclarationSyntax typeDecl)
        {
            var walker = new MemberExtractorWalker(filePath, coverage, entries);
            walker.Visit(typeDecl);
            ExtractMethodsFromNode(filePath, typeDecl, coverage, entries);
        }

        if (child is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            ExtractMethodsFromNode(filePath, child, coverage, entries);
    }
}

// Lines added or modified since the merge base of gitRef and HEAD, by file. A pure deletion
// marks the line that follows it, so the method it was removed from still counts as changed.
static Dictionary<string, HashSet<int>>? GetChangedLines(IReadOnlyList<string> sourcePaths, string gitRef)
{
    var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
    var dirs = sourcePaths
        .Select(p => Path.GetFullPath(Directory.Exists(p) ? p : Path.GetDirectoryName(Path.GetFullPath(p)) ?? "."))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var dir in dirs)
    {
        // --relative keeps paths relative to dir, which avoids comparing a symlinked temp path
        // with the real path git reports for the repository root.
        var (exitCode, output, error) = RunProcess("git", dir, "diff", "-U0", "--no-color", "--no-ext-diff", "--relative", $"{gitRef}...HEAD");
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"git diff against '{gitRef}' failed: {error.Trim()}");
            return null;
        }

        HashSet<int>? lines = null;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("+++ "))
            {
                var target = line[4..].Trim();
                lines = target == "/dev/null" ? null : GetOrAdd(result, NormalizePath(Path.Combine(dir, target.StartsWith("b/") ? target[2..] : target)));
            }
            else if (line.StartsWith("@@") && lines != null)
            {
                // @@ -old[,count] +new[,count] @@
                var added = line.Split(' ')[2].TrimStart('+').Split(',');
                var start = int.Parse(added[0]);
                var count = added.Length > 1 ? int.Parse(added[1]) : 1;
                if (count == 0)
                    lines.Add(start + 1);
                for (var n = start; n < start + count; n++)
                    lines.Add(n);
            }
        }
    }

    return result;

    static HashSet<int> GetOrAdd(Dictionary<string, HashSet<int>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = [];
        return set;
    }
}

static bool TouchesChangedLines(MethodEntry entry, Dictionary<string, HashSet<int>> changedLines) =>
    changedLines.TryGetValue(entry.File, out var lines)
    && lines.Any(l => l >= entry.StartLine && l <= entry.EndLine);

// Reads both streams while the process runs, so a chatty process cannot fill a pipe and hang.
static (int ExitCode, string Output, string Error) RunProcess(string fileName, string workingDirectory, params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var arg in args) psi.ArgumentList.Add(arg);

    using var proc = Process.Start(psi)!;
    var error = proc.StandardError.ReadToEndAsync();
    var output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    return (proc.ExitCode, output, error.Result);
}

static string FormatOutput(IReadOnlyList<MethodEntry> entries, double threshold, string format)
{
    return format.ToLowerInvariant() switch
    {
        "json" => FormatJson(entries, threshold),
        _ => FormatText(entries, threshold)
    };
}

static string FormatText(IReadOnlyList<MethodEntry> entries, double threshold)
{
    var shown = entries.Where(e => e.CrapScore == null || e.CrapScore.Value >= threshold).ToList();
    if (shown.Count == 0)
        return "No methods above the CRAP threshold.\n";

    var sb = new StringBuilder();
    sb.AppendLine("CRAP Report");
    sb.AppendLine("===========");

    var header = string.Format("{0,-30} {1,-35} {2,4} {3,7} {4,8}",
        "Method", "Class", "CC", "Cov%", "CRAP");
    sb.AppendLine(header);
    sb.AppendLine(new string('-', header.Length));

    foreach (var e in shown)
    {
        var covStr = e.Coverage.HasValue
            ? $"{e.Coverage.Value * 100,5:F1}%"
            : "  N/A ";
        var crapStr = e.CrapScore.HasValue
            ? $"{e.CrapScore.Value,8:F1}"
            : "     N/A";
        sb.AppendLine(string.Format("{0,-30} {1,-35} {2,4} {3,7} {4,8}",
            Truncate(e.MethodName, 30),
            Truncate(e.ClassName, 35),
            e.Complexity,
            covStr,
            crapStr));
    }
    return sb.ToString();
}

static string Truncate(string s, int maxLen) =>
    s.Length <= maxLen ? s : s[..(maxLen - 1)] + "\u2026";

static string FormatJson(IReadOnlyList<MethodEntry> entries, double threshold)
{
    var shown = entries.Where(e => e.CrapScore == null || e.CrapScore.Value >= threshold).ToList();
    var output = new
    {
        crap_report = new
        {
            threshold,
            methods = shown.Select(e => new
            {
                file = e.File,
                class_name = e.ClassName,
                method = e.MethodName,
                start_line = e.StartLine,
                end_line = e.EndLine,
                complexity = e.Complexity,
                coverage = e.Coverage,
                crap_score = e.CrapScore
            }).ToList()
        }
    };
    return JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
}

static string? FindTestProject(IReadOnlyList<string> sourcePaths)
{
    var searchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in sourcePaths)
    {
        var dir = Directory.Exists(p) ? Path.GetFullPath(p) : Path.GetDirectoryName(Path.GetFullPath(p));
        if (dir != null) { searchDirs.Add(dir); searchDirs.Add(Directory.GetCurrentDirectory()); }
    }

    foreach (var dir in searchDirs)
    {
        var proj = FindTestProjectInDir(dir);
        if (proj != null) return proj;
    }

    var cwd = Directory.GetCurrentDirectory();
    if (!searchDirs.Contains(cwd))
    {
        var proj = FindTestProjectInDir(cwd);
        if (proj != null) return proj;
    }

    return null;
}

static string? FindTestProjectInDir(string dir)
{
    if (!Directory.Exists(dir)) return null;
    foreach (var csproj in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
    {
        try
        {
            var content = File.ReadAllText(csproj);
            if (content.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.GetFullPath(csproj));
        }
        catch { }
    }
    return null;
}

static int RunTests(string testProjectDir) =>
    RunProcess("dotnet", testProjectDir, "test", "--collect", "XPlat Code Coverage").ExitCode;

// Test projects (Microsoft.NET.Test.Sdk) that contain a changed file or reference, directly or
// through other projects, a project that does. Projects under bin/ or obj/ are ignored.
static List<string> FindAffectedTestProjects(IReadOnlyList<string> sourcePaths, IEnumerable<string> changedFiles)
{
    var projects = sourcePaths
        .Where(Directory.Exists)
        .SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
        .Select(NormalizePath)
        .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(p => p, ReadProject, StringComparer.OrdinalIgnoreCase);

    var changedProjects = changedFiles
        .Select(file => projects.Keys
            .Where(p => file.StartsWith(Path.GetDirectoryName(p)!.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault())
        .OfType<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return projects
        .Where(p => p.Value.IsTest && Closure(p.Key).Overlaps(changedProjects))
        .Select(p => p.Key)
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();

    HashSet<string> Closure(string project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current) || !projects.TryGetValue(current, out var info)) continue;
            foreach (var reference in info.References) stack.Push(reference);
        }
        return seen;
    }

    static (bool IsTest, List<string> References) ReadProject(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var dir = Path.GetDirectoryName(path)!;
            var references = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(include => NormalizePath(Path.Combine(dir, include.Replace('\\', '/'))))
                .ToList();
            var isTest = doc.Descendants()
                .Any(e => e.Name.LocalName == "PackageReference"
                    && string.Equals(e.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
            return (isTest, references);
        }
        catch { return (false, []); }
    }
}

static string? FindCoverageXml(string testProjectDir)
{
    var searchRoots = new[]
    {
        testProjectDir,
        Directory.GetCurrentDirectory(),
        Path.Combine(testProjectDir, "TestResults")
    };

    foreach (var root in searchRoots)
    {
        if (!Directory.Exists(root)) continue;
        try
        {
            var files = Directory.EnumerateFiles(root, "coverage.cobertura.xml", SearchOption.AllDirectories)
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();
            if (files.Count > 0) return files[0];
        }
        catch { }
    }
    return null;
}

// ---- types ---------------------------------------------------------------

// Full path with forward slashes and symbolic links resolved. Coverlet writes real paths, so a
// source read through a link (macOS /var -> /private/var, a linked checkout) would match nothing.
static class PathNormalizer
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Normalize(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path.Replace('\\', '/'); }

        if (Cache.TryGetValue(full, out var known))
            return known;

        var resolved = ResolveLinks(full).Replace('\\', '/');
        Cache[full] = resolved;
        return resolved;
    }

    private static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                // The target can itself go through a link (link -> /var/... with /var -> /private/var).
                if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = ResolveLinks(target.FullName);
            }
            catch { }
        }
        return current;
    }
}

sealed record MethodEntry(
    string File,
    string ClassName,
    string MethodName,
    int StartLine,
    int EndLine,
    int Complexity,
    double? Coverage,
    double? CrapScore
);

sealed class MemberExtractorWalker : CSharpSyntaxWalker
{
    private readonly string _filePath;
    private readonly Dictionary<string, Dictionary<int, int>>? _coverage;
    private readonly List<MethodEntry> _entries;

    public MemberExtractorWalker(string filePath, Dictionary<string, Dictionary<int, int>>? coverage, List<MethodEntry> entries)
    {
        _filePath = filePath;
        _coverage = coverage;
        _entries = entries;
    }

    private TypeDeclarationSyntax? _currentType;
    private int _depth;

    public override void Visit(SyntaxNode? node)
    {
        if (node == null) return;
        if (node is TypeDeclarationSyntax typeDecl)
        {
            var prev = _currentType;
            _currentType = typeDecl;
            _depth++;
            base.Visit(typeDecl);
            _depth--;
            _currentType = prev;
            return;
        }
        base.Visit(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (_depth == 1 && HasBody(node) && _currentType != null)
            AddEntry(node, node.Identifier.Text);
        base.VisitMethodDeclaration(node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (_depth == 1 && HasBody(node) && _currentType != null)
            AddEntry(node, node.Identifier.Text);
        base.VisitConstructorDeclaration(node);
    }

    public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        if (_depth == 1 && HasBody(node) && _currentType != null)
            AddEntry(node, $"~{node.Identifier.Text}");
        base.VisitDestructorDeclaration(node);
    }

    public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        if (_depth == 1 && HasBody(node) && _currentType != null)
            AddEntry(node, $"operator {node.OperatorToken.Text}");
        base.VisitOperatorDeclaration(node);
    }

    public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        if (_depth == 1 && HasBody(node) && _currentType != null)
            AddEntry(node, $"conversion operator ({node.ImplicitOrExplicitKeyword.Text})");
        base.VisitConversionOperatorDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (_currentType != null && node.AccessorList != null)
        {
            foreach (var acc in node.AccessorList.Accessors)
                if (HasBody(acc))
                    AddEntry(acc, $"{node.Identifier.Text}.{acc.Keyword.Text}");
        }
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        if (_currentType != null && node.AccessorList != null)
        {
            foreach (var acc in node.AccessorList.Accessors)
                if (HasBody(acc))
                    AddEntry(acc, $"this[].{acc.Keyword.Text}");
        }
        base.VisitIndexerDeclaration(node);
    }

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (HasBody(node) && _currentType != null)
        {
            var span = node.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var endLine = span.EndLinePosition.Line + 1;
            var complexity = ComputeComplexity(node);
            var cov = ComputeCoverage(_filePath, startLine, endLine);
            var className = GetFullClassName(_currentType);
            _entries.Add(new MethodEntry(
                NormalizePath(_filePath), className, node.Identifier.Text, startLine, endLine,
                complexity, cov, cov.HasValue ? ComputeCrapScore(complexity, cov.Value) : null));
        }
        base.VisitLocalFunctionStatement(node);
    }

    private void AddEntry(SyntaxNode member, string name)
    {
        if (_currentType == null) return;
        var span = member.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var complexity = ComputeComplexity(member);
        var className = GetFullClassName(_currentType);
        var cov = ComputeCoverage(_filePath, startLine, endLine);
        _entries.Add(new MethodEntry(
            NormalizePath(_filePath), className, name, startLine, endLine,
            complexity, cov, cov.HasValue ? ComputeCrapScore(complexity, cov.Value) : null));
    }

    private static bool HasBody(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax m => m.Body != null || m.ExpressionBody != null,
        ConstructorDeclarationSyntax c => c.Body != null || c.ExpressionBody != null,
        DestructorDeclarationSyntax d => d.Body != null || d.ExpressionBody != null,
        OperatorDeclarationSyntax o => o.Body != null || o.ExpressionBody != null,
        ConversionOperatorDeclarationSyntax c => c.Body != null || c.ExpressionBody != null,
        AccessorDeclarationSyntax a => a.Body != null || a.ExpressionBody != null,
        LocalFunctionStatementSyntax l => l.Body != null || l.ExpressionBody != null,
        _ => false
    };

    private static int ComputeComplexity(SyntaxNode node)
    {
        var walker = new ComplexityWalker();
        walker.Visit(node);
        return walker.Complexity;
    }

    private double? ComputeCoverage(string filePath, int startLine, int endLine)
    {
        if (_coverage == null) return null;
        var normalized = NormalizePath(filePath);
        if (!_coverage.TryGetValue(normalized, out var lineMap)) return null;

        var covered = 0;
        var total = 0;
        for (var line = startLine; line <= endLine; line++)
        {
            if (lineMap.TryGetValue(line, out var hits))
            {
                total++;
                if (hits > 0) covered++;
            }
        }
        return total == 0 ? null : (double)covered / total;
    }

    private static string GetFullClassName(TypeDeclarationSyntax typeDecl)
    {
        var parts = new List<string> { typeDecl.Identifier.Text };
        var current = typeDecl.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax parentType)
                parts.Insert(0, parentType.Identifier.Text);
            current = current.Parent;
        }
        return string.Join(".", parts);
    }

    private static string NormalizePath(string path) => PathNormalizer.Normalize(path);

    internal static double ComputeCrapScore(int complexity, double coverage)
    {
        var cc = (double)complexity;
        var uncov = 1.0 - coverage;
        return cc * cc * uncov * uncov * uncov + cc;
    }
}

sealed class ComplexityWalker : CSharpSyntaxWalker
{
    public int Complexity = 1;

    public override void VisitIfStatement(IfStatementSyntax node)
    {
        Complexity++;
        base.VisitIfStatement(node);
    }

    public override void VisitForStatement(ForStatementSyntax node)
    {
        Complexity++;
        base.VisitForStatement(node);
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        Complexity++;
        base.VisitForEachStatement(node);
    }

    public override void VisitWhileStatement(WhileStatementSyntax node)
    {
        Complexity++;
        base.VisitWhileStatement(node);
    }

    public override void VisitDoStatement(DoStatementSyntax node)
    {
        Complexity++;
        base.VisitDoStatement(node);
    }

    public override void VisitCasePatternSwitchLabel(CasePatternSwitchLabelSyntax node)
    {
        Complexity++;
        base.VisitCasePatternSwitchLabel(node);
    }

    public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node)
    {
        Complexity++;
        base.VisitCaseSwitchLabel(node);
    }

    public override void VisitCatchClause(CatchClauseSyntax node)
    {
        Complexity++;
        base.VisitCatchClause(node);
    }

    public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        Complexity++;
        base.VisitConditionalExpression(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.LogicalAndExpression) ||
            node.IsKind(SyntaxKind.LogicalOrExpression) ||
            node.IsKind(SyntaxKind.CoalesceExpression))
            Complexity++;
        base.VisitBinaryExpression(node);
    }

    public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        Complexity++;
        base.VisitConditionalAccessExpression(node);
    }
}
