using System.Text;
using System.Text.Json;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Second-turn continuity after handoff. Asks about a file edited before the move.
/// Does not rewrite JSONL history. Fail closed when history needs a source absolute path.
/// </summary>
public sealed class SecondTurnAsk
{
    private readonly SessionToolPathOracle _oracle;

    public SecondTurnAsk()
        : this(new SessionToolPathOracle())
    {
    }

    public SecondTurnAsk(SessionToolPathOracle oracle)
    {
        _oracle = oracle ?? throw new ArgumentNullException(nameof(oracle));
    }

    public SecondTurnResult Run(SecondTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestCwd);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceCwd);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RelativeFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        if (!File.Exists(request.SessionFile))
        {
            return SecondTurnResult.Fail(
                ContinuityReasons.Internal,
                "dest session file missing",
                [],
                HistoryLineCount(request.SessionFile));
        }

        var historyLineCount = HistoryLineCount(request.SessionFile);
        var history = _oracle.Read(request.SessionFile);
        var historyPaths = history.Select(p => p.Path).ToArray();
        var matching = history
            .Where(p => SessionToolPathOracle.NamesFile(p.Path, request.RelativeFile))
            .ToArray();
        if (matching.Length == 0)
        {
            return SecondTurnResult.Fail(
                ContinuityReasons.Internal,
                "edited file is absent from dest JSONL tool calls",
                historyPaths,
                historyLineCount);
        }

        foreach (var tool in matching)
        {
            if (SessionToolPathOracle.IsUnderWorkspace(tool.Path, request.DestCwd))
                continue;

            if (SessionToolPathOracle.IsUnderWorkspace(tool.Path, request.SourceCwd))
            {
                return SecondTurnResult.Fail(
                    ContinuityReasons.SourcePathRequired,
                    "second turn requires the source absolute path",
                    historyPaths,
                    historyLineCount);
            }

            return SecondTurnResult.Fail(
                ContinuityReasons.SourcePathRequired,
                "second-turn tool path is outside dest workspace",
                historyPaths,
                historyLineCount);
        }

        var destFile = Path.GetFullPath(Path.Combine(request.DestCwd, request.RelativeFile));
        if (!SessionToolPathOracle.IsUnderWorkspace(destFile, request.DestCwd)
            || !File.Exists(destFile))
        {
            return SecondTurnResult.Fail(
                ContinuityReasons.Internal,
                "edited dest file missing",
                historyPaths,
                historyLineCount);
        }

        var answer = File.ReadAllText(destFile);
        AppendSecondTurn(request.SessionFile, request.Prompt, destFile, answer);
        var secondTurn = _oracle
            .ReadFromLine(request.SessionFile, historyLineCount + 1)
            .Select(p => p.Path)
            .ToArray();
        if (secondTurn.Any(p => !SessionToolPathOracle.IsUnderWorkspace(p, request.DestCwd)))
        {
            return SecondTurnResult.Fail(
                ContinuityReasons.SourcePathRequired,
                "second-turn tool path is outside dest workspace",
                historyPaths,
                historyLineCount,
                answer,
                secondTurn);
        }

        return new SecondTurnResult
        {
            Outcome = ContinuityOutcome.Success(),
            Answer = answer,
            HistoryToolPaths = historyPaths,
            SecondTurnToolPaths = secondTurn,
            HistoryLineCount = historyLineCount,
        };
    }

    private static int HistoryLineCount(string sessionFile)
    {
        if (!File.Exists(sessionFile))
            return 0;
        return File.ReadAllLines(sessionFile).Length;
    }

    private static void AppendSecondTurn(
        string sessionFile,
        string prompt,
        string destFile,
        string answer)
    {
        var lines = new[]
        {
            WriteObject(("type", "message"), ("role", "user"), ("text", prompt)),
            WriteObject(("type", "tool_call"), ("name", "read"), ("path", destFile)),
            WriteObject(("type", "tool_result"), ("path", destFile), ("content", answer)),
        };
        File.AppendAllText(sessionFile, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static string WriteObject(params (string Name, string Value)[] properties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
                writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>Second-turn ask against a dest session JSONL file.</summary>
public sealed record SecondTurnRequest
{
    public required string SessionFile { get; init; }
    public required string DestCwd { get; init; }
    public required string SourceCwd { get; init; }
    public required string RelativeFile { get; init; }
    public required string Prompt { get; init; }
}

/// <summary>Machine-readable second-turn result. The model answer is an operator note.</summary>
public sealed record SecondTurnResult
{
    public required ContinuityOutcome Outcome { get; init; }
    public string? Answer { get; init; }
    public required IReadOnlyList<string> HistoryToolPaths { get; init; }
    public IReadOnlyList<string> SecondTurnToolPaths { get; init; } = [];
    public required int HistoryLineCount { get; init; }

    public bool Ok => Outcome.Ok;
    public string? Reason => Outcome.Reason;
    public string? Detail => Outcome.Detail;

    public static SecondTurnResult Fail(
        string reason,
        string detail,
        IReadOnlyList<string> historyToolPaths,
        int historyLineCount,
        string? answer = null,
        IReadOnlyList<string>? secondTurnToolPaths = null) =>
        new()
        {
            Outcome = ContinuityOutcome.Failure(reason, detail),
            Answer = answer,
            HistoryToolPaths = historyToolPaths,
            SecondTurnToolPaths = secondTurnToolPaths ?? [],
            HistoryLineCount = historyLineCount,
        };
}
