using System.Text;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Formats;

/// <summary>Standard MIDI File (.mid) reader/writer. Type 1 on write: a tempo track plus one track per track.</summary>
public sealed class MidiFormat : IProjectFormat
{
    public FormatKind Kind => FormatKind.Midi;
    public string Name => "midi";
    public string DisplayName => "Standard MIDI File (.mid)";
    public IReadOnlyList<string> Extensions => new[] { ".mid", ".midi" };
    public bool CanRead => true;
    public bool CanWrite => true;

    public Song Read(Stream stream, string? path = null)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] data = ms.ToArray();
        return Parse(data, path);
    }

    public void Write(Song song, Stream stream)
    {
        byte[] data = Build(song);
        stream.Write(data, 0, data.Length);
    }

    // ---------------- reading ----------------

    private static Song Parse(byte[] data, string? path)
    {
        int p = 0;
        if (data.Length < 14 || ReadTag(data, ref p) != "MThd")
            throw new FormatException("Not a Standard MIDI File.");
        int headerLen = ReadInt32(data, ref p);
        int format = ReadInt16(data, ref p);
        int ntrks = ReadInt16(data, ref p);
        int division = ReadInt16(data, ref p);
        p += headerLen - 6;

        int resolution = (division & 0x8000) != 0 ? 480 : (division == 0 ? 480 : division);

        var song = new Song { Resolution = resolution, SourcePath = path, SourceFormat = FormatKind.Midi };
        song.Tempos.Clear();
        song.TimeSignatures.Clear();

        for (int t = 0; t < ntrks && p + 8 <= data.Length; t++)
        {
            if (ReadTag(data, ref p) != "MTrk") break;
            int len = ReadInt32(data, ref p);
            int trackEnd = p + len;
            var track = new Track("Track" + (t + 1));
            int absTick = 0;
            int runningStatus = -1;

            var active = new Dictionary<int, (int start, int velocity)>();
            var notes = new List<Note>();

            while (p < trackEnd)
            {
                absTick += ReadVarLen(data, ref p, trackEnd);
                if (p >= trackEnd) break;
                int status = data[p];
                if (status < 0x80)
                {
                    if (runningStatus < 0) break;
                    status = runningStatus;
                }
                else
                {
                    p++;
                    if (status < 0xF0) runningStatus = status;
                }

                int type = status & 0xF0;
                if (status == 0xFF)
                {
                    int metaType = data[p++];
                    int metaLen = ReadVarLen(data, ref p, trackEnd);
                    if (metaType == 0x51 && metaLen >= 3)
                    {
                        int usPerQuarter = (data[p] << 16) | (data[p + 1] << 8) | data[p + 2];
                        double bpm = usPerQuarter > 0 ? 60000000.0 / usPerQuarter : 120;
                        song.Tempos.Add(new Tempo(absTick, bpm));
                    }
                    else if (metaType == 0x58 && metaLen >= 2)
                    {
                        int num = data[p];
                        int den = 1 << data[p + 1];
                        song.TimeSignatures.Add(new TimeSignature(0, Math.Max(1, num), Math.Max(1, den)));
                    }
                    p += metaLen;
                }
                else if (status == 0xF0 || status == 0xF7)
                {
                    int l = ReadVarLen(data, ref p, trackEnd);
                    p += l;
                }
                else if (type == 0x90 || type == 0x80)
                {
                    int noteNum = data[p++];
                    int vel = data[p++];
                    bool on = type == 0x90 && vel > 0;
                    if (on)
                    {
                        active[noteNum] = (absTick, vel);
                    }
                    else if (active.TryGetValue(noteNum, out var st))
                    {
                        active.Remove(noteNum);
                        int dur = absTick - st.start;
                        if (dur > 0)
                            notes.Add(new Note(st.start, dur, noteNum, "a") { Velocity = st.velocity });
                    }
                }
                else if (type == 0xA0 || type == 0xB0 || type == 0xE0)
                {
                    p += 2;
                }
                else if (type == 0xC0 || type == 0xD0)
                {
                    p += 1;
                }
                else
                {
                    break;
                }
            }

            notes.Sort((a, b) => a.Position.CompareTo(b.Position));
            track.Notes.AddRange(notes);
            if (track.Notes.Count > 0) song.Tracks.Add(track);
            p = trackEnd;
        }

        if (song.Tempos.Count == 0) song.Tempos.Add(new Tempo(0, 120));
        if (song.TimeSignatures.Count == 0) song.TimeSignatures.Add(new TimeSignature(0, 4, 4));
        if (song.Tracks.Count == 0) song.Tracks.Add(new Track("Track1"));
        song.Name = Path.GetFileNameWithoutExtension(path ?? "Untitled");
        song.Normalize();
        return song;
    }

    // ---------------- writing ----------------

    private static byte[] Build(Song song)
    {
        song.Normalize();
        int resolution = song.Resolution > 0 ? song.Resolution : 480;

        var streams = new List<byte[]>();

        // Track 0: tempo map + time signature.
        using (var tempoTrack = new MemoryStream())
        {
            foreach (var ts in song.TimeSignatures)
            {
                WriteVarLen(tempoTrack, 0);
                tempoTrack.WriteByte(0xFF);
                tempoTrack.WriteByte(0x58);
                WriteVarLen(tempoTrack, 4);
                tempoTrack.WriteByte((byte)ts.Numerator);
                tempoTrack.WriteByte((byte)Math.Round(Math.Log(ts.Denominator, 2)));
                tempoTrack.WriteByte(24);
                tempoTrack.WriteByte(8);
            }
            foreach (var t in song.Tempos)
            {
                int us = t.Bpm > 0 ? (int)Math.Round(60000000.0 / t.Bpm) : 500000;
                WriteVarLen(tempoTrack, 0);
                tempoTrack.WriteByte(0xFF);
                tempoTrack.WriteByte(0x51);
                WriteVarLen(tempoTrack, 3);
                tempoTrack.WriteByte((byte)((us >> 16) & 0xFF));
                tempoTrack.WriteByte((byte)((us >> 8) & 0xFF));
                tempoTrack.WriteByte((byte)(us & 0xFF));
            }
            WriteVarLen(tempoTrack, 0);
            tempoTrack.WriteByte(0xFF);
            tempoTrack.WriteByte(0x2F);
            WriteVarLen(tempoTrack, 0);
            streams.Add(Chunk("MTrk", tempoTrack.ToArray()));
        }

        foreach (var track in song.Tracks)
        {
            using var ms = new MemoryStream();
            int last = 0;
            var events = new List<(int tick, int order, int note, int vel, bool on)>();
            foreach (var n in track.SortedNotes())
            {
                events.Add((n.Position, 1, n.Pitch, n.Velocity, true));
                events.Add((n.End, 0, n.Pitch, 0, false));
            }
            events.Sort((a, b) =>
            {
                int c = a.tick.CompareTo(b.tick);
                return c != 0 ? c : a.order.CompareTo(b.order);
            });

            foreach (var e in events)
            {
                WriteVarLen(ms, e.tick - last);
                last = e.tick;
                int status = (e.on ? 0x90 : 0x80);
                ms.WriteByte((byte)status);
                ms.WriteByte((byte)(e.note & 0x7F));
                ms.WriteByte((byte)(e.vel & 0x7F));
            }
            WriteVarLen(ms, 0);
            ms.WriteByte(0xFF);
            ms.WriteByte(0x2F);
            WriteVarLen(ms, 0);
            streams.Add(Chunk("MTrk", ms.ToArray()));
        }

        using var outMs = new MemoryStream();
        var header = new List<byte>();
        WriteInt16(header, 1);
        WriteInt16(header, streams.Count);
        WriteInt16(header, resolution & 0x7FFF);
        outMs.Write(Chunk("MThd", header.ToArray()), 0, Chunk("MThd", header.ToArray()).Length);
        foreach (var s in streams) outMs.Write(s, 0, s.Length);
        return outMs.ToArray();
    }

    // ---------------- helpers ----------------

    private static byte[] Chunk(string tag, byte[] body)
    {
        var ms = new MemoryStream();
        foreach (char c in tag) ms.WriteByte((byte)c);
        var len = new List<byte>();
        WriteInt32(len, body.Length);
        ms.Write(len.ToArray(), 0, 4);
        ms.Write(body, 0, body.Length);
        return ms.ToArray();
    }

    private static string ReadTag(byte[] d, ref int p)
    {
        if (p + 4 > d.Length) return string.Empty;
        string s = Encoding.ASCII.GetString(d, p, 4);
        p += 4;
        return s;
    }

    private static int ReadInt32(byte[] d, ref int p)
    {
        int v = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        p += 4;
        return v;
    }

    private static int ReadInt16(byte[] d, ref int p)
    {
        int v = (d[p] << 8) | d[p + 1];
        p += 2;
        return v;
    }

    private static int ReadVarLen(byte[] d, ref int p, int end)
    {
        int value = 0;
        while (p < end)
        {
            byte b = d[p++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    private static void WriteVarLen(Stream s, int value)
    {
        if (value < 0) value = 0;
        var buffer = new byte[4];
        int i = 3;
        buffer[i] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0)
        {
            i--;
            buffer[i] = (byte)((value & 0x7F) | 0x80);
        }
        s.Write(buffer, i, 4 - i);
    }

    private static void WriteInt16(List<byte> b, int v)
    {
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)(v & 0xFF));
    }

    private static void WriteInt32(List<byte> b, int v)
    {
        b.Add((byte)((v >> 24) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)(v & 0xFF));
    }
}
