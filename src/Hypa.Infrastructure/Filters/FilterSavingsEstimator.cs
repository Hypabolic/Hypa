using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Infrastructure.Filters;

public sealed class FilterSavingsEstimator(IFilterEngine engine, ITokenCounter tokenCounter)
{
    public IReadOnlyList<FilterSavingsEstimate> EstimateAll(IReadOnlyList<CompiledFilterDefinition> filters) =>
        filters
            .Where(f => f.Scope == FilterScope.BuiltIn)
            .Select(Estimate)
            .OrderByDescending(e => e.SavedTokens)
            .ThenBy(e => e.FilterId)
            .ToList();

    public FilterSavingsEstimate Estimate(CompiledFilterDefinition filter)
    {
        var sample = SyntheticFilterSamples.For(filter);
        var result = engine.Apply(filter, sample.Text);
        var originalTokens = tokenCounter.EstimateTokens(sample.Text);
        var compressedTokens = tokenCounter.EstimateTokens(result.Text);

        return new FilterSavingsEstimate
        {
            FilterId = filter.Id,
            AppliesTo = filter.AppliesTo.Count == 0 ? "(any)" : string.Join(",", filter.AppliesTo),
            OriginalTokens = originalTokens,
            CompressedTokens = compressedTokens,
            OriginalBytes = sample.Text.Length,
            CompressedBytes = result.Text.Length,
            SampleKind = sample.Kind,
        };
    }
}

internal sealed record SyntheticFilterSample(string Text, string Kind);

internal static class SyntheticFilterSamples
{
    public static SyntheticFilterSample For(CompiledFilterDefinition filter)
    {
        var id = filter.Id;
        var text = id switch
        {
            "ansi-strip" => Repeat("\u001b[32mINFO\u001b[0m compiling module\n", 80),
            "dotnet-msbuild-noise" => DotnetBuild(),
            "dotnet-test" => DotnetTest(),
            "cargo" => Cargo(),
            "npm-install" => NpmInstall(),
            "pnpm-install" => PnpmInstall(),
            "yarn-install" => YarnInstall(),
            "jest" => Jest(),
            "pytest" => Pytest(),
            "git-status" => GitStatus(),
            "git-log" => GitLog(),
            "git-diff" => GitDiff(),
            "go-test" => GoTest(),
            "rspec" => Rspec(),
            "mocha" => Mocha(),
            "mypy" or "pyright" => PythonTypecheck(),
            "playwright" => Playwright(),
            "eslint" => Linter("eslint"),
            "biome" => Biome(),
            "oxlint" => Oxlint(),
            "docker" => Docker(),
            "docker-build" => DockerBuild(),
            "docker-logs" => TimestampedLogs(),
            "gradle" => Gradle(),
            "mvn" => Mvn(),
            "hadolint" => Linter("hadolint"),
            "shellcheck" => Linter("shellcheck"),
            "yamllint" => Linter("yamllint"),
            "markdownlint" => Linter("markdownlint"),
            "ansible-playbook" => Ansible(),
            "make" => Make(),
            "gcc" => Gcc(),
            "helm" => Helm(),
            "kubectl" => KubectlTable(),
            "kubectl-logs" => TimestampedLogs(),
            "kubectl-describe" => KubectlDescribe(),
            "ping" => Ping(),
            "df" => Table("Filesystem     1K-blocks    Used Available Use% Mounted on", "/dev/sda1      999999999 123456 999876543  1% /mnt/data", 80),
            "du" => Table("", "123456\t./node_modules/pkg", 120),
            "ps" => Table("PID TTY          TIME CMD", "12345 ?        00:00:01 dotnet", 80),
            "gcloud" => Table("NAME          ZONE           STATUS", "instance-1    us-central1-a  RUNNING", 80),
            "systemctl" => Systemctl(),
            "jq" => JsonLines(),
            "turbo" => Turbo(),
            "nx" => Nx(),
            "just" => Just(),
            "task" => Task(),
            "mise" => Mise(),
            "xcodebuild" => Xcodebuild(),
            "cmake" => Cmake(),
            "ninja" => Ninja(),
            "aws" => Aws(),
            "yadm" => Yadm(),
            "terraform-plan" => TerraformPlan(),
            "tofu" or "tofu-plan" => TerraformPlan("OpenTofu"),
            "tofu-init" => TofuInit(),
            "tofu-fmt" => string.Empty,
            "tofu-validate" => "Success! The configuration is valid.",
            "pip" => Pip(),
            "poetry" => Poetry(),
            "uv" => Uv(),
            "swift-build" => SwiftBuild(),
            "stat" => Stat(),
            "liquibase" => Liquibase(),
            _ => Generic(filter),
        };

        return new SyntheticFilterSample(text, "synthetic");
    }

    private static string Repeat(string line, int count) =>
        string.Concat(Enumerable.Repeat(line, count));

    private static string DotnetBuild() => """
Microsoft (R) Build Engine version 17.8.3
Copyright (C) Microsoft Corporation. All rights reserved.

  Determining projects to restore...
""" + Repeat("  Restored /repo/src/App/App.csproj (120 ms).\n", 40) + """
  App -> /repo/src/App/bin/Debug/net10.0/App.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:02.34
""";

    private static string DotnetTest() =>
        Repeat("  Determining projects to restore...\n", 30) +
        "Passed!  - Failed: 0, Passed: 432, Skipped: 0, Total: 432, Duration: 2 s\n";

    private static string Cargo() =>
        Repeat("   Compiling crate v0.1.0 (/repo/crate)\n", 120) +
        "test result: ok. 128 passed; 0 failed; 0 ignored; finished in 1.23s\n";

    private static string NpmInstall() =>
        Repeat("npm notice package metadata\n", 40) +
        Repeat("Downloading package tarball\n", 40) +
        "added 153 packages, and audited 154 packages in 4s\n";

    private static string PnpmInstall() =>
        Repeat("Progress: resolved 10, reused 9, downloaded 1, added 1\n", 120) +
        "Done in 3.2s\n";

    private static string YarnInstall() =>
        Repeat("yarn info package fetched\n[1/4] Resolving packages...\n", 80) +
        "Done in 4.12s\n";

    private static string Jest() =>
        Repeat("PASS src/example.test.ts\n", 80) +
        "Tests:       80 passed, 80 total\nTest Suites: 10 passed, 10 total\nTime: 2.4 s\n";

    private static string Pytest() =>
        "platform linux -- Python 3.12\nrootdir: /repo\nplugins: anyio\n" +
        "128 passed in 3.21s\n";

    private static string GitStatus() => """
On branch main
Your branch is ahead of 'origin/main' by 2 commits.
Changes to be committed:
  modified:   src/App.cs
Changes not staged for commit:
  modified:   README.md
Untracked files:
""" + Repeat("  scratch/file.txt\n", 40);

    private static string GitLog() =>
        Repeat("commit 0123456789abcdef\nAuthor: Developer <dev@example.com>\nDate: Today\n\n    feat: update subsystem\n\n", 40);

    private static string GitDiff() =>
        Repeat("diff --git a/src/App.cs b/src/App.cs\nindex 111..222 100644\n@@ -1,6 +1,6 @@\n context\n-old line\n+new line\n", 80);

    private static string GoTest() =>
        Repeat("=== RUN   TestFeature\n--- PASS: TestFeature (0.01s)\n", 80) +
        "ok  example.com/project/pkg  1.23s\n";

    private static string Rspec() => Repeat(".\n", 100) + "100 examples, 0 failures\n";

    private static string Mocha() => Repeat("  ✓ works\n", 100) + "100 passing (250ms)\n";

    private static string PythonTypecheck() =>
        Repeat("src/app.py:42: error: Argument 1 has incompatible type \"str\"; expected \"int\"  [arg-type]\n", 40);

    private static string Playwright() =>
        Repeat("  ✓  tests/example.spec.ts:1:1 › renders page (120ms)\n", 40) +
        "  40 passed (3.2s)\n";

    private static string Linter(string name) =>
        Repeat($"src/file.ts:10:5 {name}/rule Unexpected value in test fixture\n", 80);

    private static string Biome() =>
        "Checked 80 files in 0.5s\n" + Linter("lint/suspicious/noExplicitAny");

    private static string Oxlint() =>
        Linter("eslint(no-console)") + "Found 80 warnings on 80 files.\nFinished in 12ms on 100 files.\n";

    private static string Docker() =>
        Repeat("#1 [internal] load build definition from Dockerfile\n#1 CACHED\n => transferring dockerfile: 1.2kB\n", 80);

    private static string DockerBuild() =>
        Repeat("#1 [internal] load build definition from Dockerfile\n#2 [1/3] FROM alpine\n#3 [2/3] RUN echo ok\n", 30);

    private static string TimestampedLogs() =>
        Repeat("2026-05-09T10:00:00Z INFO worker processed job\n", 120);

    private static string Gradle() =>
        Repeat("> Configuring project :app\n> Task :app:compileJava UP-TO-DATE\nDownload https://repo.example/artifact.jar\n", 80) +
        "BUILD SUCCESSFUL in 8s\n";

    private static string Mvn() =>
        Repeat("[INFO] Downloading org.example:lib:1.0\n[INFO] --- compiler:compile ---\n[INFO]\n", 80) +
        "[INFO] BUILD SUCCESS\n[INFO] Total time: 4.123 s\n";

    private static string Ansible() =>
        Repeat("ok: [host1]\nskipping: [host2]\n", 80) + "changed: [host3]\nPLAY RECAP\n";

    private static string Make() =>
        Repeat("make[1]: Entering directory '/repo'\nmake[1]: Leaving directory '/repo'\n", 80) +
        "gcc -o app app.c\n";

    private static string Gcc() =>
        Repeat("In file included from /usr/include/a.h:1:\n                 from src/app.c:2:\n", 80) +
        "src/app.c:10:5: warning: unused variable 'x'\n";

    private static string Helm() =>
        Repeat("W warning: values file contains deprecated key\n\n", 40) +
        "Release \"app\" has been upgraded. Happy Helming!\n";

    private static string KubectlTable() => Table("NAME        READY   STATUS    RESTARTS   AGE", "pod/app-123 1/1     Running   0          10m", 120);

    private static string KubectlDescribe() => """
Name: app-123
Namespace: default
Events:
  Type    Reason     Age   From               Message
""" + Repeat("  Normal  Pulled     10m   kubelet            Successfully pulled image\n", 80);

    private static string Ping() =>
        "PING example.com (93.184.216.34): 56 data bytes\n" +
        Repeat("64 bytes from 93.184.216.34: icmp_seq=1 ttl=55 time=10.1 ms\n", 100) +
        "--- example.com ping statistics ---\n100 packets transmitted, 100 received, 0% packet loss\n";

    private static string Systemctl() =>
        "● app.service - App\n   Loaded: loaded\n   Active: active (running)\n" +
        Repeat("May 09 host app[123]: processed request\n", 80);

    private static string JsonLines() => "[\n" + Repeat("  { \"id\": 1, \"name\": \"example\", \"status\": \"ok\" },\n", 80) + "]\n";

    private static string Turbo() => Repeat("cache hit, replaying logs app#build\n", 80) + "Tasks: 12 successful, 12 total\n";
    private static string Nx() => Repeat("> nx run app:build\n", 80) + "Successfully ran target build for project app\n";
    private static string Just() => Repeat("just --unstable recipe\n", 80) + "done\n";
    private static string Task() => Repeat("task: Task \"build\" is up to date\n", 80);
    private static string Mise() => Repeat("mise installing node@22\n", 80) + "mise node@22 installed\n";
    private static string Xcodebuild() => Repeat("CompileSwift normal arm64 File.swift\n", 80) + "** BUILD SUCCEEDED **\n";
    private static string Cmake() => Repeat("-- Detecting C compiler ABI info\n-- Configuring done\n", 80) + "-- Build files have been written\n";
    private static string Ninja() => Repeat("[12/400] Building CXX object src/file.o\n", 120) + "ninja: build stopped: subcommand failed.\n";
    private static string Aws() => "{\"Reservations\":[{\"Instances\":[{\"InstanceId\":\"i-123\"}]}],\"NextToken\":\"abc\"}\n";
    private static string Yadm() => Repeat("(use \"yadm add <file>...\" to update what will be committed)\n", 80) + "modified: .zshrc\n";

    private static string TerraformPlan(string name = "Terraform") =>
        Repeat("Refreshing state... [id=vpc-abc]\nAcquiring state lock. This may take a few moments...\n", 40) +
        $"{name} will perform the following actions:\n  # aws_instance.web will be created\n  + resource \"aws_instance\" \"web\" {{}}\nPlan: 1 to add, 0 to change, 0 to destroy.\n";

    private static string TofuInit() =>
        Repeat("Initializing provider plugins...\n- Downloading hashicorp/aws 5.0.0...\n- Using previously-installed hashicorp/random 3.5.1\n", 60);

    private static string Pip() =>
        Repeat("Collecting requests\n  Downloading requests-2.31.0-py3-none-any.whl\nUsing cached certifi.whl\n", 60);

    private static string Poetry() =>
        "Installing dependencies from lock file\n" +
        Repeat("  - Downloading requests-2.31.0-py3-none-any.whl (62.6 kB)\n  - Installing certifi (2023.11.17)\n", 60) +
        "Writing lock file\n";

    private static string Uv() =>
        "Resolved 42 packages in 123ms\nAudited 42 packages in 0.05ms\n";

    private static string SwiftBuild() =>
        Repeat("Compiling MyApp File.swift\nLinking MyApp\n", 80) + "Build complete!\n";

    private static string Stat() => """
  File: main.rs
  Size: 12345           Blocks: 24         IO Block: 4096   regular file
Device: 801h/2049d      Inode: 1234567     Links: 1
Access: (0644/-rw-r--r--)  Uid: ( 1000/ user)   Gid: ( 1000/ user)
 Birth: 2026-03-09 10:00:00.000000000 +0100
""";

    private static string Liquibase() =>
        Repeat("Liquibase Community 4.x\nStarting Liquibase\nRunning Changeset: changelog.xml::1::dev\n", 60) +
        "Liquibase command 'update' was executed successfully.\n";

    private static string Table(string header, string row, int count) =>
        (string.IsNullOrEmpty(header) ? string.Empty : header + "\n") + Repeat(row + "\n", count);

    private static string Generic(CompiledFilterDefinition filter)
    {
        var tool = filter.AppliesTo.FirstOrDefault() ?? filter.Id;
        return Repeat($"{tool}: progress line that should be compacted by {filter.Id}\n", 120);
    }
}
