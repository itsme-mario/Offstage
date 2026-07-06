using System;
using System.Collections.Generic;
using System.Text;

namespace Offstage;

/// <summary>A top-level application window plus the process that owns it.</summary>
internal sealed record ManagedWindow(IntPtr Handle, uint ProcessId, string Title);

/// <summary>
/// Enumerates real "alt-tab" application windows and reports which virtual desktop each lives on.
/// </summary>
internal static class WindowManager
{
    private static readonly IVirtualDesktopManager Vdm = (IVirtualDesktopManager)new CVirtualDesktopManager();

    public static List<ManagedWindow> EnumerateAppWindows()
    {
        var results = new List<ManagedWindow>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (IsAltTabWindow(hWnd))
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                results.Add(new ManagedWindow(hWnd, pid, GetTitle(hWnd)));
            }
            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>
    /// True if the window is on the desktop the user is currently viewing. On any COM error we
    /// return true (fail safe) so an unclassifiable window is never frozen out from under the user.
    /// </summary>
    public static bool IsOnCurrentDesktop(IntPtr hWnd)
    {
        try
        {
            int hr = Vdm.IsWindowOnCurrentVirtualDesktop(hWnd, out int onCurrent);
            return hr != 0 || onCurrent != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsAltTabWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;

        if (NativeMethods.GetWindowTextLength(hWnd) == 0)
            return false;

        int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        // Owned pop-ups are not their own alt-tab entry unless flagged as an app window.
        IntPtr owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return false;

        return true;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        int length = NativeMethods.GetWindowTextLength(hWnd);
        if (length == 0)
            return string.Empty;

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
