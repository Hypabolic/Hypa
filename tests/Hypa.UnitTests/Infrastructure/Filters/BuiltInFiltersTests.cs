using Hypa.Infrastructure.Filters;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Filters;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Filters;

public sealed class BuiltInFiltersTests
{
    [Fact]
    public void All_IncludesRequestedParityFilters()
    {
        var ids = BuiltInFilters.All.Select(f => f.Id).ToHashSet();
        var expected = new[]
        {
            "ansi-strip",
            "dotnet-msbuild-noise",
            "gradle",
            "mvn",
            "eslint",
            "biome",
            "oxlint",
            "hadolint",
            "shellcheck",
            "yamllint",
            "markdownlint",
            "ansible-playbook",
            "make",
            "gcc",
            "helm",
            "kubectl",
            "ping",
            "df",
            "du",
            "ps",
            "gcloud",
            "systemctl",
            "jq",
            "turbo",
            "nx",
            "just",
            "task",
            "mise",
            "xcodebuild",
            "yadm",
            "git-status",
            "git-log",
            "git-diff",
            "docker-build",
            "docker-logs",
            "kubectl-logs",
            "kubectl-describe",
            "mypy",
            "pyright",
            "playwright",
            "go-test",
            "rspec",
            "mocha",
            "cmake",
            "ninja",
            "aws",
            "terraform-plan",
            "tofu",
            "tofu-plan",
            "tofu-init",
            "tofu-fmt",
            "tofu-validate",
            "pip",
            "poetry",
            "uv",
            "swift-build",
            "stat",
        };

        foreach (var id in expected)
            Assert.Contains(id, ids);
    }

    [Fact]
    public void All_HasUniqueFilterIds()
    {
        var duplicateIds = BuiltInFilters.All
            .GroupBy(f => f.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicateIds);
    }

    [Fact]
    public void GetApplicableFilters_UsesMatchCommand_ToSeparateDotnetBuildAndTest()
    {
        var service = MakeService(BuiltInFilters.All);

        var buildFilters = service.GetApplicableFilters("dotnet", "dotnet build");
        var testFilters = service.GetApplicableFilters("dotnet", "dotnet test");

        Assert.Contains(buildFilters, f => f.Id == "dotnet-msbuild-noise");
        Assert.DoesNotContain(buildFilters, f => f.Id == "dotnet-test");
        Assert.Contains(testFilters, f => f.Id == "dotnet-test");
        Assert.DoesNotContain(testFilters, f => f.Id == "dotnet-msbuild-noise");
    }

    [Fact]
    public void GetApplicableFilters_UsesMatchCommand_ToAvoidBroadPythonPytestFilter()
    {
        var service = MakeService(BuiltInFilters.All);

        var scriptFilters = service.GetApplicableFilters("python", "python script.py");
        var pytestFilters = service.GetApplicableFilters("python", "python -m pytest tests");

        Assert.DoesNotContain(scriptFilters, f => f.Id == "pytest");
        Assert.Contains(pytestFilters, f => f.Id == "pytest");
    }

    [Fact]
    public void GetApplicableFilters_UsesMatchCommand_ForCommandSpecificParityFilters()
    {
        var service = MakeService(BuiltInFilters.All);

        var pnpmInstallFilters = service.GetApplicableFilters("pnpm", "pnpm install");
        var pnpmNxFilters = service.GetApplicableFilters("pnpm", "pnpm nx build app");
        var pnpmVitestFilters = service.GetApplicableFilters("pnpm", "pnpm vitest run");
        var systemctlStatusFilters = service.GetApplicableFilters("systemctl", "systemctl status nginx");
        var systemctlRestartFilters = service.GetApplicableFilters("systemctl", "systemctl restart nginx");
        var dockerBuildFilters = service.GetApplicableFilters("docker", "docker build .");
        var kubectlLogsFilters = service.GetApplicableFilters("kubectl", "kubectl logs deploy/app");
        var tofuPlanFilters = service.GetApplicableFilters("tofu", "tofu plan");
        var pythonPipFilters = service.GetApplicableFilters("python", "python -m pip install requests");

        Assert.Contains(pnpmInstallFilters, f => f.Id == "pnpm-install");
        Assert.DoesNotContain(pnpmInstallFilters, f => f.Id == "nx");
        Assert.Contains(pnpmNxFilters, f => f.Id == "nx");
        Assert.DoesNotContain(pnpmNxFilters, f => f.Id == "pnpm-install");
        Assert.Contains(pnpmVitestFilters, f => f.Id == "jest");
        Assert.DoesNotContain(pnpmVitestFilters, f => f.Id == "pnpm-install");
        Assert.Contains(systemctlStatusFilters, f => f.Id == "systemctl");
        Assert.DoesNotContain(systemctlRestartFilters, f => f.Id == "systemctl");
        Assert.Equal("docker-build", dockerBuildFilters[0].Id);
        Assert.Equal("kubectl-logs", kubectlLogsFilters[0].Id);
        Assert.Equal("tofu-plan", tofuPlanFilters[0].Id);
        Assert.Contains(pythonPipFilters, f => f.Id == "pip");
    }

    [Fact]
    public void Apply_Jest_DoesNotShortCircuitFailedSummary()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "jest");
        var result = new FilterEngine().Apply(filter, "FAIL src/app.test.ts\nTests: 1 failed, 2 passed, 3 total\nExpected 1\nReceived 2");

        Assert.Contains("FAIL", result.Text);
        Assert.Contains("1 failed", result.Text);
        Assert.NotEqual("jest: ok (all passed)", result.Text);
    }

    [Fact]
    public void Apply_DotnetBuild_PreservesWarningSummary()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "dotnet-msbuild-noise");
        var result = new FilterEngine().Apply(filter, """
Microsoft (R) Build Engine version 17.8.3+195e7f5a3
Copyright (C) Microsoft Corporation. All rights reserved.

  Determining projects to restore...
  MyApp -> /home/user/MyApp/bin/Debug/net8.0/MyApp.dll

Build succeeded.
    3 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.87
""");

        Assert.Contains("MyApp ->", result.Text);
        Assert.Contains("Build succeeded.", result.Text);
        Assert.Contains("3 Warning(s)", result.Text);
        Assert.Contains("Time Elapsed", result.Text);
        Assert.DoesNotContain("Microsoft (R)", result.Text);
    }

    [Fact]
    public void Apply_Pytest_ShortCircuitsCleanSummary()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "pytest");
        var result = new FilterEngine().Apply(filter, "platform linux -- Python 3.12\nrootdir: /repo\n3 passed in 0.12s");

        Assert.Equal("pytest: ok (all passed)", result.Text);
    }

    [Fact]
    public void Apply_Task_ReturnsOkWhenOnlyUpToDateNoiseRemains()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "task");
        var result = new FilterEngine().Apply(filter, "task: Task \"build\" is up to date\ntask: Task \"lint\" is up to date\n");

        Assert.Equal("task: ok", result.Text);
    }

    [Fact]
    public void Apply_Ping_KeepsSummaryAndDropsPacketLines()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "ping");
        var result = new FilterEngine().Apply(filter, "PING example.com\n64 bytes from 1.2.3.4\n\n--- example.com ping statistics ---\n1 packets transmitted, 1 received\n");

        Assert.DoesNotContain("64 bytes from", result.Text);
        Assert.Contains("ping statistics", result.Text);
    }

    [Fact]
    public void Apply_GitStatus_GroupsChangedFiles()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "git-status");
        var result = new FilterEngine().Apply(filter, """
On branch main
Changes to be committed:
  modified:   src/App.cs
Changes not staged for commit:
  modified:   README.md
Untracked files:
  scratch.txt
""");

        Assert.Contains("main", result.Text);
        Assert.Contains("staged: ~src/App.cs", result.Text);
        Assert.Contains("unstaged: ~README.md", result.Text);
        Assert.Contains("untracked: scratch.txt", result.Text);
    }

    [Fact]
    public void Apply_Mypy_SummarizesDiagnosticsByCode()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "mypy");
        var result = new FilterEngine().Apply(filter, """
src/auth.py:42: error: Argument 1 has incompatible type "str"; expected "int"  [arg-type]
src/auth.py:55: warning: Unused "type: ignore" comment  [unused-ignore]
""");

        Assert.Contains("2 issues in 1 files", result.Text);
        Assert.Contains("[arg-type]: 1", result.Text);
        Assert.Contains("auth.py:42", result.Text);
    }

    [Fact]
    public void Apply_DockerBuild_SummarizesSteps()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "docker-build");
        var result = new FilterEngine().Apply(filter, """
#1 [internal] load build definition from Dockerfile
#2 [1/2] FROM mcr.microsoft.com/dotnet/sdk:10.0
#3 [2/2] RUN dotnet build
""");

        Assert.Contains("3 steps", result.Text);
        Assert.Contains("last: #3", result.Text);
    }

    [Fact]
    public void Apply_Aws_SummarizesJsonTopLevelKeys()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "aws");
        var result = new FilterEngine().Apply(filter, """
{"Reservations":[{"Instances":[{"InstanceId":"i-123"}]}],"NextToken":"abc"}
""");

        Assert.Contains("JSON:", result.Text);
        Assert.Contains("Reservations: [1 items]", result.Text);
        Assert.Contains("NextToken: \"abc\"", result.Text);
    }

    [Fact]
    public void Apply_TerraformPlan_StripsRefreshNoise()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "terraform-plan");
        var result = new FilterEngine().Apply(filter, """
Acquiring state lock. This may take a few moments...
Refreshing state... [id=vpc-abc]

Terraform will perform the following actions:

  # aws_instance.web will be created
  + resource "aws_instance" "web" {}

Plan: 1 to add, 0 to change, 0 to destroy.
""");

        Assert.DoesNotContain("Refreshing state", result.Text);
        Assert.DoesNotContain("Acquiring state lock", result.Text);
        Assert.Contains("aws_instance.web", result.Text);
        Assert.Contains("Plan: 1 to add", result.Text);
    }

    [Fact]
    public void Apply_TofuSpecificFilters_MatchSuccessSummaries()
    {
        var plan = BuiltInFilters.All.Single(f => f.Id == "tofu-plan");
        var init = BuiltInFilters.All.Single(f => f.Id == "tofu-init");
        var fmt = BuiltInFilters.All.Single(f => f.Id == "tofu-fmt");
        var validate = BuiltInFilters.All.Single(f => f.Id == "tofu-validate");
        var engine = new FilterEngine();

        Assert.Equal(
            "tofu plan: no changes detected",
            engine.Apply(plan, "Refreshing state... [id=vpc-abc]\nAcquiring state lock. This may take a few moments...\nReleasing state lock. This may take a few moments...").Text);
        Assert.Equal(
            "tofu init: ok",
            engine.Apply(init, "Initializing the backend...\nInitializing provider plugins...\n- Using previously-installed hashicorp/aws 5.0.0\n").Text);
        Assert.Equal("tofu fmt: ok (no changes)", engine.Apply(fmt, string.Empty).Text);
        Assert.Equal("ok (valid)", engine.Apply(validate, "Success! The configuration is valid.").Text);
    }

    [Fact]
    public void Apply_Poetry_StripsHyphenAndBulletInstallNoise()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "poetry");
        var result = new FilterEngine().Apply(filter, """
Installing dependencies from lock file

  - Downloading requests-2.31.0-py3-none-any.whl (62.6 kB)
  - Installing certifi (2023.11.17)
  - Installing requests (2.31.0)

Writing lock file
""");

        Assert.Equal(
            "Installing dependencies from lock file\nWriting lock file".ReplaceLineEndings(Environment.NewLine),
            result.Text.ReplaceLineEndings(Environment.NewLine));
    }

    [Fact]
    public void Apply_Uv_AuditedPackagesShortCircuitToOk()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "uv");
        var result = new FilterEngine().Apply(filter, """
Resolved 42 packages in 123ms
Audited 42 packages in 0.05ms
""");

        Assert.Equal("uv: ok (up to date)", result.Text);
    }

    [Fact]
    public void Apply_SwiftBuild_DoesNotHideWarnings()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "swift-build");
        var result = new FilterEngine().Apply(filter, """
CompileSwift normal x86_64 MyFile.swift
/path/to/MyFile.swift:42:10: warning: unused variable 'x'
Build complete! (with warnings)
""");

        Assert.Contains("warning: unused variable", result.Text);
        Assert.Contains("Build complete! (with warnings)", result.Text);
    }

    [Fact]
    public void Apply_Stat_StripsDeviceAndBirthLines()
    {
        var filter = BuiltInFilters.All.Single(f => f.Id == "stat");
        var result = new FilterEngine().Apply(filter, """
  File: main.rs
  Size: 12345           Blocks: 24         IO Block: 4096   regular file
Device: 801h/2049d      Inode: 1234567     Links: 1
Access: (0644/-rw-r--r--)  Uid: ( 1000/ matthew)   Gid: ( 1000/ matthew)
 Birth: 2026-03-09 10:00:00.000000000 +0100
""");

        Assert.Contains("File: main.rs", result.Text);
        Assert.DoesNotContain("Device:", result.Text);
        Assert.DoesNotContain("Birth:", result.Text);
    }

    private static FilterService MakeService(IReadOnlyList<CompiledFilterDefinition> filters)
    {
        var repo = Substitute.For<IFilterRepository>();
        repo.GetAll().Returns(filters);
        var engine = Substitute.For<IFilterEngine>();
        return new FilterService(repo, engine);
    }
}
