namespace XKeep.Agent.Shared;

/// <summary>
/// 학습 모드에서 실행 파일을 허용할지 판단하는 규칙 (순수 함수, 단위 테스트 대상).
///
/// 허용 목록 항목 두 종류:
///  - 이름만("chrome.exe"): 표준 사용자가 쓸 수 없는 폴더(Program Files, WindowsApps 등)의 실행 파일에만 일치.
///    → 다운로드 폴더에 discord.exe 를 chrome.exe 로 이름 바꿔 실행하는 우회를 막는다.
///  - 전체 경로("C:\Users\kid\AppData\Roaming\Zoom\bin\Zoom.exe"): 정확히 그 파일만 허용.
///    사용자 폴더에 설치되는 프로그램(Zoom, Discord 등)은 이렇게 경로로 허용해야 한다.
///
/// Windows 폴더 아래 실행 파일(탐색기, 설정, 작업 표시줄 등)은 항상 허용.
/// </summary>
public sealed class StudyAppRules(string windowsDir, IReadOnlyList<string> userWritableRoots)
{
    private readonly string _windowsDir = WithSlash(windowsDir);
    private readonly IReadOnlyList<string> _userWritable = userWritableRoots.Select(WithSlash).ToList();

    /// <summary>현재 PC 의 폴더 구성으로 만든 기본 규칙</summary>
    public static StudyAppRules ForThisMachine() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        [
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // C:\ProgramData
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        ]);

    public bool IsSystem(string path) =>
        path.StartsWith(_windowsDir, StringComparison.OrdinalIgnoreCase) && !IsUserWritable(path);

    public bool IsUserWritable(string path) =>
        _userWritable.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    public bool IsAllowed(string path, IEnumerable<string> allowList)
    {
        if (IsSystem(path)) return true;
        var file = Path.GetFileName(path);
        var userWritable = IsUserWritable(path);
        foreach (var raw in allowList)
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;
            if (entry.Contains('\\') || entry.Contains('/'))
            {
                if (string.Equals(entry.Replace('/', '\\'), path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (!userWritable && string.Equals(WithExe(entry), file, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>부모앱 목록에 보여줄 id: 사용자 폴더 프로그램은 전체 경로, 나머지는 파일명</summary>
    public string InventoryId(string path) => IsUserWritable(path) ? path : Path.GetFileName(path);

    private static string WithExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

    private static string WithSlash(string dir) => dir.EndsWith('\\') ? dir : dir + '\\';
}
