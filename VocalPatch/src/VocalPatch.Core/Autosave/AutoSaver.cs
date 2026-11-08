using System.Text.Json;
using VocalPatch.Core.Formats;
using VocalPatch.Core.Model;

namespace VocalPatch.Core.Autosave;

/// <summary>One autosave event.</summary>
public sealed class AutosaveEventArgs : EventArgs
{
    public AutosaveEventArgs(string sourcePath, string backupPath, DateTime timestamp)
    {
        SourcePath = sourcePath;
        BackupPath = backupPath;
        Timestamp = timestamp;
    }

    public string SourcePath { get; }
    public string BackupPath { get; }
    public DateTime Timestamp { get; }
}

/// <summary>
/// Interval autosave for any editor that writes a project file to disk. Polls the
/// file's last-write time and, when it has changed, writes a timestamped backup
/// (byte copy, plus an optional neutral-model snapshot). This is the app-agnostic
/// half of the feature sets the original patcher adds per editor.
/// </summary>
public sealed class AutoSaver : IDisposable
{
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private DateTime _lastSeenWrite;
    private string? _lastBackupKey;

    public AutoSaver(string projectPath, TimeSpan interval)
    {
        ProjectPath = Path.GetFullPath(projectPath);
        Interval = interval <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : interval;
        BackupDirectory = Path.Combine(
            Path.GetDirectoryName(ProjectPath) ?? ".",
            ".vocalpatch-autosave");
    }

    public string ProjectPath { get; }
    public TimeSpan Interval { get; }
    public string BackupDirectory { get; set; }

    /// <summary>Also write a neutral-model snapshot (<c>.vp.json</c>) beside each backup.</summary>
    public bool WriteModelSnapshot { get; set; }

    /// <summary>Keep at most this many backups (newest kept). 0 = unlimited.</summary>
    public int MaxBackups { get; set; }

    public bool IsRunning => _timer != null;

    public event EventHandler<AutosaveEventArgs>? Saved;
    public event EventHandler<Exception>? Failed;

    public void Start()
    {
        lock (_gate)
        {
            if (_timer != null) return;
            _lastSeenWrite = SafeLastWrite();
            _timer = new System.Threading.Timer(_ => Tick(), null, Interval, Interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Force a backup now regardless of the interval.</summary>
    public string? BackupNow()
    {
        try
        {
            if (!File.Exists(ProjectPath)) return null;
            Directory.CreateDirectory(BackupDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string name = Path.GetFileNameWithoutExtension(ProjectPath);
            string ext = Path.GetExtension(ProjectPath);
            string backupPath = Path.Combine(BackupDirectory, $"{name}.{stamp}{ext}");

            // Avoid clobbering on rapid repeated backups.
            int suffix = 1;
            while (File.Exists(backupPath))
                backupPath = Path.Combine(BackupDirectory, $"{name}.{stamp}-{suffix++}{ext}");

            File.Copy(ProjectPath, backupPath, overwrite: false);

            if (WriteModelSnapshot)
            {
                try
                {
                    var song = ProjectIo.Load(ProjectPath);
                    string snapshot = Path.ChangeExtension(backupPath, ".vp.json");
                    File.WriteAllText(snapshot, JsonSerializer.Serialize(new
                    {
                        source = ProjectPath,
                        timestamp = DateTime.Now,
                        format = song.SourceFormat.ToString(),
                        tracks = song.Tracks.Count,
                        notes = song.NoteCount,
                        model = song,
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // A snapshot is best-effort; never let it defeat the byte backup.
                }
            }

            PruneOldBackups();
            _lastBackupKey = backupPath;
            Saved?.Invoke(this, new AutosaveEventArgs(ProjectPath, backupPath, DateTime.Now));
            return backupPath;
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
            return null;
        }
    }

    private void Tick()
    {
        try
        {
            var write = SafeLastWrite();
            if (write == _lastSeenWrite && _lastBackupKey != null) return;
            if (!File.Exists(ProjectPath)) return;
            _lastSeenWrite = write;
            BackupNow();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
    }

    private DateTime SafeLastWrite()
    {
        try { return File.Exists(ProjectPath) ? File.GetLastWriteTimeUtc(ProjectPath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private void PruneOldBackups()
    {
        if (MaxBackups <= 0) return;
        var files = new DirectoryInfo(BackupDirectory)
            .GetFiles("*" + Path.GetExtension(ProjectPath))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxBackups);
        foreach (var f in files)
        {
            try { f.Delete(); } catch { /* best effort */ }
        }
    }

    public void Dispose() => Stop();
}
