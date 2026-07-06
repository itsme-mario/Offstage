using System;
using System.Collections.Generic;
using System.Linq;
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
    public static IReadOnlyList<uint> Suspend(uint rootPid) => Suspend(rootPid, leaveBrokerAlive: false);

    /// <summary>
    /// Suspends the process tree rooted at <paramref name="rootPid"/> and returns the exact set of
    /// PIDs that were targeted, so the caller can resume precisely what it froze.
    ///
    /// When <paramref name="leaveBrokerAlive"/> is set and the tree is a Chromium/Electron-style
    /// single-broker app (Edge, Chrome, Slack, VS Code, …), the root "broker" process is left
    /// RUNNING and only its children — the renderer/GPU/utility workers, where the background CPU
    /// actually burns — are suspended. The broker stays responsive enough to service an "open a new
    /// window" request, so a frozen browser no longer blocks opening a fresh window on the current
    /// desktop, while you still get the bulk of the CPU savings.
    /// </summary>
    public static IReadOnlyList<uint> Suspend(uint rootPid, bool leaveBrokerAlive)
    {
        (Dictionary<uint, List<uint>> childrenByParent, Dictionary<uint, string> exeByPid) = Snapshot();
        List<uint> tree = WalkTree(rootPid, childrenByParent);

        IEnumerable<uint> targets = tree;
        if (leaveBrokerAlive && LooksLikeMultiProcessBroker(rootPid, tree, exeByPid))
            targets = tree.Where(pid => pid != rootPid);

        var suspended = targets.ToList();
        foreach (uint pid in suspended)
            Apply(pid, suspend: true);
        return suspended;
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
        (Dictionary<uint, List<uint>> childrenByParent, _) = Snapshot();
        return WalkTree(rootPid, childrenByParent);
    }

    private static List<uint> WalkTree(uint rootPid, Dictionary<uint, List<uint>> childrenByParent)
    {
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

    /// <summary>
    /// One Toolhelp pass that yields both the parent→children map (for tree walking) and a
    /// PID→executable-name map (for recognising an app's own helper processes).
    /// </summary>
    private static (Dictionary<uint, List<uint>> childrenByParent, Dictionary<uint, string> exeByPid) Snapshot()
    {
        var children = new Dictionary<uint, List<uint>>();
        var exe = new Dictionary<uint, string>();

        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == NativeMethods.INVALID_HANDLE_VALUE)
            return (children, exe);

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
                    if (!children.TryGetValue(entry.th32ParentProcessID, out List<uint>? kids))
                    {
                        kids = new List<uint>();
                        children[entry.th32ParentProcessID] = kids;
                    }
                    kids.Add(entry.th32ProcessID);
                    exe[entry.th32ProcessID] = entry.szExeFile;
                }
                while (NativeMethods.Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return (children, exe);
    }

    /// <summary>
    /// True if the tree looks like a Chromium/Electron single-broker app. Rather than maintaining a
    /// hard-coded list of browser/Electron executable names (Edge, Chrome, Brave, Slack, Discord,
    /// Teams, VS Code, Spotify, …), we detect the family by its structural signature: a helper
    /// process that shares the root's image name and carries a <c>--type=</c> switch on its command
    /// line (renderer, gpu-process, utility, …). That covers every Chromium-based app automatically
    /// and won't false-positive on ordinary apps that merely spawn same-named workers.
    /// </summary>
    private static bool LooksLikeMultiProcessBroker(uint rootPid, List<uint> tree, Dictionary<uint, string> exeByPid)
    {
        if (!exeByPid.TryGetValue(rootPid, out string? rootExe))
            return false;

        foreach (uint pid in tree)
        {
            if (pid == rootPid)
                continue;
            if (!exeByPid.TryGetValue(pid, out string? exe) ||
                !string.Equals(exe, rootExe, StringComparison.OrdinalIgnoreCase))
                continue;

            string? cmd = TryGetCommandLine(pid);
            if (cmd is not null && cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Best-effort read of a process's command line; null if it can't be obtained.</summary>
    private static string? TryGetCommandLine(uint pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            // First call sizes the buffer (returns STATUS_INFO_LENGTH_MISMATCH and sets `needed`).
            int needed = 0;
            NativeMethods.NtQueryInformationProcess(
                handle, NativeMethods.ProcessCommandLineInformation, IntPtr.Zero, 0, ref needed);
            if (needed <= 0)
                return null;

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                uint status = NativeMethods.NtQueryInformationProcess(
                    handle, NativeMethods.ProcessCommandLineInformation, buffer, needed, ref needed);
                if (status != 0)
                    return null;

                var us = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buffer);
                if (us.Buffer == IntPtr.Zero || us.Length == 0)
                    return null;
                return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null; // Any marshalling/native hiccup just means "can't tell" — treat as non-broker.
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
