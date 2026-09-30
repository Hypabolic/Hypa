using System.CommandLine;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Cli.Commands;

public sealed class SessionCommand(
    SessionService sessionService,
    MuxAttachService attachService,
    MuxSessionCatalog catalog,
    Hypa.AgentRuntime.Application.IAttachConfigLoader? attachConfig = null)
{
    public Command Build()
    {
        var cmd = new Command("session", "Manage context sessions and mux sessions.");
        cmd.Add(BuildStatus());
        cmd.Add(BuildInit());
        cmd.Add(BuildAttach());
        cmd.Add(BuildCheckpoint());
        cmd.Add(BuildList());
        cmd.Add(BuildStop());
        cmd.Add(BuildDelete());
        return cmd;
    }

    private Command BuildStatus()
    {
        var cmd = new Command("status", "Show the current compression session.");
        cmd.SetAction(async (parseResult, ct) =>
        {
            var result = await sessionService.StatusAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = false }, ct);
            if (result.IsOk)
                PrintSession(result.Value);
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                return 1;
            }

            return 0;
        });
        return cmd;
    }

    private Command BuildInit()
    {
        var cmd = new Command("init", "Start a new session or resume the latest one for this project.");
        cmd.SetAction(async (parseResult, ct) =>
        {
            var result = await sessionService.InitAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = true }, ct);
            if (result.IsOk)
                PrintSession(result.Value);
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                return 1;
            }

            return 0;
        });
        return cmd;
    }

    private Command BuildAttach()
    {
        var idArg = new Argument<string>("session-id") { Description = "Compression session GUID or mux session name." };
        var cmd = new Command("attach", "Attach to a compression session by GUID, or a mux session by name.");
        cmd.Add(idArg);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var idStr = parseResult.GetValue(idArg);
            if (Guid.TryParse(idStr, out var id))
            {
                var result = await sessionService.AttachAsync(id, ct);
                if (result.IsOk)
                    PrintSession(result.Value);
                else
                {
                    Console.Error.WriteLine($"error: {result.Error.Message}");
                    return 1;
                }

                return 0;
            }

            var name = string.IsNullOrWhiteSpace(idStr) ? "default" : idStr;
            return await attachService
                .AttachAsync(name, cwd: null, once: false, socketOverride: null, ct)
                .ConfigureAwait(false);
        });
        return cmd;
    }

    private Command BuildCheckpoint()
    {
        var cmd = new Command("checkpoint", "Force a checkpoint for the current session.");
        cmd.SetAction(async (parseResult, ct) =>
        {
            var resolve = await sessionService.StatusAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = false }, ct);
            if (!resolve.IsOk)
            {
                Console.Error.WriteLine($"error: {resolve.Error.Message}");
                return 1;
            }
            var result = await sessionService.CheckpointAsync(resolve.Value.Id, ct);
            if (result.IsOk)
                Console.WriteLine($"Checkpointed session {result.Value.Id} at {result.Value.CheckpointedAt:O}");
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                return 1;
            }

            return 0;
        });
        return cmd;
    }

    private Command BuildList()
    {
        var cmd = new Command("list", "List mux sessions and whether each is alive.");
        cmd.SetAction(async (_, ct) =>
        {
            var items = await catalog.ListAsync(ct).ConfigureAwait(false);
            if (items.Count == 0)
            {
                Console.WriteLine("No mux sessions.");
                return 0;
            }

            Console.WriteLine("name\tpid\talive\tsocket");
            foreach (var item in items)
            {
                Console.WriteLine(
                    $"{item.Name}\t{item.Pid}\t{(item.Alive ? "true" : "false")}\t{item.Socket}");
            }

            return 0;
        });
        return cmd;
    }

    private Command BuildStop()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Mux session name. Default is HYPA_SESSION or default.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var cmd = new Command("stop", "Stop a mux session with server.stop. Detach does not call this.");
        cmd.Add(nameArg);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameArg);
            var sessionExplicit = !string.IsNullOrWhiteSpace(name);
            if (!Hypa.AgentRuntime.Application.AttachConfigErrors.TryLoad(
                    attachConfig, Console.Error, out var config))
            {
                return 1;
            }

            if (!sessionExplicit)
            {
                name = Hypa.AgentRuntime.Application.AttachSessionResolver.Resolve(
                    sessionOption: null, config);
            }

            return await MuxCommand.StopAsync(name ?? "default", socketOverride: null, sessionExplicit)
                .ConfigureAwait(false);
        });
        return cmd;
    }

    private Command BuildDelete()
    {
        var nameArg = new Argument<string>("name") { Description = "Mux session name to delete locally." };
        var cmd = new Command("delete", "Delete a stopped mux session directory. Refuses if ping succeeds.");
        cmd.Add(nameArg);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var name = parseResult.GetValue(nameArg);
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("error: session name is required");
                return 1;
            }

            string socket;
            try
            {
                socket = UnixSocketServer.ResolveSocketPath(name, honorEnvironment: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }

            var ping = await MuxControlPlane.TryPingAsync(socket, ct).ConfigureAwait(false);
            if (ping is not null)
            {
                Console.Error.WriteLine(
                    $"error: mux session '{name}' is alive. Stop it first. Delete does not call server.stop.");
                return 1;
            }

            if (!catalog.TryDeleteSessionDir(name, out var error))
            {
                Console.Error.WriteLine($"error: {error}");
                return 1;
            }

            Console.WriteLine($"Deleted mux session '{name}'.");
            return 0;
        });
        return cmd;
    }

    private static void PrintSession(ContextSession s)
    {
        Console.WriteLine($"id:           {s.Id}");
        Console.WriteLine($"project_root: {s.ProjectRoot}");
        Console.WriteLine($"created_at:   {s.CreatedAt:O}");
        Console.WriteLine($"updated_at:   {s.UpdatedAt:O}");
        if (s.CheckpointedAt.HasValue)
            Console.WriteLine($"checkpoint:   {s.CheckpointedAt:O}");
        Console.WriteLine($"tool_calls:   {s.Stats.ToolCallCount}");
        Console.WriteLine($"file_touches: {s.Stats.FileTouchCount}");
        Console.WriteLine($"tokens_saved: {s.Stats.TokensSaved}");
    }
}
