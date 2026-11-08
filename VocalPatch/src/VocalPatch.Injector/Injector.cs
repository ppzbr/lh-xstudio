using System.Runtime.InteropServices;

namespace VocalPatch.Injector;

/// <summary>
/// Minimal Windows DLL injector: allocate a buffer in the target, write the DLL path
/// there, and start a thread on <c>LoadLibraryW</c>. The same native loader is used
/// for the CLR-hosting proxy that then loads the managed VocalPatch.Editor runtime.
/// </summary>
internal static class Injector
{
    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr address, IntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr address, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr attributes, IntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out IntPtr threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static void LoadLibrary(int processId, string dllPath)
    {
        string full = Path.GetFullPath(dllPath);
        if (!File.Exists(full)) throw new FileNotFoundException("DLL not found.", full);

        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
        if (hProcess == IntPtr.Zero)
            throw new InvalidOperationException($"OpenProcess failed ({Marshal.GetLastWin32Error()}). Run as administrator if the editor is elevated.");

        IntPtr remote = IntPtr.Zero;
        try
        {
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(full + "\0");
            remote = VirtualAllocEx(hProcess, IntPtr.Zero, (IntPtr)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                throw new InvalidOperationException($"VirtualAllocEx failed ({Marshal.GetLastWin32Error()}).");

            if (!WriteProcessMemory(hProcess, remote, pathBytes, (IntPtr)pathBytes.Length, out _))
                throw new InvalidOperationException($"WriteProcessMemory failed ({Marshal.GetLastWin32Error()}).");

            IntPtr kernel32 = GetModuleHandle("kernel32.dll");
            IntPtr loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new InvalidOperationException("Could not resolve LoadLibraryW.");

            IntPtr thread = CreateRemoteThread(hProcess, IntPtr.Zero, IntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
                throw new InvalidOperationException($"CreateRemoteThread failed ({Marshal.GetLastWin32Error()}).");

            WaitForSingleObject(thread, INFINITE);
            CloseHandle(thread);
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(hProcess, remote, IntPtr.Zero, MEM_RELEASE);
            CloseHandle(hProcess);
        }
    }
}
