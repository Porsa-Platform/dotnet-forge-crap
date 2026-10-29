#!/usr/bin/env -S dotnet
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property PublishAot=false

// Tests for scripts/crap4cs.cs
// Usage: dotnet tests/test-crap4cs.cs --

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

var passed = 0;
var failed = 0;

Run("high crasp for complex, untested method", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "calc.cs", """
class Calc {
    int Score(int a, int b, int c) {
        int r = 0;
        if (a > 0) { r += a; }
        if (b > 0) { r += b; if (b > 10) r *= 2; }
        if (c > 0) { r += c; for (int i = 0; i < c; i++) r += i; }
        return r;
    }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "calc.cs", new[] { (4, 0), (5, 0), (7, 0), (8, 0), (9, 0), (10, 0) });
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "1", dir);
        AssertExit(0, exit);
        AssertContains("CRAP Report", stdout);
        AssertContains("Score", stdout);
        AssertContains("Calc", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("crap score 1 for simple method with full coverage", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", """
class Util {
    int Add(int a, int b) {
        return a + b;
    }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "simple.cs", new[] { (3, 1) });
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "0", dir);
        AssertExit(0, exit);
        AssertContains("CC", stdout);
        AssertContains("100.0%", stdout);
        AssertContains("1.0", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("excludes methods below threshold", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", """
class Util {
    int Add(int a, int b) {
        return a + b;
    }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "simple.cs", new[] { (3, 1) });
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "999", dir);
        AssertExit(0, exit);
        AssertContains("No methods above the CRAP threshold.", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("outputs JSON with --json flag", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "test.cs", """
class C {
    int Simple(int x) { return x + 1; }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "test.cs", new[] { (2, 1) });
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--json", "--threshold", "0", dir);
        AssertExit(0, exit);
        var doc = JsonDocument.Parse(stdout);
        var methods = doc.RootElement.GetProperty("crap_report").GetProperty("methods").GetArrayLength();
        AssertEq(1, methods);
    }
    finally { TryDeleteDir(dir); }
});

Run("handles empty directories without error", () =>
{
    var dir = PrepDir();
    try
    {
        var (exit, stdout) = RunScript("--threshold", "0", dir);
        AssertExit(0, exit);
    }
    finally { TryDeleteDir(dir); }
});

Run("exits with 1 on invalid threshold", () =>
{
    var (exit, _) = RunScript("--threshold", "not-a-number");
    AssertExit(1, exit);
});

Run("exits with 1 on missing coverage file", () =>
{
    var (exit, _) = RunScript("--coverage", "nonexistent.xml");
    AssertExit(1, exit);
});

Run("reports N/A for methods without coverage data", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "test.cs", """
class C {
    int Foo(int x) { if (x > 0) return 1; return 0; }
}
""");
        var (exit, stdout) = RunScript("--threshold", "1", dir);
        AssertExit(0, exit);
        AssertContains("N/A", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("computes CC=1 for method with no decision points", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "test.cs", """
class C {
    int Foo() { return 42; }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "test.cs", new[] { (2, 1) });
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--json", "--threshold", "0", dir);
        AssertExit(0, exit);
        var doc = JsonDocument.Parse(stdout);
        var complexity = doc.RootElement.GetProperty("crap_report").GetProperty("methods")[0].GetProperty("complexity").GetInt32();
        AssertEq(1, complexity);
    }
    finally { TryDeleteDir(dir); }
});

Run("detects local function in enclosing type", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "test.cs", """
class C {
    int Bar() {
        int Local() { return 1; }
        return Local();
    }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "test.cs", new[] { (2, 1), (3, 1), (4, 1) });
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "0", dir);
        AssertExit(0, exit);
        AssertContains("CC", stdout);
        AssertContains("Bar", stdout);
        AssertContains("Local", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("reads a decimal threshold whatever the system culture", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", """
class Util {
    int Add(int a, int b) {
        return a + b;
    }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "simple.cs", new[] { (3, 1) });
        var (exit, stdout) = RunScriptWithCulture("fr_FR.UTF-8", "--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "0.5", dir);
        AssertExit(0, exit);
        AssertContains("Add", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("prints decimals with a dot whatever the system culture", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", """
class Util {
    int Add(int a, int b) {
        return a + b;
    }
}
""");
        WriteCoverageXml(dir, "coverage.xml", "simple.cs", new[] { (3, 1) });
        var (exit, stdout) = RunScriptWithCulture("fr_FR.UTF-8", "--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "0", dir);
        AssertExit(0, exit);
        AssertContains("100.0%", stdout);
        AssertContains("1.0", stdout);
    }
    finally { TryDeleteDir(dir); }
});

const string SimpleUtil = """
class Util {
    int Add(int a, int b) {
        return a + b;
    }
}
""";

Run("keeps the highest hit count when a line appears in several classes", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", SimpleUtil);
        // Coverlet lists the lines of an async method under its state machine class too.
        WriteCobertura(Path.Combine(dir, "coverage.xml"),
            (Path.Combine(dir, "simple.cs"), new[] { (3, 0) }),
            (Path.Combine(dir, "simple.cs"), new[] { (3, 4) }));
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "0", "--json", dir);
        AssertExit(0, exit);
        AssertEq(1.0, FirstMethod(stdout).GetProperty("coverage").GetDouble());
    }
    finally { TryDeleteDir(dir); }
});

Run("merges several coverage files given with --coverage", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", SimpleUtil);
        WriteCobertura(Path.Combine(dir, "unit.xml"), (Path.Combine(dir, "simple.cs"), new[] { (3, 1) }));
        WriteCobertura(Path.Combine(dir, "specs.xml"), (Path.Combine(dir, "simple.cs"), new[] { (3, 0) }));
        var (exit, stdout) = RunScript(
            "--coverage", Path.Combine(dir, "unit.xml"),
            "--coverage", Path.Combine(dir, "specs.xml"),
            "--threshold", "0", "--json", dir);
        AssertExit(0, exit);
        AssertEq(1.0, FirstMethod(stdout).GetProperty("coverage").GetDouble());
    }
    finally { TryDeleteDir(dir); }
});

Run("finds coverage files with a glob pattern", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", SimpleUtil);
        var results = Path.Combine(dir, "TestResults");
        Directory.CreateDirectory(Path.Combine(results, "a"));
        Directory.CreateDirectory(Path.Combine(results, "b"));
        WriteCobertura(Path.Combine(results, "a", "coverage.cobertura.xml"), (Path.Combine(dir, "simple.cs"), new[] { (3, 0) }));
        WriteCobertura(Path.Combine(results, "b", "coverage.cobertura.xml"), (Path.Combine(dir, "simple.cs"), new[] { (3, 2) }));
        var (exit, stdout) = RunScript("--coverage", Path.Combine(results, "**", "coverage.cobertura.xml"), "--threshold", "0", "--json", dir);
        AssertExit(0, exit);
        AssertEq(1.0, FirstMethod(stdout).GetProperty("coverage").GetDouble());
    }
    finally { TryDeleteDir(dir); }
});

Run("exits with 1 when a coverage pattern matches no file", () =>
{
    var dir = PrepDir();
    try
    {
        var (exit, _) = RunScript("--coverage", Path.Combine(dir, "**", "coverage.cobertura.xml"), dir);
        AssertExit(1, exit);
    }
    finally { TryDeleteDir(dir); }
});

Run("skips source files matching --exclude", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "Keep.cs", "class Keep { int A() { return 1; } }");
        Directory.CreateDirectory(Path.Combine(dir, "Migrations"));
        WriteCs(dir, Path.Combine("Migrations", "Initial.cs"), "class Initial { int B() { return 1; } }");
        Directory.CreateDirectory(Path.Combine(dir, "obj", "Debug"));
        WriteCs(dir, Path.Combine("obj", "Debug", "Generated.cs"), "class Generated { int C() { return 1; } }");
        var (exit, stdout) = RunScript("--exclude", "**/Migrations/**", "--exclude", "**/obj/**", "--threshold", "0", "--json", dir);
        AssertExit(0, exit);
        var methods = JsonDocument.Parse(stdout).RootElement.GetProperty("crap_report").GetProperty("methods");
        AssertEq(1, methods.GetArrayLength());
        AssertEq("A", methods[0].GetProperty("method").GetString());
    }
    finally { TryDeleteDir(dir); }
});

const string ComplexCalc = """
class Calc {
    int Score(int a, int b, int c) {
        int r = 0;
        if (a > 0) { r += a; }
        if (b > 0) { r += b; if (b > 10) r *= 2; }
        if (c > 0) { r += c; for (int i = 0; i < c; i++) r += i; }
        return r;
    }
}
""";

Run("exits with 2 when --fail-above is set and a method reaches the threshold", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "calc.cs", ComplexCalc);
        WriteCobertura(Path.Combine(dir, "coverage.xml"), (Path.Combine(dir, "calc.cs"), new[] { (4, 0), (5, 0), (6, 0), (7, 0), (8, 0) }));
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "30", "--fail-above", dir);
        AssertExit(2, exit);
        AssertContains("Score", stdout);
    }
    finally { TryDeleteDir(dir); }
});

Run("exits with 0 when --fail-above is set and every method is below the threshold", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "calc.cs", ComplexCalc);
        WriteCobertura(Path.Combine(dir, "coverage.xml"), (Path.Combine(dir, "calc.cs"), new[] { (4, 1), (5, 1), (6, 1), (7, 1), (8, 1) }));
        var (exit, _) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "30", "--fail-above", dir);
        AssertExit(0, exit);
    }
    finally { TryDeleteDir(dir); }
});

Run("still exits with 0 above the threshold without --fail-above", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "calc.cs", ComplexCalc);
        WriteCobertura(Path.Combine(dir, "coverage.xml"), (Path.Combine(dir, "calc.cs"), new[] { (4, 0), (5, 0), (6, 0), (7, 0), (8, 0) }));
        var (exit, _) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "30", dir);
        AssertExit(0, exit);
    }
    finally { TryDeleteDir(dir); }
});

Run("reports the start and end line of each method in JSON", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "simple.cs", SimpleUtil);
        var (exit, stdout) = RunScript("--threshold", "0", "--json", "--coverage", WriteEmptyCoverage(dir), dir);
        AssertExit(0, exit);
        var method = FirstMethod(stdout);
        AssertEq(2, method.GetProperty("start_line").GetInt32());
        AssertEq(4, method.GetProperty("end_line").GetInt32());
    }
    finally { TryDeleteDir(dir); }
});

Run("keeps only the methods changed since a git ref with --changed-since", () =>
{
    var dir = PrepDir();
    try
    {
        Git(dir, "init", "-q");
        WriteCs(dir, "Pair.cs", """
class Pair {
    int Left(int a) {
        return a;
    }
    int Right(int b) {
        return b;
    }
}
""");
        Git(dir, "add", ".");
        Git(dir, "commit", "-q", "-m", "initial");
        WriteCs(dir, "Pair.cs", """
class Pair {
    int Left(int a) {
        return a;
    }
    int Right(int b) {
        return b + 1;
    }
}
""");
        Git(dir, "commit", "-q", "-am", "change Right");
        var (exit, stdout) = RunScript("--changed-since", "HEAD~1", "--threshold", "0", "--json", "--coverage", WriteEmptyCoverage(dir), dir);
        AssertExit(0, exit);
        var methods = JsonDocument.Parse(stdout).RootElement.GetProperty("crap_report").GetProperty("methods");
        AssertEq(1, methods.GetArrayLength());
        AssertEq("Right", methods[0].GetProperty("method").GetString());
    }
    finally { TryDeleteDir(dir); }
});

Run("exits with 1 when --changed-since names an unknown ref", () =>
{
    var dir = PrepDir();
    try
    {
        Git(dir, "init", "-q");
        WriteCs(dir, "simple.cs", SimpleUtil);
        Git(dir, "add", ".");
        Git(dir, "commit", "-q", "-m", "initial");
        var (exit, _) = RunScript("--changed-since", "no-such-ref", "--coverage", WriteEmptyCoverage(dir), dir);
        AssertExit(1, exit);
    }
    finally { TryDeleteDir(dir); }
});

Run("counts a method missing from the coverage as untested with --missing-coverage zero", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "calc.cs", ComplexCalc);
        var (exit, stdout) = RunScript("--coverage", WriteEmptyCoverage(dir), "--missing-coverage", "zero", "--threshold", "30", "--fail-above", "--json", dir);
        AssertExit(2, exit);
        var method = FirstMethod(stdout);
        AssertEq(0.0, method.GetProperty("coverage").GetDouble());
        AssertEq(42.0, method.GetProperty("crap_score").GetDouble());
    }
    finally { TryDeleteDir(dir); }
});

Run("hides a method missing from the coverage with --missing-coverage skip", () =>
{
    var dir = PrepDir();
    try
    {
        WriteCs(dir, "calc.cs", ComplexCalc);
        var (exit, stdout) = RunScript("--coverage", WriteEmptyCoverage(dir), "--missing-coverage", "skip", "--threshold", "30", "--json", dir);
        AssertExit(0, exit);
        AssertEq(0, JsonDocument.Parse(stdout).RootElement.GetProperty("crap_report").GetProperty("methods").GetArrayLength());
    }
    finally { TryDeleteDir(dir); }
});

Run("exits with 1 on an unknown --missing-coverage mode", () =>
{
    var (exit, _) = RunScript("--missing-coverage", "maybe");
    AssertExit(1, exit);
});

Run("matches coverage written with the real path of a symlinked source directory", () =>
{
    var dir = PrepDir();
    try
    {
        var real = Path.Combine(dir, "real");
        Directory.CreateDirectory(real);
        WriteCs(real, "simple.cs", SimpleUtil);
        var link = Path.Combine(dir, "link");
        Directory.CreateSymbolicLink(link, real);
        // Coverlet writes the real path of the source, not the path it was read through.
        WriteCobertura(Path.Combine(dir, "coverage.xml"), (Path.Combine(real, "simple.cs"), new[] { (3, 1) }));
        var (exit, stdout) = RunScript("--coverage", Path.Combine(dir, "coverage.xml"), "--threshold", "0", "--json", link);
        AssertExit(0, exit);
        AssertEq(1.0, FirstMethod(stdout).GetProperty("coverage").GetDouble());
    }
    finally { TryDeleteDir(dir); }
});

// Core <- Lib <- Lib.Tests, Other <- Other.Tests, and Lib <- App.Specs (a test project
// without Microsoft.NET.Test.Sdk, like the specs that run on Microsoft.Testing.Platform).
static void WriteSolution(string dir)
{
    WriteProject(dir, "Core", isTest: false);
    WriteProject(dir, "Lib", isTest: false, "Core");
    WriteProject(dir, "Lib.Tests", isTest: true, "Lib");
    WriteProject(dir, "Other", isTest: false);
    WriteProject(dir, "Other.Tests", isTest: true, "Other");
    WriteProject(dir, "App.Specs", isTest: false, "Lib");
    foreach (var project in new[] { "Core", "Lib", "Lib.Tests", "Other", "Other.Tests", "App.Specs" })
        WriteCs(dir, Path.Combine(project, "Code.cs"), $"class {project.Replace(".", "")} {{ int A() {{ return 1; }} }}");
    Git(dir, "init", "-q");
    Git(dir, "add", ".");
    Git(dir, "commit", "-q", "-m", "initial");
}

static void ChangeAndCommit(string dir, string project)
{
    WriteCs(dir, Path.Combine(project, "Code.cs"), $"class {project.Replace(".", "")} {{ int A() {{ return 2; }} }}");
    Git(dir, "commit", "-q", "-am", $"change {project}");
}

static string[] ListAffected(string dir)
{
    var (exit, stdout) = RunScript("--affected-since", "HEAD~1", "--list-affected", dir);
    AssertExit(0, exit);
    return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Path.GetFileNameWithoutExtension).ToArray()!;
}

Run("selects the test projects that reference a changed project", () =>
{
    var dir = PrepDir();
    try
    {
        WriteSolution(dir);
        ChangeAndCommit(dir, "Lib");
        AssertEq("Lib.Tests", string.Join(",", ListAffected(dir)));
    }
    finally { TryDeleteDir(dir); }
});

Run("selects the test projects that reference a changed project transitively", () =>
{
    var dir = PrepDir();
    try
    {
        WriteSolution(dir);
        ChangeAndCommit(dir, "Core");
        AssertEq("Lib.Tests", string.Join(",", ListAffected(dir)));
    }
    finally { TryDeleteDir(dir); }
});

Run("selects a test project whose own code changed", () =>
{
    var dir = PrepDir();
    try
    {
        WriteSolution(dir);
        ChangeAndCommit(dir, "Other.Tests");
        AssertEq("Other.Tests", string.Join(",", ListAffected(dir)));
    }
    finally { TryDeleteDir(dir); }
});

Run("selects no test project when no referenced code changed", () =>
{
    var dir = PrepDir();
    try
    {
        WriteSolution(dir);
        ChangeAndCommit(dir, "App.Specs");
        AssertEq("", string.Join(",", ListAffected(dir)));
    }
    finally { TryDeleteDir(dir); }
});

// Lib.Calc.Add is tested by Lib.Tests; the second commit changes Add and adds an untested Risky method.
static void WriteTestedLibrary(string dir, string assertion)
{
    WriteProject(dir, "Lib", isTest: false);
    WriteProject(dir, "Lib.Tests", isTest: true, "Lib");
    WriteCs(dir, Path.Combine("Lib", "Calc.cs"), """
public class Calc {
    public int Add(int a, int b) {
        return a + b;
    }
}
""");
    WriteCs(dir, Path.Combine("Lib.Tests", "CalcTests.cs"), $$"""
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CalcTests {
    [TestMethod]
    public void Add_ReturnsTheSum() => {{assertion}};
}
""");
    Git(dir, "init", "-q");
    Git(dir, "add", ".");
    Git(dir, "commit", "-q", "-m", "initial");
    WriteCs(dir, Path.Combine("Lib", "Calc.cs"), """
public class Calc {
    public int Add(int a, int b) {
        return b + a;
    }
    public int Risky(int a, int b, int c) {
        int r = 0;
        if (a > 0) { r += a; }
        if (b > 0) { r += b; if (b > 10) r *= 2; }
        if (c > 0) { r += c; for (int i = 0; i < c; i++) r += i; }
        return r;
    }
}
""");
    Git(dir, "commit", "-q", "-am", "change Add, add Risky");
}

Run("runs the affected tests and scores the changed methods with their coverage", () =>
{
    var dir = PrepDir();
    try
    {
        WriteTestedLibrary(dir, "Assert.AreEqual(3, new Calc().Add(1, 2))");
        var (exit, stdout) = RunScript("--affected-since", "HEAD~1", "--missing-coverage", "zero", "--threshold", "0", "--json", dir);
        AssertExit(0, exit);
        var methods = JsonDocument.Parse(stdout).RootElement.GetProperty("crap_report").GetProperty("methods")
            .EnumerateArray().ToDictionary(m => m.GetProperty("method").GetString()!, m => m.GetProperty("coverage").GetDouble());
        AssertEq(1.0, methods["Add"]);
        AssertEq(0.0, methods["Risky"]);
    }
    finally { TryDeleteDir(dir); }
});

Run("exits with 1 when an affected test fails", () =>
{
    var dir = PrepDir();
    try
    {
        WriteTestedLibrary(dir, "Assert.AreEqual(4, new Calc().Add(1, 2))");
        var (exit, _) = RunScript("--affected-since", "HEAD~1", dir);
        AssertExit(1, exit);
    }
    finally { TryDeleteDir(dir); }
});

static Dictionary<string, int> ComplexityByMethod(string dir, string source)
{
    WriteCs(dir, "code.cs", source);
    var (exit, stdout) = RunScript("--coverage", WriteEmptyCoverage(dir), "--threshold", "0", "--json", dir);
    AssertExit(0, exit);
    return JsonDocument.Parse(stdout).RootElement.GetProperty("crap_report").GetProperty("methods")
        .EnumerateArray().ToDictionary(m => m.GetProperty("method").GetString()!, m => m.GetProperty("complexity").GetInt32());
}

Run("counts each switch expression arm except the discard", () =>
{
    var dir = PrepDir();
    try
    {
        var complexity = ComplexityByMethod(dir, """
class Codes {
    int Of(string s) => s switch
    {
        "a" => 1,
        "b" => 2,
        _ => 0
    };
}
""");
        AssertEq(3, complexity["Of"]);
    }
    finally { TryDeleteDir(dir); }
});

Run("counts the or and and patterns", () =>
{
    var dir = PrepDir();
    try
    {
        var complexity = ComplexityByMethod(dir, """
class Ranges {
    bool Small(int x) => x is 1 or 2 or 3;
    bool Between(int x) => x is > 0 and < 10;
}
""");
        AssertEq(3, complexity["Small"]);
        AssertEq(2, complexity["Between"]);
    }
    finally { TryDeleteDir(dir); }
});

Run("does not count a local function in the complexity of its method", () =>
{
    var dir = PrepDir();
    try
    {
        var complexity = ComplexityByMethod(dir, """
class Outer {
    int Run() {
        return Local(1);

        int Local(int a) {
            if (a > 0) return 1;
            return 0;
        }
    }
}
""");
        AssertEq(1, complexity["Run"]);
        AssertEq(2, complexity["Local"]);
    }
    finally { TryDeleteDir(dir); }
});

Console.WriteLine($"\nResult: {passed} passed, {failed} failed, {passed + failed} total");
if (failed > 0) Environment.Exit(1);

// --- helpers -----------------------------------------------------------

void Run(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}\n      {ex.Message}"); }
}

static string PrepDir()
{
    var dir = Path.Combine(Path.GetTempPath(), $"crap4cs-test-{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    return dir;
}

static void WriteCs(string dir, string name, string content)
{
    File.WriteAllText(Path.Combine(dir, name), content);
}

static void WriteCoverageXml(string dir, string covFileName, string sourceFileName, (int Line, int Hits)[] lines)
{
    var doc = new XDocument(
        new XElement("coverage",
            new XAttribute("line-rate", "0.5"),
            new XAttribute("branch-rate", "0.5"),
            new XAttribute("version", "1"),
            new XElement("packages",
                new XElement("package",
                    new XAttribute("name", "Test"),
                    new XElement("classes",
                        new XElement("class",
                            new XAttribute("name", "Test"),
                            new XAttribute("filename", Path.Combine(dir, sourceFileName)),
                            new XAttribute("line-rate", "0.5"),
                            new XElement("methods",
                                new XElement("method",
                                    new XAttribute("name", "TestMethod"),
                                    new XAttribute("signature", "()"),
                                    new XElement("lines",
                                        lines.Select(l =>
                                            new XElement("line",
                                                new XAttribute("number", l.Line),
                                                new XAttribute("hits", l.Hits),
                                                new XAttribute("branch", "false")
                                            )
                                        )
                                    )
                                )
                            ),
                            new XElement("lines",
                                lines.Select(l =>
                                    new XElement("line",
                                        new XAttribute("number", l.Line),
                                        new XAttribute("hits", l.Hits),
                                        new XAttribute("branch", "false")
                                    )
                                )
                            )
                        )
                    )
                )
            )
        )
    );
    doc.Save(Path.Combine(dir, covFileName));
}

static void WriteCobertura(string path, params (string File, (int Line, int Hits)[] Lines)[] classes)
{
    var doc = new XDocument(
        new XElement("coverage",
            new XElement("packages",
                new XElement("package",
                    new XAttribute("name", "Test"),
                    new XElement("classes",
                        classes.Select((c, i) =>
                            new XElement("class",
                                new XAttribute("name", $"Test{i}"),
                                new XAttribute("filename", c.File),
                                new XElement("lines",
                                    c.Lines.Select(l =>
                                        new XElement("line",
                                            new XAttribute("number", l.Line),
                                            new XAttribute("hits", l.Hits)))))))))));
    doc.Save(path);
}

static void WriteProject(string dir, string name, bool isTest, params string[] references)
{
    Directory.CreateDirectory(Path.Combine(dir, name));
    var packages = isTest ? """
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="MSTest" Version="3.8.3" />
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
""" : "";
    var refs = string.Concat(references.Select(r => $"    <ProjectReference Include=\"..\\{r}\\{r}.csproj\" />\n"));
    File.WriteAllText(Path.Combine(dir, name, $"{name}.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
{packages}{refs}  </ItemGroup>
</Project>
""");
}

// A coverage file listing no line, to keep the tests from running dotnet test.
static string WriteEmptyCoverage(string dir)
{
    var path = Path.Combine(dir, "empty-coverage.xml");
    WriteCobertura(path);
    return path;
}

static void Git(string dir, params string[] args)
{
    var psi = new ProcessStartInfo { FileName = "git", WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "Tests";
    psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "tests@example.com";
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = Process.Start(psi)!;
    var error = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed: {error}");
}

static JsonElement FirstMethod(string json) =>
    JsonDocument.Parse(json).RootElement.GetProperty("crap_report").GetProperty("methods")[0];

static (int ExitCode, string Stdout) RunScript(params string[] args) => RunScriptWithCulture(null, args);

static (int ExitCode, string Stdout) RunScriptWithCulture(string? culture, params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    if (culture is not null)
    {
        psi.Environment["LC_ALL"] = culture;
        psi.Environment["LANG"] = culture;
    }
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("scripts/crap4cs.cs");
    psi.ArgumentList.Add("--");
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = new Process { StartInfo = psi };
    proc.Start();
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit(60000);
    return (proc.ExitCode, stdout);
}

static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

static void AssertEq<T>(T expected, T actual, string? msg = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception(msg ?? $"expected {expected} but got {actual}");
}

static void AssertExit(int expected, int actual)
{
    if (expected != actual) throw new Exception($"expected exit code {expected} but got {actual}");
}

static void AssertContains(string needle, string haystack)
{
    if (!haystack.Contains(needle, StringComparison.Ordinal))
        throw new Exception($"expected to find '{needle}' in '{haystack}'");
}