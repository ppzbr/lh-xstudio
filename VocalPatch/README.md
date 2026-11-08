# VocalPatch

A cross-editor quality-of-life toolkit for singing-synthesis editors — **CeVIO**,
**Synthesizer V Studio**, **VoiSona**, **OpenUtau** and **Piapro Studio**.

It reimplements the *functionality* of the VOCALOIDPatcher feature set (project
interchange, batch edit jobs, lyric romanisation, autosave, and an injected
in-process runtime) without any UI-localisation content: no translation XML, no
language packs. Just the features.

```
VocalPatch.slnx
├─ src/
│  ├─ VocalPatch.Core        app-neutral model, format codecs, jobs, lyrics, autosave
│  ├─ VocalPatch.Cli         `vpatch` command line front end
│  ├─ VocalPatch.OpenUtau    real OpenUtau legacy plugin (plugin.txt + vpatch-utau.exe)
│  ├─ VocalPatch.Editor      in-process runtime injected into closed editors (Harmony)
│  └─ VocalPatch.Injector    `vpatch-inject` DLL loader, shared actions, file autosave
├─ native/
│  ├─ vocalpatch_loader.c    self-contained CLR-hosting loader (kernel32 only)
│  ├─ load_host.c            throwaway host used to prove the loader works
│  └─ build_loader.ps1       builds both, with Zig / MSVC / gcc / TinyCC
├─ dist/                     assembled by package.ps1 (loaders, payload, plugin folders)
├─ tests/
│  └─ VocalPatch.Tests       round-trip, job, action and OpenUtau-contract tests
└─ QUICKSTART.zh.md          usage in Chinese
```

## What it does

### 1. Project interchange (`VocalPatch.Core`)
A neutral `Song` model that every editor's project file maps to and from:

| Format id   | Extensions                     | Read | Write | Typical source editor |
|-------------|--------------------------------|:----:|:-----:|-----------------------|
| `ust`       | `.ust`                         | ✔ | ✔ | UTAU / OpenUtau |
| `ustx`      | `.ustx`                        | ✔ | ✔ | OpenUtau |
| `vsqx`      | `.vsqx`                        | ✔ | ✔ | VOCALOID3/4 |
| `vpr`       | `.vpr`                         | ✔ | ✔ | VOCALOID4 |
| `ppsf`      | `.ppsf`                        | ✔ | ✔ | Piapro Studio |
| `ccs`       | `.ccs`                         | ✔ | ✔ | CeVIO |
| `svp`       | `.svp`                         | ✔ | ✔ | Synthesizer V Studio |
| `midi`      | `.mid`, `.midi`                | ✔ | ✔ | VoiSona & all |
| `ufdata`    | `.ufdata.json`                 | ✔ | ✔ | utaformatix bridge |
| `commonnote`| `.commonnote.json`             | ✔ | ✔ | ExpressiveLabs bridge |
| `musicxml`  | `.musicxml`, `.xml`            | ✔ | ✔ | notation |
| `lrc`       | `.lrc`                         | ✔ | ✔ | timed lyrics |
| `srt`       | `.srt`                         | ✔ | ✔ | subtitles |
| `ass`       | `.ass`, `.ssa`                 | ✔ | ✔ | subtitles |
| `json`      | `.vp.json`                     | ✔ | ✔ | lossless snapshot |

So "a plugin for CeVIO / SV / VoiSona / OpenUtau / Piapro" works even for the
closed editors: their project files convert to and from every other format.

X Studio's `.svip3` is deliberately not in that table — one engine's format is not the
neutral model's. It is Protocol Buffers, and the schema was recovered from the editor's own
image; that work lives outside this folder, in `..\xstudio\`: `xstudio-format.md` for the
field-by-field spec, `tools\xstudio_schema.py` to decode a project today, and a codec plus
an `svip3` command that plugs in with a single `FormatRegistry.Register` call.

### 2. Batch edit jobs (`VocalPatch.Core.Jobs`)
Same job set the patcher adds to the piano roll, usable on any format:

- `swing` — 8th/16th swing or shuffle, adjustable amount
- `quantize` — snap note lengths to a grid with a strength blend
- `lyrics` — replace lyrics with one syllable or a cycled list
- `harmony` — diatonic (third/fourth/fifth/sixth/octave) or chromatic harmony track
- `transpose` — semitones / octaves
- `normalize` — clamp pitch, drop empties, fix overlaps
- `staccato` — shorten notes (weak/medium/strong)

### 3. Lyrics (`VocalPatch.Core.Lyrics`)
- Chinese hanzi → pinyin (built-in common-character table + `LoadTable` for a full one)
- Japanese kana → romaji
- Script detection and a syllabifier (split a line into one-syllable notes)
- Tone modes: none / numbers / diacritics

### 4. Autosave (`VocalPatch.Core.Autosave`)
Interval backups of any project file to a `.vocalpatch-autosave` folder, with an
optional neutral-model JSON snapshot. App-agnostic — works with all five editors.

### 5. OpenUtau plugin (`VocalPatch.OpenUtau`)
A genuine OpenUtau legacy plugin. `Install` writes `plugin.txt`, a `vpatch.json`
job list, and the executable; copy the folder into OpenUtau's `Plugins` directory.
When OpenUtau runs it, it edits the UST temp file in place — preserving pitch
points, vibrato, velocity and oto aliases — and applies pinyin / quantize /
staccato / transpose / normalize / lyrics / swing.

### 6. Shared edit actions (`VocalPatch.Editor.EditorActions`)
The in-editor feature set, and the same entry point the CLI uses, so a hotkey and a
command line do exactly the same thing:

| Action | In-place effect |
|--------|-----------------|
| `snapshot` | timestamped copy into `.vocalpatch-autosave\` |
| `swing` / `quantize` / `staccato` / `transpose` / `normalize` / `lyrics` / `harmony` | apply one job to the current project, keeping a `.bak` |
| `pinyin` | romanise lyrics, keeping a `.bak` |
| `export` | write every editor's format into `vocalpatch-export\` |
| `restore` | put the `.bak` back |

An action that changes nothing never writes: the file is fingerprinted before and
after, so a no-op hotkey cannot churn the project or the backup.

### 7. Injected runtime (`VocalPatch.Editor` + `VocalPatch.Injector` + `native/`)
For the closed editors that have no plugin API:
- `vpatch-inject inject <pid|process-name> [loader.dll]` loads the native loader, which
  hosts the .NET Framework CLR and calls `VocalPatch.Editor.Bootstrap.Run`.
- The runtime detects the host editor profile, watches its project folder, and installs
  the action hotkeys: `Ctrl+Alt+V/S/Q/C/T/N/P/H/L/E/Z` — all eleven actions (snapshot,
  swing, quantize, staccato, transpose, normalize, pinyin, harmony, lyrics, export,
  restore). Rebinding is one line of `vpatch-editor.ini`. `lyrics` reads the clipboard
  (one syllable per line) because a hotkey cannot take typed input.
- Every hotkey press logs its outcome to `%TEMP%\vocalpatch-editor.log`
  (`hotkey snapshot -> <path>`), and `hotkeydebug=true` logs every key the hook sees.
  Hotkeys are otherwise silent, which makes a dead hook indistinguishable from a
  working one that had nothing to do.
- Modifier state for the chord is tracked off the hook stream rather than read from
  `GetAsyncKeyState`: a low-level hook runs ahead of the async key state, so the async
  read reports Ctrl/Alt as still-up while the chord is being typed.
- Snapshots, exports and `.bak` files are recognised as this runtime's own output
  (`EditorActions.IsGeneratedPath`) and are never treated as projects to watch, act on
  or snapshot again. A snapshot keeps the project's extension, so without that guard
  autosave would snapshot its own snapshot, recursively.
- Harmony-patches the .NET save methods to snapshot on every save, and
  `vpatch-inject autosave <file> --interval 300` gives file-based autosave with no
  injection at all.
- Patching the BCL's own writers (`File.WriteAllText`, `File.WriteAllBytes`,
  `XmlDocument.Save`, `XDocument.Save`) is what lets the save hook work without knowing
  any editor's save API. It only intercepts the *host's* saves when the host shares our
  runtime, and the runtime names which case it is in the log: `host code in this runtime:
  <name>` or the `note:` line. Measured across the five editors, the first case belongs to
  the .NET Framework hosts. CeVIO AI's own binary is one — `CeVIO AI.orig.exe` is a
  .NET Framework/WPF app, so the CLR hosting API reuses its runtime and the patch lands on
  the same BCL methods it calls. Synthesizer V Studio and VoiSona are native hosts
  (Synthesizer V loads .NET only for scripting, and saves from native code), and OpenUtau
  and VOCALOID6 run on .NET 5+, whose runtime we cannot patch into. For those, the folder
  watcher is the autosave, which is why `watch=<dir>` matters.
- Two statements in that paragraph were written from the source and are **contradicted by
  measurement**, so they are corrected here rather than quietly rewritten:
  - *"Piapro Studio NT is a native host"* — it is not. `Piapro Studio NT.exe` (x64) and
    `Common\PPS3.dll` both statically import `mscoree.dll` and neither imports
    `hostfxr.dll`: the host bootstraps the .NET Framework CLR itself. The
    `hostfxr`-vs-`mscoree` distinction is what decides whether our Framework loader can
    share the runtime, which is why this matters. (`VocalPatch\dist\dropin\pe-imports.ps1`
    reproduces the check.)
  - *"only CeVIO AI is in the first case"* — the log has recorded the `note:` line, i.e.
    the **second** case, for a CeVIO process on this machine
    (`%TEMP%\vocalpatch-editor.log`, 2026-11-08 09:23:49, profile `CeVIO`). The honest
    reading is that the split is per *executable*, not per editor: `CeVIO AI.orig.exe` is
    the Framework/WPF app and the patched `CeVIO AI.exe` launcher is not, and which one
    the loader ends up hosting decides the outcome.
- That check is `SavePathPatcher.HostAssemblyOutsideOurRuntime`: the first loaded assembly
  that is neither ours nor the platform's. The GAC counts as the platform — leaving it out
  made every host look as if it shared our runtime.
- Editor scratch files are excluded as well as our own output: `recovery\`, `Backups\` and
  names containing `autosave`/`auto-save`/`~$` (`EditorActions.IsEditorScratchPath`). They
  carry ordinary project extensions, so they would otherwise be copied every cycle.
  Measured on a real machine: every `.svp` under `Documents\Dreamtonics` is a crash-recovery
  copy, and OpenUtau's only project file under `Documents\OpenUtau` is its own 360 KB
  `Backups\Untitled-autosave.ustx` — both were being snapshotted, 94 MB of it.
- Every injected editor installs its own global keyboard hook, and the first hook in the
  chain swallows the chord, so with several editors injected a hotkey would land on an
  arbitrary one. A chord now acts only when the foreground window belongs to this process,
  and only then is it swallowed; the other editors log `ignored (not the foreground
  editor)` and pass it on. The decision is injectable, because a test process cannot make
  itself the foreground window and would otherwise have no way to test the positive case.
- Process matching is a prefix on the name with any extension stripped, not equality: real
  executables carry suffixes the profile does not (`CeVIO AI.orig`, `Piapro Studio NT`),
  and equality silently degraded those to "watch every target folder instead of this one".
- Each log line carries the pid of the process that wrote it, and appends are retried:
  several injected editors share one log file, and `File.AppendAllText` opens it for
  exclusive write.

Watched folders come from the profile, plus any `watch=<dir>` entries in the ini; if a
profile's folders don't exist on the machine, the user's Documents folder is used so
autosave still has somewhere to look.

**Injection payload.** `VocalPatch.Editor` multi-targets `net472` + `net8.0`, and the
`net472` build has `CopyLocalLockFileAssemblies=true`, so
`src/VocalPatch.Editor/bin/Release/net472/` is a complete, self-contained folder:
`VocalPatch.Editor.dll`, `VocalPatch.Core.dll`, `0Harmony.dll`, `YamlDotNet.dll`,
`System.Text.Json.dll` and its shims. That folder is exactly what the native loader
needs — put `vocalpatch_loader.dll` in it (the loader resolves
`VocalPatch.Editor.dll` beside itself) and inject from there. `net8.0` is only what
the Injector and CLI compile against.

**Loader.** `native/vocalpatch_loader.c` is dependency-free on purpose: it includes
nothing at all — not even `<windows.h>` — and declares its own kernel32 imports, CLR
hosting GUIDs/vtable slots (transcribed from `metahost.h`/`mscoree.h`, not recalled)
and `<windows.h>` types. It builds the wide strings it needs at runtime instead of
using wide literals, so `wchar_t` width differences between compilers cannot bite. That
means it compiles with TinyCC too, which matters because TinyCC is a 1.7 MB download
and needs no SDK. It starts the CLR on its own thread (never from `DllMain`), tries
`v4.0.30319` then `v2.0.50727`, falls back to `EnumerateInstalledRuntimes`, calls
`Start()` before `ExecuteInDefaultAppDomain`, and honours `VOCALPATCH_EDITOR_DIR`
(where the runtime lives) and `VOCALPATCH_PROFILE` (force a profile id). Every step is
appended to `%TEMP%\vocalpatch-loader.log`, so a failure is diagnosable without a
debugger — that log is how the two GUID/slot bugs above were found and fixed.

Both bitnesses are built: `vocalpatch_loader_x64.dll` and `_x86.dll`. The loader must
match the host's bitness exactly, and so must the injector. `native/load_host.c` is a
throwaway host that `LoadLibrary`s the loader so `build_loader.ps1 -TestHost` can prove
a fresh build works with no editor involved.

## Build

Requires the .NET SDK (8 or 10). The native loader needs a C compiler — any of
Zig, MSVC, mingw-w64 or TinyCC, and `native/build_loader.ps1` autodetects one:

```powershell
dotnet build VocalPatch.slnx -c Release
dotnet test  VocalPatch.slnx

# native loaders -> native/out/vocalpatch_loader_x64.dll and _x86.dll,
# builds/loads a throwaway host to prove them, and drops the 64-bit one
# straight into the injection payload folder
powershell -ExecutionPolicy Bypass -File native\build_loader.ps1 -TestHost `
    -Payload (Resolve-Path src\VocalPatch.Editor\bin\Release\net472)

# everything a user needs -> dist\
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

`package.ps1` produces `dist\` with the loader, the injector, the CLI, the managed
payload (plus a starter `vpatch-editor.ini`) and one ready-to-copy OpenUtau plugin
folder per action.

Repackaging never wipes `dist\` and tolerates locked files: an editor that already has
the payload injected keeps those DLLs memory-mapped, and Windows refuses to overwrite a
mapped image. The locked file is renamed to `<name>.locked-<stamp>` and the new one takes
its place, so `dist\` can be refreshed without closing the editor — the running editor
keeps the old image, and the next injection loads the new one. `package.ps1` lists what
it refreshed that way.

## Use

```powershell
# convert between any two editors' formats
vpatch convert song.ustx song.svp
vpatch convert song.svp  song.vpr

# batch jobs
vpatch job swing     song.vpr out.vpr  --division 8 --amount 55
vpatch job quantize  song.ustx out.ustx --grid 4 --strength 80
vpatch jobs

# lyrics
vpatch pinyin song.svp song.pinyin.svp --split --tones marks
vpatch harmony song.ust out.ust --interval thirdUp --root C --scale major

# autosave
vpatch autosave song.vpr --interval 120 --snapshot

# OpenUtau plugin
vpatch-utau install "$env:APPDATA\OpenUtau\Plugins\VP-Swing" --job swing --set division 8 --set amount 55

# the same actions the injected runtime exposes, on a project file
vpatch-inject actions
vpatch-inject action song.ust swing
vpatch-inject action song.ust export

# injected runtime
vpatch-inject profiles
vpatch-inject targets
vpatch-inject inject synthv-studio vocalpatch_loader.dll
```

## Notes and limits

- The neutral model stores notes flat per track; multi-part projects are flattened
  on read and written back as one part per track.
- `ccs` (CeVIO) has no public schema, so that codec is deliberately tolerant on read
  and emits a conventional `CCSProject` tree on write. Verify output against your
  CeVIO version before relying on it.
- The OpenUtau plugin speaks OpenUtau's real diff protocol: notes it *adds* are written
  as `[#INSERT]` blocks, because OpenUtau pairs blocks with its own sequence by index
  and silently ignores a numbered block past the end. `[#PREV]` / `[#NEXT]` reference
  notes and every field a job doesn't own survive untouched. `OpenUtauPluginContractTests`
  replays OpenUtau's pairing algorithm so this can't regress unnoticed.
- OpenUtau's plugin path is fixed at 480 ticks per quarter and cannot split one note
  into several, so `OpenUtau → another format → OpenUtau` is the lossless route.
- The injected runtime's save-hook patches are postfix-only and guarded, so a host
  can never fail a save because of the patch.
- Per-editor *UI* tweaks (rounded notes, waveform opacity, spectrum widget …) are
  not included: they need per-version method targets that can only be derived by
  reverse-engineering each editor build. The runtime gives you the injection,
  hotkey, action and autosave scaffolding to add them.
- An editor running on self-contained .NET 5+ (CoreCLR) cannot use the
  `ICLRMetaHost` path — that needs a `hostfxr`/`nethost` loader instead. The four
  target editors are .NET Framework/WPF, which is what the shipped loader hosts. Such a
  host is still given the folder watcher and the hotkeys, and the log says so.
- A Framework editor's project folder is not necessarily where the profile looks: on the
  machine this was tested on, `Documents\Dreamtonics` holds only recovery copies and
  `Documents\OpenUtau` only the editor's own backups. The save hook covers projects
  anywhere (it keys off the save, not the path); the folder watcher covers only the
  profile's directories, so add `watch=<dir>` when projects live elsewhere.
- Repacking while editors are open cannot overwrite a payload file that is mapped into a
  running process, so `package.ps1` renames it to `<name>.locked-<stamp>` and drops the
  new file in. The leftovers are tied to the running editors and can be deleted once they
  exit; the next package run sweeps whatever is free.
- `tools/hotkey_host.exe` (source beside it) and `tools/savehook_probe.ps1` are the two
  probes used to verify the hotkey chain and the save hook without opening an editor.
  The save-hook probe is the reason we know the postfix intercepts the host's *own*
  writes rather than only ours.

## Licence

GPL-3.0-or-later. See [`LICENSE`](LICENSE).

## Not affiliated

VocalPatch is an independent, unofficial tool. It is not made, endorsed or supported by
Dreamtonics, Techno-Speech, Crypton Future Media, the CeVIO project, Internet Co., NetEase
or the OpenUtau project. Product names are trademarks of their respective owners.

It works by loading into an editor process that is already running on your own machine —
`native/vocalpatch_loader.c` hosts the CLR in-process, and `native/proxy_engine.c` builds
the drop-in loader proxies. It reads and rewrites only the project files you point it at,
and leaves a `.bak` beside every in-place edit.

Injection itself is in-process and ends when the editor exits; the drop-in route writes
nothing outside the editor's own folder. The one exception is the *optional* AppInit route
(`install-appinit.ps1`), which sets the `AppInit_DLLs` registry value — and
`uninstall-appinit.ps1` removes it again.
