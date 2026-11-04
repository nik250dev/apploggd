using System.Collections.Generic;

namespace BackloggdMirror.Services;

/// <summary>The blacklist plus, for one pass (under the detection gate), the processes of the games already being timed.</summary>
internal sealed class DetectionFilter : IDetectionBlacklist
{
    private static readonly HashSet<int> None = new();

    private readonly IDetectionBlacklist? _blacklist;
    private HashSet<int> _excluded = None;

    public DetectionFilter(IDetectionBlacklist? blacklist)
    {
        _blacklist = blacklist;
    }

    public long Version => _blacklist?.Version ?? 0;

    public void Exclude(HashSet<int>? processIds) => _excluded = processIds ?? None;

    public bool IsApplicationBlocked(int processId, string? processName) =>
        _excluded.Contains(processId) || _blacklist?.IsApplicationBlocked(processId, processName) == true;

    public bool IsContentBlocked(string emulatorName, string contentKey) =>
        _blacklist?.IsContentBlocked(emulatorName, contentKey) == true;
}
