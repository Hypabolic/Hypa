namespace Hypa.Runtime.Domain.Projects;

public sealed record ProjectRegistration(
    string RootPath,
    string AgentKey,
    DateTimeOffset InstalledAt);
