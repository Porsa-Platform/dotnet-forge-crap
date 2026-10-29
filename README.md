# dotnet-forge-crap

**CRAP** (Change Risk Anti-Pattern) analysis for C# projects.

Combines cyclomatic complexity with test coverage to identify methods that are both complex and under-tested — the riskiest code to change.

## Quick Start

```bash
# Analyze source with auto-detected test project
dotnet scripts/crap4cs.cs --

# Analyze with an existing coverage file
dotnet scripts/crap4cs.cs -- --coverage path/to/coverage.cobertura.xml

# Merge every coverage file of a multi-project solution
dotnet scripts/crap4cs.cs -- --coverage "TestResults/**/coverage.cobertura.xml"

# Analyze specific source paths
dotnet scripts/crap4cs.cs -- src/MyApp src/MyLib

# Set a custom threshold
dotnet scripts/crap4cs.cs -- --threshold 25

# JSON output
dotnet scripts/crap4cs.cs -- --json
```

## How It Works

crap4cs works in three steps:

1. **Parse source files** — uses Roslyn to extract every method, constructor, property accessor, and local function, computing cyclomatic complexity for each.
2. **Get coverage data** — either from Cobertura XML files (`--coverage`, merged when several are given) or by auto-detecting a test project and running `dotnet test --collect:"XPlat Code Coverage"`.
3. **Compute CRAP scores** — matches coverage data to methods by file and line range, then applies the CRAP formula.

## CRAP Formula

```
CRAP(m) = CC² × (1 - cov)³ + CC
```

- **CC** = cyclomatic complexity (decision points + 1)
- **cov** = line coverage fraction (0.0 to 1.0)

### Score Interpretation

| Score | Risk                                            |
| ----- | ----------------------------------------------- |
| 1–5   | Low — clean code                                |
| 5–30  | Moderate — consider refactoring or adding tests |
| 30+   | High — complex and under-tested                 |

### What Counts Toward Complexity

Each of these adds 1 to cyclomatic complexity:

- `if` / `else if`
- `for`, `foreach`, `while`, `do` loops
- `case` labels (each)
- `catch` clauses
- `&&`, `||` (conditional logical operators)
- `?:` (ternary conditional)
- `??` (null coalescing)
- `?.` (conditional access)

Base complexity is 1.

## CLI

```
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
  --fail-above     Exit with 2 when a method reaches the threshold (1 is kept
                   for usage and input errors).
  --format F       Output format: text or json (default text).
  --json           Short for --format json.
  --help           Show this help message.
```

If no source paths are given, defaults to `src`.

### Coverage Auto-Discovery

When `--coverage` is omitted, crap4cs:

1. Scans for `.csproj` files containing `Microsoft.NET.Test.Sdk`
2. Runs `dotnet test --collect:"XPlat Code Coverage"` on the first test project found
3. Finds the generated `coverage.cobertura.xml` in `TestResults/`

If no test project is found or tests fail, the report shows methods with `N/A` coverage.

## Output

### Text (default)

```
CRAP Report
===========
Method                         Class                                 CC    Cov%     CRAP
----------------------------------------------------------------------------------------
RiskyMethod                    MyApp.Processor                         14   35.0%   86.4
SimpleMethod                   MyApp.Util                               1  100.0%    1.0
```

### JSON

```json
{
	"crap_report": {
		"threshold": 30,
		"methods": [
			{
				"file": "/path/to/File.cs",
				"class_name": "MyApp.Processor",
				"method": "RiskyMethod",
				"start_line": 12,
				"end_line": 40,
				"complexity": 14,
				"coverage": 0.35,
				"crap_score": 86.4
			}
		]
	}
}
```

## Dependencies

- [Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/) — Roslyn syntax tree analysis
- [Coverlet](https://github.com/coverlet-coverage/coverlet) — test coverage (via `dotnet test --collect:"XPlat Code Coverage"`)

## Tests

```bash
dotnet tests/test-crap4cs.cs --
```

## License

MIT
