using System.Security.AccessControl;
using System.Security.Principal;

namespace XKeep.Agent.Service.State;

/// <summary>
/// %ProgramData%\xKeep — 정책 캐시, 기기 토큰, 사용량.
/// SYSTEM 과 Administrators 만 접근 가능하도록 ACL 을 설정해 자녀(표준 사용자)가
/// 토큰을 훔치거나 사용량 파일을 지워 시간을 초기화하지 못하게 한다.
/// </summary>
public static class AgentPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "xKeep");

    public static string ConfigFile => Path.Combine(Root, "agent.json");
    public static string PolicyFile => Path.Combine(Root, "policy.json");
    public static string UsageFile => Path.Combine(Root, "usage.json");

    public static void EnsureSecureRoot()
    {
        var dir = new DirectoryInfo(Root);
        if (!dir.Exists) dir.Create();

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // 상속(Users 읽기) 제거
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        dir.SetAccessControl(security);
    }

    /// <summary>임시 파일에 쓰고 교체 → 전원 차단 시에도 파일이 반쯤 쓰인 상태로 남지 않음</summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
