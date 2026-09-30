using System.Text.Json;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Pending last-review files under the plugin state directory.
/// </summary>
public static class PendingLastReviewFiles
{
    public static Result<string, string> WritePending(string stateDirectory, PendingLastReview pending)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fileName = $"last-pending-{millis}-{Environment.ProcessId}.json";
            var path = Path.GetFullPath(Path.Combine(stateDirectory, fileName));
            var json = JsonSerializer.Serialize(pending, AnnotateJsonContext.Default.PendingLastReview);
            File.WriteAllText(path, json + "\n");
            return Result<string, string>.Ok(path);
        }
        catch (IOException error)
        {
            return Result<string, string>.Fail(error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return Result<string, string>.Fail(error.Message);
        }
    }

    public static Result<PendingLastReview, string> ReadPending(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var pending = JsonSerializer.Deserialize(text, AnnotateJsonContext.Default.PendingLastReview);
            if (pending is null
                || string.IsNullOrEmpty(pending.CapturedAt)
                || string.IsNullOrEmpty(pending.AgentMessage)
                || string.IsNullOrWhiteSpace(pending.TargetPaneId))
            {
                return Result<PendingLastReview, string>.Fail("Pending last review is invalid");
            }

            return Result<PendingLastReview, string>.Ok(pending);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<PendingLastReview, string>.Fail(error.Message);
        }
    }

    public static Result<Unit, string> RemovePending(string path)
    {
        try
        {
            File.Delete(path);
            return Result<Unit, string>.Ok(default);
        }
        catch (FileNotFoundException)
        {
            return Result<Unit, string>.Ok(default);
        }
        catch (IOException error)
        {
            return Result<Unit, string>.Fail(error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return Result<Unit, string>.Fail(error.Message);
        }
    }
}
