"""X Studio's project format: recover the schema from the editor, read a project.

    python tools/xstudio_schema.py schema "D:\\NetEase Cloud Music XStudio\\NetEase Cloud Music XStudio.exe"
    python tools/xstudio_schema.py read   song.svip3 [more.svip3 ...]
    python tools/xstudio_schema.py wire   anything.bin      # raw field dump, any protobuf

`.svip3` is Protocol Buffers. There is no .proto to be found on disk, but a protobuf
C++ build registers a serialized FileDescriptorProto in its own image, and that message
begins `0a <len> "<file>.proto"` - so the schema ships inside the editor and can be
walked out of it. `schema` does that; `read` decodes a project with the field names
this recovered, and is what proves the schema is right.

Stdlib only, on purpose: it is a probe, not a dependency.
"""
from __future__ import annotations

import re
import struct
import sys
from pathlib import Path

# ---------------------------------------------------------------- wire format

TYPE = {
    1: "double", 2: "float", 3: "int64", 4: "uint64", 5: "int32", 6: "fixed64",
    7: "fixed32", 8: "bool", 9: "string", 10: "group", 11: "message", 12: "bytes",
    13: "uint32", 14: "enum", 15: "sfixed32", 16: "sfixed64", 17: "sint32", 18: "sint64",
}
LABEL = {1: "optional", 2: "required", 3: "repeated"}


def varint(b: bytes, i: int) -> tuple[int, int]:
    v = 0
    shift = 0
    while True:
        if i >= len(b):
            raise ValueError("varint ran off the end")
        c = b[i]
        v |= (c & 0x7F) << shift
        i += 1
        if not c & 0x80:
            return v, i
        shift += 7
        if shift > 63:
            raise ValueError("varint too long")


def fields(b: bytes, start: int = 0, end: int | None = None):
    """Lenient walk: yields (field, wiretype, value) until it stops making sense."""
    i = start
    end = len(b) if end is None else end
    while i < end:
        try:
            tag, j = varint(b, i)
        except ValueError:
            return
        f, wt = tag >> 3, tag & 7
        if f == 0 or wt not in (0, 1, 2, 5):
            return
        try:
            if wt == 0:
                v, i = varint(b, j)
            elif wt == 1:
                if j + 8 > end:
                    return
                v = b[j : j + 8]
                i = j + 8
            elif wt == 5:
                if j + 4 > end:
                    return
                v = b[j : j + 4]
                i = j + 4
            else:
                ln, k = varint(b, j)
                if k + ln > end or ln > 8_000_000:
                    return
                v = b[k : k + ln]
                i = k + ln
        except ValueError:
            return
        yield f, wt, v


def text(v) -> str:
    if isinstance(v, (bytes, bytearray)):
        try:
            return v.decode("utf-8")
        except UnicodeDecodeError:
            return ""
    return str(v)


def f32(v) -> float:
    return struct.unpack("<f", v)[0] if isinstance(v, (bytes, bytearray)) and len(v) == 4 else float("nan")


def f64(v) -> float:
    return struct.unpack("<d", v)[0] if isinstance(v, (bytes, bytearray)) and len(v) == 8 else float("nan")


def one(msg: bytes, field: int, want: str = "raw"):
    for f, wt, v in fields(msg):
        if f != field:
            continue
        return text(v) if want == "str" else v
    return None


def many(msg: bytes, field: int) -> list[bytes]:
    return [v for f, wt, v in fields(msg) if f == field]


# --------------------------------------------------- recovery of the descriptor

def _field_descriptor(v: bytes) -> dict:
    out: dict = {}
    for f, wt, val in fields(v):
        if wt != 2:
            if f == 3:
                out["number"] = val
            elif f == 4:
                out["label"] = LABEL.get(val, str(val))
            elif f == 5:
                out["type"] = TYPE.get(val, str(val))
            continue
        if f == 1:
            out["name"] = text(val)
        elif f == 6:
            out["type_name"] = text(val)
    return out


def _enum(v: bytes) -> dict:
    en = {"name": "", "values": []}
    for f, wt, val in fields(v):
        if wt != 2:
            continue
        if f == 1:
            en["name"] = text(val)
        elif f == 2:
            name, num = "", None
            for vf, vwt, vv in fields(val):
                if vwt == 0 and vf == 2:
                    num = vv
                elif vwt == 2 and vf == 1:
                    name = text(vv)
            en["values"].append((name, num))
    return en


def _message(v: bytes) -> dict:
    out: dict = {"fields": [], "nested": [], "enums": []}
    for f, wt, val in fields(v):
        if wt != 2:
            continue
        if f == 1:
            out["name"] = text(val)
        elif f == 2:
            fd = _field_descriptor(val)
            if fd.get("name"):
                out["fields"].append(fd)
        elif f == 3:
            n = _message(val)
            if n.get("name"):
                out["nested"].append(n)
        elif f == 4:
            e = _enum(val)
            if e["name"]:
                out["enums"].append(e)
    return out


def _file_descriptor(b: bytes, start: int) -> dict:
    fd: dict = {"name": "", "package": "", "messages": [], "enums": []}
    for f, wt, val in fields(b, start):
        if wt != 2:
            continue
        if f == 1:
            fd["name"] = text(val)
        elif f == 2:
            fd["package"] = text(val)
        elif f == 4:
            m = _message(val)
            if m.get("name"):
                fd["messages"].append(m)
        elif f == 5:
            e = _enum(val)
            if e["name"]:
                fd["enums"].append(e)
    return fd


def cmd_schema(image: Path) -> None:
    b = image.read_bytes()
    print(f"{image.name}: {len(b):,} bytes")
    hits = [m.start() for m in re.finditer(rb"\x0a[\x05-\x7f][\w./-]*\.proto", b)]
    print(f"{len(hits)} candidate FileDescriptorProto start(s)\n")
    seen: set[str] = set()

    def show(m: dict, indent: int = 1) -> None:
        pad = "   " * indent
        print(f"{pad}message {m['name']}   ({len(m['fields'])} field(s))")
        for f in m["fields"]:
            base = f.get("type", "?")
            if f.get("type_name"):
                base = f["type_name"].rsplit(".", 1)[-1]
            print(f"{pad}   {f.get('number', '?'):>3}  {f.get('label', 'optional'):<8} "
                  f"{base:<22} {f.get('name', '')}")
        for e in m.get("enums", []):
            print(f"{pad}   enum {e['name']}: " + ", ".join(f"{n}={v}" for n, v in e["values"]))
        for n in m["nested"]:
            show(n, indent + 1)

    for off in hits:
        fd = _file_descriptor(b, off)
        if not fd["name"] or fd["name"] in seen or not fd["messages"]:
            continue
        seen.add(fd["name"])
        print(f"===== {fd['name']}   package={fd['package'] or '-'}")
        for m in fd["messages"]:
            show(m)
        for e in fd["enums"]:
            print(f"   enum {e['name']}: " + ", ".join(f"{n}={v}" for n, v in e["values"]))
        print()
    if not seen:
        print("no FileDescriptorProto recovered - this build may be protobuf-lite, in "
              "which case field numbers have to come from the wire data instead")


# ------------------------------------------------------------------- the reader

# Field names as recovered by `schema` from xstudio_model.proto (package xstudio.proto).
APPMODEL = {
    1: "project_file_path", 2: "version", 3: "duration", 4: "tempo_list", 5: "beat_list",
    6: "track_list(Any)", 7: "master", 8: "current_tone", 9: "piano_cells",
    10: "loop_start", 11: "loop_end", 12: "is_open_adsorb", 13: "params_version",
    14: "tone_list", 15: "is_triplets", 16: "is_loop", 17: "is_last_play", 18: "mark_list",
}
TRACK = {
    1: "volume", 2: "pan", 3: "mute", 4: "name", 5: "solo", 6: "color", 7: "type",
    8: "pattern_list", 9: "ai_singer_id", 10: "is_reverb_open", 11: "reverb_type",
    12: "reverb_db", 13: "is_rap_track",
}
PATTERN = {
    1: "name", 2: "type", 3: "real_pos", 4: "real_dur", 5: "play_pos", 6: "play_dur",
    7: "is_mute", 8: "note_List",
    9: "edited_pitch_line", 10: "edited_volume_line", 11: "edited_power_line",
    12: "merge_pitch_line", 13: "merge_power_line", 14: "edited_spec_trans_coef_line",
    15: "edited_ap_coef_line", 16: "edited_energy_value_line", 17: "merge_energy_value_line",
    18: "rap_type", 19: "sing_method_id",
}
NOTE = {
    1: "start_pos", 2: "width_pos", 3: "key_index", 4: "lyric", 5: "pronouncing",
    6: "consonant_len", 7: "has_consonant", 8: "user_consonant_len", 9: "sp_len",
    10: "sil_len", 11: "length_validate_tag", 12: "vibrato", 13: "user_sp_len",
    14: "tone", 15: "left_pitch", 16: "right_pitch",
}
STRINGS = {
    "APPMODEL": {1, 2, 8}, "TRACK": {4, 6, 9}, "PATTERN": {1, 19}, "NOTE": {4, 5},
}
FLOATS = {"APPMODEL": set(), "TRACK": {1, 2, 12}, "PATTERN": set(), "NOTE": {15, 16}}
TRACK_TYPE = {0: "None", 1: "Audio", 2: "Singing"}
PATTERN_TYPE = {0: "None", 1: "Audio", 2: "Singing"}


def render(msg: bytes, table: dict, kind: str, skip: set[int] = frozenset(),
           hide: set[int] = frozenset()) -> list[str]:
    out: list[str] = []
    for f, wt, v in fields(msg):
        name = table.get(f, f"field_{f}")
        if f in hide:
            continue
        if f in skip:
            out.append(f"{name}=" + (f"<{len(v)} bytes>" if wt == 2 else str(v)))
            continue
        if f in STRINGS[kind]:
            out.append(f"{name}={text(v)!r}")
        elif f in FLOATS[kind]:
            out.append(f"{name}={f32(v):.4g}")
        elif wt == 0:
            label = v
            if name == "type":
                label = f"{v} ({TRACK_TYPE.get(v) or PATTERN_TYPE.get(v)})"
            out.append(f"{name}={label}")
        elif wt == 2:
            out.append(f"{name}=<{len(v)} bytes>")
        else:
            out.append(f"{name}={v!r}")
    return out


def cmd_read(path: Path) -> None:
    b = path.read_bytes()
    print(f"===== {path.name}   {len(b):,} bytes")
    print(f"  project_file_path = {one(b, 1, 'str')!r}")
    print(f"  version           = {one(b, 2, 'str')!r}")
    duration = one(b, 3)
    print(f"  duration          = {duration}  (ticks; 480/beat -> {duration / 480:.1f} beats)"
          if isinstance(duration, int) else f"  duration          = {duration}")
    print(f"  piano_cells       = {one(b, 9)}   is_triplets={one(b, 15)}   is_loop={one(b, 16)}")
    for t in many(b, 4):
        tempo = one(t, 2)
        print(f"  tempo  pos={one(t, 1)} tempo={tempo}  "
              f"({tempo / 100:.2f} BPM)" if isinstance(tempo, int) else f"  tempo {t.hex()}")
    for s in many(b, 5):
        bs = one(s, 2)
        print(f"  beat   pos={one(s, 1)} size={one(bs, 1)}/{one(bs, 2)}")

    tracks = many(b, 6)
    print(f"  track_list: {len(tracks)} track(s)")
    for ti, any_msg in enumerate(tracks):
        url = one(any_msg, 1, "str")
        value = one(any_msg, 2) or b""
        print(f"   [{ti}] {url}")
        print("        " + "  ".join(render(value, TRACK, "TRACK", skip={8})))
        patterns = many(value, 8)
        for pi, pat in enumerate(patterns[:8]):
            notes = many(pat, 8)
            curves = {PATTERN.get(f, f): len(list(fields(v))) for f, wt, v in fields(pat)
                      if 9 <= f <= 17}
            print(f"         [{pi}] " + "  ".join(
                render(pat, PATTERN, "PATTERN", hide=set(range(8, 20)))))
            print(f"              notes={len(notes)} curves{{name:points}}={curves}")
            for n in notes[:6]:
                print("                " + "  ".join(render(n, NOTE, "NOTE", skip={12})))
            if notes:
                print(f"              first {min(8, len(notes))} of {len(notes)}: "
                      f"start={[one(n, 1) for n in notes[:8]]} "
                      f"width={[one(n, 2) for n in notes[:8]]} "
                      f"key={[one(n, 3) for n in notes[:8]]}")
    print()


def cmd_wire(path: Path, budget: int = 3000, indent: int = 0) -> None:
    b = path.read_bytes()
    print(f"{path.name}: {len(b):,} bytes")

    def walk(msg: bytes, depth: int) -> None:
        nonlocal budget
        for f, wt, v in fields(msg):
            if budget <= 0:
                print("   ... budget exhausted")
                return
            budget -= 1
            pad = "   " * depth
            if wt == 0:
                print(f"{pad}{f} (varint) = {v}")
            elif wt in (1, 5):
                size = 8 if wt == 1 else 4
                print(f"{pad}{f} ({'i64' if wt == 1 else 'i32'}) = "
                      f"{int.from_bytes(v, 'little')}  f={f64(v) if size == 8 else f32(v):.6g}")
            else:
                if v and all(32 <= c < 127 or c in (9, 10, 13) for c in v[:64]) and len(v) < 200:
                    print(f"{pad}{f} (len {len(v)}) = {text(v)!r}")
                else:
                    print(f"{pad}{f} (len {len(v)}) = bytes {v[:16].hex(' ')}"
                          f"{' ...' if len(v) > 16 else ''}")
                    if 0 < len(v) < 4096:
                        walk(v, depth + 1)

    walk(b, indent)
    print()


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        raise SystemExit(2)
    what = sys.argv[1]
    for arg in sys.argv[2:]:
        p = Path(arg)
        if what == "schema":
            cmd_schema(p)
        elif what == "read":
            cmd_read(p)
        elif what == "wire":
            cmd_wire(p)
        else:
            print(__doc__)
            raise SystemExit(2)
