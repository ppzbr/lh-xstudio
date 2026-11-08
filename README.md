# lh-xstudio

What works for **X Studio** (`NetEase Cloud Music XStudio.exe`, the UI XiaoIce ships
inside the NetEase Cloud Music wrapper), what does not, and the measurements behind
both.

The honest headline first: **there is no live, in-process note control for X Studio.**
Its project file is no longer the obstacle, though — `.svip3` is Protocol Buffers,
the schema was recovered from the editor's own image, and a codec for the note layer
ships in `xstudio/`, checked note for note against every template project the editor
installs.

So the split is:

| thing | state |
|---|---|
| read and write an `.svip3` project's notes — pitch, length, lyric, pinyin, tempo | **built and verified** against all eight shipped templates |
| convert `.svip3` to and from a dozen other editors' formats | built (`svip3` CLI, and `vpatch` once the codec is registered) |
| run code inside the running editor (a one-file drop-in) | built; the mechanism is proven on six other editors |
| edit the notes of the project open in the editor's memory | **does not exist** — and nothing here pretends otherwise |
| anything that makes sound | server-side, behind a logged-in NetEase account (see the closed lead below) |

Measured on the machine this was written on: x64, 92 MB, a **Qt 6** application,
with `SoundTouch.dll`, a native `QNrbfFormat.dll`, `SharedRead.exe`/`SharedWrite.exe`,
and an **x86** .NET Framework `NAudio.dll` that an x64 process cannot load.

## Requirements

* Windows, with X Studio installed.
* The **.NET SDK (8 or 10)**.
* Only if you want the drop-in runtime: one C compiler for the native loader —
  `zig` (`python -m pip install ziglang`), MSVC, mingw-w64 or TinyCC.
  `build_loader.ps1` autodetects one.

## The one thing that works with nothing installed

```powershell
git clone https://github.com/ppzbr/lh-xstudio
cd lh-xstudio
dotnet build xstudio\cli -c Release
$svip3 = "xstudio\cli\bin\Release\net8.0\svip3.exe"
& $svip3 info "$env:APPDATA\XiaoIce\X Studio3\auto_backup\*.svip3"
```

`xstudio/README.md` has the full story; `xstudio/xstudio-format.md` has the schema.

```powershell
& $svip3 convert song.svip3 song.svp      # notes out: pitch, length, lyric, pinyin, tempo
& $svip3 convert song.svp   song.svip3    # notes back in, as a project the editor can open
& $svip3 info  song.svip3
& $svip3 notes song.svip3
```

What a file-level round trip does **not** carry, because the neutral song model has
nowhere to put them: the piano-roll parameter curves, per-note vibrato,
consonant/SP/silence lengths, per-note tone — and the audio track, so a project
written this way arrives without its backing track.

## `.svip3` is Protocol Buffers, and the schema is in the image

This part is measured, not guessed:

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
  `google/protobuf` ones. Message names, field names, field numbers, types and enums
  all come back.

```powershell
python xstudio\tools\xstudio_schema.py schema "D:\NetEase Cloud Music XStudio\NetEase Cloud Music XStudio.exe"
python xstudio\tools\xstudio_schema.py read   "…\X Studio3\auto_backup\39971138.svip3"
python xstudio\tools\xstudio_schema.py wire   anything.bin
```

* **The tick unit is 480 per beat**, measured: each template project's
  `AudioPattern.real_dur` divided by its own mp3's exact frame count gives 480.00 on
  seven of the eight templates (the eighth has a trimmed clip). `key_index` is a MIDI
  note number, `tempo` is BPM × 100, `lyric` is the character and `pronouncing` its
  pinyin.
* **The codec ships, in `xstudio/`.** `xstudio/src/Svip3Format.cs` reads and writes the
  note layer on a hand-rolled proto3 reader/writer (`xstudio/src/ProtoWire.cs`) — no
  protobuf dependency, no generated code, and unknown fields are skipped by wire type
  the way proto3 says to.
* **Verified against real material.** All eight template projects the editor installs,
  all four file versions (3.0.0/3.0.1/3.0.3): 21 singing tracks and 4535 notes,
  identical in position, length, pitch, lyric and `pronouncing` after a round trip,
  read back by `xstudio/tools/xstudio_schema.py` — a reader that shares no code with
  the C# one. Four conventions came out of that, all recorded in `xstudio-format.md`:
  a rest is a note with `lyric = "-"`; a pattern may start before tick 0 (one project
  has `real_pos = -1920`); `duration` is a canvas padded past the content, so the codec
  reproduces the observed envelope rather than deriving a value; and the shipped
  templates are a macOS zip, so every project is accompanied by a `._name.svip3`
  AppleDouble sidecar that is not a project at all.
* **One trap when converting through another editor.** X Studio's `-` rest is
  Synthesizer V's `-` continuation. In 风雪千千, sending the project out to `.svp` and
  back turned 14 rests into holds (164 notes in, 150 out). Inside `.svip3`, in and out,
  nothing changes.

## The drop-in: code inside the running editor

`NetEase Cloud Music XStudio.exe` **statically imports `WINMM.dll`** (verified with a
PE import walk; `Qt6Multimedia.dll` does too). `winmm` is not in the KnownDLLs list, so
Windows resolves that import from the **application directory first** — which is what
makes a one-file drop-in possible.

```powershell
dotnet build VocalPatch\VocalPatch.slnx -c Release

powershell -ExecutionPolicy Bypass -File VocalPatch\native\build_loader.ps1 -TestHost `
    -Payload (Resolve-Path VocalPatch\src\VocalPatch.Editor\bin\Release\net472)

powershell -ExecutionPolicy Bypass -File VocalPatch\package.ps1
```

Do not skip `-TestHost`: it is the only step that proves the loader works before any
editor is involved. The loader writes every step to `%TEMP%\vocalpatch-loader.log`.

Confirm the drop-in can get in, then install it:

```powershell
powershell -ExecutionPolicy Bypass -File VocalPatch\dist\dropin\pe-imports.ps1 `
    -Path "D:\NetEase Cloud Music XStudio\NetEase Cloud Music XStudio.exe" -NamesOnly -Only WINMM.dll
# prints nothing -> stop: the drop-in cannot load into this build

Copy-Item VocalPatch\dist\dropin\winmm.dll "D:\NetEase Cloud Music XStudio\"
```

`VocalPatch\native\deploy_dropin.ps1` can do it (`-Apply` / `-Apply -Remove`), but its
target table hard-codes the development machine's paths — check the `NetEase XStudio`
row first. The shim forwards all 180 `winmm` exports to `System32\winmm.dll` and, in
`DllMain`, starts a thread that loads `vocalpatch_loader.dll`; the editor's own audio
behaviour cannot change. Uninstalling is deleting that one file.

Then start X Studio and read the logs:

```powershell
Get-Content "$env:TEMP\vocalpatch-loader.log" -Tail 5
Get-Content "$env:TEMP\vocalpatch-editor.log" -Tail 20
```

```
profile: NetEase Cloud Music XStudio
watching 1 folder(s)                      <-- and it is the wrong one; see below
patched N save method(s)
hotkey: Ctrl+Alt+V, Ctrl+Alt+S, ...       <-- the hotkeys do exist
```

### As cloned, the hotkeys do nothing useful — two measured reasons

**1. The profile row is wrong.** The `xstudio` row in
`VocalPatch/src/VocalPatch.Editor/EditorProfile.cs` says extensions `.svip, .mid` and
folders `{AppData}\SXSEditor`, `{Documents}\NetEase Cloud Music XStudio`. The editor
on disk says otherwise: its data root is `%APPDATA%\XiaoIce\X Studio3\`
(`Config\setting.ini`, `db\xstudio_local.db`, `auto_backup\*.svip3`, `log_file\`,
`template_projects\`) and its projects are **`.svip3`**. The watcher and every hotkey
action filter by that extension list, so as cloned the runtime sees nothing at all.
The corrected row:

```csharp
new EditorProfile("xstudio", "NetEase Cloud Music XStudio", "NetEase Cloud Music XStudio",
    new[] { ".svip3" },
    new[] { @"{AppData}\XiaoIce\X Studio3\auto_backup" }),
```

`auto_backup` is the folder that gains `.svip3` files while the editor runs, and the
watcher recurses. `template_projects\` is left out on purpose: those never change, and
copying 30 MB of them per cycle is noise.

Where the human saves their *own* projects is not knowable from the install: there is
no project-path key in `Config\setting.ini`, and `db\xstudio_local.db` has an
`xs_work_info_tb.path` column that stays empty until something is saved (measured: 0
rows on a machine that had only ever opened templates). Ask the human, or read the
folder off a saved `.svip3`, then set `watch=<that folder>` in `vpatch-editor.ini`
beside the payload. Until then the hotkeys act on the newest `.svip3` under
`auto_backup\` — the editor's own autosave copy, not the file the human saved.

**2. The codec is not registered — and the obvious wiring does not compile.** The
actions edit a project through a codec, and the `.svip3` codec in `xstudio/` is
referenced by nothing in `VocalPatch`. Both of the two-line fixes fail, and both were
measured:

* `VocalPatch.Core` is `netstandard2.0` and the codec is `net8.0`. Adding
  `<ProjectReference Include="..\..\..\xstudio\src\VocalPatch.XStudio.csproj" />` to
  `VocalPatch.Core.csproj` fails with **MSB4006: a circular dependency in the target
  dependency graph** — the codec already references Core.
* The runtime that executes the actions *inside the editor* is the payload's `net472`
  target, and a `net472` assembly cannot load a `net8.0` one either.

The shape that does work — compiled, not guessed — is three steps:

```powershell
# 1. retarget the codec so the in-editor runtime can use it (it compiles as-is)
#    xstudio\src\VocalPatch.XStudio.csproj
#      <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>

# 2. reference it from the project that hosts the actions, which already references
#    Core, never from Core itself
#    VocalPatch\src\VocalPatch.Editor\VocalPatch.Editor.csproj
#      <ProjectReference Include="..\..\..\xstudio\src\VocalPatch.XStudio.csproj" />

# 3. register it where the runtime starts, not in FormatRegistry's constructor
#    FormatRegistry.Register(new Svip3Format());

dotnet build VocalPatch\VocalPatch.slnx -c Release
```

Either do that, or use `xstudio`'s own `svip3` command and the file path instead of
the hotkeys. Both are honest; claiming the hotkeys work without one of them is not.
`vpatch` says "Cannot determine format" for an `.svip3` until the codec is registered.

Once that is done, an action on an `.svip3` really does edit it, and autosave
snapshots whatever the watcher finds into a `.vocalpatch-autosave\` folder next to it.
What still does not exist is live, in-process control.

## Why `VocalPatch/` is in this repository

The drop-in, the hotkey actions, the loader and the profiles are the toolkit's, not
X Studio's. It sits here at the tree it was developed in, with its own `README.md` and
`QUICKSTART.zh.md`, so that this repository is self-contained: the profile patch above
and the drop-in both live in it. `VocalPatch/dist/` is **not** committed — the build
steps above produce it.

## A lead that is closed, so it is not chased

The image embeds `fastspeech_singing_v3.proto` (package `WL.Singing`), which looks
like a singing engine sitting on this machine. It is not. What is embedded is the
**client**: a generated gRPC stub and X Studio's own caller, against four methods
(`FetchBaseMetadataInput`, `FetchAdditionMetadata`, `FetchWave`, `FetchRapMetadata`),
with gRPC and TLS statically linked and the default target in the string table:
`xstudio-grpc.music.163.com:443`. Synthesis runs on NetEase's servers, behind a
logged-in account (`account_netease_access_token` in `Config\setting.ini`), and the
client fetches `base_meta_url` / `addition_meta_url` / `wave_url` back. There is no
engine, no model and no local server on the machine. **This is not an editor-free path
to sound; do not spend the session looking for one.**

Open leads, in the order worth trying:

1. **`fragment_feature_v3.proto`** — the cached per-fragment features (`oriF0`,
   `powerCoef`, `specTransCoef`, `apCoef`, `energyValue`, fades, real/fragment times).
   Find where those blobs are written and that is the render cache.
2. **`%APPDATA%\XiaoIce\X Studio3\db\xstudio_local.db`** — SQLite, one table
   `xs_work_info_tb(id, path, duration, singers, modify_time)`, **0 rows** on a machine
   that has only ever opened templates. It is the app's saved-work index: the row
   appears when the human saves a project from the UI, and that `path` is the folder
   worth `watch=`.
3. **`QNrbfFormat.dll`** — NRBF is .NET's BinaryFormatter wire format. It is *not* what
   `.svip3` uses; something else in this application does. Do not spend the first hour
   here.
4. **`NAudio.dll` is x86 while this process is x64**, so it cannot load here.

## Layout

| path | what |
|---|---|
| `prompt.md` | the paste-ready brief: the whole story for an agent that has never seen this repository |
| `xstudio/src/` | the `.svip3` codec: `Svip3Format.cs`, `ProtoWire.cs` |
| `xstudio/cli/` | the `svip3` command — `convert`, `info`, `notes` |
| `xstudio/tools/xstudio_schema.py` | the independent Python reader: `schema`, `read`, `wire` |
| `xstudio/xstudio-format.md` | the recovered schema and the measurements |
| `xstudio/tests/` | the codec's own suite |
| `VocalPatch/` | the toolkit: the runtime the drop-in starts, the loader, the profiles, the drop-in shim |
| `tests/test_repo.py` | this repository's own consistency checks |
| `MANIFEST.txt` | sha256 of every tracked file; `python tools/make-manifest.py --check` re-derives it |

## Tests

```powershell
dotnet test xstudio\tests -c Release         # the codec
dotnet test VocalPatch\VocalPatch.slnx       # the toolkit
python -m pytest                             # this repository's own consistency
```

## Licence

MIT for this repository's own work; the `VocalPatch` tree carries its own `LICENSE`.
