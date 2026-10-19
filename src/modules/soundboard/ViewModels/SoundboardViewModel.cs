using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Modules.Soundboard.Audio;
using MultiTool.Modules.Soundboard.Models;
using MultiTool.Modules.Soundboard.Services;

namespace MultiTool.Modules.Soundboard.ViewModels;

public partial class SoundboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private readonly Action _bindingsChanged;

    private readonly List<SoundItemViewModel> _allSounds = [];
    private readonly Dictionary<Guid, SoundItemViewModel> _soundsById = [];
    private readonly List<FolderNodeViewModel> _allFolders = [];
    private readonly Dictionary<Guid, FolderNodeViewModel> _foldersById = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private object? _selectedNode;

    public SoundboardViewModel(AppServices services, IDialogService dialogs, Action bindingsChanged)
    {
        _services = services;
        _dialogs = dialogs;
        _bindingsChanged = bindingsChanged;

        foreach (var entry in services.Folders)
        {
            var node = new FolderNodeViewModel(entry, this);
            _allFolders.Add(node);
            _foldersById[entry.Id] = node;
        }

        foreach (var entry in services.Sounds)
        {
            var item = new SoundItemViewModel(entry, this);
            _allSounds.Add(item);
            _soundsById[entry.Id] = item;
        }

        ApplyView();
    }

    private readonly List<object> _rootNodes = [];

    /// <summary>
    /// The tree as a flat list, indented via <c>Depth</c>.
    ///
    /// Deliberately no <c>TreeView</c>: its expansion state would need binding to a property folders have and sounds
    /// don't, causing a binding error on every leaf. A flat list avoids that entirely and makes drag &amp; drop
    /// clearer.
    /// </summary>
    public ObservableCollection<object> VisibleItems { get; } = [];

    public IReadOnlyList<SoundItemViewModel> All => _allSounds;

    public bool IsSearching => SearchText.Trim().Length > 0;

    public bool ShowEmptyState => _allSounds.Count == 0 && _allFolders.Count == 0;

    public bool ShowNoResults => IsSearching && VisibleItems.Count == 0;

    public string CountText
    {
        get
        {
            var sounds = _allSounds.Count == 1 ? "1 sound" : $"{_allSounds.Count} sounds";
            if (_allFolders.Count == 0) return sounds;
            var folders = _allFolders.Count == 1 ? "1 folder" : $"{_allFolders.Count} folders";
            return $"{sounds} · {folders}";
        }
    }

    // ================= Playback =================

    public void Toggle(SoundItemViewModel item)
    {
        if (item.IsMissing)
        {
            item.RefreshMissing();
            if (item.IsMissing)
            {
                return;
            }
        }

        // Don't play on the UI thread: a cache miss would decode here and freeze the UI for milliseconds.
        _services.Triggers.Post(() => _services.Engine.Toggle(item.Entry));
    }

    public void SetPlayingState(Guid soundId, bool playing)
    {
        if (_soundsById.TryGetValue(soundId, out var item)) item.IsPlaying = playing;
    }

    [RelayCommand]
    private void StopAll() => _services.Triggers.Post(() => _services.Engine.StopAll());

    public void OnItemVolumeChanged(SoundItemViewModel item)
    {
        _services.Engine.SetSoundVolume(item.Id, (float)item.Volume);
        PersistLibrary();
    }

    // ================= Hotkeys =================

    public void BeginHotkeyCapture(SoundItemViewModel item)
    {
        if (item.IsRecordingHotkey)
        {
            _services.Recorder.Cancel();
            return;
        }

        item.IsRecordingHotkey = true;

        _services.Recorder.Begin(hotkey =>
        {
            item.IsRecordingHotkey = false;
            if (hotkey is null) return;

            // A hotkey belongs to one sound only. Reassigning takes it from the previous owner; more
            // predictable than an error that leaves the user guessing where it's bound.
            var previous = _allSounds.FirstOrDefault(o => o != item && hotkey.Equals(o.Entry.Hotkey));
            previous?.SetHotkey(null);

            item.SetHotkey(hotkey);
            PersistLibrary();
            RefreshHotkeyBindings();
        });
    }

    public void RefreshHotkeyBindings() => _bindingsChanged();

    // ================= Folders =================

    [RelayCommand]
    private void AddRootFolder() => AddFolder(null);

    public void AddFolder(FolderNodeViewModel? parent)
    {
        var entry = new FolderEntry
        {
            Name = UniqueFolderName(parent?.Id),
            ParentId = parent?.Id,
            Order = NextFolderOrder(parent?.Id),
        };

        var node = new FolderNodeViewModel(entry, this);
        _services.Folders.Add(entry);
        _allFolders.Add(node);
        _foldersById[entry.Id] = node;

        if (parent is not null) parent.IsExpanded = true;

        ApplyView();
        PersistLibrary();
        SelectedNode = node;
    }

    /// <summary>Deletes the folder but none of its content: sounds and subfolders move up a level. A click must not destroy a library.</summary>
    public void RemoveFolder(FolderNodeViewModel folder)
    {
        var parentId = folder.Entry.ParentId;

        foreach (var sound in _allSounds.Where(s => s.Entry.FolderId == folder.Id))
            sound.Entry.FolderId = parentId;

        foreach (var child in _allFolders.Where(f => f.Entry.ParentId == folder.Id))
            child.Entry.ParentId = parentId;

        _allFolders.Remove(folder);
        _foldersById.Remove(folder.Id);
        _services.Folders.RemoveAll(f => f.Id == folder.Id);

        ApplyView();
        PersistLibrary();
    }

    // ================= Moving =================

    public void MoveSound(SoundItemViewModel sound, Guid? targetFolderId)
    {
        if (sound.Entry.FolderId == targetFolderId) return;

        sound.Entry.FolderId = targetFolderId;
        sound.Entry.Order = NextSoundOrder(targetFolderId);

        // Expand the target folder so the moved sound stays visible.
        if (targetFolderId is { } id && _foldersById.TryGetValue(id, out var folder))
            folder.IsExpanded = true;

        ApplyView();
        PersistLibrary();
    }

    public bool CanMoveFolder(FolderNodeViewModel folder, Guid? targetParentId)
    {
        if (targetParentId == folder.Id) return false;
        if (folder.Entry.ParentId == targetParentId) return false;

        // A folder must not move into its own descendant; the tree would loop and the branch
        // become unreachable.
        var current = targetParentId;
        while (current is { } id && _foldersById.TryGetValue(id, out var node))
        {
            if (node.Id == folder.Id) return false;
            current = node.Entry.ParentId;
        }

        return true;
    }

    public void MoveFolder(FolderNodeViewModel folder, Guid? targetParentId)
    {
        if (!CanMoveFolder(folder, targetParentId)) return;

        folder.Entry.ParentId = targetParentId;
        folder.Entry.Order = NextFolderOrder(targetParentId);

        // Expand the target folder so the moved folder stays visible.
        if (targetParentId is { } id && _foldersById.TryGetValue(id, out var parent))
            parent.IsExpanded = true;

        ApplyView();
        PersistLibrary();
    }

    // ================= Library =================

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var files = await _dialogs.PickAudioFilesAsync();
        await ImportAsync(files);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var folders = await _dialogs.PickFoldersAsync();
        await ImportAsync(folders, mirrorStructure: true);
    }

    /// <summary>Import from dialog or drag &amp; drop.</summary>
    /// <param name="mirrorStructure">For selected folders, mirror their structure as folders in the library instead of one pile.</param>
    public async Task ImportAsync(IEnumerable<string> paths, bool mirrorStructure = true)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;

        IsBusy = true;

        try
        {
            var target = SelectedNode as FolderNodeViewModel;
            var expanded = await Task.Run(() => ExpandForImport(list, mirrorStructure));

            var existing = _allSounds
                .Select(i => i.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = new List<SoundItemViewModel>();
            var createdFolders = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in expanded)
            {
                var full = Path.GetFullPath(item.FilePath);
                if (!existing.Add(full)) continue;

                var folderId = item.RelativeFolder is null
                    ? target?.Id
                    : EnsureFolderPath(item.RelativeFolder, target?.Id, createdFolders);

                var entry = SoundLibrary.CreateEntry(full);
                entry.FolderId = folderId;
                entry.Order = NextSoundOrder(folderId);

                var vm = new SoundItemViewModel(entry, this);
                _services.Sounds.Add(entry);
                _allSounds.Add(vm);
                _soundsById[entry.Id] = vm;
                added.Add(vm);
            }

            ApplyView();
            PersistLibrary();


            if (added.Count > 0) _ = WarmUpAsync(added);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed record ImportItem(string FilePath, string? RelativeFolder);

    /// <summary>Resolves files and folders while remembering the folder structure.</summary>
    private static List<ImportItem> ExpandForImport(IReadOnlyList<string> paths, bool mirrorStructure)
    {
        var result = new List<ImportItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                    var rootName = Path.GetFileName(root);

                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!AudioFormat.IsSupported(file)) continue;
                        if (!seen.Add(Path.GetFullPath(file))) continue;

                        string? relative = null;
                        if (mirrorStructure)
                        {
                            var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? root;
                            var suffix = directory.Length > root.Length
                                ? directory[(root.Length + 1)..]
                                : string.Empty;
                            relative = suffix.Length == 0 ? rootName : Path.Combine(rootName, suffix);
                        }

                        result.Add(new ImportItem(file, relative));
                    }
                }
                else if (File.Exists(path) && AudioFormat.IsSupported(path) && seen.Add(Path.GetFullPath(path)))
                {
                    result.Add(new ImportItem(path, null));
                }
            }
            catch (Exception)
            {
                // Skip unreadable folders (permissions, broken links).
            }
        }

        result.Sort((a, b) => string.Compare(
            Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath), StringComparison.CurrentCultureIgnoreCase));

        return result;
    }

    /// <summary>Creates the folders of a relative path where missing.</summary>
    private Guid EnsureFolderPath(string relative, Guid? parentId, Dictionary<string, Guid> cache)
    {
        var current = parentId;
        var key = string.Empty;

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;

            key = key.Length == 0 ? part : key + "/" + part;
            if (cache.TryGetValue(key, out var known))
            {
                current = known;
                continue;
            }

            var existing = _allFolders.FirstOrDefault(f =>
                f.Entry.ParentId == current &&
                string.Equals(f.Name, part, StringComparison.CurrentCultureIgnoreCase));

            if (existing is not null)
            {
                current = existing.Id;
                cache[key] = existing.Id;
                continue;
            }

            var entry = new FolderEntry
            {
                Name = part,
                ParentId = current,
                Order = NextFolderOrder(current),
            };

            var node = new FolderNodeViewModel(entry, this);
            _services.Folders.Add(entry);
            _allFolders.Add(node);
            _foldersById[entry.Id] = node;

            cache[key] = entry.Id;
            current = entry.Id;
        }

        return current ?? Guid.Empty;
    }

    public void Remove(SoundItemViewModel item)
    {
        _services.Engine.Stop(item.Id);
        _services.Engine.Cache.Remove(item.FilePath);

        _allSounds.Remove(item);
        _soundsById.Remove(item.Id);
        _services.Sounds.RemoveAll(s => s.Id == item.Id);

        ApplyView();
        PersistLibrary();
        RefreshHotkeyBindings();
    }

    public void ShowInFolder(SoundItemViewModel item) => _dialogs.RevealInExplorer(item.FilePath);

    public void PersistLibrary() => _services.SaveLibrarySoon();

    // ================= Building the view =================

    partial void OnSearchTextChanged(string value) => ApplyView();

    /// <summary>Called after every change to structure, search or expansion state.</summary>
    public void ApplyView()
    {
        VisibleItems.Clear();

        if (IsSearching)
        {
            var query = SearchText.Trim();

            // In search mode finding counts, not structure: a flat hit list saves expanding folders.
            foreach (var item in _allSounds
                         .Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                     i.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                         .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                item.Depth = 0;
                VisibleItems.Add(item);
            }
        }
        else
        {
            BuildTree();
            Flatten(_rootNodes, 0);
        }

        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(CountText));
    }

    private void BuildTree()
    {
        var folders = _allFolders
            .OrderBy(f => f.Entry.Order)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var folder in folders) folder.Children.Clear();
        _rootNodes.Clear();

        // Folders first, so they're on top at every level.
        foreach (var folder in folders)
        {
            if (folder.Entry.ParentId is { } parentId && _foldersById.TryGetValue(parentId, out var parent))
                parent.Children.Add(folder);
            else
                _rootNodes.Add(folder);
        }

        foreach (var sound in _allSounds
                     .OrderBy(s => s.Entry.Order)
                     .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (sound.Entry.FolderId is { } folderId && _foldersById.TryGetValue(folderId, out var folder))
                folder.Children.Add(sound);
            else
                _rootNodes.Add(sound);
        }

        foreach (var folder in folders) folder.NotifyChildrenChanged();
    }

    /// <summary>Flattens the tree into the visible list; collapsed folders end here.</summary>
    private void Flatten(IEnumerable<object> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case FolderNodeViewModel folder:
                    folder.Depth = depth;
                    VisibleItems.Add(folder);
                    if (folder.IsExpanded) Flatten(folder.Children, depth + 1);
                    break;

                case SoundItemViewModel sound:
                    sound.Depth = depth;
                    VisibleItems.Add(sound);
                    break;
            }
        }
    }

    private int NextSoundOrder(Guid? folderId)
    {
        var siblings = _allSounds.Where(s => s.Entry.FolderId == folderId).ToList();
        return siblings.Count == 0 ? 0 : siblings.Max(s => s.Entry.Order) + 1;
    }

    private int NextFolderOrder(Guid? parentId)
    {
        var siblings = _allFolders.Where(f => f.Entry.ParentId == parentId).ToList();
        return siblings.Count == 0 ? 0 : siblings.Max(f => f.Entry.Order) + 1;
    }

    private string UniqueFolderName(Guid? parentId)
    {
        var taken = _allFolders
            .Where(f => f.Entry.ParentId == parentId)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        if (!taken.Contains("New folder")) return "New folder";

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"New folder {i}";
            if (!taken.Contains(candidate)) return candidate;
        }

        return "New folder";
    }

    // ================= Background work =================

    /// <summary>
    /// Determines lengths and prewarms the PCM cache so the first key press doesn't wait for decoding. Runs in
    /// the background and stops once the cache budget is mostly used.
    /// </summary>
    public Task WarmUpAsync(IReadOnlyList<SoundItemViewModel>? subset = null)
    {
        var items = (subset ?? _allSounds).ToList();
        if (items.Count == 0) return Task.CompletedTask;

        var token = _lifetime.Token;

        return Task.Run(() =>
        {
            var cache = _services.Engine.Cache;

            foreach (var item in items)
            {
                // The module is closed: stop decoding. Until 0.9.0 this ran to completion after closing,
                // CPU time and dozens of MB for the cache of an audio graph that no longer existed.
                if (token.IsCancellationRequested) return;

                var path = item.FilePath;

                if (!File.Exists(path))
                {
                    Dispatcher.UIThread.Post(() => item.IsMissing = true);
                    continue;
                }

                if (item.Entry.DurationSeconds <= 0 && AudioFormat.TryGetDuration(path) is { } duration)
                {
                    Dispatcher.UIThread.Post(() => item.SetDuration(duration));
                }

                if (cache.UsedBytes < cache.BudgetBytes * 0.8)
                {
                    cache.GetOrLoad(path);
                }
            }

            Dispatcher.UIThread.Post(PersistLibrary);
        }, token);
    }

    /// <summary>Ends with the module. See <see cref="WarmUpAsync"/>.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Stops running background work. From module disposal.</summary>
    public void CancelWork() => _lifetime.Cancel();
}
