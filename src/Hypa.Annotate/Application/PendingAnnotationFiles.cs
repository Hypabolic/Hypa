using System.Text.Json;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Pending capture files under the plugin state directory.
/// </summary>
public static class PendingAnnotationFiles
{
    public static Result<string, string> WritePending(string stateDirectory, PendingAnnotation pending)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fileName = $"pending-{millis}-{Environment.ProcessId}.json";
            var path = Path.GetFullPath(Path.Combine(stateDirectory, fileName));
            var json = JsonSerializer.Serialize(pending, AnnotateJsonContext.Default.PendingAnnotation);
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

    public static Result<PendingAnnotation, string> ReadPending(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var pending = JsonSerializer.Deserialize(text, AnnotateJsonContext.Default.PendingAnnotation);
            if (pending is null
                || string.IsNullOrEmpty(pending.CapturedAt)
                || string.IsNullOrEmpty(pending.SelectedText))
            {
                return Result<PendingAnnotation, string>.Fail("Pending annotation is invalid");
            }

            return Result<PendingAnnotation, string>.Ok(pending);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return Result<PendingAnnotation, string>.Fail(error.Message);
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
