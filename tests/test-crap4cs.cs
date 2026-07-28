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

static (int ExitCode, string Stdout) RunScript(params string[] args)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
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