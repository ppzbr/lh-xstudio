using System.Runtime.InteropServices;
using System.Text;

namespace VocalPatch.Editor;

/// <summary>
/// Reads the Windows clipboard directly through user32, without a reference to
/// WinForms or WPF.
///
/// Why not System.Windows.Forms.Clipboard: the payload is injected into editors
/// whose UI framework is not ours. Referencing WinForms (or WPF) would make the
/// loader pull a whole presentation framework into a process we do not own, and
/// a mismatched one at that - some of these hosts are Qt, some WPF, some native.
/// Three P/Invokes weigh nothing and are compatible with all of them.
/// </summary>
internal static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>Clipboard text, or null when it is empty or held by another process.</summary>
    public static string? Read()
    {
        // The clipboard is a single-owner shared resource: any process can hold it
        // for the moment we ask. Retry briefly instead of reporting "empty" on a
        // race, because the user pressed a hotkey and expects their paste to land.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return null;

                    IntPtr pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(pointer); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    /// <summary>
    /// Clipboard text as syllables: one per line, blank lines dropped. This is the
    /// shape a lyric list is copied in, so it feeds the "texts" option directly.
    /// </summary>
    public static string[] ReadSyllables()
    {
        string? text = Read();
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var parts = new List<string>();
        foreach (string line in text!.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) parts.Add(trimmed);
        }
        return parts.ToArray();
    }
}
