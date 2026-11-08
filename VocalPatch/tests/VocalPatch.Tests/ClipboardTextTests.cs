using System;
using System.Runtime.InteropServices;
using System.Threading;
using VocalPatch.Core.Formats;
using VocalPatch.Core.Model;
using VocalPatch.Editor;
using Xunit;

namespace VocalPatch.Tests;

/// <summary>
/// Exercises the clipboard reader against the real Windows clipboard.
///
/// The reader is hand-rolled P/Invoke with no UI framework behind it - the payload is
/// injected into processes whose presentation stack is not ours, so WinForms/WPF was
/// deliberately not referenced. That is exactly the part a compile cannot prove: the
/// marshalling has to actually hand back the string.
/// </summary>
public sealed class ClipboardTextTests
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>Put Unicode text on the clipboard. Returns false only if the session has no clipboard.</summary>
    private static bool TrySetClipboard(string text)
    {
        // Any process can hold the clipboard for the moment we ask, so retry.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard()) return false;

                    IntPtr block = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
                    if (block == IntPtr.Zero) return false;

                    IntPtr target = GlobalLock(block);
                    if (target == IntPtr.Zero) return false;
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);   // NUL terminator
                    }
                    finally { GlobalUnlock(block); }

                    // On success the clipboard owns the block; do not free it.
                    return SetClipboardData(CF_UNICODETEXT, block) != IntPtr.Zero;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return false;
    }

    [Fact]
    public void Reads_one_syllable_per_line_and_drops_blank_lines()
    {
        Assert.True(TrySetClipboard("ni\r\nhao\r\n\r\n  shi  "),
                    "the clipboard could not be opened in this session");

        Assert.Equal(new[] { "ni", "hao", "shi" }, ClipboardText.ReadSyllables());
    }

    [Fact]
    public void Empty_clipboard_yields_no_syllables()
    {
        Assert.True(TrySetClipboard(string.Empty),
                    "the clipboard could not be opened in this session");

        Assert.Empty(ClipboardText.ReadSyllables());
    }

    /// <summary>
    /// The whole hotkey path for `lyrics`, minus the keyboard hook: Bootstrap.RunAction
    /// is what the hook calls. This is the glue that turns a clipboard paste into a job,
    /// and the second half pins down the safety property that replaced the old
    /// write-a-fixed-syllable-to-every-note behaviour - an empty clipboard must leave
    /// the project exactly as it was.
    /// </summary>
    [Fact]
    public void Lyrics_action_takes_its_words_from_the_clipboard()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vpatch-lyrics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "song.ust");

        var song = new Song { Name = "Clip", Resolution = 480 };
        song.Tempos.Clear();
        song.Tempos.Add(new Tempo(0, 120));
        var track = new Track("Lead");
        track.Notes.Add(new Note(0, 240, 60, "a"));
        track.Notes.Add(new Note(240, 240, 62, "b"));
        track.Notes.Add(new Note(480, 240, 64, "c"));
        song.Tracks.Add(track);
        song.Normalize();
        ProjectIo.Save(song, file);

        Assert.True(TrySetClipboard("ni\nhao\nshi"),
                    "the clipboard could not be opened in this session");

        string result = Bootstrap.RunAction("lyrics", file);

        Assert.Contains("3 note(s) changed", result);
        Assert.Equal(new[] { "ni", "hao", "shi" },
                     ProjectIo.Load(file).Tracks[0].SortedNotes().Select(n => n.Lyric).ToArray());

        // Empty clipboard: nothing may be touched.
        Assert.True(TrySetClipboard(string.Empty));

        string again = Bootstrap.RunAction("lyrics", file);

        Assert.Contains("clipboard is empty", again);
        Assert.Equal(new[] { "ni", "hao", "shi" },
                     ProjectIo.Load(file).Tracks[0].SortedNotes().Select(n => n.Lyric).ToArray());
    }
}
