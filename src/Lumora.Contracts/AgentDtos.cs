namespace Lumora.Contracts;

// Protocol between the site and Lumora Agent (the program that runs game servers on a machine).
// The agent keeps one outbound SignalR connection to /agent/hub, authenticated with its key
// (Authorization: Bearer <key>). The site calls agent methods by name (AgentMethods) and gets results back;
// the agent reports state through hub methods (AgentHubMethods).

/// <summary>Names of methods the site invokes on the agent.</summary>
public static class AgentMethods
{
    public const string ApplyConfig = "ApplyConfig";
    public const string RemoveServer = "RemoveServer";
    public const string Install = "Install";
    public const string Start = "Start";
    public const string Stop = "Stop";
    public const string Restart = "Restart";
    public const string Kill = "Kill";
    public const string SendCommand = "SendCommand";
    public const string GetConsole = "GetConsole";
    public const string ListFiles = "ListFiles";
    public const string ReadFile = "ReadFile";
    public const string WriteFile = "WriteFile";
    public const string ListBackups = "ListBackups";
    public const string CreateBackup = "CreateBackup";
    public const string RestoreBackup = "RestoreBackup";
    public const string DeleteBackup = "DeleteBackup";
}

/// <summary>Names of hub methods the agent calls on the site.</summary>
public static class AgentHubMethods
{
    /// <summary>First call after connecting; returns the servers this agent must run.</summary>
    public const string Hello = "Hello";
    public const string Heartbeat = "Heartbeat";
    public const string Status = "Status";
    public const string Console = "Console";
}

public enum ManagedServerState
{
    Unknown,
    NotInstalled,
    Installing,
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
}

/// <summary>The machine the agent runs on.</summary>
public sealed record AgentMachineDto(
    string AgentVersion,
    string Os,
    string Architecture,
    string Hostname,
    int CpuCount,
    long MemoryMb,
    long DiskFreeMb,
    long DiskTotalMb);

/// <summary>POST /api/agent/register: exchanges a one-time join token for the agent key.</summary>
public sealed record AgentRegisterRequest(string Token, string? Name, AgentMachineDto Machine);

public sealed record AgentRegisterResponse(Guid AgentId, string Name, string Key);

/// <summary>Everything the agent needs to run one server. Sent on connect and whenever an admin changes it.</summary>
public sealed record ManagedServerConfigDto(
    Guid ServerId,
    string Name,
    int Port,
    int MemoryMb,
    IReadOnlyList<string> JvmArguments,
    bool AutoStart,
    bool AutoRestart,
    IReadOnlyList<string> RestartTimes,
    IReadOnlyList<string> BackupTimes,
    int BackupsToKeep,
    bool EulaAccepted,
    Guid? BuildId,
    int BuildRevision,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>Live state of one server, reported by the agent on every change and with each heartbeat.</summary>
public sealed record ServerStatusReportDto(
    Guid ServerId,
    ManagedServerState State,
    string? Activity,
    int? ProgressPercent,
    Guid? InstalledBuildId,
    int? InstalledRevision,
    IReadOnlyList<string> Players,
    double CpuPercent,
    long MemoryMb,
    long DiskUsedMb,
    DateTimeOffset? StartedAt,
    string? LastError);

public sealed record AgentHeartbeatDto(AgentMachineDto Machine, double CpuPercent, long MemoryUsedMb, IReadOnlyList<ServerStatusReportDto> Servers);

public sealed record ConsoleLinesDto(Guid ServerId, IReadOnlyList<string> Lines);

/// <summary>Result of a command; Error is a message for the admin in Russian.</summary>
public sealed record AgentResult(bool Ok, string? Error = null)
{
    public static AgentResult Success { get; } = new(true);
    public static AgentResult Fail(string error) => new(false, error);
}

public sealed record ServerFileEntryDto(string Name, bool IsDirectory, long Size, DateTimeOffset Modified, bool IsEditable);

public sealed record ServerFileListDto(bool Ok, string? Error, string Path, IReadOnlyList<ServerFileEntryDto> Entries);

public sealed record ServerFileContentDto(bool Ok, string? Error, string Path, string? Content, bool IsReadOnly);

public sealed record BackupDto(string Name, long Size, DateTimeOffset CreatedAt);

/// <summary>GET /api/agent/servers/{id}/manifest: what to install for a server.</summary>
public sealed record ServerManifestDto(
    Guid ServerId,
    Guid BuildId,
    string BuildName,
    int Revision,
    string MinecraftVersion,
    LoaderType Loader,
    string? LoaderVersion,
    int JavaMajorVersion,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<BuildFileDto> Files,
    IReadOnlyList<SyncRuleDto> SyncRules,
    AuthlibInjectorDto? AuthlibInjector,
    string YggdrasilUrl);
