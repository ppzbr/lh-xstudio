"""Classify the editor executables: bitness, whether the exe is itself a managed
assembly, and which managed runtime flavour it is built on.

Why this matters for injection:
  * The native loader hosts the .NET Framework CLR (v4) through ICLRMetaHost. It works in
    any process of matching bitness, managed or not - the CLR is brought in by the loader.
  * Harmony-patching the editor's own save methods only works when the editor runs on the
    same .NET Framework CLR. An editor built on .NET 5+ (CoreCLR) has its own runtime, and
    you cannot patch across two runtimes, so only the file-level features apply there.

Flavour detection:
  * a CLR Runtime Header (data directory 14) means the exe is a managed assembly;
  * '<name>.runtimeconfig.json' / '.deps.json' beside it means .NET 5+ (CoreCLR);
  * 'System.Private.CoreLib' in the image means .NET 5+;
  * 'mscorlib' without CoreLib means .NET Framework;
  * no CLR header at all means a native host that may still load a managed runtime
    dynamically - only the loaded-module list can settle that one.
"""

import os
import struct

TARGETS = [
    r"D:\Synthesizer V Studio 2 Pro\synthv-studio.exe",
    r"D:\Synthesizer V Studio Pro\synthv-studio.exe",
    r"D:\VoiSona118\VoiSona.exe",
    r"C:\Program Files\Techno-Speech\VoiSona\VoiSona.exe",
    r"D:\OpenUtau\OpenUtau.exe",
    r"D:\VOCALOID6\Editor\VOCALOID6.exe",
    r"C:\Program Files\Crypton\Piapro Studio NT\Standalone\Piapro Studio NT.exe",
    r"C:\Program Files\CeVIO\CeVIO AI\CeVIO AI.exe",
]

MACHINE = {0x8664: "x64", 0x14C: "x86", 0xAA64: "arm64"}

# Loaded-runtime evidence collected from the live processes (Get-Process .Modules).
LIVE_RUNTIME = {
    r"D:\Synthesizer V Studio 2 Pro\synthv-studio.exe": ".NET Framework (clr.dll, mscorlib.ni.dll)",
    r"D:\VOCALOID6\Editor\VOCALOID6.exe": ".NET 5+ CoreCLR (coreclr.dll, System.Private.CoreLib.dll) + WPF",
}


def pe_info(path):
    blob = open(path, "rb").read()
    pe_offset = struct.unpack_from("<I", blob, 0x3C)[0]
    machine = struct.unpack_from("<H", blob, pe_offset + 4)[0]

    # COFF header is 20 bytes, optional header follows; its magic says PE32 vs PE32+.
    opt = pe_offset + 24
    magic = struct.unpack_from("<H", blob, opt)[0]
    # Data directory 14 (CLR runtime header) lives at: opt + 96 (PE32) / opt + 112 (PE32+) + 14*8
    dirs = opt + (96 if magic == 0x10B else 112)
    clr_rva, clr_size = struct.unpack_from("<II", blob, dirs + 14 * 8)
    return MACHINE.get(machine, "0x%X" % machine), len(blob), clr_rva, blob


def flavour(path, blob, clr_rva):
    folder = os.path.dirname(path)
    stem = os.path.splitext(os.path.basename(path))[0]

    if os.path.exists(os.path.join(folder, stem + ".runtimeconfig.json")):
        return ".NET 5+ (CoreCLR) - runtimeconfig.json"
    if os.path.exists(os.path.join(folder, stem + ".deps.json")):
        return ".NET 5+ (CoreCLR) - deps.json"
    if b"System.Private.CoreLib" in blob:
        return ".NET 5+ (CoreCLR) - CoreLib in image"
    if b"mscorlib" in blob:
        return ".NET Framework - mscorlib in image"
    if clr_rva:
        return "managed, framework unknown in this image"
    return "native host (runtime, if any, is loaded dynamically)"


def main():
    for path in TARGETS:
        if not os.path.exists(path):
            print("MISSING %s" % path)
            continue
        arch, size, clr_rva, blob = pe_info(path)
        managed = "managed exe" if clr_rva else "native exe"
        print("%-5s %-6s %-11s %s" % (arch, "%dMB" % (size // 1048576), managed, path))
        print("        build: %s" % flavour(path, blob, clr_rva))
        live = LIVE_RUNTIME.get(path)
        print("        live : %s" % (live if live else "(not running, no module evidence)"))


if __name__ == "__main__":
    main()
