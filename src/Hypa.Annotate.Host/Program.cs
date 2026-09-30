using System.Diagnostics;
using System.Text.Json;
using Hypa.Annotate.Application;
using Hypa.Annotate.Application.Tui;
using Hypa.Annotate.Domain;

// Separate annotate process.
// Not a product brand. User command stays hypa plugin action.
return AnnotateProgram.Run(args);

internal static class AnnotateProgram
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("hypa-annotate capture|copy-context|copy-archive|editor|manage|manager|last|last-review|doctor");
            return 0;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: hypa-annotate capture|copy-context|copy-archive|editor|manage|manager|last|last-review|doctor");
            return 4;
        }

        return args[0] switch
        {
            "copy-context" => RunCopyContext(),
            "copy-archive" => RunCopyArchive(),
            "capture" => RunCapture(),
            "editor" => RunEditor(),
            "manage" => RunManage(),
            "manager" => RunManager(),
            "last" => RunLast(),
            "last-review" => RunLastReview(),
            "doctor" => RunDoctor(),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: hypa-annotate capture|copy-context|copy-archive|editor|manage|manager|last|last-review|doctor");
        return 4;
    }

    private static int RunCopyContext()
    {
        var stateDirectory = RequireEnv(AnnotateEnv.StateDir);
        if (stateDirectory is null)
            return 1;

        var outcome = AnnotateCopyContext.Run(stateDirectory, new NativeClipboardWriter());
        if (!outcome.IsOk)
        {
            Console.Error.WriteLine(outcome.Error);
            return 1;
        }

        if (outcome.Value.Kind == CopyContextKind.Empty)
        {
            Notify(AnnotateCopyContext.EmptyTitle, AnnotateCopyContext.EmptyBody);
            return 0;
        }

        Notify(AnnotateCopyContext.CopiedTitle, AnnotateCopyContext.CopiedBody(outcome.Value.Count));
        return 0;
    }

    private static int RunCopyArchive()
    {
        var stateDirectory = RequireEnv(AnnotateEnv.StateDir);
        if (stateDirectory is null)
            return 1;

        var outcome = AnnotateCopyArchiveService.Run(new CopyArchiveRequest
        {
            StateDirectory = stateDirectory,
            ClipboardWriter = new NativeClipboardWriter(),
            Osc52Emitter = RejectedOsc52Emitter.Instance,
        });
        if (!outcome.IsOk)
        {
            Console.Error.WriteLine(outcome.Error);
            Notify("Copy and archive failed", outcome.Error);
            return 1;
        }

        var report = outcome.Value;
        if (report.IsError)
            Console.Error.WriteLine(report.Line);
        else
            Console.Out.WriteLine(report.Line);
        Notify(report.Title, report.Body);
        return report.ExitCode;
    }

    private static int RunCapture()
    {
        var stateDirectory = RequireEnv(AnnotateEnv.StateDir);
        var pluginRoot = RequireEnv(AnnotateEnv.PluginRoot);
        if (stateDirectory is null || pluginRoot is null)
            return 1;

        var targetPaneId = Environment.GetEnvironmentVariable(AnnotateEnv.PaneId) ?? "";
        var contextJson = Environment.GetEnvironmentVariable(AnnotateEnv.ContextJson);
        var handoff = SelectionHandoff.TakeDefaultHandoff(DateTimeOffset.UtcNow);
        if (!handoff.IsOk)
        {
            Console.Error.WriteLine(handoff.Error);
            Notify("Annotate failed", handoff.Error);
            return 1;
        }

        var result = AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = stateDirectory,
                PluginRoot = pluginRoot,
                TargetPaneId = targetPaneId,
                ContextJson = contextJson,
                HandoffText = handoff.Value,
                ClipboardReader = new NativeClipboardReader(),
                PaneOpener = new HypaBinPluginPaneOpener(),
            },
            CancellationToken.None).GetAwaiter().GetResult();

        if (!result.IsOk)
        {
            Console.Error.WriteLine(result.Error);
            Notify("Annotate failed", result.Error);
            return 1;
        }

        if (result.Value == AnnotateCaptureCompletion.BlankSelection)
        {
            Console.Out.WriteLine(AnnotateCaptureService.BlankSelectionMessage);
            Notify(AnnotateCaptureService.BlankSelectionTitle, AnnotateCaptureService.BlankSelectionBody);
        }

        return 0;
    }

    private static int RunEditor()
    {
        var stateDirectory = RequireEnv(AnnotateEnv.StateDir);
        if (stateDirectory is null)
            return 1;

        var pendingPath = Environment.GetEnvironmentVariable(AnnotateEnv.PendingPath);
        if (string.IsNullOrWhiteSpace(pendingPath))
        {
            Console.Error.WriteLine("Missing pending annotation");
            return 1;
        }

        var loaded = PendingAnnotationFiles.ReadPending(pendingPath);
        if (!loaded.IsOk)
        {
            Console.Error.WriteLine(loaded.Error);
            return 1;
        }

        _ = PendingAnnotationFiles.RemovePending(pendingPath);
        var app = new AnnotateEditorApp(loaded.Value);
        var code = PluginTuiLoop.RunEditor(app, stateDirectory, saveToStore: true);
        return app.Saved || app.Quit ? 0 : code;
    }

    private static int RunManage()
    {
        var pluginRoot = RequireEnv(AnnotateEnv.PluginRoot);
        if (pluginRoot is null)
            return 1;
        var targetPaneId = Environment.GetEnvironmentVariable(AnnotateEnv.PaneId) ?? "";
        var opened = AnnotateManageService.RunAsync(
            new AnnotateManageRequest
            {
                PluginRoot = pluginRoot,
                TargetPaneId = targetPaneId,
                PaneOpener = new HypaBinPluginPaneOpener(),
            },
            CancellationToken.None).GetAwaiter().GetResult();
        if (!opened.IsOk)
        {
            Console.Error.WriteLine(opened.Error);
            Notify("Unable to open annotations", opened.Error);
            return 1;
        }

        return 0;
    }

    private static int RunManager()
    {
        var stateDirectory = RequireEnv(AnnotateEnv.StateDir);
        if (stateDirectory is null)
            return 1;

        var app = new AnnotateManagerApp(
            stateDirectory,
            new NativeClipboardWriter(),
            new ConsoleOsc52Emitter(Console.Out));
        return PluginTuiLoop.RunManager(app);
    }

    private static int RunLast()
    {
        var stateDirectory = RequireEnv(AnnotateEnv.StateDir);
        var pluginRoot = RequireEnv(AnnotateEnv.PluginRoot);
        if (stateDirectory is null || pluginRoot is null)
            return 1;
        var targetPaneId = Environment.GetEnvironmentVariable(AnnotateEnv.PaneId) ?? "";
        var contextJson = Environment.GetEnvironmentVariable(AnnotateEnv.ContextJson);
        var result = AnnotateLastService.RunAsync(
            new AnnotateLastRequest
            {
                StateDirectory = stateDirectory,
                PluginRoot = pluginRoot,
                TargetPaneId = targetPaneId,
                ContextJson = contextJson,
                PaneOpener = new HypaBinPluginPaneOpener(),
            },
            CancellationToken.None).GetAwaiter().GetResult();
        if (!result.IsOk)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        return 0;
    }

    private static int RunLastReview()
    {
        var result = AnnotateLastReviewService.RunAsync(
            new AnnotateLastReviewRequest
            {
                PendingPath = Environment.GetEnvironmentVariable(AnnotateEnv.LastReviewPath),
                ReviewSession = new TuiLastReviewSession(),
                PaneSender = new HypaBinPluginPaneSender(),
            },
            CancellationToken.None).GetAwaiter().GetResult();
        if (!result.IsOk)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }

        return result.Value;
    }

    private static int RunDoctor()
    {
        WriteDoctor(AnnotateDoctorService.Report(Environment.GetEnvironmentVariable(AnnotateEnv.StateDir)));
        return 0;
    }

    private static void WriteDoctor(PluginDoctorStdout report)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(report, AnnotateJsonContext.Default.PluginDoctorStdout));
    }

    private static string? RequireEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        Console.Error.WriteLine(name + " is not set");
        return null;
    }

    /// <summary>
    // / Best-effort toast.
    /// Hypa CLI uses <c>notification show --title TEXT</c>.
    /// </summary>
    internal static void Notify(string title, string? body)
    {
        var bin = Environment.GetEnvironmentVariable(AnnotateEnv.BinPath);
        if (string.IsNullOrWhiteSpace(bin))
            return;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var pluginId = Environment.GetEnvironmentVariable(AnnotateEnv.PluginId);
            if (string.IsNullOrWhiteSpace(pluginId))
                pluginId = AnnotateEnv.AnnotatePluginId;
            foreach (var argument in HypaBinNotifier.BuildShowArguments(title, body, pluginId))
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
