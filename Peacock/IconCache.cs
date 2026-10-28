namespace TaskbarIconOverlay.Peacock;

internal enum IconKind
{
    /// <summary>The application's own icon recoloured; set on the window with WM_SETICON.</summary>
    Recolored,
    /// <summary>A small coloured badge for ITaskbarList3.SetOverlayIcon.</summary>
    Badge,
}

/// <summary>Identifies a generated icon: same source, size, colour and design → same HICON.</summary>
internal readonly record struct IconKey(string Source, int Size, int Rgb, IconKind Kind);

/// <summary>
/// Reference-counted cache of native icon handles. Handles given to other windows via
/// WM_SETICON must stay alive while in use, so an entry is only destroyed once nothing
/// references it and the number of idle entries exceeds <c>idleCapacity</c>.
/// </summary>
internal sealed class IconCache : IDisposable
{
    sealed class Entry
    {
        public IntPtr Handle;
        public int RefCount;
        public long LastUsed;
    }

    readonly Func<IconKey, IntPtr> _create;
    readonly Action<IntPtr> _destroy;
    readonly int _idleCapacity;
    readonly Dictionary<IconKey, Entry> _entries = new();
    long _clock;

    public IconCache(Func<IconKey, IntPtr> create, Action<IntPtr> destroy, int idleCapacity = 16)
    {
        _create = create;
        _destroy = destroy;
        _idleCapacity = idleCapacity;
    }

    public int Count => _entries.Count;
    public int CreatedCount { get; private set; }

    /// <summary>Returns a handle for the key, creating it once; IntPtr.Zero if creation failed.</summary>
    public IntPtr Acquire(IconKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            IntPtr handle = _create(key);
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            CreatedCount++;
            entry = new Entry { Handle = handle };
            _entries[key] = entry;
        }
        entry.RefCount++;
        entry.LastUsed = ++_clock;
        return entry.Handle;
    }

    public void Release(IconKey key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        if (entry.RefCount > 0) entry.RefCount--;
        entry.LastUsed = ++_clock;
        Trim();
    }

    void Trim()
    {
        var idle = _entries.Where(e => e.Value.RefCount == 0).OrderBy(e => e.Value.LastUsed).ToList();
        for (int i = 0; i < idle.Count - _idleCapacity; i++)
        {
            _destroy(idle[i].Value.Handle);
            _entries.Remove(idle[i].Key);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) _destroy(entry.Handle);
        _entries.Clear();
    }
}
