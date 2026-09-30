namespace Hypa.Placement.Infrastructure;

internal static class RemoteMuxCommands
{
    internal static string RemoteBridgeCommand(string session) =>
        session == "default"
            ? "exec hypa remote-client-bridge"
            : "exec hypa remote-client-bridge --session "
              + OpenSshArgumentBuilder.Quote(session);

    internal static string RemoteStartCommand(string session) =>
        "hypa mux serve --session "
        + OpenSshArgumentBuilder.Quote(session)
        + " </dev/null >/dev/null 2>&1 &";

    internal static string LiveHandoffCommand(string session) =>
        "hypa mux live-handoff --session " + OpenSshArgumentBuilder.Quote(session);
}
