using Mutagen.Bethesda;
using SafePatch.Host;
using SafePatch.Synthesis;

namespace SafePatch.Authoring.Sources;

/// <summary>
/// A Mod Organizer 2 profile, read the way MO2 builds its virtual Data folder, without MO2 or its virtual file
/// system: the game's Data folder, then the profile's enabled mods from lowest to highest priority, then
/// overwrite. The instance is only ever read.
/// </summary>
/// <param name="InstanceFolder">The folder holding <c>ModOrganizer.ini</c> (a portable instance's install folder, or
/// a global instance under <c>%LOCALAPPDATA%\ModOrganizer</c>), or the ini itself.</param>
/// <param name="Profile">The profile; by default, the one MO2 last had selected.</param>
/// <param name="Release">The game release; by default, from the instance's game.</param>
public sealed record Mo2ProfileSource(string InstanceFolder, string? Profile = null, GameRelease? Release = null) : LoadOrderSource
{
    public const string IniName = "ModOrganizer.ini";

    public override ResolvedSource Resolve()
    {
        var iniPath = File.Exists(InstanceFolder) ? InstanceFolder : Path.Combine(InstanceFolder, IniName);
        if (!File.Exists(iniPath)) throw new SafePatchException($"{InstanceFolder} is not a Mod Organizer 2 instance: it has no {IniName}.");
        var instance = Path.GetDirectoryName(Path.GetFullPath(iniPath))!;
        var ini = QtIni.Read(iniPath);

        var gamePath = ini.Get("General", "gamePath") ?? throw new SafePatchException($"{iniPath} names no game folder (gamePath).");
        var release = Release ?? ReleaseOf(ini.Get("General", "gameName"), ini.Get("General", "game_edition"));
        var baseDirectory = Folder(ini.Get("Settings", "base_directory"), instance, instance);
        string Setting(string key, string fallback) => Folder(ini.Get("Settings", key), baseDirectory, Path.Combine(baseDirectory, fallback));
        var mods = Setting("mod_directory", "mods");
        var overwrite = Setting("overwrite_directory", "overwrite");
        var profiles = Setting("profiles_directory", "profiles");

        var profileName = Profile ?? ini.Get("General", "selected_profile") ?? "Default";
        var profile = Path.Combine(profiles, profileName);
        if (!Directory.Exists(profile)) throw new SafePatchException($"MO2 profile {profileName} does not exist in {profiles}.");
        var modList = Path.Combine(profile, "modlist.txt");
        var plugins = Path.Combine(profile, "plugins.txt");
        if (!File.Exists(modList)) throw new SafePatchException($"MO2 profile {profileName} has no modlist.txt.");
        if (!File.Exists(plugins)) throw new SafePatchException($"MO2 profile {profileName} has no plugins.txt.");

        var data = Path.Combine(gamePath, "Data");
        var warnings = new List<string>();
        var layers = new List<DataLayer> { new("Data", data) };
        foreach (var mod in EnabledMods(modList).Reverse())
        {
            var folder = Path.Combine(mods, mod);
            if (Directory.Exists(folder)) layers.Add(new DataLayer(mod, folder));
            else warnings.Add($"Enabled mod {mod} has no folder in {mods}.");
        }
        layers.Add(new DataLayer("overwrite", overwrite));

        // With profile-specific game INI files, MO2 hands the game the profile's copy.
        var defaultIni = SynthesisInputs.DefaultGameIni(release, data);
        var localSettings = QtIni.Read(Path.Combine(profile, "settings.ini")).Get("General", "LocalSettings");
        var gameIni = string.Equals(localSettings, "true", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(profile, defaultIni is null ? "Skyrim.ini" : Path.GetFileName(defaultIni))
            : defaultIni;

        return new ResolvedSource($"MO2 profile {profileName} in {instance}", release, new DataView(data, layers), plugins, gameIni ?? "",
            [modList, plugins], [instance, baseDirectory, mods, overwrite, profiles, gamePath], warnings);
    }

    /// <summary>
    /// The enabled mods in modlist.txt, highest priority first (MO2 writes the list top-down from the highest).
    /// Disabled (<c>-</c>) and unmanaged (<c>*</c>, the game's own DLC) entries and separators are skipped.
    /// </summary>
    public static IReadOnlyList<string> EnabledMods(string modList) =>
        [.. File.ReadAllLines(modList)
            .Where(line => line.StartsWith('+'))
            .Select(line => line[1..].Trim())
            .Where(name => name.Length > 0 && !name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))];

    private static GameRelease ReleaseOf(string? gameName, string? edition) => gameName switch
    {
        "Skyrim Special Edition" when string.Equals(edition, "GOG", StringComparison.OrdinalIgnoreCase) => GameRelease.SkyrimSEGog,
        "Skyrim Special Edition" => GameRelease.SkyrimSE,
        "Skyrim VR" => GameRelease.SkyrimVR,
        "Skyrim" => GameRelease.SkyrimLE,
        "Enderal Special Edition" => GameRelease.EnderalSE,
        "Enderal" => GameRelease.EnderalLE,
        _ => throw new SafePatchException($"MO2 instance manages {gameName ?? "an unnamed game"}, which is not a Skyrim release SafePatch reads. Pass --release to override."),
    };

    /// <summary>An MO2 folder setting: <c>%BASE_DIR%</c> expanded, and relative paths taken from <paramref name="relativeTo"/>.</summary>
    private static string Folder(string? value, string relativeTo, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(fallback);
        var expanded = value.Replace("%BASE_DIR%", relativeTo, StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(Path.Combine(relativeTo, expanded));
    }
}

/// <summary>
/// The Qt INI format MO2 writes: <c>[Section]</c> and <c>key=value</c> lines, values optionally quoted or wrapped in
/// <c>@ByteArray(...)</c>, with backslashes escaped. Read-only; unknown syntax is kept as text.
/// </summary>
internal sealed class QtIni
{
    private readonly Dictionary<(string Section, string Key), string> _values = new();

    public static QtIni Read(string path)
    {
        var ini = new QtIni();
        if (!File.Exists(path)) return ini;
        var section = "General";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            ini._values[(section.ToLowerInvariant(), line[..equals].Trim().ToLowerInvariant())] = Value(line[(equals + 1)..].Trim());
        }
        return ini;
    }

    public string? Get(string section, string key) => _values.GetValueOrDefault((section.ToLowerInvariant(), key.ToLowerInvariant()));

    private static string Value(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (value.StartsWith("@ByteArray(", StringComparison.Ordinal) && value.EndsWith(')')) value = value["@ByteArray(".Length..^1];
        return value.Replace(@"\\", @"\");
    }
}
