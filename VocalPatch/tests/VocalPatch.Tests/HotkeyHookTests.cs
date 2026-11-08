using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using VocalPatch.Editor;
using Xunit;

namespace VocalPatch.Tests;

/// <summary>
/// Drives the hotkey hook with real keyboard events, so the whole chain is exercised:
/// the low-level hook, modifier tracking off the hook stream, the foreground decision and
/// the action itself.
///
/// The foreground decision is the one input a test cannot produce honestly - the desktop
/// belongs to whatever the developer has focused, and Windows refuses to hand focus to a
/// window of a background process. It is therefore injected, and this test pins down the
/// rule that mattered: an injected editor that is not the one on screen must leave the
/// chord alone, because every injected editor installs its own global hook and the first
/// one in the chain would otherwise swallow it and act on an arbitrary editor.
///
/// A low-level hook only ever sees input through the interactive input desktop. In a
/// locked, disconnected or headless session the keys synthesised here are never routed to
/// it, and every assertion below would fail for a reason that has nothing to do with the
/// gate - so delivery is probed first and the test skips when it is unavailable.
/// </summary>
public sealed class HotkeyHookTests
{
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_F9 = 0x78;     // a chord no editor binds by default, so no other
                                        // injected runtime can react to this test
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static void PressCtrlAltF9()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F9, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F9, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    [SkippableFact]
    public void The_chord_acts_only_for_the_editor_that_owns_the_foreground()
    {
        int fired = 0;
        bool ownsForeground = true;

        using var hook = new HotkeyHook(
            new Dictionary<uint, Action> { [VK_F9] = () => Interlocked.Increment(ref fired) },
            () => ownsForeground);

        hook.Start();
        Assert.True(hook.IsRunning, "the low-level keyboard hook could not be installed in this session");

        // Probe: does this session deliver synthetic keys to a low-level hook at all?
        PressCtrlAltF9();
        bool delivered = WaitFor(() => Volatile.Read(ref fired) > 0, 5000);

        Skip.IfNot(delivered,
            "synthetic keys do not reach a low-level keyboard hook in this session " +
            "(locked, disconnected or headless input desktop) - re-run with the desktop unlocked");

        // The probe already proved the chord fires while this process owns the foreground.
        // Now the rule that matters: some other editor is on screen, so this one must not answer.
        Interlocked.Exchange(ref fired, 0);
        ownsForeground = false;
        PressCtrlAltF9();
        Thread.Sleep(600);
        Assert.Equal(0, Volatile.Read(ref fired));

        // This editor is the one being looked at, so the chord acts.
        ownsForeground = true;
        PressCtrlAltF9();

        Assert.True(WaitFor(() => Volatile.Read(ref fired) > 0, 5000),
                    "the chord did not fire for the foreground editor");
        Assert.Equal(1, Volatile.Read(ref fired));
    }
}
