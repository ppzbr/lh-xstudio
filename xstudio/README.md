# X Studio — `.svip3`, and what can be done with it

X Studio here means **`NetEase Cloud Music XStudio.exe`** (the UI is XiaoIce's X Studio,
shipped inside the NetEase Cloud Music wrapper): a native x64 Qt 6 application whose project
files are `.svip3`.

This folder holds everything learned about that editor, kept deliberately **outside**
`VocalPatch/`: one engine's format is not part of a neutral song model, and the codec plugs
into the toolkit with a single public call instead of an edit. Nothing in `VocalPatch/` needs
to change for any of this to work.

| file | what it is |
|---|---|
| `xstudio-format.md` | the recovered `.svip3` schema — field numbers, units, and the method that recovered them |
| `tools/xstudio_schema.py` | the recovery tool: `schema` pulls the `.proto` out of the exe, `read` decodes a project, `wire` dumps any protobuf |
| `src/Svip3Format.cs` | the codec: `.svip3` ↔ the neutral `Song` model |
| `src/ProtoWire.cs` | a hand-rolled proto3 reader/writer (no `Google.Protobuf`, no generated code) |
| `cli/` | the `svip3` command: `convert`, `info`, `notes` |
| `tests/` | 9 tests, including one that reads a wire fixture built by hand rather than by this codec |

## Build and use

```powershell
dotnet build xstudio\cli -c Release

$svip3 = "xstudio\cli\bin\Release\net8.0\svip3.exe"

& $svip3 info  "$env:APPDATA\XiaoIce\X Studio3\auto_backup\39971138.svip3"
& $svip3 notes "$env:APPDATA\XiaoIce\X Studio3\auto_backup\39971138.svip3"
& $svip3 convert song.svip3 song.svp      # notes out, to any format the toolkit knows
& $svip3 convert song.vpr   song.svip3    # notes in, as a project the editor can open

dotnet test xstudio\tests -c Release

python xstudio\tools\xstudio_schema.py read "%APPDATA%\XiaoIce\X Studio3\auto_backup\*.svip3"
```

`convert` needs no configuration because the CLI registers the codec on startup — that call
is the entire integration, and `ProjectIo` then treats `.svip3` like any other extension.

## What it carries, and what it does not

| | |
|---|---|
| carried | tracks, patterns flattened to absolute note positions, notes (position, length, MIDI pitch, lyric, pinyin), tempo, time signature |
| not carried | the piano-roll parameter curves, per-note vibrato, consonant/SP/silence lengths, per-note tone, track colour and reverb |
| dropped | the audio track — the neutral model is notes only, so an exported project has no backing track |

A file written here is a project the editor can open and sing. It is not a byte-for-byte copy
of what the editor saved, and it does not claim to be.

## Verification

`svip3 convert` was run `.svip3` → `.svip3` on **all eight template projects** the editor
installs — all four file versions (3.0.0 / 3.0.1 / 3.0.3), 5.7 MB down to 1.0 MB — and the
input and output were then read by `tools/xstudio_schema.py`, a reader that shares no code
with the C# one. Result: **21 singing tracks, 4535 notes, identical** in position, length,
pitch, lyric and `pronouncing`.

The only differences are the ones listed as dropped above, plus the canvas length
(`AppModel.duration`, see below). Each template also ships an audio track: it is skipped, and
it has no notes to lose.

Crossing into another format is honest about itself too. Synthesizer V's `.svp` uses `-` to
mean *hold the previous syllable* while X Studio uses `-` for a **rest**, so a rest that goes
through `.svp` comes back as a hold — measured on 风雪千千: 164 notes in, 150 out, the 14
rests absorbed. Inside `.svip3`, in and out, nothing is lost.

## Conventions measured from real projects

- **480 ticks per beat.** Each template's `AudioPattern.real_dur` divided by its own mp3's
  exact frame count gives 480.00 on seven of eight (the eighth has a trimmed clip). The
  neutral model's own resolution is 480, so positions map one to one.
- **A rest is a note with `lyric = "-"`**, carrying its own pitch and normally sitting in a
  gap — never the previous note's pitch, which is what separates a rest from a hold. Some
  projects also put `"-"` in `pronouncing`, some leave the field out; both forms exist, so the
  codec writes the lyric and leaves `pronouncing` as it arrived.
- **A pattern may start before tick 0.** One project has `real_pos = -1920` (a clip dragged
  left of the origin), encoded as a 10-byte sign-extended varint. The codec flattens patterns
  to absolute positions, which is what turns that into an ordinary note position.
- **`AppModel.duration` is a canvas, not a content length** — 1.05–2.11× the content end, with
  a floor of 115200 for an empty project. Its unit cannot be derived from the file, so the
  codec reproduces the observed envelope (the floor, otherwise twice the content) instead of
  inventing one.
- **`tempo` is BPM × 100**, `key_index` is a MIDI note number, `lyric` is the character and
  `pronouncing` its pinyin.
- The shipped templates arrive in a macOS zip, so every project is accompanied by a `._name.svip3`
  **AppleDouble sidecar** (218 bytes, starting `00 05 16 07`). Those are not projects; the codec
  says so by name rather than failing with a parser error.

## Hooking it into the toolkit

Two edits, both in `VocalPatch/`, both yours to make — this folder does not make them for you:

1. **The codec.** Add a project reference from `VocalPatch.Core` (or any front end) to
   `src/VocalPatch.XStudio.csproj` and one line in `FormatRegistry`'s static constructor:
   `Register(new Svip3Format());`. After that `vpatch convert song.svip3 song.svp` works and
   `.svip3` joins `vpatch formats`. (`Svip3Format.Kind` reports `FormatKind.Unknown` — the
   enum is the core's and has no X Studio member; nothing dispatches on it.)
2. **The profile.** The `xstudio` row in `VocalPatch/src/VocalPatch.Editor/EditorProfile.cs`
   is wrong for the editor on disk — measured, not guessed:

```csharp
// was: new[] { ".svip", ".mid" },
//      new[] { @"{AppData}\SXSEditor", @"{Documents}\NetEase Cloud Music XStudio" }
new EditorProfile("xstudio", "NetEase Cloud Music XStudio", "NetEase Cloud Music XStudio",
    new[] { ".svip3" },
    new[] { @"{AppData}\XiaoIce\X Studio3\auto_backup" }),
```

`auto_backup` is the folder that gains `.svip3` files while the editor runs, and the watcher
recurses. `template_projects\` is not listed on purpose: those never change. **Where the human
saves their own projects is not discoverable from the editor's files** — `Config\setting.ini`
has no key for it and `db\xstudio_local.db`'s `xs_work_info_tb.path` column was empty
(0 rows) on a machine that had only ever opened templates. That folder is set with
`watch=<dir>` in `vpatch-editor.ini`.

Without both edits the injected runtime still sees nothing: `.svip3` is not in the profile's
extension list, and nothing loads this codec into the editor's process.

## The singing engine is a cloud service (settled — do not chase it)

The image embeds `fastspeech_singing_v3.proto` (package `WL.Singing`), and it is worth being
precise about what that means, because the obvious reading is wrong.

What X Studio contains is the **client** half: a generated gRPC stub
(`…Stub@FastspeechSinging@Singing@WL@@`, `StubInterface`, `async_interface`) and its own caller
(`xstudio::WRSingingHandler::handle`, `xstudio::WRSingingTask::run`), speaking four methods:

```
/WL.Singing.FastspeechSinging/FetchBaseMetadataInput
/WL.Singing.FastspeechSinging/FetchAdditionMetadata
/WL.Singing.FastspeechSinging/FetchWave
/WL.Singing.FastspeechSinging/FetchRapMetadata
```

gRPC and TLS are statically linked into the exe (conan-built `grpc++`,
`grpc_secure_channel_create`, a c-ares resolver, HTTP/2), and the default target is in the
string table: **`xstudio-grpc.music.163.com:443`**, with `https://xstudio-api.music.163.com` as
the HTTP side. The app calls this overridable state its `ServerEnv` — `server_env_grpc_url`,
`server_env_http_url` and `server_env_preset` in preferences, logged as
`ServerEnv saved: preset=%1 http=%2 grpc=%3`.

So the engine is on NetEase's servers, behind a logged-in account
(`account_netease_access_token` in `Config\setting.ini`), and the client fetches
`base_meta_url` / `addition_meta_url` / `wave_url` back from it. **There is no engine on this
machine to reach instead:**

- the install is 52 files — Qt, ffmpeg, `SoundTouch.dll`, the application — no model, no
  engine binary, no local server;
- `%APPDATA%\XiaoIce\X Studio3` is 109 MB, essentially all of it the template projects and
  their mp3 backing tracks;
- `SharedRead.exe` / `SharedWrite.exe` (23 KB each) are `SharedMem` named-mapping helpers for
  moving data between instances, not an engine — and the image contains no named pipes at all.

The protocol is fully documented above and the endpoint is **configurable**, so a self-hosted
`FastspeechSinging` server would be accepted by the editor — but not without the model, and no
weights exist on disk. **This is not an editor-free path to sound**, and it is not worth a
session spent hunting one.

## Still open

1. **`fragment_feature_v3.proto`** — the cached per-fragment features (`oriF0`, `powerCoef`,
   `specTransCoef`, `apCoef`, `energyValue`). Find where those blobs are written and that is the
   render cache.
2. **The curves.** `Svip3Format` deliberately ignores them; carrying `edited_pitch_line` and
   friends would need a place in the neutral model, not just a field number.
3. **The audio track.** Skipped today. The data is right there (`audio_file_path`, `real_dur` in
   ticks) so the loss is a model decision, not a format limitation.

## Rules for anything working on this machine

- Never modify, replace or delete an X Studio file: `%APPDATA%\XiaoIce\X Studio3\`
  (`db\xstudio_local.db`, `auto_backup\`, `Config\`, `log_file\`) is the editor's, and it may be
  running.
- Never kill the editor process. An editor holding unsaved work is not the agent's to close.
- Work on copies. `auto_backup\*.svip3` and `template_projects\` are ready-made fixtures — copy
  them out to a temp folder rather than writing in place.
