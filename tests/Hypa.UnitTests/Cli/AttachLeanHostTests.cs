using Hypa.Cli.Mux;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachLeanHostTests
{
    [Fact]
    public void Empty_argv_and_attach_are_the_client_role()
    {
        Assert.True(MuxInvocation.IsAttach([]));
        Assert.True(MuxInvocation.IsAttach(["attach"]));
        Assert.True(MuxInvocation.IsAttach(["--connect-placement", "plc_1"]));
        Assert.True(MuxInvocation.IsAttach(["attach", "--connect-placement", "plc_1"]));
        Assert.True(MuxInvocation.IsAttach(["attach", "--session", "demo"]));
        Assert.True(MuxInvocation.IsAttach(["--session", "named"]));
        Assert.True(MuxInvocation.IsAttach(["--once"]));
        Assert.True(MuxInvocation.IsAttach(["--remote", "host"]));
        Assert.True(MuxInvocation.IsAttach(["--remote=host", "--remote-keybindings", "server"]));
        Assert.True(MuxInvocation.IsAttach(["--remote", "host", "--handoff"]));
    }

    [Fact]
    public void Compression_and_other_commands_are_not_attach()
    {
        Assert.False(MuxInvocation.IsAttach(["-c", "echo hello"]));
        Assert.False(MuxInvocation.IsAttach(["doctor"]));
        Assert.False(MuxInvocation.IsAttach(["mux", "serve"]));
        Assert.False(MuxInvocation.IsAttach(["--help"]));
        Assert.False(MuxInvocation.IsAttach(["code", "index"]));
        Assert.True(MuxInvocation.IsMuxServe(["mux", "serve"]));
    }

    [Fact]
    public void Thin_host_parses_attach_flags()
    {
        AttachHostEntry.Parse(
            ["attach", "--session", "s1", "--cwd", "/tmp/ws", "--once"],
            out var session,
            out var cwd,
            out var once,
            out var sessionSet,
            out var connectPlacement);
        Assert.Equal("s1", session);
        Assert.Equal("/tmp/ws", cwd);
        Assert.True(once);
        Assert.True(sessionSet);
        Assert.Null(connectPlacement);
        Assert.True(AttachHostEntry.HasHelp(["attach", "--help"]));
        Assert.False(AttachHostEntry.HasHelp(["attach", "--session", "s1"]));
    }

    [Fact]
    public void Lean_attach_exec_skips_compression_and_non_hypa_hosts()
    {
        Assert.Equal("hypa-attach", MuxInvocation.LeanAttachFileName);
        Assert.False(MuxInvocation.TryExecLeanAttach(["-c", "echo"]));
        Assert.False(MuxInvocation.TryExecLeanAttach(["doctor"]));
        Assert.Null(MuxInvocation.ExecLeanAttachIfProductHypa(["attach"]));
        Assert.False(MuxInvocation.IsProductHypaProcess(Environment.ProcessPath));
        Assert.Null(MuxInvocation.ResolveLeanAttachPath());
    }

    [Fact]
    public void Managed_sdk_apphost_beside_hypa_dll_does_not_exec_or_fail_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-sdk-apphost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hypa = Path.Combine(root, OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
        File.WriteAllText(hypa, "fixture\n");
        File.WriteAllText(Path.Combine(root, "hypa.dll"), "managed\n");
        try
        {
            Assert.True(MuxInvocation.LooksLikeManagedSdkApphost(hypa));
            Assert.False(MuxInvocation.IsProductHypaProcess(hypa));
            var errors = new StringWriter();
            var code = MuxInvocation.ExecLeanAttachIfProductHypa(
                ["attach"],
                errors,
                hypa,
                root);
            Assert.Null(code);
            Assert.Equal(string.Empty, errors.ToString());
            Assert.Null(MuxInvocation.ResolveLeanAttachPath(hypa, root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void Managed_sdk_apphost_beside_deps_json_does_not_exec_or_fail_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-sdk-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hypa = Path.Combine(root, OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
        File.WriteAllText(hypa, "fixture\n");
        File.WriteAllText(Path.Combine(root, "hypa.deps.json"), "{}\n");
        try
        {
            Assert.True(MuxInvocation.LooksLikeManagedSdkApphost(hypa));
            Assert.False(MuxInvocation.IsProductHypaProcess(hypa));
            var errors = new StringWriter();
            var code = MuxInvocation.ExecLeanAttachIfProductHypa(
                ["attach"],
                errors,
                hypa,
                root);
            Assert.Null(code);
            Assert.Equal(string.Empty, errors.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void Product_hypa_without_sibling_writes_stderr_and_does_not_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-lean-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hypa = Path.Combine(root, OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
        File.WriteAllText(hypa, "fixture\n");
        try
        {
            var errors = new StringWriter();
            var code = MuxInvocation.ExecLeanAttachIfProductHypa(
                ["attach", "--once"],
                errors,
                hypa,
                root);
            Assert.Equal(1, code);
            var text = errors.ToString();
            Assert.Contains("hypa-attach", text, StringComparison.Ordinal);
            Assert.Contains(hypa, text, StringComparison.Ordinal);
            Assert.Contains("does not run inside product hypa", text, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(root), text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void Lean_attach_path_resolves_process_dir_symlink_and_base_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-lean-res-" + Guid.NewGuid().ToString("N"));
        var packed = Path.Combine(root, "packed");
        var linkDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(packed);
        Directory.CreateDirectory(linkDir);
        var attachName = MuxInvocation.LeanAttachSiblingName;
        var hypaName = OperatingSystem.IsWindows() ? "hypa.exe" : "hypa";
        var packedHypa = Path.Combine(packed, hypaName);
        var packedAttach = Path.Combine(packed, attachName);
        File.WriteAllText(packedHypa, "hypa\n");
        File.WriteAllText(packedAttach, "attach\n");
        try
        {
            Assert.Equal(
                Path.GetFullPath(packedAttach),
                MuxInvocation.ResolveLeanAttachPath(packedHypa, baseDirectory: null));

            var baseOnlyHypa = Path.Combine(linkDir, hypaName);
            File.WriteAllText(baseOnlyHypa, "hypa\n");
            Assert.Equal(
                Path.GetFullPath(packedAttach),
                MuxInvocation.ResolveLeanAttachPath(baseOnlyHypa, packed));

            if (!OperatingSystem.IsWindows())
            {
                var linkedHypa = Path.Combine(linkDir, hypaName);
                File.Delete(linkedHypa);
                File.CreateSymbolicLink(linkedHypa, packedHypa);
                var resolved = MuxInvocation.ResolveLeanAttachPath(linkedHypa, baseDirectory: null);
                Assert.Equal(Path.GetFullPath(packedAttach), resolved);
                var dirs = MuxInvocation.LeanAttachSearchDirectories(linkedHypa, null);
                Assert.Contains(Path.GetFullPath(linkDir), dirs);
                Assert.Contains(Path.GetFullPath(packed), dirs);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void Unix_process_replace_returns_false_on_missing_file_and_windows()
    {
        Assert.False(UnixProcessReplace.TryExec("", ["attach"], out var emptyErrno));
        Assert.Equal(0, emptyErrno);
        Assert.False(UnixProcessReplace.TryExec(Path.Combine(Path.GetTempPath(), "missing-hypa-attach"), ["attach"], out var missingErrno));
        Assert.Equal(0, missingErrno);
        if (OperatingSystem.IsWindows())
        {
            var dummy = Path.Combine(Path.GetTempPath(), "hypa-attach-win-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(dummy, "fixture\n");
            try
            {
                Assert.False(UnixProcessReplace.TryExec(dummy, ["attach"], out var winErrno));
                Assert.Equal(0, winErrno);
            }
            finally
            {
                try { File.Delete(dummy); } catch { /* cleanup */ }
            }
        }
    }

    [Fact]
    public void Format_exec_failure_uses_caller_errno_not_process_global_state()
    {
        var first = UnixProcessReplace.FormatExecFailure("/tmp/hypa-attach-a", 8);
        var second = UnixProcessReplace.FormatExecFailure("/tmp/hypa-attach-b", 13);
        Assert.Contains("/tmp/hypa-attach-a", first, StringComparison.Ordinal);
        Assert.Contains("errno 8", first, StringComparison.Ordinal);
        Assert.DoesNotContain("errno 13", first, StringComparison.Ordinal);
        Assert.Contains("/tmp/hypa-attach-b", second, StringComparison.Ordinal);
        Assert.Contains("errno 13", second, StringComparison.Ordinal);
        Assert.DoesNotContain("errno 8", second, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Product_hypa_execve_failure_writes_path_and_errno_and_does_not_fallback()
    {
        Skip.If(OperatingSystem.IsWindows(), "execve is the Unix product path");

        var root = Path.Combine(Path.GetTempPath(), "hypa-lean-execve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hypa = Path.Combine(root, "hypa");
        var attach = Path.Combine(root, MuxInvocation.LeanAttachSiblingName);
        File.WriteAllText(hypa, "fixture\n");
        File.WriteAllText(attach, "not-an-image\n");
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(
            attach,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            Assert.False(MuxInvocation.LooksLikeManagedSdkApphost(hypa));
            Assert.True(MuxInvocation.IsProductHypaProcess(hypa));
            Assert.False(UnixProcessReplace.TryExec(attach, ["attach"], out var errno));
            Assert.NotEqual(0, errno);

            var errors = new StringWriter();
            var code = MuxInvocation.ExecLeanAttachIfProductHypa(
                ["attach", "--once"],
                errors,
                hypa,
                root);
            Assert.Equal(1, code);
            var text = errors.ToString();
            Assert.Contains("execve", text, StringComparison.Ordinal);
            Assert.Contains(attach, text, StringComparison.Ordinal);
            Assert.Contains($"errno {errno}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("does not run inside product hypa", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AttachHostEntry", text, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }
}
