using System.Text;

namespace Hypa.Cli.Attach.Copy;

/// <summary>Host-TTY OSC 52 yank. Never send this sequence to the pane.</summary>
public static class Osc52Yank
{
    public static byte[] Encode(string text)
    {
        var payload = EncodePayload(text);
        return Encoding.ASCII.GetBytes(payload);
    }

    public static string EncodePayload(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text ?? "");
        var b64 = Convert.ToBase64String(raw);
        return "\u001b]52;c;" + b64 + "\u0007";
    }
}
