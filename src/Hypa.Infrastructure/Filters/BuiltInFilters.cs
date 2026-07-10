using System.Text.RegularExpressions;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Infrastructure.Filters;

public static class BuiltInFilters
{
    public static readonly IReadOnlyList<CompiledFilterDefinition> All =
    [
        Compile(new FilterDefinition
        {
            Id = "ansi-strip",
            Description = "Strip ANSI escape sequences from output.",
            AppliesTo = [],
            Scope = FilterScope.BuiltIn,
            Stages = [new FilterStage { Kind = FilterStageKind.StripAnsi }],
        }),
        Compile(new FilterDefinition
        {
            Id = "dotnet-msbuild-noise",
            Description = "Strip ANSI and keep only MSBuild diagnostic lines from dotnet output.",
            AppliesTo = ["dotnet"],
            MatchCommand = @"^dotnet\s+build\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"0 Warning\(s\)\n\s+0 Error\(s\)",
                    Replacement = "ok (build succeeded)",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^Microsoft \(R\)|^Copyright \(C\)|^\s*Determining projects|^\s*Restored\s+",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "dotnet-test",
            Description = "Compact dotnet test output; keep pass/fail summary and failures.",
            AppliesTo = ["dotnet"],
            MatchCommand = @"^dotnet\s+test\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^Microsoft \(R\)|^Copyright|^\s*Determining projects",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.KeepLines,
                    Pattern = @"(?i)(failed|passed|skipped|error|exception|\bFail\b|\bPass\b|Test run|Total:|Duration:|\[FAIL\]|\[PASS\]|^\s+at )",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"(?i)(passed|Total:).*([1-9]\d*)",
                    Replacement = "dotnet test: ok (all passed)",
                    Guard = @"(?i)(failed|error|exception|\[FAIL\])",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "dotnet test: ok (all passed)" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "cargo",
            Description = "Compact cargo build and test output.",
            AppliesTo = ["cargo"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*Compiling |^\s*Downloading |^\s*Fetching |^\s*Updating |^\s*Blocking |^\s*Finished |^\s*Fresh |^\s*Locking |^\s*$",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = "test result: ok",
                    Replacement = "cargo test: ok (all passed)",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "cargo: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "npm-install",
            Description = "Compact npm/npx install and ci output.",
            AppliesTo = ["npm", "npx"],
            MatchCommand = @"^(npm|npx)\s+(install|ci|i)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"added \d+ packages?",
                    Replacement = "npm install: ok",
                    Guard = @"(?i)(error|failed|warn)",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^npm (warn|notice)|^\s*(WARN|notice)|^added \d+ package|^(Downloading|Fetching|Resolving|Linking|Auditing|Checking) |^\s*$",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "npm install: ok (no changes)" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "pnpm-install",
            Description = "Compact pnpm install output.",
            AppliesTo = ["pnpm"],
            MatchCommand = @"^pnpm\s+(install|i)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*Progress:|^Packages are hard linked|^\s*$",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "pnpm install: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "yarn-install",
            Description = "Compact yarn install output.",
            AppliesTo = ["yarn"],
            MatchCommand = @"^yarn\s+(install|add)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^yarn (warn|info)|^\[\d+/\d+\]|^Done in \d|^\s*$",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "yarn install: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "jest",
            Description = "Compact Jest and Vitest test output; keep failures and summary.",
            AppliesTo = ["jest", "vitest", "npx", "pnpm", "bunx", "bun"],
            MatchCommand = @"^((?:npx|pnpm|bunx|bun\s+(?:x|run))\s+)?(jest|vitest)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.KeepLines,
                    Pattern = "FAIL|PASS|\\u25cf|\\u2713|\\u2717|\\u00d7|FAILED|PASSED|Tests:|Test Suites:|Snapshots:|Time:|\\u2715|\\u2718|Expected|Received|^\\s+at |Error:",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"Tests:.*\d+ passed",
                    Replacement = "jest: ok (all passed)",
                    Guard = @"(?i)(fail|error|expected|received)",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "jest: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "pytest",
            Description = "Compact pytest output; keep failures and summary line.",
            AppliesTo = ["pytest", "python", "python3"],
            MatchCommand = @"^(pytest|python3?\s+-m\s+pytest)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^collecting |^platform |^rootdir:|^plugins:|^\s*$",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"\d+ passed",
                    Replacement = "pytest: ok (all passed)",
                    Guard = @"(?i)(failed|error|exception)",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "pytest: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "git-status",
            Description = "Compact git status output into branch and changed-file groups.",
            AppliesTo = ["git"],
            MatchCommand = @"^git\s+status\b",
            Scope = FilterScope.BuiltIn,
            Stages = [new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "git.status" }],
        }),
        Compile(new FilterDefinition
        {
            Id = "git-log",
            Description = "Compact git log output into one line per commit.",
            AppliesTo = ["git"],
            MatchCommand = @"^git\s+log\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "git.log" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 120 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "git-diff",
            Description = "Compact git diff output while preserving changed lines and limited context.",
            AppliesTo = ["git"],
            MatchCommand = @"^git\s+(diff|show)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "git.diff" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 300 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "go-test",
            Description = "Compact go test output into package and pass/fail summary.",
            AppliesTo = ["go"],
            MatchCommand = @"^go\s+test\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "test.go" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "rspec",
            Description = "Compact RSpec test output into the final example/failure summary.",
            AppliesTo = ["rspec", "bundle"],
            MatchCommand = @"^(bundle\s+exec\s+)?rspec\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "test.rspec" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "mocha",
            Description = "Compact Mocha test output into pass/fail summary.",
            AppliesTo = ["mocha", "npx", "pnpm", "bunx", "bun"],
            MatchCommand = @"^((?:npx|pnpm|bunx|bun\s+(?:x|run))\s+)?mocha\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "test.mocha" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "mypy",
            Description = "Compact mypy diagnostics by file, code, and first errors.",
            AppliesTo = ["mypy", "python", "python3"],
            MatchCommand = @"^(mypy|python3?\s+-m\s+mypy)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "typecheck.python" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "pyright",
            Description = "Compact pyright diagnostics by file, code, and first errors.",
            AppliesTo = ["pyright", "python", "python3"],
            MatchCommand = @"^(pyright|python3?\s+-m\s+pyright)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "typecheck.python" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "playwright",
            Description = "Compact Playwright test output and preserve failure artifact paths.",
            AppliesTo = ["playwright", "npx", "pnpm", "bunx", "bun"],
            MatchCommand = @"^((?:npx|pnpm|bunx|bun\s+(?:x|run))\s+)?playwright\s+test\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "playwright" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 100 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "eslint",
            Description = "Compact ESLint output; keep violations and summary.",
            AppliesTo = ["eslint"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "eslint: ok (no violations)" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "biome",
            Description = "Compact Biome lint and format output.",
            AppliesTo = ["biome"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^Checked \d+ file|^Fixed \d+ file|^The following command|^Run it with",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "biome: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "oxlint",
            Description = "Compact oxlint output.",
            AppliesTo = ["oxlint"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^Finished in|^Found \d+ warning|found \d+ warning",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "oxlint: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "docker",
            Description = "Compact docker build, ps, and logs output.",
            AppliesTo = ["docker"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^#\d+ \[|^#\d+ (CACHED|sha256)|^ => |^\s*---\s*$|^\s*$|^Use 'docker",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "docker: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "docker-build",
            Description = "Compact docker build output into step and error summary.",
            AppliesTo = ["docker"],
            MatchCommand = @"^docker\s+(build|buildx\s+build)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "docker.build" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "docker-logs",
            Description = "Compact docker logs output by stripping timestamps and folding repeats.",
            AppliesTo = ["docker"],
            MatchCommand = @"^docker\s+(compose\s+)?logs\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "docker.logs" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "gradle",
            Description = "Compact Gradle build output.",
            AppliesTo = ["gradle", "gradlew", "./gradlew"],
            MatchCommand = @"^(\./)?gradlew?\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^> Task .*UP-TO-DATE$|^> Task .*NO-SOURCE$|^> Task .*FROM-CACHE$|^\s*$|^Download |^Downloading\s+http|^\d+% CONFIGURING|^\d+% EXECUTING|^\d+% WAITING|^Parallel execution|^> Configuring project|^> Resolving dependencies|^> Transform |^Starting a Gradle Daemon|^Daemon will be stopped",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "gradle: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "mvn",
            Description = "Compact Maven build output.",
            AppliesTo = ["mvn", "mvnw", "./mvnw"],
            MatchCommand = @"^(\./)?mvnw?\s+(compile|package|clean|install)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\[INFO\] Download|^\[INFO\] Downloaded|^\[INFO\] --------|^\[INFO\] Building\s|^\[INFO\] Building jar|^\[INFO\]\s*$|^\[INFO\] --- |^Downloading:|^Downloaded:|^Progress \(\d+\)|^Progress",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "mvn: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "hadolint",
            Description = "Compact hadolint Dockerfile linting output.",
            AppliesTo = ["hadolint"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "shellcheck",
            Description = "Compact shellcheck output.",
            AppliesTo = ["shellcheck"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "yamllint",
            Description = "Compact yamllint output.",
            AppliesTo = ["yamllint"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "markdownlint",
            Description = "Compact markdownlint output.",
            AppliesTo = ["markdownlint"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "ansible-playbook",
            Description = "Compact ansible-playbook output.",
            AppliesTo = ["ansible-playbook", "ansible"],
            MatchCommand = @"^ansible-playbook\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^ok: \[|^skipping: \[",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "make",
            Description = "Compact make output.",
            AppliesTo = ["make"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^make\[\d+\]:|^\s*$|^Nothing to be done",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "make: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "gcc",
            Description = "Compact gcc/g++ compiler output.",
            AppliesTo = ["gcc", "g++", "cc"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s+\|\s*$|^In file included from|^\s+from\s|^\d+ warnings? generated|^\d+ errors? generated",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "gcc: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "helm",
            Description = "Compact helm output.",
            AppliesTo = ["helm"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$|^W\d{4}|^W " },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "kubectl",
            Description = "Compact kubectl output.",
            AppliesTo = ["kubectl"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "kubectl-logs",
            Description = "Compact kubectl logs output by stripping timestamps and folding repeats.",
            AppliesTo = ["kubectl"],
            MatchCommand = @"^kubectl\s+logs?\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "kubectl.logs" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "kubectl-describe",
            Description = "Compact kubectl describe output into section summaries and recent events.",
            AppliesTo = ["kubectl"],
            MatchCommand = @"^kubectl\s+describe\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "kubectl.describe" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 100 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "ping",
            Description = "Compact ping output; keep only summary.",
            AppliesTo = ["ping"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^PING |^Pinging |^\d+ bytes from |^Reply from .+: bytes=|^\s*$",
                },
                new FilterStage { Kind = FilterStageKind.TailLines, Count = 4 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "df",
            Description = "Compact df output.",
            AppliesTo = ["df"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 80 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 20 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "du",
            Description = "Compact du output.",
            AppliesTo = ["du"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "ps",
            Description = "Compact ps output.",
            AppliesTo = ["ps"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "gcloud",
            Description = "Compact gcloud output.",
            AppliesTo = ["gcloud"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "systemctl",
            Description = "Compact systemctl status output.",
            AppliesTo = ["systemctl"],
            MatchCommand = @"^systemctl\s+status\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 20 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "jq",
            Description = "Compact jq output.",
            AppliesTo = ["jq"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.StripLines, Pattern = @"^\s*$" },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "turbo",
            Description = "Compact Turborepo output.",
            AppliesTo = ["turbo"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s*cache (hit|miss|bypass)|^\s*\d+ packages in scope|^\s*Tasks:\s+\d+|^\s*Duration:\s+|^\s*Remote caching (enabled|disabled)",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "turbo: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "nx",
            Description = "Compact Nx monorepo output.",
            AppliesTo = ["nx", "pnpm"],
            MatchCommand = @"^(pnpm\s+)?nx\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s*>\s*NX\s+Running target|^\s*>\s*NX\s+Nx read the output|^\s*>\s*NX\s+View logs|^\u2014{3,}|^\s+Nx \(powered by",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "just",
            Description = "Compact just task runner output.",
            AppliesTo = ["just"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s*Available recipes:|^\s*just --list",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "task",
            Description = "Compact go-task output.",
            AppliesTo = ["task"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^task: \[.*\] |^task: Task .* is up to date",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "task: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "mise",
            Description = "Compact mise task runner output.",
            AppliesTo = ["mise"],
            MatchCommand = @"^mise\s+(run|exec|install|upgrade)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^mise\s+(trust|install|upgrade).*✓|^mise\s+Installing\s|^mise\s+Downloading\s|^mise\s+Extracting\s|^mise\s+\w+@[\d.]+ installed",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 150 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 50 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "mise: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "xcodebuild",
            Description = "Compact xcodebuild output.",
            AppliesTo = ["xcodebuild"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^CompileC\s|^CompileSwift\s|^Ld\s|^CreateBuildDirectory\s|^MkDir\s|^ProcessInfoPlistFile\s|^CopySwiftLibs\s|^CodeSign\s|^Signing Identity:|^RegisterWithLaunchServices|^Validate\s|^ProcessProductPackaging|^Touch\s|^LinkStoryboards|^CompileStoryboard|^CompileAssetCatalog|^GenerateDSYMFile|^PhaseScriptExecution|^PBXCp\s|^SetMode\s|^SetOwnerAndGroup\s|^Ditto\s|^CpResource\s|^CpHeader\s|^\s+cd\s+/|^\s+export\s|^\s+/Applications/Xcode|^\s+/usr/bin/|^\s+builtin-|^note: Using new build system",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 60 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "xcodebuild: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "cmake",
            Description = "Compact cmake configure/build output into errors, warnings, or success summary.",
            AppliesTo = ["cmake", "ctest"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "cmake" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "ninja",
            Description = "Compact ninja build output into progress, errors, and unique warnings.",
            AppliesTo = ["ninja"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "ninja" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 100 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "aws",
            Description = "Compact AWS CLI text and JSON responses.",
            AppliesTo = ["aws"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.NativeTransform, TransformId = "aws" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "yadm",
            Description = "Compact yadm output.",
            AppliesTo = ["yadm"],
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s*\(use ""git |^\s*\(use ""yadm ",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "terraform-plan",
            Description = "Compact Terraform plan output; strip refresh and state-lock noise.",
            AppliesTo = ["terraform"],
            MatchCommand = @"^terraform\s+plan\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^Refreshing state|^\s*Refreshing state|^\s*#.*(unchanged|has no changes)|^\s*$|^Acquiring state lock|^\s*Acquiring state lock|^Releasing state lock|^\s*Releasing state lock",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "terraform plan: no changes detected" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "tofu-plan",
            Description = "Compact OpenTofu plan output; strip refresh and state-lock noise.",
            AppliesTo = ["tofu"],
            MatchCommand = @"^tofu\s+plan(\s|$)",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^Refreshing state|^\s*Refreshing state|^\s*#.*(unchanged|has no changes)|^\s*$|^Acquiring state lock|^\s*Acquiring state lock|^Releasing state lock|^\s*Releasing state lock",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "tofu plan: no changes detected" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "tofu-init",
            Description = "Compact OpenTofu init output.",
            AppliesTo = ["tofu"],
            MatchCommand = @"^tofu\s+init(\s|$)",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^- Downloading|^- Installing|^- Using previously-installed|^\s*$|^Initializing provider|^Initializing the backend|^Initializing modules",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 20 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "tofu init: ok" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "tofu-fmt",
            Description = "Compact OpenTofu fmt output.",
            AppliesTo = ["tofu"],
            MatchCommand = @"^tofu\s+fmt(\s|$)",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "tofu fmt: ok (no changes)" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "tofu-validate",
            Description = "Compact OpenTofu validate output.",
            AppliesTo = ["tofu"],
            MatchCommand = @"^tofu\s+validate(\s|$)",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = "Success! The configuration is valid",
                    Replacement = "ok (valid)",
                    Guard = @"(?i)(error|warning)",
                },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "tofu",
            Description = "Compact OpenTofu plan, init, fmt, and validate output.",
            AppliesTo = ["tofu"],
            MatchCommand = @"^tofu\s+(plan|init|fmt|validate)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = "Success! The configuration is valid",
                    Replacement = "tofu validate: ok (valid)",
                    Guard = @"(?i)(error|warning)",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^Refreshing state|^\s*Refreshing state|^\s*#.*(unchanged|has no changes)|^\s*$|^Acquiring state lock|^\s*Acquiring state lock|^Releasing state lock|^\s*Releasing state lock|^- Downloading|^- Installing|^- Using previously-installed|^Initializing provider|^Initializing the backend|^Initializing modules",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 80 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "tofu: ok (no changes)" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "pip",
            Description = "Compact pip install output.",
            AppliesTo = ["pip", "pip3", "python", "python3"],
            MatchCommand = @"^(pip3?|python3?\s+-m\s+pip)\s+(install|sync)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^Downloading |^Collecting |^  Downloading |^Using cached |^\s*$",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "pip: ok (up to date)" },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "poetry",
            Description = "Compact Poetry install and lock output.",
            AppliesTo = ["poetry"],
            MatchCommand = @"^poetry\s+(install|lock|update)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"No dependencies to install or update|No changes\.",
                    Replacement = "poetry install: ok (up to date)",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^  [-\u2022] Downloading |^  [-\u2022] Installing .* \(|^Creating virtualenv|^Using virtualenv",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 30 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "uv",
            Description = "Compact uv sync and pip install output.",
            AppliesTo = ["uv"],
            MatchCommand = @"^uv\s+(sync|pip\s+install)\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = @"Audited \d+ package",
                    Replacement = "uv: ok (up to date)",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s+Downloading |^\s+Using cached |^\s+Preparing |^Resolved |^Prepared |^Audited ",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 20 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "swift-build",
            Description = "Compact swift build output.",
            AppliesTo = ["swift"],
            MatchCommand = @"^swift\s+build\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.MatchOutput,
                    Pattern = "Build complete!",
                    Replacement = "swift build: ok (build complete)",
                    Guard = @"warning:|error:",
                },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^Compiling |^Linking ",
                },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 40 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "stat",
            Description = "Compact stat output; strip device, inode, and birth noise.",
            AppliesTo = ["stat"],
            MatchCommand = @"^stat\b",
            Scope = FilterScope.BuiltIn,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^\s*Device:|^\s*Birth:",
                },
                new FilterStage { Kind = FilterStageKind.TruncateLinesAt, Count = 120 },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 20 },
            ],
        }),
        Compile(new FilterDefinition
        {
            Id = "liquibase",
            Description = "Compact Liquibase output, including diagnostics written to stderr.",
            AppliesTo = ["liquibase"],
            Scope = FilterScope.BuiltIn,
            MergeStderr = true,
            Stages =
            [
                new FilterStage { Kind = FilterStageKind.StripAnsi },
                new FilterStage
                {
                    Kind = FilterStageKind.StripLines,
                    Pattern = @"^\s*$|^Liquibase Community |^Liquibase Version:|^Starting Liquibase|^Running Changeset:|^\s*INFO\s",
                },
                new FilterStage { Kind = FilterStageKind.OnEmpty, Replacement = "liquibase: ok" },
                new FilterStage { Kind = FilterStageKind.MaxLines, Count = 200 },
            ],
        }),
    ];

    public static CompiledFilterDefinition Compile(FilterDefinition def) =>
        new()
        {
            Id = def.Id,
            Description = def.Description,
            AppliesTo = def.AppliesTo,
            MatchCommand = def.MatchCommand,
            CompiledMatchCommand = def.MatchCommand is not null
                ? new Regex(def.MatchCommand, RegexOptions.None)
                : null,
            Scope = def.Scope,
            MergeStderr = def.MergeStderr,
            Stages = def.Stages.Select(CompileStage).ToArray(),
        };

    private static CompiledFilterStage CompileStage(FilterStage stage) =>
        new()
        {
            Stage = stage,
            CompiledRegex = stage.Pattern is not null
                ? new Regex(stage.Pattern, RegexOptions.None)
                : null,
            CompiledGuard = stage.Guard is not null
                ? new Regex(stage.Guard, RegexOptions.None)
                : null,
        };
}
