using System;
using System.Runtime.InteropServices;

namespace Offstage;

/// <summary>
/// The public, documented Shell interface for asking which virtual desktop a window belongs to.
/// This is intentionally the *stable* API — it only answers "is this window on the current
/// desktop?" and cannot enumerate or switch desktops. That is all v1 needs, and it avoids the
/// undocumented IVirtualDesktopManagerInternal interface that changes between Windows builds.
/// </summary>
[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport]
[Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
internal class CVirtualDesktopManager
{
}
