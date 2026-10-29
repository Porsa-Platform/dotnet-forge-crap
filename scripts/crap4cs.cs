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
var runTests = true;
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

Dictionary<string, Dictionary<int, int>>? coverage = null;

if (coveragePatterns.Count > 0)
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

var files = GetFiles(sourcePaths).ToList();
var allEntries = new List<MethodEntry>();

foreach (var file in files)
{
    var text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithKind(SourceCodeKind.Regular));
    var root = tree.GetRoot();
    var entries = ExtractMethods(file, root, coverage);
    allEntries.AddRange(entries);
}

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
return 0;

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

static string NormalizePath(string path)
{
    try { return Path.GetFullPath(path).Replace('\\', '/'); }
    catch { return path.Replace('\\', '/'); }
}

static IEnumerable<string> GetFiles(IReadOnlyList<string> paths)
{
    foreach (var path in paths)
    {
        if (File.Exists(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            yield return Path.GetFullPath(path);
        else if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(
                path, "*.cs", SearchOption.AllDirectories))
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

static int RunTests(string testProjectDir)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = testProjectDir
    };
    psi.ArgumentList.Add("test");
    psi.ArgumentList.Add("--collect");
    psi.ArgumentList.Add("XPlat Code Coverage");

    using var proc = new Process { StartInfo = psi };
    proc.Start();
    proc.WaitForExit(120000);
    return proc.ExitCode;
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

sealed record MethodEntry(
    string File,
    string ClassName,
    string MethodName,
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
                NormalizePath(_filePath), className, node.Identifier.Text,
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
            NormalizePath(_filePath), className, name,
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

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).Replace('\\', '/'); }
        catch { return path.Replace('\\', '/'); }
    }

    private static double ComputeCrapScore(int complexity, double coverage)
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
