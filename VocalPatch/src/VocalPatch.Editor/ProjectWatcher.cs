namespace VocalPatch.Editor;

/// <summary>
/// Watches an editor's project folders and snapshots the files it touches. This is
/// the app-agnostic autosave: it needs no knowledge of the editor's internals, only
/// where it writes projects and how often to keep a copy.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, DateTime> _lastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly HashSet<string> _extensions;

    public ProjectWatcher(IEnumerable<string> directories, IEnumerable<string> extensions, TimeSpan minInterval)
    {
        MinInterval = minInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : minInterval;
        _extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch
            {
                // A missing or inaccessible folder just means nothing to watch there.
            }
        }
    }

    public TimeSpan MinInterval { get; }

    public event EventHandler<string>? SnapshotTaken;

    public IReadOnlyList<string> WatchedDirectories => _watchers.Select(w => w.Path).ToList();

    private void OnRenamed(object sender, RenamedEventArgs e) => Handle(e.FullPath);

    private void OnChanged(object sender, FileSystemEventArgs e) => Handle(e.FullPath);

    private void Handle(string path)
    {
        if (!Matches(path)) return;
        lock (_gate)
        {
            if (_lastSnapshot.TryGetValue(path, out var last) && DateTime.UtcNow - last < MinInterval)
                return;
            _lastSnapshot[path] = DateTime.UtcNow;
        }

        var snapshot = EditorActions.Snapshot(path);
        if (snapshot != null)
            SnapshotTaken?.Invoke(this, snapshot);
    }

    private bool Matches(string path)
    {
        string ext = Path.GetExtension(path);
        // Two checks keep the watcher off files that are not the user's project: its own
        // snapshots (they carry the project's extension and would nest forever) and the
        // editor's own autosave/recovery files (they would be copied every cycle).
        return _extensions.Contains(ext)
            && !EditorActions.IsGeneratedPath(path)
            && !EditorActions.IsEditorScratchPath(path)
            && File.Exists(path);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }
}
