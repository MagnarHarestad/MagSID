using System.Text.Json;

namespace MagSid.Editor.App;

/// <summary>
/// Small, disk-backed store for editor preferences that should outlive a single dialog and a
/// single run - the instrument editor's "show hex values GoatTracker-style" toggle, and the
/// author name typed into the .sid export, which is the same on every export and is not worth
/// retyping.
/// Lives at %AppData%\MagSid.Editor\settings.json; a missing or unreadable file is silently
/// treated as defaults rather than surfaced, since a lost UI preference is not worth interrupting
/// startup over.
/// </summary>
/// <summary>
/// The file dialogs that each remember the folder they were last used in - each its own, so
/// opening a demo song and then saving an instrument does not offer the song's folder for the
/// instrument. The names are the keys in settings.json's "Folders".
/// </summary>
public enum DialogFolder
{
    /// <summary>Menu → Open: a song.</summary>
    Open,

    /// <summary>Menu → Save As: a song.</summary>
    SaveAs,

    ImportMidi,

    ImportSid,

    /// <summary>Export to .sid.</summary>
    ExportSid,

    /// <summary>Load and Play .sid - the player's own.</summary>
    PlaySid,

    LoadInstrument,

    SaveInstrument,

    /// <summary>An instrument picked for a voice in Import MIDI.</summary>
    MidiInstrument,

    /// <summary>Audio written from the SID player.</summary>
    ExportAudio,

    /// <summary>A .wav opened in Digi to SID-osc.</summary>
    DigiWav,
}

public static class EditorSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagSid.Editor", "settings.json");

    /// <summary>Where instruments of the user's own replace the GM sound bank's: Piano.json,
    /// Bass.json and so on (see MagSid.Core.Midi.GmBank).</summary>
    public static string BankDirectory => Path.Combine(Path.GetDirectoryName(FilePath)!, "Bank");

    private static bool _showHighHex;
    private static bool _swapSpacebar;
    private static bool _compactLoops;
    private static bool _chip6581;
    private static bool? _checkForUpdates;
    private static string _sidAuthor = string.Empty;
    private static int _sidLoadAddress = MagSid.Core.SidLoadAddress.Default;
    private static readonly Dictionary<DialogFolder, string> _folders = [];
    private static string _midiDevice = string.Empty;
    private static string _midiKnobBindings = string.Empty;
    private static bool _loaded;

    public static bool ShowHighHex
    {
        get
        {
            EnsureLoaded();
            return _showHighHex;
        }
        set
        {
            EnsureLoaded();
            if (_showHighHex == value)
                return;

            _showHighHex = value;
            Save();
        }
    }

    /// <summary>Which SID the editor plays through: the 8580 until the user picks the 6581, and
    /// from then on whichever was picked last.</summary>
    public static bool Chip6581
    {
        get
        {
            EnsureLoaded();
            return _chip6581;
        }
        set
        {
            EnsureLoaded();
            if (_chip6581 == value)
                return;

            _chip6581 = value;
            Save();
        }
    }

    /// <summary>False - the default - gives Space the Edit/Jam toggle. True hands it the
    /// transport instead, and F5 takes over Edit/Jam. Which one a tracker musician wants depends
    /// entirely on which tracker they came from, so it is a preference rather than a choice to
    /// make for them.</summary>
    public static bool SwapSpacebar
    {
        get
        {
            EnsureLoaded();
            return _swapSpacebar;
        }
        set
        {
            EnsureLoaded();
            if (_swapSpacebar == value)
                return;

            _swapSpacebar = value;
            Save();
        }
    }

    /// <summary>False - the default - draws the retro view's LS/LE loops unrolled, once per
    /// pass. True draws a loop's rows once, as the view always used to.</summary>
    public static bool CompactLoops
    {
        get
        {
            EnsureLoaded();
            return _compactLoops;
        }
        set
        {
            EnsureLoaded();
            if (_compactLoops == value)
                return;

            _compactLoops = value;
            Save();
        }
    }

    /// <summary>
    /// Whether the editor looks for a newer release each time it starts: null until the user has
    /// been asked, which the editor does once, on its first start - off until they say yes.
    /// </summary>
    public static bool? CheckForUpdates
    {
        get
        {
            EnsureLoaded();
            return _checkForUpdates;
        }
        set
        {
            EnsureLoaded();
            if (_checkForUpdates == value)
                return;

            _checkForUpdates = value;
            Save();
        }
    }

    /// <summary>The author last exported under, offered as the default next time. Empty until
    /// one has been typed.</summary>
    public static string SidAuthor
    {
        get
        {
            EnsureLoaded();
            return _sidAuthor;
        }
        set
        {
            EnsureLoaded();
            string trimmed = value?.Trim() ?? string.Empty;
            if (_sidAuthor == trimmed)
                return;

            _sidAuthor = trimmed;
            Save();
        }
    }

    /// <summary>The address last exported to. Someone building a demo exports to the same spot
    /// every time, and $1000 is only the right default until they have chosen otherwise once.</summary>
    public static int SidLoadAddress
    {
        get
        {
            EnsureLoaded();
            return _sidLoadAddress;
        }
        set
        {
            EnsureLoaded();
            if (_sidLoadAddress == value)
                return;

            _sidLoadAddress = value;
            Save();
        }
    }

    /// <summary>
    /// The folder <paramref name="dialog"/> was last used in; empty when it has not been, or the
    /// folder is gone. Each dialog has its own: a save dialog with no folder of its own inherits
    /// whichever one the process used last, which is how exporting audio once offered to write it
    /// into the folder the tunes live in, and a three-megabyte render ended up in this repository.
    /// </summary>
    public static string FolderFor(DialogFolder dialog)
    {
        EnsureLoaded();
        return _folders.TryGetValue(dialog, out string? folder) && folder.Length > 0 && Directory.Exists(folder)
            ? folder
            : string.Empty;
    }

    /// <summary>Remembers the folder of <paramref name="file"/> - a file the dialog was just used
    /// on - for <paramref name="dialog"/>.</summary>
    public static void RememberFolder(DialogFolder dialog, string? file)
    {
        EnsureLoaded();
        if (Path.GetDirectoryName(file?.Trim() ?? "") is not { Length: > 0 } folder)
            return;
        if (_folders.TryGetValue(dialog, out string? was) && was == folder)
            return;

        _folders[dialog] = folder;
        Save();
    }

    /// <summary>The MIDI input last used, by name rather than index - a device's number moves when
    /// another is plugged in, and reopening by number would quietly attach to the wrong one.</summary>
    public static string MidiDevice
    {
        get { EnsureLoaded(); return _midiDevice; }
        set
        {
            EnsureLoaded();
            string trimmed = value?.Trim() ?? string.Empty;
            if (_midiDevice == trimmed) return;
            _midiDevice = trimmed;
            Save();
        }
    }

    /// <summary>Which controller turns which knob, as MidiKnobBindings spells it.</summary>
    public static string MidiKnobBindings
    {
        get { EnsureLoaded(); return _midiKnobBindings; }
        set
        {
            EnsureLoaded();
            string text = value ?? string.Empty;
            if (_midiKnobBindings == text) return;
            _midiKnobBindings = text;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath)) is { } stored)
            {
                _showHighHex = stored.ShowHighHex;
                _swapSpacebar = stored.SwapSpacebar;
                _compactLoops = stored.CompactLoops;
                _chip6581 = stored.Chip6581;
                _checkForUpdates = stored.CheckForUpdates;
                _sidAuthor = stored.SidAuthor ?? string.Empty;
                // Absent in settings written before the address was settable, and 0 there means
                // "never chosen" rather than "load at $0000".
                if (stored.SidLoadAddress is > 0)
                    _sidLoadAddress = stored.SidLoadAddress.Value;

                foreach (var (name, folder) in stored.Folders ?? [])
                {
                    if (Enum.TryParse(name, out DialogFolder dialog) && !string.IsNullOrWhiteSpace(folder))
                        _folders[dialog] = folder;
                }

                // Settings from before each dialog had its own folder: the folder dialogs shared
                // then is where each of them starts now.
                Inherit(stored.SidOpenDirectory, DialogFolder.ImportSid, DialogFolder.PlaySid);
                Inherit(stored.InstrumentDirectory, DialogFolder.LoadInstrument, DialogFolder.SaveInstrument, DialogFolder.MidiInstrument);
                Inherit(stored.MidiImportDirectory, DialogFolder.ImportMidi);
                Inherit(stored.AudioExportDirectory, DialogFolder.ExportAudio);
                _midiDevice = stored.MidiDevice ?? string.Empty;
                _midiKnobBindings = stored.MidiKnobBindings ?? string.Empty;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults rather than block startup.
        }
    }

    private static void Inherit(string? folder, params DialogFolder[] dialogs)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        foreach (var dialog in dialogs)
            _folders.TryAdd(dialog, folder);
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new StoredSettings
            {
                ShowHighHex = _showHighHex,
                SwapSpacebar = _swapSpacebar,
                CompactLoops = _compactLoops,
                Chip6581 = _chip6581,
                CheckForUpdates = _checkForUpdates,
                SidAuthor = _sidAuthor,
                SidLoadAddress = _sidLoadAddress,
                Folders = _folders.ToDictionary(f => f.Key.ToString(), f => f.Value),
                MidiDevice = _midiDevice,
                MidiKnobBindings = _midiKnobBindings,
            }));
        }
        catch
        {
            // Best-effort - losing a UI preference is not worth surfacing an error for.
        }
    }

    private sealed class StoredSettings
    {
        public bool ShowHighHex { get; set; }
        public bool SwapSpacebar { get; set; }
        public bool CompactLoops { get; set; }
        public bool Chip6581 { get; set; }
        public bool? CheckForUpdates { get; set; }
        public string? SidAuthor { get; set; }
        public int? SidLoadAddress { get; set; }
        public Dictionary<string, string>? Folders { get; set; }

        // Read from settings written before each dialog had a folder of its own; never written.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? SidOpenDirectory { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? AudioExportDirectory { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? InstrumentDirectory { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? MidiImportDirectory { get; set; }
        public string? MidiDevice { get; set; }
        public string? MidiKnobBindings { get; set; }
    }
}
