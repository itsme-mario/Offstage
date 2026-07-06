using System;
using System.Windows.Forms;

namespace Offstage;

/// <summary>
/// A hidden message-only window whose sole job is to receive WM_HOTKEY from the global hotkeys
/// registered against its handle.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow
{
    public event Action<int>? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
            HotkeyPressed?.Invoke(m.WParam.ToInt32());

        base.WndProc(ref m);
    }
}
