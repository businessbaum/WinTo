using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiTool.Host.ControlCenter;

/// <summary>What one media session shows at a moment: track, state, timeline, and the cover when it changed.</summary>
/// <param name="Thumbnail">The cover's bytes; <c>null</c> when the track is the one asked about (unchanged) or has none.</param>
/// <param name="Updated">When <paramref name="Position"/> was true, in UTC.</param>
internal sealed record MediaSnapshot(
    string AppId,
    string Title,
    string Artist,
    int Status,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    bool CanSeek,
    TimeSpan Start,
    TimeSpan End,
    TimeSpan Position,
    DateTime Updated,
    byte[]? Thumbnail,
    bool SameTrack,
    IReadOnlyList<string> AppIds)
{
    public const int Playing = 4;

    public string TrackKey => $"{AppId}|{Title}|{Artist}";
}

public enum MediaCommand
{
    TogglePlayPause,
    Next,
    Previous,
    Seek,
}

/// <summary>Windows' media sessions (Global System Media Transport Controls): what Spotify, a browser or a player is playing.</summary>
/// <remarks>
/// <para>
/// The same source as the media flyout next to Windows' volume: every app that reports to the system media controls
/// shows here with title, artist, cover and its own buttons. Hand-written WinRT like the radios; slots and IIDs are
/// from <c>System32\WinMetadata\Windows.Media.winmd</c>.
/// </para>
/// <para>
/// Read fresh on every call, on an MTA thread, and released at once: the control center reads once a second while
/// open. Subscribing to the sessions' change events would need COM callback objects for no gain at that rate.
/// </para>
/// </remarks>
internal static unsafe partial class MediaSessions
{
    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    private static readonly Guid IidManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    private static readonly Guid IidStream = new("0000000c-0000-0000-c000-000000000046");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>The session of <paramref name="preferred"/> if it still exists, otherwise Windows' current one.</summary>
    /// <param name="knownTrack">The track shown now; its cover isn't read again.</param>
    public static MediaSnapshot? Read(string? preferred, string? knownTrack)
    {
        var manager = Manager();
        if (manager == 0) return null;

        nint list = 0, chosen = 0;
        try
        {
            // IGlobalSystemMediaTransportControlsSessionManager: GetCurrentSession 6, GetSessions 7.
            list = WinRt.ReadObject(manager, 7);
            var ids = new List<string>();
            var size = list == 0 ? 0u : (uint)WinRt.ReadInt32(list, 7);
            for (uint i = 0; i < size; i++)
            {
                var session = At(list, i);
                if (session == 0) continue;

                var id = WinRt.ReadString(session, 6);
                ids.Add(id);
                if (chosen == 0 && preferred is not null && id == preferred) chosen = session;
                else WinRt.Release(session);
            }

            if (chosen == 0) chosen = WinRt.ReadObject(manager, 6);
            if (chosen == 0) return null;

            return Describe(chosen, knownTrack, ids);
        }
        finally
        {
            WinRt.Release(chosen);
            WinRt.Release(list);
            WinRt.Release(manager);
        }
    }

    /// <summary>Sends a button press to the session of <paramref name="appId"/>. Returns whether it took it.</summary>
    public static bool Send(string appId, MediaCommand command, long position = 0)
    {
        var manager = Manager();
        if (manager == 0) return false;

        nint list = 0, session = 0;
        try
        {
            list = WinRt.ReadObject(manager, 7);
            var size = list == 0 ? 0u : (uint)WinRt.ReadInt32(list, 7);
            for (uint i = 0; i < size && session == 0; i++)
            {
                var candidate = At(list, i);
                if (candidate == 0) continue;
                if (WinRt.ReadString(candidate, 6) == appId) session = candidate;
                else WinRt.Release(candidate);
            }

            if (session == 0) return false;

            // IGlobalSystemMediaTransportControlsSession: TrySkipNextAsync 16, TrySkipPreviousAsync 17,
            // TryTogglePlayPauseAsync 20, TryChangePlaybackPositionAsync(Int64) 24. Each an IAsyncOperation<Boolean>.
            nint operation;
            var hr = command switch
            {
                MediaCommand.Next => ((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(session, 16))(session, &operation),
                MediaCommand.Previous => ((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(session, 17))(session, &operation),
                MediaCommand.Seek => ((delegate* unmanaged[Stdcall]<nint, long, nint*, int>)WinRt.Slot(session, 24))(session, position, &operation),
                _ => ((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(session, 20))(session, &operation),
            };
            WinRt.Check(hr);

            try
            {
                WinRt.WaitFor(operation, Timeout);
                byte accepted;
                return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)WinRt.Slot(operation, 8))(operation, &accepted) >= 0 && accepted != 0;
            }
            finally
            {
                WinRt.Release(operation);
            }
        }
        finally
        {
            WinRt.Release(session);
            WinRt.Release(list);
            WinRt.Release(manager);
        }
    }

    /// <summary>The session manager; 0 where Windows has none (before Windows 10 1809).</summary>
    private static nint Manager()
    {
        nint statics;
        try
        {
            statics = WinRt.Factory(ManagerClass, IidManagerStatics);
        }
        catch (COMException)
        {
            return 0;
        }

        try
        {
            // RequestAsync, slot 6.
            nint operation;
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(statics, 6))(statics, &operation));
            return WinRt.Results(operation, Timeout);
        }
        finally
        {
            WinRt.Release(statics);
        }
    }

    /// <summary>IVectorView.GetAt, slot 6.</summary>
    private static nint At(nint list, uint index)
    {
        nint item;
        return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)WinRt.Slot(list, 6))(list, index, &item) >= 0 ? item : 0;
    }

    private static MediaSnapshot Describe(nint session, string? knownTrack, List<string> ids)
    {
        var appId = WinRt.ReadString(session, 6);
        nint properties = 0, info = 0, controls = 0, timeline = 0;

        try
        {
            // TryGetMediaPropertiesAsync 7: Title 6, Subtitle 7, AlbumArtist 8, Artist 9, Thumbnail 15.
            nint operation;
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(session, 7))(session, &operation));
            properties = WinRt.Results(operation, Timeout);

            var title = properties == 0 ? "" : WinRt.ReadString(properties, 6);
            var artist = properties == 0 ? "" : WinRt.ReadString(properties, 9);
            if (artist.Length == 0 && properties != 0) artist = WinRt.ReadString(properties, 8);
            if (artist.Length == 0 && properties != 0) artist = WinRt.ReadString(properties, 7);

            // GetPlaybackInfo 9: Controls 6, PlaybackStatus 7. Controls: play 6, pause 7, next 12, previous 13,
            // play/pause toggle 16, playback position 20.
            info = WinRt.ReadObject(session, 9);
            var status = info == 0 ? 0 : WinRt.ReadInt32(info, 7);
            controls = info == 0 ? 0 : WinRt.ReadObject(info, 6);
            var canToggle = controls != 0 && (WinRt.ReadBool(controls, 16) || WinRt.ReadBool(controls, 6) || WinRt.ReadBool(controls, 7));

            // GetTimelineProperties 8: StartTime 6, EndTime 7, Position 10, LastUpdatedTime 11 (100 ns since 1601, UTC).
            timeline = WinRt.ReadObject(session, 8);
            var start = timeline == 0 ? 0 : WinRt.ReadInt64(timeline, 6);
            var end = timeline == 0 ? 0 : WinRt.ReadInt64(timeline, 7);
            var position = timeline == 0 ? 0 : WinRt.ReadInt64(timeline, 10);
            var updated = timeline == 0 ? 0 : WinRt.ReadInt64(timeline, 11);

            var key = $"{appId}|{title}|{artist}";
            var same = key == knownTrack;
            var thumbnail = same || properties == 0 ? null : Thumbnail(WinRt.ReadObject(properties, 15));

            return new MediaSnapshot(appId, title, artist, status,
                controls != 0 && WinRt.ReadBool(controls, 13),
                controls != 0 && WinRt.ReadBool(controls, 12),
                canToggle,
                controls != 0 && WinRt.ReadBool(controls, 20) && end > start,
                TimeSpan.FromTicks(start), TimeSpan.FromTicks(end), TimeSpan.FromTicks(position),
                updated > 0 ? DateTime.FromFileTimeUtc(updated) : DateTime.UtcNow,
                thumbnail, same, ids);
        }
        finally
        {
            WinRt.Release(timeline);
            WinRt.Release(controls);
            WinRt.Release(info);
            WinRt.Release(properties);
        }
    }

    /// <summary>The cover's bytes: IRandomAccessStreamReference.OpenReadAsync (6), then read as an IStream.</summary>
    private static byte[]? Thumbnail(nint reference)
    {
        if (reference == 0) return null;

        nint stream = 0, com = 0;
        try
        {
            nint operation;
            WinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WinRt.Slot(reference, 6))(reference, &operation));
            stream = WinRt.Results(operation, Timeout);
            if (stream == 0) return null;

            var iid = IidStream;
            WinRt.Check(CreateStreamOverRandomAccessStream(stream, &iid, &com));

            // IStream.Read, slot 3; at most 8 MB, covers are a few hundred KB.
            using var bytes = new MemoryStream();
            var buffer = new byte[64 * 1024];
            fixed (byte* b = buffer)
            {
                while (bytes.Length < 8 * 1024 * 1024)
                {
                    uint read;
                    var hr = ((delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)WinRt.Slot(com, 3))(com, b, (uint)buffer.Length, &read);
                    if (hr < 0 || read == 0) break;
                    bytes.Write(buffer, 0, (int)read);
                }
            }

            return bytes.Length > 0 ? bytes.ToArray() : null;
        }
        catch (Exception)
        {
            // Without a cover the card shows a note.
            return null;
        }
        finally
        {
            WinRt.Release(com);
            WinRt.Release(stream);
            WinRt.Release(reference);
        }
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["308046B0AF4A39CB"] = "Firefox",
        ["MSEdge"] = "Edge",
        ["Chrome"] = "Chrome",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["Microsoft.ZuneVideo"] = "Movies & TV",
    };

    private static readonly Dictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A name for the app behind a session id: "Spotify.exe", "MSEdge", "SpotifyAB.SpotifyMusic_…!Spotify".</summary>
    /// <remarks>For an EXE, the description of a running process with that name ("Google Chrome"), else the file name.</remarks>
    public static string AppName(string appId)
    {
        lock (Resolved)
        {
            if (Resolved.TryGetValue(appId, out var known)) return known;
        }

        var name = appId.Contains('!') ? appId[(appId.LastIndexOf('!') + 1)..] : appId;
        string result;

        if (Names.TryGetValue(name, out var mapped))
        {
            result = mapped;
        }
        else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.GetFileNameWithoutExtension(name);
            result = Describe(file) ?? Capitalize(file);
        }
        else
        {
            result = Capitalize(name.Split('.').Last());
        }

        lock (Resolved) Resolved[appId] = result;
        return result;
    }

    private static string? Describe(string process)
    {
        foreach (var running in Process.GetProcessesByName(process))
        {
            using (running)
            {
                try
                {
                    var description = running.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
                }
                catch (Exception)
                {
                    // An elevated or protected process: its file name has to do.
                }
            }
        }

        return null;
    }

    private static string Capitalize(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    [LibraryImport("shcore.dll")]
    private static partial int CreateStreamOverRandomAccessStream(nint randomAccessStream, Guid* iid, nint* stream);
}
