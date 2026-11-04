using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BackloggdMirror.Services.Platform.Linux;

namespace BackloggdMirror.Services.Platform;

/// <summary>A process with its ancestors and descendants: a game can be several (a launcher waiting on its "-Shipping" build).</summary>
internal static class ProcessTree
{
    public static HashSet<int> Lineage(IReadOnlyCollection<uint> roots)
    {
        var lineage = new HashSet<int>();
        foreach (uint root in roots)
            lineage.Add((int)root);

        var parents = ReadParents();
        if (parents == null)
            return lineage;

        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in parents)
        {
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<int>();
            list.Add(pid);
        }

        var startTimes = new Dictionary<int, long?>();

        foreach (uint root in roots)
        {
            int current = (int)root;
            while (parents.TryGetValue(current, out int parent) && parent > 0
                   && IsGenuineParent(parent, current, startTimes) && lineage.Add(parent))
            {
                current = parent;
            }

            var queue = new Queue<int>();
            queue.Enqueue((int)root);
            while (queue.Count > 0)
            {
                int pid = queue.Dequeue();
                if (!children.TryGetValue(pid, out var list))
                    continue;

                foreach (int child in list)
                {
                    if (IsGenuineParent(pid, child, startTimes) && lineage.Add(child))
                        queue.Enqueue(child);
                }
            }
        }

        return lineage;
    }

    /// <summary>Child to parent; null where there is no way to read it.</summary>
    private static Dictionary<int, int>? ReadParents()
    {
        if (OperatingSystem.IsLinux())
        {
            var parents = new Dictionary<int, int>();
            foreach (int pid in LinuxProcFs.ListProcessIds())
            {
                if (LinuxProcFs.ReadParentId(pid) is int parent)
                    parents[pid] = parent;
            }

            return parents;
        }

        if (!OperatingSystem.IsWindows())
            return null;

        var entries = WindowsProcessSnapshot.Take();
        if (entries == null)
            return null;

        var map = new Dictionary<int, int>(entries.Count);
        foreach (var entry in entries)
            map[(int)entry.Id] = (int)entry.ParentId;

        return map;
    }

    /// <summary>Windows keeps a dead parent's PID, which may be reused, so the parent must have started first; Linux re-parents orphans.</summary>
    private static bool IsGenuineParent(int parent, int child, Dictionary<int, long?> startTimes)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        long? parentStart = StartTime(parent, startTimes);
        long? childStart = StartTime(child, startTimes);
        return parentStart != null && childStart != null && parentStart <= childStart;
    }

    private static long? StartTime(int pid, Dictionary<int, long?> startTimes)
    {
        if (startTimes.TryGetValue(pid, out long? cached))
            return cached;

        long? start = null;
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetProcessTimes(handle, out long creation, out _, out _, out _))
                    start = creation;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        startTimes[pid] = start;
        return start;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr handle, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
