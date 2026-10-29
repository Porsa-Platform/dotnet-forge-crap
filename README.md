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
- switch expression arms (each, except the `_` discard)
- `or` and `and` patterns
- `catch` clauses
- `&&`, `||` (conditional logical operators)
- `?:` (ternary conditional)
- `??` (null coalescing)
- `?.` (conditional access)

Base complexity is 1. A local function is reported on its own and does not add to the complexity of its enclosing method.

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
  --missing-coverage MODE
                   What to do with a method that has no coverage data: show it as
                   N/A (default), count it as untested with zero, or skip it. With
                   zero, a method of a file the report measures stays N/A with a
                   warning: the coverage tool left it out, the tests did not miss it.
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
```

If no source paths are given, defaults to `src`.

### Affected Tests

In a solution with many test projects, `--affected-since REF` judges a change on the tests that can reach it:

1. Lists the `.cs` files changed since the merge base of `REF` and `HEAD`.
2. Finds the project of each file (nearest `.csproj`), then every test project (`Microsoft.NET.Test.Sdk`) that contains it or references it, directly or through other projects.
3. Runs `dotnet test --collect "XPlat Code Coverage"` on each of them, and stops with exit code 1 if a test fails.
4. Merges their coverage and reports only the methods with a changed line.

```bash
# Before a push: block when a changed method is risky
dotnet scripts/crap4cs.cs -- Src --affected-since origin/main --missing-coverage zero \
  --exclude "**/Migrations/**" --exclude "**/obj/**" --fail-above

# See which test projects would run
dotnet scripts/crap4cs.cs -- Src --affected-since origin/main --list-affected
```

Test projects without `Microsoft.NET.Test.Sdk` (for example those on Microsoft.Testing.Platform only) are not run.

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

## Upgrading

Existing options and exit codes keep working, and the new ones are opt-in. Some results change, because they fix the way scores were computed:

- **Complexity**: switch expression arms (except `_`) and `or`/`and` patterns now count, and a local function no longer adds to its enclosing method. Scores of such methods move, and can cross the threshold either way.
- **Coverage**: a line listed several times keeps its highest hit count instead of the first one read, so some methods report more coverage.
- **Paths**: the `file` of a method is its real path, with symbolic links resolved (on macOS, `/tmp/...` is reported as `/private/tmp/...`).
- **Numbers**: the text report prints decimals with a dot whatever the system culture (`100.0%`, not `100,0%`). `--threshold` accepts a dot or a comma.
- **Coverage files**: a valid report that lists no class is accepted (methods show N/A) instead of failing with "Could not parse coverage file". A `--coverage` path with `*` or `?` is a glob pattern.
- **Auto-run**: the test run is no longer stopped after two minutes, which used to crash the script; a test run that never ends now blocks it.
- **JSON**: each method gets `start_line` and `end_line`.

## Dependencies

- [Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/) — Roslyn syntax tree analysis
- [Coverlet](https://github.com/coverlet-coverage/coverlet) — test coverage (via `dotnet test --collect:"XPlat Code Coverage"`)

## Tests

```bash
dotnet tests/test-crap4cs.cs --
```

## License

MIT
