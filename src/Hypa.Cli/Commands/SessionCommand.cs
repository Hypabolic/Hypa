using System.CommandLine;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Cli.Commands;

public sealed class SessionCommand(SessionService sessionService)
{
    public Command Build()
    {
        var cmd = new Command("session", "Manage context sessions.");
        cmd.AddCommand(BuildStatus());
        cmd.AddCommand(BuildInit());
        cmd.AddCommand(BuildAttach());
        cmd.AddCommand(BuildCheckpoint());
        return cmd;
    }

    private Command BuildStatus()
    {
        var cmd = new Command("status", "Show the current session.");
        cmd.SetHandler(async context =>
        {
            var ct = context.GetCancellationToken();
            var result = await sessionService.StatusAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = false }, ct);
            if (result.IsOk)
                PrintSession(result.Value);
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                context.ExitCode = 1;
            }
        });
        return cmd;
    }

    private Command BuildInit()
    {
        var cmd = new Command("init", "Start a new session or resume the latest one for this project.");
        cmd.SetHandler(async context =>
        {
            var ct = context.GetCancellationToken();
            var result = await sessionService.InitAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = true }, ct);
            if (result.IsOk)
                PrintSession(result.Value);
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                context.ExitCode = 1;
            }
        });
        return cmd;
    }

    private Command BuildAttach()
    {
        var idArg = new Argument<string>("session-id", "Session ID to attach to.");
        var cmd = new Command("attach", "Attach to an existing session by ID.");
        cmd.AddArgument(idArg);
        cmd.SetHandler(async context =>
        {
            var idStr = context.ParseResult.GetValueForArgument(idArg);
            var ct = context.GetCancellationToken();
            if (!Guid.TryParse(idStr, out var id))
            {
                Console.Error.WriteLine($"error: invalid session ID '{idStr}'");
                context.ExitCode = 1;
                return;
            }
            var result = await sessionService.AttachAsync(id, ct);
            if (result.IsOk)
                PrintSession(result.Value);
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                context.ExitCode = 1;
            }
        });
        return cmd;
    }

    private Command BuildCheckpoint()
    {
        var cmd = new Command("checkpoint", "Force a checkpoint for the current session.");
        cmd.SetHandler(async context =>
        {
            var ct = context.GetCancellationToken();
            var resolve = await sessionService.StatusAsync(
                new SessionResolveOptions { ProjectRoot = Directory.GetCurrentDirectory(), CreateIfMissing = false }, ct);
            if (!resolve.IsOk)
            {
                Console.Error.WriteLine($"error: {resolve.Error.Message}");
                context.ExitCode = 1;
                return;
            }
            var result = await sessionService.CheckpointAsync(resolve.Value.Id, ct);
            if (result.IsOk)
                Console.WriteLine($"Checkpointed session {result.Value.Id} at {result.Value.CheckpointedAt:O}");
            else
            {
                Console.Error.WriteLine($"error: {result.Error.Message}");
                context.ExitCode = 1;
            }
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
