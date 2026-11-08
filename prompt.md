# X Studio (NetEase Cloud Music XStudio) — what works, what does not

> Paste everything below the rule into an agent that has shell access to the Windows
> machine running X Studio. It is written to stand alone, and it starts with the truth:
> **there is no live, in-process note control for X Studio.** Its project file is no
> longer the obstacle, though — `.svip3` is Protocol Buffers, the schema was recovered from
> the editor's own image, and a codec for the note layer now ships in `xstudio/`, checked
> note for note against every template project the editor installs.

---

## Read this first

X Studio here means **`NetEase Cloud Music XStudio.exe`** (the UI is XiaoIce's X Studio,
shipped inside the NetEase Cloud Music wrapper). Measured on the machine this was
written on: x64, 92 MB, a **Qt 6** application
(`Qt6Core/Qt6Gui/Qt6Widgets/Qt6Multimedia/Qt6Sql/Qt6Network/Qt6Svg/Qt6Xml/Qt6Core5Compat`),
with `msvcp140`/`vcruntime140`, `SoundTouch.dll`, a native `QNrbfFormat.dll`,
`SharedRead.exe`/`SharedWrite.exe`, and an **x86** .NET Framework `NAudio.dll` that an
x64 process cannot load — it is shipped but inert here.

| what an AI wants | state for X Studio |
|---|---|
| read/write a project file | **works for the note layer** — the `.svip3` codec and an `svip3 convert` command live in `xstudio/` (step 0, step 4); it is not wired into the toolkit as cloned |
| live note editing inside the editor | **does not exist** |
| an MCP bridge into the process | **does not exist** |
| the eleven edit actions inside the running editor, bound to hotkeys | the drop-in installs and the hotkeys exist, but as cloned they act on nothing: the profile's row is wrong and the codec is not loaded into the editor's process (step 3) |
| autosave of project files while the editor runs | nothing, until the profile row is patched (step 3); then `auto_backup\` is covered and `watch=<dir>` covers the human's own folder |
| making sound without the editor | **no**: the singing engine is a NetEase cloud gRPC service the editor is a client of — settled, with evidence, in step 4 leads; do not chase it |

If the human asked for "AI control of X Studio", the correct answer is: *not yet* — and
here is exactly what is missing and what to measure next. Do not install something and
call it control.

## Rules

1. The only thing written outside this repository is one `winmm.dll` in the editor's
   application directory. Delete it to uninstall.
2. Never modify, replace or delete an X Studio file, and never touch
   `%APPDATA%\XiaoIce\X Studio3\` — `db\xstudio_local.db`, `auto_backup\`, `Config\` and
   `log_file\` are the editor's own, and it is running.
3. Never kill the editor process.
4. Do not claim a capability before the code that would implement it has been pointed at.
5. If a step fails, stop at that step and report exactly what happened.
6. Work only inside this repository. Nothing else on the machine is yours to change.

## Step 0 — the repository, and the one thing that works today

```powershell
git clone https://github.com/ppzbr/lh-xstudio
cd lh-xstudio
```

Build the X Studio codec first, because it is the only part of this page that works without
editing anything:

```powershell
dotnet build xstudio\cli -c Release
$svip3 = "xstudio\cli\bin\Release\net8.0\svip3.exe"
& $svip3 info "$env:APPDATA\XiaoIce\X Studio3\auto_backup\*.svip3"
```

`xstudio/README.md` has the full story; `xstudio/xstudio-format.md` has the schema. Only after
that does the drop-in runtime in `VocalPatch/` come into play, and it needs the **.NET SDK
(8 or 10)** plus one C compiler for the loader — `zig` (`python -m pip install ziglang`),
MSVC, mingw-w64 or TinyCC; `build_loader.ps1` autodetects one.

```powershell
dotnet build VocalPatch\VocalPatch.slnx -c Release

powershell -ExecutionPolicy Bypass -File VocalPatch\native\build_loader.ps1 -TestHost `
    -Payload (Resolve-Path VocalPatch\src\VocalPatch.Editor\bin\Release\net472)

powershell -ExecutionPolicy Bypass -File VocalPatch\package.ps1
```

Do not skip `-TestHost`: it is the only step that proves the loader works before any
editor is involved. The loader writes every step to `%TEMP%\vocalpatch-loader.log`.

## Step 1 — confirm the drop-in can even get in

`NetEase Cloud Music XStudio.exe` **statically imports `WINMM.dll`** (verified with a PE
import walk; `Qt6Multimedia.dll` does too). `winmm` is not in the KnownDLLs list, so
Windows resolves that import from the **application directory first**:

```powershell
powershell -ExecutionPolicy Bypass -File VocalPatch\dist\dropin\pe-imports.ps1 `
    -Path "D:\NetEase Cloud Music XStudio\NetEase Cloud Music XStudio.exe" -NamesOnly -Only WINMM.dll
```

If that prints nothing, stop: the drop-in cannot load into this build, and nothing below
applies.

## Step 2 — install the drop-in

```powershell
Copy-Item VocalPatch\dist\dropin\winmm.dll "D:\NetEase Cloud Music XStudio\"
```

`VocalPatch\native\deploy_dropin.ps1` can do it (`-Apply` / `-Apply -Remove`), but **its
target table hard-codes the development machine's paths** — check the `NetEase XStudio`
row first, or just copy the file yourself. The shim forwards all 180 `winmm` exports to
`System32\winmm.dll` and, in `DllMain`, starts a thread that loads
`vocalpatch_loader.dll`; the editor's own audio behaviour cannot change.

Start X Studio and read the logs:

```powershell
Get-Content "$env:TEMP\vocalpatch-loader.log" -Tail 5
Get-Content "$env:TEMP\vocalpatch-editor.log" -Tail 20
```

What you should see, and what to do about each part:

```
profile: NetEase Cloud Music XStudio
watching 1 folder(s)                      <-- and it is the wrong one; step 3
patched N save method(s)
hotkey: Ctrl+Alt+V, Ctrl+Alt+S, ...       <-- the hotkeys do exist
```

The chords are `Ctrl+Alt+V/S/Q/C/T/N/P/H/L/E/Z` (snapshot, swing, quantize, staccato,
transpose, normalize, pinyin, harmony, lyrics, export, restore). Settings live in
`vpatch-editor.ini` beside the payload: `profile=`, `autosave=`, `patchsaves=`,
`snapshotinterval=`, `watch=<dir>`, `hotkey.<action>=`, `hotkeydebug=true`.
`hotkeydebug=true` logs every key the hook sees — the only way to tell a dead hook from
a working one with nothing to do.

## Step 3 — the profile is wrong, and here is the row it should have

This is a measured mismatch, not a guess. The `xstudio` row in
`VocalPatch\src\VocalPatch.Editor\EditorProfile.cs` says:

```
id         xstudio
process    "NetEase Cloud Music XStudio"     (prefix match, so …XStudio.exe does match)
extensions .svip, .mid
folders    {AppData}\SXSEditor, {Documents}\NetEase Cloud Music XStudio
```

The editor on disk says otherwise: its data root is `%APPDATA%\XiaoIce\X Studio3\`
(`Config\setting.ini`, `db\xstudio_local.db`, `auto_backup\*.svip3`, `log_file\`,
`template_projects\`) and its projects are **`.svip3`**. The watcher and every hotkey action
filter by that extension list, so as cloned the runtime sees nothing at all. The corrected
row:

```csharp
new EditorProfile("xstudio", "NetEase Cloud Music XStudio", "NetEase Cloud Music XStudio",
    new[] { ".svip3" },
    new[] { @"{AppData}\XiaoIce\X Studio3\auto_backup" }),
```

`auto_backup` is the folder that gains `.svip3` files while the editor runs, and the watcher
recurses. `template_projects\` is left out on purpose: those never change, and copying 30 MB
of them per cycle is noise.

**Two further things it cannot know.** First, where the human saves their own projects: there
is no project-path key in `Config\setting.ini`, and `db\xstudio_local.db` has a
`xs_work_info_tb.path` column that stays empty until something is saved (measured: 0 rows on a
machine that had only ever opened templates). Ask the human, or read the folder off a saved
`.svip3`, then set `watch=<that folder>` in `vpatch-editor.ini` beside the payload — `watch=`
adds a folder, it does not replace the profile's own. Until then the hotkeys act on the newest
`.svip3` under `auto_backup\`: the editor's own autosave copy, not the project file the human
saved. Say that out loud rather than letting someone believe a hotkey rewrote their project.

Second, and more important: **patching the row is not enough for the hotkeys to work.** The
actions edit a project through a codec, and the `.svip3` codec lives in this repository's
`xstudio/` folder, loaded by nothing. Wiring it in is *not* the two lines it looks like —
both of the obvious ones fail, and both were measured:

* `VocalPatch.Core` is `netstandard2.0` and the codec is `net8.0`, so Core cannot reference
  it. Adding `<ProjectReference Include="..\..\..\xstudio\src\VocalPatch.XStudio.csproj" />`
  to `VocalPatch.Core.csproj` fails with **MSB4006: a circular dependency in the target
  dependency graph** — the codec already references Core.
* The runtime that executes the actions *inside the editor* is the payload's `net472`
  target, and a `net472` assembly cannot load a `net8.0` one either.

The shape that does work — compiled, not guessed — is three steps:

```powershell
# 1. retarget the codec so the in-editor runtime can use it (it compiles as-is)
#    xstudio\src\VocalPatch.XStudio.csproj
#      <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>

# 2. reference it from the project that hosts the actions, which already references Core,
#    never from Core itself
#    VocalPatch\src\VocalPatch.Editor\VocalPatch.Editor.csproj
#      <ProjectReference Include="..\..\..\xstudio\src\VocalPatch.XStudio.csproj" />

# 3. register it where the runtime starts, not in FormatRegistry's constructor
#    FormatRegistry.Register(new Svip3Format());

dotnet build VocalPatch\VocalPatch.slnx -c Release
```

Either do that, or use `xstudio`'s own `svip3` command and the file path instead of the
hotkeys. Both are honest; claiming the hotkeys work without one of them is not.

The actions go through `VocalPatch.Core`'s codecs, so once `.svip3` is registered and the row
is patched, an action on an `.svip3` really does edit it, and autosave snapshots whatever the
watcher finds into a `.vocalpatch-autosave\` folder next to it. What still does not exist is
live, in-process control — the rest of this page is about what does.

## Step 4 — the file format is identified: `.svip3` is Protocol Buffers

This part is measured, not guessed, and it is what changed: `.svip3` was the last blind
format in the toolkit's table, and it is now fully specified — in `xstudio/`, deliberately not
in `VocalPatch.Core`.

* **`.svip3` is a serialized `xstudio.proto.AppModel`.** The first bytes are already a
  protobuf field — `0a 2f //Users/wey/Desktop/….svip3`, i.e. field 1, wire type 2,
  length-delimited — and the image names its own schema in plain ASCII:
  `type.googleapis.com/xstudio.proto.SingingTrack`, `xstudio.proto.Note.lyric`,
  `xstudio.proto.SingingTrack.ai_singer_id`, `xstudio.proto.LineParamNode`, …
* **The whole `.proto` is embedded in the exe.** A protobuf C++ build registers a
  serialized `FileDescriptorProto`, and that message begins `0a <len> "<name>.proto"`.
  Scanning the 92 MB image for that shape recovers nine descriptors — X Studio's own
  `xstudio_model.proto` (the project), `xstudio_config.proto`,
  `fastspeech_singing_v3.proto`, `fragment_feature_v3.proto`, plus the five
  `google/protobuf` ones. Message names, field names, field numbers, types and enums all
  come back.
* **The tool ships in this repository.**
  ```powershell
  python xstudio\tools\xstudio_schema.py schema "D:\NetEase Cloud Music XStudio\NetEase Cloud Music XStudio.exe"
  python xstudio\tools\xstudio_schema.py read   "…\X Studio3\auto_backup\39971138.svip3"
  python xstudio\tools\xstudio_schema.py wire   anything.bin
  ```
  `read` decodes a real project down to tracks → patterns → notes (lyric, pinyin, pitch)
  and curves. Full schema and measurements: `xstudio/xstudio-format.md`.
* **The tick unit is measured at 480 per beat**: each template project's
  `AudioPattern.real_dur` divided by its own mp3's exact frame count gives 480.00 on
  seven of the eight templates (the eighth has a trimmed clip). `key_index` is a MIDI
  note number, `tempo` is BPM × 100, `lyric` is the character and `pronouncing` its
  pinyin.
* **The codec ships, in `xstudio/`.** `xstudio/src/Svip3Format.cs` reads and writes the note
  layer, on a hand-rolled proto3 reader/writer (`xstudio/src/ProtoWire.cs`) — no protobuf
  dependency, no generated code, and unknown fields are skipped by wire type the way proto3
  says to. `xstudio`'s own CLI does the conversion:

  ```powershell
  dotnet build xstudio\cli -c Release
  xstudio\cli\bin\Release\net8.0\svip3.exe convert song.svip3 song.svp   # notes out
  xstudio\cli\bin\Release\net8.0\svip3.exe convert song.vpr   song.svip3 # notes in
  ```
  What it does **not** carry, because the neutral model has nowhere to put them: the
  piano-roll parameter curves, per-note vibrato, consonant/SP/silence lengths, per-note tone
  — and the audio track, so a project written this way arrives without its backing track.
* **Verified against real material.** All eight template projects the editor installs, all
  four file versions (3.0.0/3.0.1/3.0.3): 21 singing tracks and 4535 notes, identical in
  position, length, pitch, lyric and `pronouncing` after a round trip, read back by
  `xstudio\tools\xstudio_schema.py` — a reader that shares no code with the C# one. Four conventions
  came out of that, all recorded in `xstudio-format.md`: a rest is a note with `lyric = "-"`;
  a pattern may start before tick 0 (one project has `real_pos = -1920`); `duration` is a
  canvas padded past the content, so the codec reproduces the observed envelope rather than
  deriving a value; and the shipped templates are a macOS zip, so every project is accompanied
  by a `._name.svip3` AppleDouble sidecar that is not a project at all.
* **One trap when converting through another editor.** X Studio's `-` rest is Synthesizer V's
  `-` continuation. In 风雪千千, sending the project out to `.svp` and back turned 14 rests
  into holds (164 notes in, 150 out). Inside `.svip3`, in and out, nothing changes.

Leads that are still open, in the order worth trying:

0. **First, a lead that is closed, so it is not chased.** The image embeds
   `fastspeech_singing_v3.proto` (package `WL.Singing`), which looks like a singing engine
   sitting on this machine. It is not. What is embedded is the **client**: a generated gRPC
   stub (`…Stub@FastspeechSinging@Singing@WL@@`) and X Studio's own caller
   (`xstudio::WRSingingHandler::handle`, `xstudio::WRSingingTask::run`), against four methods
   (`FetchBaseMetadataInput`, `FetchAdditionMetadata`, `FetchWave`, `FetchRapMetadata`), with
   gRPC and TLS statically linked and the default target in the string table:
   `xstudio-grpc.music.163.com:443`. Synthesis runs on NetEase's servers, behind a logged-in
   account (`account_netease_access_token` in `Config\setting.ini`), and the client fetches
   `base_meta_url` / `addition_meta_url` / `wave_url` back. There is no engine, no model and no
   local server on the machine (the install is 52 files of Qt/ffmpeg/`SoundTouch.dll`; the
   109 MB data root is template projects and their mp3s; `SharedRead.exe`/`SharedWrite.exe` are
   23 KB `SharedMem` helpers, and the image has no named pipes). The endpoint *is*
   configurable — the app calls it `ServerEnv` (`server_env_grpc_url` in preferences, logged as
   `ServerEnv saved: preset=%1 http=%2 grpc=%3`) — so a self-hosted `FastspeechSinging` server
   would be accepted, but not without the model. **This is not an editor-free path to sound;
   do not spend the session looking for one.**
1. **`fragment_feature_v3.proto`** — the cached per-fragment features (`oriF0`,
   `powerCoef`, `specTransCoef`, `apCoef`, `energyValue`, fades, real/fragment times).
   Find where those blobs are written and that is the render cache.
2. **`%APPDATA%\XiaoIce\X Studio3\db\xstudio_local.db`** — SQLite (`SQLite format 3\0`), one
   table `xs_work_info_tb(id, path, duration, singers, modify_time)`, **0 rows** on a machine
   that has only ever opened templates. It is the app's saved-work index: the row appears when
   the human saves a project from the UI, and that `path` is the folder worth `watch=`.
   (`AppConfig.recent_project_list` carries the same idea as `RecentProject.file_path`.)
3. **`QNrbfFormat.dll`** — NRBF is .NET's BinaryFormatter wire format. It is *not* what
   `.svip3` uses; something else in this application does. Do not spend the first hour
   here.
4. **`NAudio.dll` is x86 while this process is x64**, so it cannot load here. Do not
   build a plan on it.

And the honest framing for the human: the drop-in proves *we can run code inside X
Studio*; the recovered schema and `Svip3Format` prove *we can read and write its
projects*. **Live note control inside the running editor is the thing that still does not
exist.**

## Step 5 — what you can do meanwhile

Two separate things, and it matters which one you are using.

**X Studio's own command**, which works as cloned and needs nobody's permission:

```powershell
$svip3 = "xstudio\cli\bin\Release\net8.0\svip3.exe"
& $svip3 convert song.svip3 song.svp      # notes out: pitch, length, lyric, pinyin, tempo
& $svip3 convert song.svp   song.svip3    # notes back in, as a project the editor can open
& $svip3 info  song.svip3
& $svip3 notes song.svip3
```

**The toolkit's own command**, which speaks the same formats — `ust`, `ustx`, `vsqx`, `vpr`,
`ppsf`, `ccs`, `svp`, `midi`, `musicxml`, `lrc`, `srt`, `ass`, `ufdata.json`,
`commonnote.json`, `vp.json` — plus every job (`swing`, `quantize`, `pinyin`, `harmony`, …):

```powershell
VocalPatch\dist\cli\vpatch.exe convert song.svp song.ustx
VocalPatch\dist\cli\vpatch.exe job quantize song.ustx out.ustx --grid 4
```

`vpatch` does **not** know `.svip3` as cloned — it says "Cannot determine format" — because the
codec lives in `xstudio/` and nothing registers it (step 3). The MCP server's own project
tools (`project_create`, `notes_add`, `lyrics_fill`, `export`, `export_midi`, `engines`,
`open_in`) know four engines and **X Studio is not one of them**. And note what does not
survive any file path: curves, vibrato and the backing track.

## Step 6 — portability and traps

* The shim has a loader path baked in; on another machine it resolves through
  `VOCALPATCH_LOADER` → a `vocalpatch.ini` beside the shim → a loader beside the shim →
  `%LOCALAPPDATA%\VocalPatch\payload\` → `%ProgramData%\VocalPatch\payload\` → the baked
  path. Set `VOCALPATCH_EDITOR_DIR` when the runtime is not next to the loader.
* Loader, injector and host bitness must all match. This host is x64.
* Repackaging while the editor is injected cannot overwrite a mapped payload file; the
  old one is renamed `<name>.locked-<stamp>` and the new one dropped in.
* Uninstalling is deleting the `winmm.dll` from the application directory.

## Step 7 — when it goes wrong

| symptom | cause |
|---|---|
| no `vocalpatch-loader.log` | the shim is not in the application directory, or this build does not import winmm (step 1) |
| loader log stops before `loader ok` | the payload path is wrong — set `VOCALPATCH_EDITOR_DIR` or put the payload beside the loader |
| no line in `vocalpatch-editor.log` for this pid | the loader ran, the runtime did not; the loader log says why |
| hotkeys appear dead | `hotkeydebug=true`, then read the log |
| an action logs "nothing to do" | as cloned, always: `.svip3` is not in the profile's extension list and the codec is not registered (step 3) |
| `vpatch` says "Cannot determine format" for an `.svip3` | same cause — use `xstudio`'s `svip3` command, or register the codec (step 3) |
| `svip3` refuses a file it should read | check the name: `._name.svip3` is an AppleDouble sidecar from the shipped macOS zip, not a project |

Stop at the first unexplained failure and report both logs verbatim. Never restart or
kill the editor: it is the human's session.
