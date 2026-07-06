using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Offstage;

/// <summary>
/// Suspends and resumes whole process trees. Multi-process apps (Chrome, Edge, Electron apps such
/// as Slack/Discord/VS Code) are a browser/broker process plus renderer, GPU and network-service
/// children, so freezing the single window-owning PID does almost nothing — we walk descendants and
/// suspend the entire tree. After suspending, EmptyWorkingSet trims each process's resident pages.
/// </summary>
internal static class ProcessSuspender
{
    /// <summary>
    /// Suspends the process tree rooted at <paramref name="rootPid"/> and returns the exact set of
    /// PIDs that were targeted, so the caller can resume precisely what it froze.
    /// </summary>
    public static IReadOnlyList<uint> Suspend(uint rootPid)
    {
        var pids = GetProcessTreePids(rootPid);
        foreach (uint pid in pids)
            Apply(pid, suspend: true);
        return pids;
    }

    public static void Resume(IEnumerable<uint> pids)
    {
        foreach (uint pid in pids)
            Apply(pid, suspend: false);
    }

    private static void Apply(uint pid, bool suspend)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_ACCESS, false, pid);
        if (handle == IntPtr.Zero)
            return; // Access denied or process gone — skip; never abort the batch.

        try
        {
            if (suspend)
            {
                NativeMethods.NtSuspendProcess(handle);
                NativeMethods.EmptyWorkingSet(handle);
            }
            else
            {
                NativeMethods.NtResumeProcess(handle);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Returns the root PID plus every descendant, from a single live snapshot.</summary>
    public static List<uint> GetProcessTreePids(uint rootPid)
    {
        Dictionary<uint, List<uint>> childrenByParent = BuildChildMap();

        var ordered = new List<uint>();
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>();
        stack.Push(rootPid);

        while (stack.Count > 0)
        {
            uint pid = stack.Pop();
            if (!seen.Add(pid))
                continue;

            ordered.Add(pid);
            if (childrenByParent.TryGetValue(pid, out List<uint>? kids))
                foreach (uint child in kids)
                    stack.Push(child);
        }

        return ordered;
    }

    private static Dictionary<uint, List<uint>> BuildChildMap()
    {
        var map = new Dictionary<uint, List<uint>>();

        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == NativeMethods.INVALID_HANDLE_VALUE)
            return map;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>()
            };

            if (NativeMethods.Process32First(snapshot, ref entry))
            {
                do
                {
                    if (!map.TryGetValue(entry.th32ParentProcessID, out List<uint>? kids))
                    {
                        kids = new List<uint>();
                        map[entry.th32ParentProcessID] = kids;
                    }
                    kids.Add(entry.th32ProcessID);
                }
                while (NativeMethods.Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return map;
    }
}
