using System.Runtime.InteropServices;

namespace XKeep.Agent.Service.Native;

public readonly record struct SessionInfo(uint SessionId, string? UserName, bool IsActive, bool IsLocked)
{
    public bool HasUser => !string.IsNullOrEmpty(UserName);
}

/// <summary>
/// Remote Desktop Services(WTS) API. 서비스(Session 0)에서 사용자 세션 상태를 조회·제어한다.
///
/// 주의: LockWorkStation() 은 "호출한 프로세스의 세션"을 잠그므로 Session 0 서비스에서 호출하면
/// 아무 효과가 없다. 그래서 화면 잠금은 사용자 세션의 UserHost 가 하고,
/// 서비스는 최후 수단으로 WTSDisconnectSession(로그인 화면으로 돌려보냄)을 쓴다.
/// </summary>
public static class WtsApi
{
    private static readonly IntPtr CurrentServer = IntPtr.Zero;
    public const uint NoSession = 0xFFFFFFFF;

    private enum WtsInfoClass
    {
        WTSUserName = 5,
        WTSConnectState = 8,
        WTSSessionInfoEx = 25,
    }

    private enum WtsConnectState { Active = 0, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }

    private const int WTS_SESSIONSTATE_LOCK = 0;

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, WtsInfoClass infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    /// <summary>현재 콘솔(모니터 앞) 세션의 사용자·잠금 상태</summary>
    public static SessionInfo? GetConsoleSession()
    {
        var id = WTSGetActiveConsoleSessionId();
        if (id == NoSession) return null;

        var user = QueryString(id, WtsInfoClass.WTSUserName);
        var state = QueryInt(id, WtsInfoClass.WTSConnectState, 0);
        // WTSINFOEXW { DWORD Level; WTSINFOEX_LEVEL1_W { ULONG SessionId; WTS_CONNECTSTATE_CLASS SessionState; LONG SessionFlags; ... } }
        // SessionFlags 는 오프셋 12. Windows 10+ 에서 0 = 잠김, 1 = 잠금 해제
        var flags = QueryInt(id, WtsInfoClass.WTSSessionInfoEx, 12);
        return new SessionInfo(
            id, user,
            IsActive: state == (int)WtsConnectState.Active,
            IsLocked: flags == WTS_SESSIONSTATE_LOCK);
    }

    /// <summary>세션 연결 끊기 → 콘솔이 로그인 화면으로 전환. 실행 중인 앱은 유지된다.</summary>
    public static bool Disconnect(uint sessionId) => WTSDisconnectSession(CurrentServer, sessionId, false);

    /// <summary>강제 로그오프 (저장 안 된 작업 손실). 최후 수단.</summary>
    public static bool Logoff(uint sessionId) => WTSLogoffSession(CurrentServer, sessionId, false);

    private static string? QueryString(uint sessionId, WtsInfoClass cls)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    private static int? QueryInt(uint sessionId, WtsInfoClass cls, int offset)
    {
        if (!WTSQuerySessionInformationW(CurrentServer, sessionId, cls, out var buf, out var bytes)) return null;
        try { return bytes >= offset + 4 ? Marshal.ReadInt32(buf, offset) : null; }
        finally { WTSFreeMemory(buf); }
    }
}
