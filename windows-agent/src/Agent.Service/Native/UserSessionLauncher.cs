using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XKeep.Agent.Service.Native;

/// <summary>
/// LocalSystem 서비스에서 사용자 세션(데스크톱)에 UserHost(트레이·잠금 화면)를 띄운다.
/// WTSQueryUserToken 은 SE_TCB_NAME 권한이 필요하며 LocalSystem 은 이를 가진다.
/// UserHost 는 해당 사용자 권한으로 실행된다 (관리자 권한 아님).
/// </summary>
public sealed class UserSessionLauncher(ILogger<UserSessionLauncher> log)
{
    public const string UserHostExe = "XKeep.Agent.UserHost.exe";

    public static string UserHostPath => Path.Combine(AppContext.BaseDirectory, UserHostExe);

    public bool IsUserHostRunning(uint sessionId) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UserHostExe)).Any(p =>
        {
            using (p) return p.SessionId == sessionId;
        });

    public bool EnsureUserHost(uint sessionId)
    {
        if (IsUserHostRunning(sessionId)) return true;
        if (!File.Exists(UserHostPath))
        {
            log.LogError("UserHost 실행 파일 없음: {Path}", UserHostPath);
            return false;
        }
        return Launch(sessionId, UserHostPath);
    }

    private bool Launch(uint sessionId, string exePath)
    {
        IntPtr userToken = IntPtr.Zero, primaryToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WtsApi.WTSQueryUserToken(sessionId, out userToken))
            {
                log.LogWarning("WTSQueryUserToken 실패 ({Err}) — 로그인한 사용자가 없을 수 있음", Marshal.GetLastWin32Error());
                return false;
            }
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                return Fail("DuplicateTokenEx");
            if (!CreateEnvironmentBlock(out env, primaryToken, false))
                return Fail("CreateEnvironmentBlock");

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = $"\"{exePath}\"";
            if (!CreateProcessAsUserW(primaryToken, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(exePath), ref si, out var pi))
                return Fail("CreateProcessAsUser");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log.LogInformation("UserHost 실행: session {Session}, pid {Pid}", sessionId, pi.dwProcessId);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    private bool Fail(string api)
    {
        log.LogError("{Api} 실패: {Err}", api, Marshal.GetLastWin32Error());
        return false;
    }

    // ---------------- P/Invoke ----------------
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
