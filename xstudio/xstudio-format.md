# X Studio's project file — `.svip3`

It is Protocol Buffers. The schema is not on disk, but it ships inside the editor, and
`tools/xstudio_schema.py` walks it back out. Nothing here is guesswork: every field
number, name and unit below was read from the editor's own descriptor or measured
against a project file the editor wrote.

```
python tools/xstudio_schema.py schema "D:\NetEase Cloud Music XStudio\NetEase Cloud Music XStudio.exe"
python tools/xstudio_schema.py read   "…\X Studio3\auto_backup\39971138.svip3"
python tools/xstudio_schema.py wire   anything.bin
```

That matters because `.svip3` was the last blind spot in the toolkit's format table (`ust
ustx vsqx vpr ppsf ccs svp midi ufdata commonnote musicxml lrc srt ass vp.json`, no
`svip3`). It is no longer a reverse-engineering project: it is a codec — `Svip3Format` in
`src/Svip3Format.cs`, beside this file — checked note for note against every template
project the editor installs. It is kept out of `VocalPatch.Core` on purpose: one engine's
format is not part of the neutral model, and it plugs into the toolkit with a single
`FormatRegistry.Register` call.

---

## How the schema was recovered

1. **The first bytes are already a protobuf field.** `0a 2f //Users/wey/Desktop/…svip3` —
   field 1, wire type 2, length 47, and the payload is the project path. No container,
   no compression, no NRBF.
2. **The image names its own schema.** As plain ASCII inside the exe:
   `type.googleapis.com/xstudio.proto.SingingTrack`, `xstudio.proto.Note.lyric`,
   `xstudio.proto.Note.pronouncing`, `xstudio.proto.SingingTrack.ai_singer_id`,
   `xstudio.proto.LineParamNode`, `xstudio.proto.Vibrato`, `xstudio.proto.SongTone`,
   `xstudio.proto.AppModel.version`, `xstudio.proto.RecentProject.thumbnail_path`, …
3. **The image carries the whole `.proto`.** A protobuf C++ build registers a serialized
   `FileDescriptorProto`, and that message begins `0a <len> "<file>.proto"`. Scanning the
   92 MB image for that shape found 9 descriptors, four of them X Studio's own:

   | file | package | what it is |
   |---|---|---|
   | `xstudio_model.proto` | `xstudio.proto` | the project model — this is `.svip3` |
   | `xstudio_config.proto` | `xstudio.proto` | `AppConfig`, `recent_project_list` |
   | `fastspeech_singing_v3.proto` | `WL.Singing` | the singing engine's request/response |
   | `fragment_feature_v3.proto` | `xstudio.aiv3` | cached per-fragment F0/power/AP features |

   plus the five `google/protobuf/*.proto` the runtime needs (`Any` among them).
   A build that embeds descriptors is *not* protobuf-lite; if a future version stops
   yielding them, the fallback is to derive field numbers from the wire data.

## The file is an `AppModel`

`xstudio.proto.AppModel`, written as a single message — not wrapped in an `Any`. Tracks
inside it are.

```proto
message AppModel {
  string              project_file_path = 1;
  string              version           = 2;   // file-format version: '3.0.0', '3.0.1', '3.0.3'
  int32               duration          = 3;   // canvas length — see "Units" below
  repeated SongTempo  tempo_list        = 4;   // {int32 pos = 1; int32 tempo = 2;}  tempo = BPM*100
  repeated SongBeat   beat_list         = 5;   // {int32 pos = 1; BeatSize beat_size = 2;}
  repeated Any        track_list        = 6;   // type_url + value, see below
  Master              master            = 7;   // {float volume = 1;}
  string              current_tone      = 8;
  int32               piano_cells       = 9;   // 8 in every file measured
  int32               loop_start        = 10;
  int32               loop_end          = 11;
  bool                is_open_adsorb    = 12;
  int32               params_version    = 13;
  repeated SongTone   tone_list         = 14;  // {int32 pos = 1; string tone = 2;}
  bool                is_triplets       = 15;
  bool                is_loop           = 16;
  bool                is_last_play      = 17;
  repeated SongMark   mark_list         = 18;  // {int32 pos = 1; string mark = 2;}
}
message BeatSize { int32 numerator = 1; int32 denominator = 2; }
```

Each `track_list` entry is a `google.protobuf.Any`:

```proto
message Any { string type_url = 1; bytes value = 2; }
```

with `type_url = "type.googleapis.com/xstudio.proto.SingingTrack"` (or `…AudioTrack`)
and `value` = that message serialized. Measured on a template project: 8 entries, all
`SingingTrack`.

> Note the one case mismatch to keep in mind: the pattern field is `note_List`
> (capital L), everything else in the model is snake_case.

```proto
message SingingTrack {
  float    volume         = 1;    float  pan          = 2;   bool   mute = 3;
  string   name           = 4;    bool   solo         = 5;   string color = 6;
  TrackType type          = 7;    // Audio_Track=1, Singing_Track=2
  repeated SingingPattern pattern_list = 8;
  string   ai_singer_id   = 9;    // a GUID, not a name
  bool     is_reverb_open = 10;   int32 reverb_type = 11;   float reverb_db = 12;
  bool     is_rap_track   = 13;
}
message AudioTrack {                                     // same head as SingingTrack,
  float volume = 1; float pan = 2; bool mute = 3;         // then:
  string name = 4; bool solo = 5; string color = 6;
  TrackType type = 7; repeated AudioPattern pattern_list = 8;
}
message AudioPattern {
  string name = 1; PatternType type = 2;                 // Audio_Pattern = 1
  int32 real_pos = 3; int32 real_dur = 4;
  int32 play_pos = 5; int32 play_dur = 6; bool is_mute = 7;
  string audio_file_path = 8;                            // the backing track
  float rising_falling_tone = 9;
}
message SingingPattern {
  string      name     = 1;  PatternType type = 2;       // Singing_Pattern = 2
  int32       real_pos = 3;  int32 real_dur = 4;
  int32       play_pos = 5;  int32 play_dur = 6;  bool is_mute = 7;
  repeated Note note_List = 8;
  repeated LineParamNode edited_pitch_line      = 9;     // only *edited* curves are
  repeated LineParamNode edited_volume_line     = 10;    // stored in the file; the
  repeated LineParamNode edited_power_line      = 11;    // merge_* lines are rebuilt
  repeated LineParamNode merge_pitch_line       = 12;    // on load
  repeated LineParamNode merge_power_line       = 13;
  repeated LineParamNode edited_spec_trans_coef_line = 14;
  repeated LineParamNode edited_ap_coef_line         = 15;
  repeated LineParamNode edited_energy_value_line    = 16;
  repeated LineParamNode merge_energy_value_line     = 17;
  int32  rap_type       = 18;
  string sing_method_id = 19;
}
message Note {
  int32 start_pos = 1;  int32 width_pos = 2;   int32 key_index = 3;
  string lyric = 4;     string pronouncing = 5;
  int32 consonant_len = 6;  bool has_consonant = 7;  int32 user_consonant_len = 8;
  int32 sp_len = 9;     int32 sil_len = 10;
  NoteLengthValidateTag length_validate_tag = 11;      // NONE=0 TOO_LONG=1 TOO_SHORT=2
  Vibrato vibrato = 12; int32 user_sp_len = 13; int32 tone = 14;
  float left_pitch = 15;  float right_pitch = 16;
}
message LineParamNode { sint32 pos = 1; float value = 2; }   // pos is zig-zag
message Vibrato {
  float frequency = 1; float amplitude = 2; float phase = 3;
  float start = 4; float end = 5;
  float attack_x = 6; float attack_y = 7; float release_x = 8; float release_y = 9;
}
message VibratoStyle     { bool is_anti_phase = 1;
                           repeated LineParamNode amp_line  = 2;
                           repeated LineParamNode freq_line = 3; }
message VibratoPercentInfo { float start_percent = 1; float end_percent = 2; }
enum TrackType            { Track_None=0; Audio_Track=1; Singing_Track=2; }
enum PatternType          { Pattern_None=0; Audio_Pattern=1; Singing_Pattern=2; }
enum NoteLengthValidateTag{ NONE=0; TOO_LONG=1; TOO_SHORT=2; }
```

`Note` is 16 fields with no `optional` keyword in the source — this is proto3, so
absent fields are simply zero/default on the wire and in the read. Write only what
differs from the default and the file stays small; that is why a 264-byte file is a
complete, valid project.

## Units and semantics (measured, not inferred)

| question | answer | how it was measured |
|---|---|---|
| ticks per beat | **480** (960/half, 1920/whole) | `AudioPattern.real_dur` of the backing track ÷ that mp3's exact frame count × 60 ÷ BPM. Seven templates gave **480.00**; the eighth (476.28) has a trimmed clip, `real_pos=1401`, so its `real_dur` is not the file length. |
| `key_index` | MIDI note number | 59/61/62/64/66 with notes B3/C#4/D4/E4/F#4 |
| `tempo` | BPM × 100 | `7250` at 72.5 BPM, `12000` at 120 |
| `lyric` | the character as typed | `风` `开` `一` `页` … |
| `pronouncing` | pinyin, lowercase | `fan kai yi ye huang hun` |
| `AppModel.duration` | **canvas length, not those ticks** | it is 1.05–2.11× the project's own content end across the 8 templates, so it is a padded canvas. Copy it; do not compute it. |
| note placement | not grid-quantised | `start_pos` 5757/5891/6001/6238… at 480/beat is 11.994 / 12.27 / 12.50 beats — the first lands on a bar line, the rest do not sit on any 8- or 16-cell grid |

A real project, read end to end by `read` (8 tracks, 1054 notes):

```
===== 风雪山神-无水印.svip3   5,777,361 bytes
  version           = '3.0.1'
  tempo  pos=None tempo=7250  (72.50 BPM)
  beat   pos=None size=6/8
   [0] type.googleapis.com/xstudio.proto.SingingTrack
        volume=0.7  name='无水印1'  color='#EC8E59'  type=2 (Singing)
        ai_singer_id='c5ed53a0-9d84-4fa7-bef5-e99087b046f0'  is_reverb_open=1  reverb_db=-18.3
         [0] name='未命名'  type=2 (Singing)  real_pos=8640  real_dur=107597  play_dur=107597
              notes=263 curves{name:points}={'edited_pitch_line': 2, 'edited_power_line': 1}
                start_pos=5757  width_pos=118  key_index=59  lyric='风'  pronouncing='fan'  user_consonant_len=113
                start_pos=5891  width_pos=98   key_index=61  lyric='开'  pronouncing='kai'
```

Two things fall out of that: `real_pos = 8640` is 18 beats (480/beat), i.e. a bar line in
6/8, and the curves are stored as two points for a pitch edit the user made — the
editor's own merged curve is not in the file.

## The codec

`src/Svip3Format.cs`, beside this file, is that codec, built on a hand-rolled proto3
reader/writer (`src/ProtoWire.cs`) so nothing needs `Google.Protobuf` or generated code.
`svip3 convert song.svip3 song.svp` and back works, and `.svip3` resolves by extension once
the codec is registered — one `FormatRegistry.Register` call, which is all `cli/` does.

What it carries: tracks, patterns flattened to absolute note positions, notes (position,
length, MIDI pitch, lyric, `pronouncing`), tempo (`BPM × 100`) and time signature. What it
does not, because the neutral model has nowhere to put them: the parameter curves, per-note
vibrato, consonant/SP/silence lengths, per-note tone, track colour and reverb — and the
audio track, so an exported project arrives without its backing track.

Three things measured from real projects and written down here because a codec has to
decide one way or the other:

- **A rest is a note with `lyric = "-"`** — it carries its own pitch (never the previous
  note's) and usually sits in a gap, so it is a rest and not a held syllable. Some projects
  also put `"-"` in `pronouncing`, some leave that field out; both forms exist, so the codec
  writes the lyric and leaves `pronouncing` exactly as it arrived.
- **A pattern can start before tick 0.** One project has `real_pos = -1920`, a clip dragged
  left of the origin. The sign is a 10-byte varint (a 64-bit sign-extended `int32`), and
  flattening patterns into absolute positions is what turns that into ordinary note
  positions.
- **`duration` is a canvas, not a content length** (1.05–2.11× the content end, floor
  115200 for an empty project). A codec cannot derive its unit, so this one reproduces the
  envelope — the floor, otherwise twice the content — instead of inventing a value.

### Verification

`vpatch convert` was run on all eight template projects shipped with the editor (all four
file versions, 3.0.0/3.0.1/3.0.3), and both the input and the output were then read by
`tools/xstudio_schema.py` — a reader that shares no code with the C# one. Result: 21 singing
tracks and 4535 notes, identical in position, length, pitch, lyric and `pronouncing`. The
only differences are the audio tracks (dropped) and the canvas.

## What a codec needs

- **Write**: `AppModel` → `Any{type_url, value}` per track → `SingingPattern` → `Note`
  and `LineParamNode`. All the field numbers above; nothing else.
- **Read**: tolerate unknown fields (proto3 rules — skip by wire type, do not fail), and
  keep `params_version`, `duration` and `piano_cells` verbatim even if their meaning is
  unclear; the editor may use them on load.
- **Curves**: positions are `sint32` (zig-zag), so decode accordingly; `pos` is relative
  to `real_pos`, the same base as `Note.start_pos`.
- **Audio**: `AudioPattern.audio_file_path` is a plain path and `real_dur` is the audio's
  length in ticks at 480/beat, which is how a codec can map an audio file onto the grid.
- **Verify**: `Python → read` then `editor → save` then `read` again; a round trip that
  loses a curve or a `pronouncing` is the failure to watch for.

## The other three files in the image

- **`fastspeech_singing_v3.proto` (`WL.Singing`) — the singing engine, and it is not on this
  machine.** What the image carries is the *client* side: a generated gRPC stub
  (`…Stub@FastspeechSinging@Singing@WL@@`, `StubInterface`, `async_interface`) and X
  Studio's own caller (`xstudio::WRSingingHandler::handle`, `xstudio::WRSingingTask::run`),
  against four methods — `FetchBaseMetadataInput`, `FetchAdditionMetadata`, `FetchWave`,
  `FetchRapMetadata`. gRPC and TLS are statically linked into the exe (conan-built `grpc++`,
  `grpc_secure_channel_create`, c-ares resolver, HTTP/2), and the default target sits in the
  string table: **`xstudio-grpc.music.163.com:443`**, with `https://xstudio-api.music.163.com`
  as the HTTP side. The app names this state its `ServerEnv` — `server_env_grpc_url`,
  `server_env_http_url`, `server_env_preset` in preferences, logged as
  `ServerEnv saved: preset=%1 http=%2 grpc=%3`.
  Synthesis therefore runs on NetEase's servers, against a logged-in account
  (`account_netease_access_token` in `Config\setting.ini`); the client fetches
  `base_meta_url` / `addition_meta_url` / `wave_url` back. There is no engine here to reach:
  the install is 52 files of Qt + ffmpeg + `SoundTouch.dll` + the application, and
  `%APPDATA%\XiaoIce\X Studio3` is 109 MB of template projects and their mp3s.
  (`SharedRead.exe` / `SharedWrite.exe`, 23 KB each, are `SharedMem` named-mapping helpers for
  moving data between instances — not an engine, and there are no named pipes in the image.)
  The message shapes are documented above if they are wanted, and the endpoint being
  configurable means a self-hosted `FastspeechSinging` server *would* be accepted by the
  editor — but not without the model, and no weights exist on disk. **Conclusion: this is not
  an editor-free path to sound.** Do not spend a session on it looking for one.
- **`fragment_feature_v3.proto` (`xstudio.aiv3`)** — `ProtoFragmentFeature`, one blob per
  rendered fragment: fades, real/fragment times, `oriF0`, `powerCoef`, `specTransCoef`,
  `apCoef`, `energyValue`. This is what a rendered/cached fragment looks like.
- **`xstudio_config.proto`** — `AppConfig` with `recent_project_list`, `default_beat_y_value`.
  `RecentProject.duration` is a *string*, and holds a thumbnail path too.

## Where the files actually live

Not `{Documents}\NetEase Cloud Music XStudio`, which is what
`src/VocalPatch.Editor/EditorProfile.cs` assumes for the `xstudio` profile (with `.svip`
and `.mid`). On this machine:

- installer/editor: `D:\NetEase Cloud Music XStudio\NetEase Cloud Music XStudio.exe`
  (native x64 Qt6, imports `WINMM` — so `VocalPatch`'s drop-in applies; its `NAudio.dll`
  is x86 and never loads).
- data root: `%APPDATA%\XiaoIce\X Studio3` — projects (`auto_backup\*.svip3`, written
  every few minutes), `template_projects\`, `db\xstudio_local.db`.
- file extension: **`.svip3`**. The profile's `.svip`/`.mid` and the wrong folders mean the
  watcher, and every hotkey action, see nothing at all — see `README.md` in this folder for
  the corrected row. The `.svip3` codec is `src/Svip3Format.cs`, but nothing in the injected
  runtime loads it, so the hotkeys still act on nothing until the row is patched and the
  codec registered.

## Open questions

- `duration`'s unit. It is not the 480/beat ticks; it is a canvas. Measured range
  1.05–2.11× content end. **Settled as far as the codec needs it**: `Svip3Format` writes the
  observed envelope (the 115200 floor of an empty project, otherwise twice the content)
  rather than deriving a unit it cannot see. Whether the editor ever reads it back is still
  open — worth one edit-and-save in the editor against a converted project.
- `real_dur` vs `play_dur` — equal in most patterns, but they differ on some tracks
  (`real_dur=107597` / `play_dur=107597` on one, `real_dur=21120` on the empty one with
  no `play_pos`). Likely a trimmed-region pair; needs one hand edit to settle.
- `tone`, `rap_type`, `sing_method_id`, `consonant_len` vs `user_consonant_len`,
  `sp_len`/`sil_len`/`user_sp_len` — the `user_*` fields are the ones the user dragged;
  the plain ones are engine output. Confirm by editing one note in the editor and
  diffing the two files.
- Does the editor rewrite `%APPDATA%\XiaoIce\X Studio3\db\xstudio_local.db` in step with
  the project, or is the `.svip3` the only copy? `xstudio_local.db` is SQLite
  (`SQLite format 3\0`). On the machine measured it held no saved works at all
  (`xs_work_info_tb`, 0 rows), so a project saved once from the UI would answer this and
  the "where does the human save" question together.
- **The `-` collision.** X Studio marks a rest with `lyric = "-"`; several other editors —
  Synthesizer V's `.svp` among them — use `-` to mean *hold the previous syllable*. So a rest
  that passes through such a format comes back as a hold: measured on 风雪千千, 14 rest notes
  in one track were absorbed into the notes before them, 164 notes in, 150 out. Within
  `.svip3` (in and out) nothing is lost; this is a property of the format in between.
