using System.Text.Json;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.State;

public sealed record AgentConfig(string ServerUrl, string DeviceId, string Token)
{
    /// <summary>https://host → wss://host/ws/agent</summary>
    public Uri WebSocketUri =>
        new(System.Text.RegularExpressions.Regex.Replace(ServerUrl.TrimEnd('/'), "^http", "ws") + "/ws/agent");
}

/// <summary>페어링 결과 (서버 주소, 기기 ID, 기기 토큰)</summary>
public static class AgentConfigStore
{
    public static AgentConfig? Load()
    {
        try
        {
            return File.Exists(AgentPaths.ConfigFile)
                ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(AgentPaths.ConfigFile), AgentJson.Options)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(AgentConfig config)
    {
        AgentPaths.EnsureSecureRoot();
        AgentPaths.WriteAtomic(AgentPaths.ConfigFile, JsonSerializer.Serialize(config, AgentJson.Options));
    }
}
