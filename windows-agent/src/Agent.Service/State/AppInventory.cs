using System.Collections.Concurrent;
using System.Diagnostics;
using XKeep.Agent.Service.Net;
using XKeep.Agent.Shared;

namespace XKeep.Agent.Service.State;

/// <summary>
/// 화면에 창을 띄운 적 있는 프로그램 목록 → 부모앱의 "앱 목록에서 고르기".
/// id 는 StudyAppRules.InventoryId 규칙(사용자 폴더 프로그램은 전체 경로, 나머지는 파일명)이라
/// 부모가 고른 값을 그대로 학습 허용 목록에 넣으면 된다.
/// 새 프로그램이 보이면 최대 1분에 한 번, 아니면 30분에 한 번 서버에 보고.
/// </summary>
public sealed class AppInventory(ServerConnection server)
{
    private const int MaxItems = 300;
    private readonly ConcurrentDictionary<string, (string Label, DateTime Seen)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _dirty;
    private DateTime _sentAt = DateTime.MinValue;

    public void Saw(string id, string path)
    {
        var isNew = !_apps.ContainsKey(id);
        _apps[id] = (isNew ? LabelOf(path) : _apps[id].Label, DateTime.UtcNow);
        if (isNew) _dirty = true;
    }

    /// <summary>주기적으로 호출 (ProcessMonitorWorker 스캔 루프)</summary>
    public void FlushIfDue()
    {
        var now = DateTime.UtcNow;
        var due = _dirty ? now - _sentAt >= TimeSpan.FromMinutes(1) : now - _sentAt >= TimeSpan.FromMinutes(30);
        if (!due || !server.IsConnected || _apps.IsEmpty) return;
        var items = _apps
            .OrderByDescending(kv => kv.Value.Seen)
            .Take(MaxItems)
            .Select(kv => new AppInfo(kv.Key, kv.Value.Label))
            .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        server.Enqueue(new AppsMessage(items));
        _sentAt = now;
        _dirty = false;
    }

    private static string LabelOf(string path)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim().Length > 120 ? desc.Trim()[..120] : desc.Trim();
        }
        catch (Exception)
        {
            // 버전 정보 없음
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
