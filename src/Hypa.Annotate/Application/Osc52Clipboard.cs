using System.Text;

namespace Hypa.Annotate.Application;

/// <summary>
// / OSC 52 clipboard sequence encoding.
/// </summary>
public static class Osc52Clipboard
{
    public static string EncodePayload(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text ?? "");
        var encoded = Convert.ToBase64String(raw);
        return "\u001b]52;c;" + encoded + "\u0007";
    }
}
