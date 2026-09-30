using System.Text.Json;
using Hypa.Infrastructure.Rewrite;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Runner;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class PackageManagerScriptResolverTests
{
    private const string WorkingDirectory = "/workspace/project";

    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly PackageManagerScriptResolver _resolver;

    public PackageManagerScriptResolverTests()
    {
        _resolver = new PackageManagerScriptResolver(_fileSystem, new ShellLexer());
    }

    [Theory]
    [InlineData("pnpm", "check", "biome check .", "biome")]
    [InlineData("yarn", "format", "prettier --write .", "prettier")]
    public void TryResolve_DirectCustomScript_ReturnsExactResolvedScript(
        string packageManager,
        string scriptName,
        string scriptCommand,
        string expectedExecutable)
    {
        ConfigureScripts((scriptName, scriptCommand));

        var resolved = Resolve(packageManager, [scriptName], $"{packageManager} {scriptName}");

        Assert.Equal(new ResolvedPackageScript(expectedExecutable, scriptCommand), resolved);
    }

    [Fact]
    public void TryResolve_NpmDirectCustomScript_ReturnsNull()
    {
        ConfigureScripts(("lint", "eslint ."));

        var resolved = _resolver.TryResolve(Invocation("npm", ["lint"], "npm lint"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("npm", "run")]
    [InlineData("npm", "run-script")]
    [InlineData("npm", "rum")]
    [InlineData("npm", "urn")]
    [InlineData("pnpm", "run")]
    [InlineData("yarn", "run")]
    [InlineData("pnpm", "run-script")]
    [InlineData("yarn", "run-script")]
    public void TryResolve_ExplicitRunForm_ReturnsExactResolvedScript(
        string packageManager,
        string runCommand)
    {
        const string scriptCommand = "eslint --max-warnings 0 .";
        ConfigureScripts(("lint", scriptCommand));

        var resolved = Resolve(
            packageManager,
            [runCommand, "lint"],
            $"{packageManager} {runCommand} lint");

        Assert.Equal(new ResolvedPackageScript("eslint", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("test", "test")]
    [InlineData("t", "test")]
    [InlineData("tst", "test")]
    [InlineData("start", "start")]
    [InlineData("stop", "stop")]
    [InlineData("restart", "restart")]
    public void TryResolve_NpmLifecycleShorthandOrTestAlias_ResolvesCanonicalScript(
        string invocationCommand,
        string scriptName)
    {
        const string scriptCommand = "node lifecycle.js";
        ConfigureScripts((scriptName, scriptCommand));

        var resolved = Resolve("npm", [invocationCommand], $"npm {invocationCommand}");

        Assert.Equal(new ResolvedPackageScript("node", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("t")]
    [InlineData("tst")]
    public void TryResolve_PnpmTestAlias_ResolvesTestScript(string invocationCommand)
    {
        const string scriptCommand = "vitest run";
        ConfigureScripts(("test", scriptCommand));

        var resolved = Resolve("pnpm", [invocationCommand], $"pnpm {invocationCommand}");

        Assert.Equal(new ResolvedPackageScript("vitest", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("eslint .", "eslint")]
    [InlineData("biome check .", "biome")]
    [InlineData("oxlint src", "oxlint")]
    [InlineData("jest --runInBand", "jest")]
    [InlineData("vitest run --coverage", "vitest")]
    [InlineData("tsc --noEmit", "tsc")]
    public void TryResolve_RecognizesToolLeadExecutables(string scriptCommand, string expectedExecutable)
    {
        ConfigureScripts(("verify", scriptCommand));

        var resolved = Resolve("pnpm", ["verify"], "pnpm verify");

        Assert.Equal(expectedExecutable, resolved.Executable);
        Assert.Equal(scriptCommand, resolved.Command);
    }

    [Theory]
    [InlineData("/opt/homebrew/bin/npm", "/workspace/project/node_modules/.bin/eslint", "eslint", "eslint")]
    [InlineData("/usr/local/bin/pnpm.cmd", "./node_modules/.bin/biome.cmd check .", "biome", "biome check .")]
    [InlineData("C:/Program Files/nodejs/yarn.exe", "C:/workspace/node_modules/.bin/oxlint.exe src", "oxlint", "oxlint src")]
    [InlineData(@"C:\Program Files\nodejs\npm.cmd", @"C:\workspace\project\node_modules\.bin\eslint.cmd --fix .", "eslint", "eslint --fix .")]
    [InlineData(@"C:\Program Files\nodejs\NpM.BaT", "./node_modules/.bin/eslint --fix .", "eslint", "eslint --fix .")]
    [InlineData("npm", @"C:\workspace\project\node_modules\.bin\EsLiNt.BaT --cache .", "eslint", "eslint --cache .")]
    public void TryResolve_NormalizesPackageManagerAndReturnsCanonicalMatchCommand(
        string packageManagerExecutable,
        string scriptCommand,
        string expectedExecutable,
        string expectedCommand)
    {
        ConfigureScripts(("lint", scriptCommand));

        var resolved = Resolve(
            packageManagerExecutable,
            ["run", "lint"],
            "package-manager run lint");

        Assert.Equal(new ResolvedPackageScript(expectedExecutable, expectedCommand), resolved);
    }

    [Fact]
    public void TryResolve_ArbitraryScriptExtension_PreservesExtension()
    {
        const string scriptCommand = "./node_modules/.bin/eslint.js --fix .";
        ConfigureScripts(("lint", scriptCommand));

        var resolved = Resolve("npm", ["run", "lint"], "npm run lint");

        Assert.Equal(new ResolvedPackageScript("eslint.js", "eslint.js --fix ."), resolved);
    }

    [Fact]
    public void TryResolve_QuotedLeadExecutable_ReturnsCanonicalMatchCommandWithArguments()
    {
        const string scriptCommand = "\"./node_modules/.bin/eslint.cmd\" --fix .";
        ConfigureScripts(("lint", scriptCommand));

        var resolved = Resolve("npm", ["run", "lint"], "npm run lint");

        Assert.Equal("eslint", resolved.Executable);
        Assert.Equal("eslint --fix .", resolved.Command);
    }

    [Theory]
    [InlineData("\"eslint\"-wrapper .")]
    [InlineData("es\"lint\" .")]
    [InlineData("\"es\"\"lint\" .")]
    public void TryResolve_CompoundQuotedLeadShellWord_ReturnsNull(string scriptCommand)
    {
        ConfigureScripts(("lint", scriptCommand));

        var resolved = _resolver.TryResolve(Invocation("npm", ["run", "lint"], "npm run lint"));

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_ChainedScript_UsesFirstExecutableAndReturnsEntireCommand()
    {
        const string scriptCommand = "vitest run && tsc --noEmit";
        ConfigureScripts(("verify", scriptCommand));

        var resolved = Resolve("yarn", ["verify"], "yarn verify");

        Assert.Equal("vitest", resolved.Executable);
        Assert.Equal(scriptCommand, resolved.Command);
    }

    [Theory]
    [InlineData("NODE_ENV=\"test mode\" ./node_modules/.bin/vitest run", "vitest run")]
    [InlineData("NODE_ENV=test CI=1 ./node_modules/.bin/vitest run", "vitest run")]
    public void TryResolve_AssignmentPrefixedScript_UsesHostPackageScriptSemantics(
        string scriptCommand,
        string expectedCommand)
    {
        ConfigureScripts(("test:ci", scriptCommand));

        var resolved = _resolver.TryResolve(Invocation("pnpm", ["test:ci"], "pnpm test:ci"));

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(resolved);
            return;
        }

        Assert.Equal(new ResolvedPackageScript("vitest", expectedCommand), resolved);
    }

    [Theory]
    [InlineData("&& eslint .")]
    [InlineData("| eslint .")]
    [InlineData("> output.txt")]
    [InlineData("& eslint .")]
    public void TryResolve_ScriptBeginningWithControlToken_ReturnsNull(string scriptCommand)
    {
        ConfigureScripts(("lint", scriptCommand));

        var resolved = _resolver.TryResolve(Invocation("npm", ["run", "lint"], "npm run lint"));

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_ReadsPackageJsonFromInvocationWorkingDirectory()
    {
        const string invocationDirectory = "/workspace/packages/web";
        const string fallbackDirectory = "/unrelated/current-directory";
        var expectedPath = PackageJsonPath(invocationDirectory);
        _fileSystem.GetCurrentDirectory().Returns(fallbackDirectory);
        ConfigureScripts(invocationDirectory, ("lint", "eslint ."));

        var resolved = Resolve(
            "pnpm",
            ["lint"],
            "pnpm lint",
            workingDirectory: invocationDirectory);

        Assert.Equal("eslint", resolved.Executable);
        Assert.Equal("eslint .", resolved.Command);
        _fileSystem.Received().FileExists(expectedPath);
        _fileSystem.Received().ReadAllText(expectedPath);
        _fileSystem.DidNotReceive().ReadAllText(PackageJsonPath(fallbackDirectory));
    }

    [Fact]
    public void TryResolve_WithoutInvocationWorkingDirectory_UsesFileSystemCurrentDirectory()
    {
        const string currentDirectory = "/workspace/current";
        _fileSystem.GetCurrentDirectory().Returns(currentDirectory);
        ConfigureScripts(currentDirectory, ("test", "vitest run"));

        var resolved = Resolve("npm", ["test"], "npm test", workingDirectory: null);

        Assert.Equal("vitest", resolved.Executable);
        Assert.Equal("vitest run", resolved.Command);
        _fileSystem.Received().ReadAllText(PackageJsonPath(currentDirectory));
    }

    [Theory]
    [InlineData("npm", "install")]
    [InlineData("npm", "ci")]
    [InlineData("pnpm", "add")]
    [InlineData("yarn", "remove")]
    [InlineData("pnpm", "exec")]
    [InlineData("yarn", "publish")]
    [InlineData("npm", "i")]
    [InlineData("npm", "x")]
    [InlineData("pnpm", "i")]
    [InlineData("pnpm", "x")]
    [InlineData("yarn", "i")]
    [InlineData("yarn", "x")]
    [InlineData("pnpm", "c")]
    [InlineData("pnpm", "self-update")]
    [InlineData("pnpm", "unpublish")]
    [InlineData("pnpm", "it")]
    [InlineData("yarn", "lockfile")]
    [InlineData("yarn", "prune")]
    [InlineData("yarn", "self-update")]
    [InlineData("yarn", "patch-commit")]
    public void TryResolve_KnownBuiltIn_DoesNotReinterpretAsScript(
        string packageManager,
        string builtInCommand)
    {
        ConfigureScripts((builtInCommand, "eslint ."));

        var resolved = _resolver.TryResolve(Invocation(
            packageManager,
            [builtInCommand],
            $"{packageManager} {builtInCommand}"));

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_ExplicitPnpmRunOfCAliasNamedScript_ResolvesScript()
    {
        const string scriptCommand = "node clean.js";
        ConfigureScripts(("c", scriptCommand));

        var resolved = Resolve("pnpm", ["run", "c"], "pnpm run c");

        Assert.Equal(new ResolvedPackageScript("node", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("pnpm", "it")]
    [InlineData("yarn", "prune")]
    [InlineData("yarn", "patch-commit")]
    public void TryResolve_ExplicitRunOfNewlyReservedCommand_ResolvesScript(
        string packageManager,
        string scriptName)
    {
        const string scriptCommand = "node script.js";
        ConfigureScripts((scriptName, scriptCommand));

        var resolved = Resolve(
            packageManager,
            ["run", scriptName],
            $"{packageManager} run {scriptName}");

        Assert.Equal(new ResolvedPackageScript("node", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("npm", "run")]
    [InlineData("npm", "run-script")]
    [InlineData("pnpm", "run")]
    [InlineData("yarn", "run")]
    [InlineData("pnpm", "run-script")]
    [InlineData("yarn", "run-script")]
    public void TryResolve_ExplicitRunOfBuiltInNamedScript_ResolvesScript(
        string packageManager,
        string runCommand)
    {
        const string scriptCommand = "node install.js";
        ConfigureScripts(("install", scriptCommand));

        var resolved = Resolve(
            packageManager,
            [runCommand, "install"],
            $"{packageManager} {runCommand} install");

        Assert.Equal(new ResolvedPackageScript("node", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("npm", "run")]
    [InlineData("npm", "run-script")]
    [InlineData("pnpm", "run")]
    [InlineData("yarn", "run")]
    [InlineData("pnpm", "run-script")]
    [InlineData("yarn", "run-script")]
    public void TryResolve_RunWithoutScriptName_DoesNotResolve(
        string packageManager,
        string runCommand)
    {
        ConfigureScripts((runCommand, "eslint ."));

        var resolved = _resolver.TryResolve(Invocation(
            packageManager,
            [runCommand],
            $"{packageManager} {runCommand}"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("--silent")]
    [InlineData("-s")]
    public void TryResolve_SafeLeadingSilentOptionBeforeExplicitRun_ResolvesScript(string option)
    {
        const string scriptCommand = "eslint .";
        ConfigureScripts(("lint", scriptCommand));

        var resolved = Resolve(
            "npm",
            [option, "run", "lint"],
            $"npm {option} run lint");

        Assert.Equal(new ResolvedPackageScript("eslint", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("--silent")]
    [InlineData("-s")]
    public void TryResolve_SafeSilentOptionAfterExplicitRun_ResolvesScript(string option)
    {
        const string scriptCommand = "eslint .";
        ConfigureScripts(("lint", scriptCommand));

        var resolved = Resolve(
            "npm",
            ["run", option, "lint"],
            $"npm run {option} lint");

        Assert.Equal(new ResolvedPackageScript("eslint", scriptCommand), resolved);
    }

    [Fact]
    public void TryResolve_UnknownLeadingOption_ReturnsNull()
    {
        ConfigureScripts(("lint", "eslint ."));

        var resolved = _resolver.TryResolve(Invocation(
            "npm",
            ["--future-option", "run", "lint"],
            "npm --future-option run lint"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("npm", "--registry", "https://registry.example.test")]
    [InlineData("npm", "--prefix", "/workspace/other")]
    [InlineData("npm", "--workspace", "web")]
    [InlineData("pnpm", "--dir", "/workspace/other")]
    [InlineData("yarn", "--cwd", "/workspace/other")]
    public void TryResolve_ValueTakingOrTargetingLeadingOption_ReturnsNull(
        string packageManager,
        string option,
        string value)
    {
        ConfigureScripts(("lint", "eslint ."));

        var resolved = _resolver.TryResolve(Invocation(
            packageManager,
            [option, value, "run", "lint"],
            $"{packageManager} {option} {value} run lint"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("--workspace", "web")]
    [InlineData("--workspace=web", null)]
    [InlineData("-w", "web")]
    [InlineData("-w=web", null)]
    [InlineData("--workspaces", null)]
    [InlineData("--workspaces=web", null)]
    [InlineData("--ws", null)]
    [InlineData("--ws=web", null)]
    [InlineData("-ws", null)]
    [InlineData("-ws=web", null)]
    [InlineData("--prefix", "/workspace/other")]
    [InlineData("--prefix=/workspace/other", null)]
    [InlineData("-C", "/workspace/other")]
    [InlineData("-C=/workspace/other", null)]
    public void TryResolve_NpmTargetingOptionBeforePassThroughSeparator_ReturnsNull(
        string targetingOption,
        string? targetingValue)
    {
        ConfigureScripts(("lint", "eslint ."));
        string[] arguments = targetingValue is null
            ? ["run", "lint", targetingOption, "--", "--fix"]
            : ["run", "lint", targetingOption, targetingValue, "--", "--fix"];

        var resolved = _resolver.TryResolve(Invocation(
            "npm",
            arguments,
            $"npm {string.Join(' ', arguments)}"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("--workspace", "web")]
    [InlineData("--workspace=web", null)]
    [InlineData("-w", "web")]
    [InlineData("-w=web", null)]
    [InlineData("--workspaces", null)]
    [InlineData("--workspaces=web", null)]
    [InlineData("--ws", null)]
    [InlineData("--ws=web", null)]
    [InlineData("-ws", null)]
    [InlineData("-ws=web", null)]
    [InlineData("--prefix", "/workspace/other")]
    [InlineData("--prefix=/workspace/other", null)]
    [InlineData("-C", "/workspace/other")]
    [InlineData("-C=/workspace/other", null)]
    public void TryResolve_NpmTargetingOptionAfterPassThroughSeparator_ResolvesScript(
        string targetingOption,
        string? targetingValue)
    {
        const string scriptCommand = "eslint .";
        ConfigureScripts(("lint", scriptCommand));
        string[] arguments = targetingValue is null
            ? ["run", "lint", "--", targetingOption]
            : ["run", "lint", "--", targetingOption, targetingValue];

        var resolved = Resolve(
            "npm",
            arguments,
            $"npm {string.Join(' ', arguments)}");

        Assert.Equal(new ResolvedPackageScript("eslint", scriptCommand), resolved);
    }

    [Theory]
    [InlineData("npm", "prelint")]
    [InlineData("npm", "postlint")]
    [InlineData("pnpm", "prelint")]
    [InlineData("pnpm", "postlint")]
    [InlineData("yarn", "prelint")]
    [InlineData("yarn", "postlint")]
    public void TryResolve_ScriptWithLifecycleHook_ReturnsNull(
        string packageManager,
        string hookName)
    {
        ConfigureScripts(
            ("lint", "eslint ."),
            (hookName, "node hook.js"));

        var resolved = _resolver.TryResolve(Invocation(
            packageManager,
            ["run", "lint"],
            $"{packageManager} run lint"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("bun")]
    [InlineData("node")]
    [InlineData("/opt/bin/corepack.cmd")]
    [InlineData("/opt/bin/npm.sh")]
    public void TryResolve_UnsupportedExecutable_ReturnsNullWithoutFileSystemCalls(
        string executable)
    {
        var resolved = _resolver.TryResolve(Invocation(
            executable,
            ["run", "lint"],
            $"{executable} run lint"));

        Assert.Null(resolved);
        Assert.Empty(_fileSystem.ReceivedCalls());
    }

    [Fact]
    public void TryResolve_MissingPackageJson_ReturnsNullWithoutReadingFile()
    {
        var packageJsonPath = PackageJsonPath(WorkingDirectory);
        _fileSystem.FileExists(packageJsonPath).Returns(false);

        var resolved = _resolver.TryResolve(Invocation("pnpm", ["lint"], "pnpm lint"));

        Assert.Null(resolved);
        _fileSystem.DidNotReceive().ReadAllText(packageJsonPath);
    }

    [Fact]
    public void TryResolve_UnreadablePackageJson_ReturnsNull()
    {
        var packageJsonPath = PackageJsonPath(WorkingDirectory);
        _fileSystem.FileExists(packageJsonPath).Returns(true);
        _fileSystem.ReadAllText(packageJsonPath).Returns(_ => throw new IOException("unreadable"));

        var resolved = _resolver.TryResolve(Invocation("pnpm", ["lint"], "pnpm lint"));

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_InvalidJson_ReturnsNull()
    {
        ConfigurePackageJson("{ not valid json");

        var resolved = _resolver.TryResolve(Invocation("pnpm", ["lint"], "pnpm lint"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"scripts\":null}")]
    [InlineData("{\"scripts\":[]}")]
    public void TryResolve_AbsentOrInvalidScriptsObject_ReturnsNull(string packageJson)
    {
        ConfigurePackageJson(packageJson);

        var resolved = _resolver.TryResolve(Invocation("yarn", ["lint"], "yarn lint"));

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolve_AbsentRequestedScript_ReturnsNull()
    {
        ConfigureScripts(("build", "tsc"));

        var resolved = _resolver.TryResolve(Invocation("npm", ["run", "lint"], "npm run lint"));

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("{\"scripts\":{\"lint\":null}}")]
    [InlineData("{\"scripts\":{\"lint\":42}}")]
    [InlineData("{\"scripts\":{\"lint\":{}}}")]
    [InlineData("{\"scripts\":{\"lint\":\"\"}}")]
    [InlineData("{\"scripts\":{\"lint\":\"   \"}}")]
    public void TryResolve_NonStringOrEmptyScript_ReturnsNull(string packageJson)
    {
        ConfigurePackageJson(packageJson);

        var resolved = _resolver.TryResolve(Invocation("pnpm", ["lint"], "pnpm lint"));

        Assert.Null(resolved);
    }

    private ResolvedPackageScript Resolve(
        string executable,
        IReadOnlyList<string> arguments,
        string originalCommand,
        string? workingDirectory = WorkingDirectory)
    {
        var resolved = _resolver.TryResolve(Invocation(executable, arguments, originalCommand, workingDirectory));
        return Assert.IsType<ResolvedPackageScript>(resolved);
    }

    private static CommandInvocation Invocation(
        string executable,
        IReadOnlyList<string> arguments,
        string originalCommand,
        string? workingDirectory = WorkingDirectory) =>
        new()
        {
            Executable = executable,
            Arguments = arguments,
            OriginalCommand = originalCommand,
            WorkingDirectory = workingDirectory,
        };

    private void ConfigureScripts(params (string Name, string Command)[] scripts) =>
        ConfigureScripts(WorkingDirectory, scripts);

    private void ConfigureScripts(string workingDirectory, params (string Name, string Command)[] scripts)
    {
        var scriptMap = scripts.ToDictionary(script => script.Name, script => script.Command);
        ConfigurePackageJson(JsonSerializer.Serialize(new { scripts = scriptMap }), workingDirectory);
    }

    private void ConfigurePackageJson(string json, string workingDirectory = WorkingDirectory)
    {
        var packageJsonPath = PackageJsonPath(workingDirectory);
        _fileSystem.FileExists(packageJsonPath).Returns(true);
        _fileSystem.ReadAllText(packageJsonPath).Returns(json);
    }

    private static string PackageJsonPath(string workingDirectory) =>
        Path.Combine(workingDirectory, "package.json");
}
