// A minimal .NET Framework GUI host used to exercise the injected hotkeys and the
// foreground-window gate without opening a real editor.
//
// Windows Forms matters here: the gate asks GetForegroundWindow() who owns the keyboard
// and only acts when it is this process. A console host would not do - a console window
// belongs to conhost.exe, so the gate would (correctly) refuse.
//
// Build (no SDK needed, the .NET Framework compiler is already on the machine):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
//     /out:tools\hotkey_host.exe tools\hotkey_host.cs ^
//     /r:System.Windows.Forms.dll /r:System.Drawing.dll

using System;
using System.Drawing;
using System.Windows.Forms;

internal static class HotkeyHost
{
    [STAThread]
    private static void Main()
    {
        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "VocalPatch hotkey host\r\n\r\nkeep this window focused, then press Ctrl+Alt+V",
        };

        var form = new Form
        {
            Text = "VocalPatch hotkey host",
            ClientSize = new Size(460, 160),
            StartPosition = FormStartPosition.CenterScreen,
        };

        form.Controls.Add(label);
        Application.Run(form);
    }
}
