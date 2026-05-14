namespace Hypa.Runtime.Domain.Hooks;

[Flags]
public enum HarnessCapability
{
    None = 0,
    PreToolUse = 1,
    RulesFileSupport = 2,
    McpServer = 4,
}
