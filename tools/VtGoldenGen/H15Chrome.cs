using System.Text;

namespace VtGoldenGen;

/// <summary>
/// Reconstructed 120×40 Codex / Claude TUI streams for.
/// Capture kind is reconstructed_chrome. This is not an offline PTY recording.
/// Generic box TUI: alt-screen, DECSTBM, CUP footer, box drawing, RGB SGR,
/// synchronized update, hide-cursor, DECSC/DECRC.
/// </summary>
internal static class H15Chrome
{
    public const int Cols = 120;
    public const int Rows = 40;
    public const int ScrollBottom1Based = 36;
    public const string CodexVersion = "0.147.0";
    public const string ClaudeVersion = "2.1.233";

    public static string CodexWorking() => CodexFrame(
        body:
        [
            "user: add a health check to the mux",
            "",
            "I'll inspect src/Program.cs first.",
            "",
            "Tool call  read_file  path=src/Program.cs",
            "",
            "The file exposes a doctor command. Next I will add a ready probe.",
        ],
        footerTitle: "working",
        footerLine1: "Thinking about the next edit",
        footerLine2: "Running ████████░░░░  esc to interrupt",
        lastLine: null);

    public static string CodexBlocked() => CodexFrame(
        body:
        [
            "user: apply the health-check patch",
            "",
            "Proposed command: apply patch src/Program.cs",
            "",
            "The command writes a ready probe next to doctor.",
        ],
        footerTitle: "approval",
        footerLine1: "Needs your approval  ·  Do you want to proceed with this command? (y/n)",
        footerLine2: "[Y] yes    [n] no",
        lastLine: null);

    public static string CodexIdle() => CodexFrame(
        body: LowerBody(
            ["user: add a health check to the mux"],
            [
                "Thinking about the next edit",
                "Tool call  read_file  path=src/Program.cs",
                "Running ████████░░░░",
                "esc to interrupt",
                "",
                "Ready for the next instruction.",
            ]),
        footerTitle: "idle",
        footerLine1: "gpt-5.6-luna high · Workspace · Context 0% used",
        footerLine2: "/tmp/hypa-fixture",
        lastLine: "› ");

    public static string CodexDone() => CodexFrame(
        body:
        [
            "user: add a health check to the mux",
            "",
            "Added a ready probe next to doctor.",
            "",
            "All tasks complete.",
        ],
        footerTitle: "done",
        footerLine1: "gpt-5.6-luna high · Workspace · Context 0% used",
        footerLine2: "/tmp/hypa-fixture",
        lastLine: "Done.");

    public static string ClaudeWorking() => ClaudeFrame(
        body:
        [
            "> add a health check to the mux",
            "",
            "Thinking through the change",
            "",
            "Invoking Read  src/Program.cs",
            "",
            "Generating a patch ████",
        ],
        footerTitle: "working",
        footerLine1: "Thinking through the change",
        footerLine2: "Generating a patch ████",
        lastLine: null);

    public static string ClaudeBlocked() => ClaudeFrame(
        body:
        [
            "> apply the health-check patch",
            "",
            "Allow this action to edit src/Program.cs?",
        ],
        footerTitle: "approval",
        footerLine1: "Allow this action to edit src/Program.cs?",
        footerLine2: "Do you want to proceed? (y/n)",
        lastLine: null);

    public static string ClaudeIdle() => ClaudeFrame(
        body: LowerBody(
            ["> add a health check to the mux"],
            [
                "Thinking through the change",
                "Invoking Read  src/Program.cs",
                "Generating a patch ████",
                "",
                "Settled. Waiting at the prompt.",
            ]),
        footerTitle: "idle",
        footerLine1: "/tmp/hypa-fixture  08:02:29  Opus 5",
        footerLine2: "auto mode (shift+tab to cycle)",
        lastLine: "❯ ");

    public static string ClaudeDone() => ClaudeFrame(
        body:
        [
            "> add a health check to the mux",
            "",
            "Completed the requested change.",
        ],
        footerTitle: "done",
        footerLine1: "/tmp/hypa-fixture  08:02:29  Opus 5",
        footerLine2: "auto mode (shift+tab to cycle)",
        lastLine: "Completed");

    public static string ChromeKindCodex() => CodexFrame(
        workspace: "/tmp/hypa-chrome-kind",
        body:
        [
            "user: list the fixture workspace",
            "",
            "Tool call  list_dir  path=/tmp/hypa-chrome-kind",
            "",
            "Thinking about the directory listing",
        ],
        footerTitle: "working",
        footerLine1: "Thinking about the directory listing",
        footerLine2: "Running ████░░░░  esc to interrupt",
        lastLine: null);

    public static string ChromeKindClaude() => ClaudeFrame(
        workspace: "/tmp/hypa-claude-kind",
        body:
        [
            "> find the mux entrypoint",
            "",
            "Invoking Glob  **/*Program.cs",
            "",
            "Thinking through the match list",
        ],
        footerTitle: "working",
        footerLine1: "Thinking through the match list",
        footerLine2: "Invoking Glob ████",
        lastLine: null);

    public static string LeftoverWorkingIdle() => CodexFrame(
        workspace: "/tmp/hypa-leftover-idle",
        body: LowerBody(
            ["user: inspect the leftover idle case"],
            [
                "Thinking about an old step",
                "esc to interrupt",
                "Running ████",
                "",
                "The previous turn settled.",
            ]),
        footerTitle: "idle",
        footerLine1: "gpt-5.6-luna high · Workspace · Context 0% used",
        footerLine2: "previous turn settled",
        lastLine: "› ");

    public static string LeftoverWorkingDone() => ClaudeFrame(
        workspace: "/tmp/hypa-leftover-done",
        body: LowerBody(
            ["> finish the leftover done case"],
            [
                "Generating a patch ████",
                "Thinking through the last edit",
                "",
                "Completed the leftover-done request.",
            ]),
        footerTitle: "done",
        footerLine1: "/tmp/hypa-leftover-done  08:02:29  Opus 5",
        footerLine2: "auto mode (shift+tab to cycle)",
        lastLine: "Completed");

    public static string LeftoverBlockedIdle() => CodexFrame(
        workspace: "/tmp/hypa-leftover-blocked",
        body: LowerBody(
            ["user: apply a leftover blocked case"],
            [
                "Needs your approval  ·  Do you want to proceed with this command? (y/n)",
                "",
                "The previous approval settled.",
            ]),
        footerTitle: "idle",
        footerLine1: "gpt-5.6-luna high · Workspace · Context 0% used",
        footerLine2: "/tmp/hypa-leftover-blocked",
        lastLine: "› ");

    public static string LeftoverEllipsisIdle() => ClaudeFrame(
        workspace: "/tmp/hypa-leftover-ellipsis",
        body: LowerBody(
            ["> show what's new"],
            [
                "What's new  ·  Added GitLab merge request URL support…",
                "Added an opt-in forward_user_identity apps gateway setting…",
                "Opus 5 · Claude Pro · connecting…",
                "",
                "Settled at the prompt.",
            ]),
        footerTitle: "idle",
        footerLine1: "/tmp/hypa-leftover-ellipsis  08:02:29  Opus 5",
        footerLine2: "auto mode (shift+tab to cycle)",
        lastLine: "❯ ");

    public static string CursorWorking() => Frame(
        title: "Cursor Agent",
        subtitle: "workspace /tmp/hypa-cursor",
        rgbR: 100,
        rgbG: 180,
        rgbB: 120,
        body:
        [
            "user: inspect the cursor chrome",
            "",
            "Thinking about the next tool",
            "Running ████",
        ],
        footerTitle: "working",
        footerLine1: "Thinking about the next tool",
        footerLine2: "Running ████",
        lastLine: null);

    public static string UnknownIdle() =>
        "random idle text\r\n"
        + "no agent chrome here\r\n"
        + "still waiting without a prompt\r\n";

    public static string StaleThinking()
    {
        var sb = new StringBuilder();
        sb.Append("Thinking about an old step\r\n");
        for (var i = 0; i < 30; i++)
            sb.Append("filler line ").Append(i).Append(" settled output\r\n");
        sb.Append("random idle text after the window\r\n");
        return sb.ToString();
    }

    public static string ShellOnly() =>
        "user@host:/tmp/hypa-fixture $ ";

    public static string CompletedCountInProgress() =>
        "OpenAI Codex\r\n"
        + "Completed 3 of 10 tools\r\n";

    private static string CodexFrame(
        string[] body,
        string footerTitle,
        string? footerLine1,
        string? footerLine2,
        string? lastLine,
        string workspace = "/tmp/hypa-fixture") =>
        Frame(
            title: "OpenAI Codex  " + CodexVersion,
            subtitle: "workspace " + workspace,
            rgbR: 88,
            rgbG: 166,
            rgbB: 255,
            body: body,
            footerTitle: footerTitle,
            footerLine1: footerLine1,
            footerLine2: footerLine2,
            lastLine: lastLine);

    private static string ClaudeFrame(
        string[] body,
        string footerTitle,
        string? footerLine1,
        string? footerLine2,
        string? lastLine,
        string workspace = "/tmp/hypa-fixture") =>
        Frame(
            title: "Claude Code  " + ClaudeVersion + "  Anthropic",
            subtitle: workspace,
            rgbR: 217,
            rgbG: 119,
            rgbB: 87,
            body: body,
            footerTitle: footerTitle,
            footerLine1: footerLine1,
            footerLine2: footerLine2,
            lastLine: lastLine);

    private static string Frame(
        string title,
        string subtitle,
        int rgbR,
        int rgbG,
        int rgbB,
        string[] body,
        string footerTitle,
        string? footerLine1,
        string? footerLine2,
        string? lastLine)
    {
        var sb = new StringBuilder(4096);
        sb.Append("\u001b[?1049h");
        sb.Append("\u001b[?25l");
        sb.Append("\u001b[?2026h");
        sb.Append("\u001b[1;").Append(ScrollBottom1Based).Append('r');
        sb.Append("\u001b[H\u001b[2J");
        sb.Append("\u001b7");

        var fg = Fg(rgbR, rgbG, rgbB);
        PaintBoxTop(sb, 1, title, fg);
        PaintBoxInner(sb, 2, subtitle, fg);
        PaintBoxBottom(sb, 3, fg);

        for (var i = 0; i < body.Length; i++)
            PaintPlain(sb, 5 + i, body[i]);

        sb.Append("\u001b8");
        if (lastLine is not null)
            PaintPlain(sb, 36, lastLine);
        PaintBoxTop(sb, 37, footerTitle, fg);
        PaintBoxInner(sb, 38, footerLine1 ?? string.Empty, fg);
        PaintBoxInner(sb, 39, footerLine2 ?? string.Empty, fg);
        PaintBoxBottom(sb, 40, fg);

        sb.Append("\u001b[?2026l");
        return sb.ToString();
    }

    private static string Fg(int r, int g, int b) =>
        "\u001b[38;2;" + r + ";" + g + ";" + b + "m";

    private static string Cup(int row, int col) =>
        "\u001b[" + row + ";" + col + "H";

    private static void PaintBoxTop(StringBuilder sb, int row, string title, string fg)
    {
        var label = "─ " + title + " ";
        var rest = Cols - 2 - label.Length;
        if (rest < 1)
        {
            label = Fit(title, Cols - 2);
            rest = 0;
        }

        sb.Append(Cup(row, 1));
        sb.Append(fg);
        sb.Append('┌');
        sb.Append(label);
        sb.Append(new string('─', rest));
        sb.Append('┐');
        sb.Append("\u001b[0m");
    }

    private static void PaintBoxInner(StringBuilder sb, int row, string text, string fg)
    {
        sb.Append(Cup(row, 1));
        sb.Append(fg);
        sb.Append('│');
        sb.Append(Fit(text, Cols - 2));
        sb.Append('│');
        sb.Append("\u001b[0m");
    }

    private static void PaintBoxBottom(StringBuilder sb, int row, string fg)
    {
        sb.Append(Cup(row, 1));
        sb.Append(fg);
        sb.Append('└');
        sb.Append(new string('─', Cols - 2));
        sb.Append('┘');
        sb.Append("\u001b[0m");
    }

    private static void PaintPlain(StringBuilder sb, int row, string text)
    {
        sb.Append(Cup(row, 1));
        sb.Append("\u001b[K");
        sb.Append(Fit(text, Cols));
    }

    private static string Fit(string text, int width)
    {
        if (text.Length == width)
            return text;
        if (text.Length > width)
            return text[..width];
        return text.PadRight(width);
    }

    // Body starts at row 5. Keep leftover Working markers inside the last 24 rows.
    private static string[] LowerBody(string[] head, string[] tail, int startRow = 5, int targetTailRow = 22)
    {
        var pad = targetTailRow - startRow - head.Length;
        if (pad < 0)
            pad = 0;
        var list = new List<string>(head.Length + pad + tail.Length);
        list.AddRange(head);
        for (var i = 0; i < pad; i++)
            list.Add(string.Empty);
        list.AddRange(tail);
        return list.ToArray();
    }
}
