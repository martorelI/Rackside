using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rackside.Core.Settings;

/// <summary>Defaults of "Configure switches" taken from settings.json (Push).</summary>
public sealed class PushDefaults
{
    public string LoginBanner { get; set; } = string.Empty;
    public string MotdBanner { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public string UtcOffset { get; set; } = string.Empty;
    public string SummerTime { get; set; } = string.Empty;
    public string VtyIdleTimeout { get; set; } = "15";
}

/// <summary>Subfolder names under the data folder (Folders).</summary>
public sealed class FolderNames
{
    public string Restores { get; set; } = "Restores";
    public string FactoryResets { get; set; } = "FactoryResets";
    public string Upgrades { get; set; } = "Upgrades";
    public string Backups { get; set; } = "Backups";
    public string SSHFingerprints { get; set; } = "SSHFingerprints";
    public string Inventory { get; set; } = "Inventory";
    public string Pushes { get; set; } = "Pushes";
}

/// <summary>
/// Settings\settings.json next to the program: created
/// with the defaults the first time; an invalid value is ignored (the default is used) and reported.
/// </summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public string DataFolder { get; set; } = string.Empty;
    public FolderNames Folders { get; set; } = new();
    public string PreferredCOMPort { get; set; } = "COM3";
    public PushDefaults Push { get; set; } = new();
    public IReadOnlyList<string> ExtraAllowedCommands { get; set; } = [];

    /// <summary>Problems found when reading, as (English template, arguments): translated when shown.</summary>
    public List<(string Template, object[] Args)> Warnings { get; } = [];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string PathFor(string baseFolder) => Path.Combine(baseFolder, "Settings", "settings.json");

    /// <summary>The effective settings: the file's, validated, on top of the defaults. Creates the file if it does not exist.</summary>
    public static AppSettings Read(string path)
    {
        var s = new AppSettings();
        if (!File.Exists(path))
        {
            try
            {
                s.Save(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                s.Warnings.Add(("Could not create {0}: {1}", [path, ex.Message]));
            }
            return s;
        }
        JsonObject? j;
        try
        {
            j = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            s.Warnings.Add(("{0} is not valid JSON ({1}). Default values are used: fix it or delete it so it is created again.", [path, ex.Message]));
            return s;
        }
        static string? Str(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var x) ? x : v.ToJsonString().Trim('"')) : null;
        static JsonNode? Get(JsonObject? o, string name) => o?.FirstOrDefault(p => Ci.Eq(p.Key, name)).Value;

        if (Str(Get(j, "Language")) is { } lang)
        {
            if (lang.Equals("en", StringComparison.OrdinalIgnoreCase) || lang.Equals("es", StringComparison.OrdinalIgnoreCase)) { s.Language = lang.ToLowerInvariant(); }
            else { s.Warnings.Add(("Language \"{0}\" not recognized (en / es): \"en\" is used.", [lang])); }
        }
        if (Str(Get(j, "DataFolder")) is { } df)
        {
            s.DataFolder = df.Trim();
        }
        var folders = Get(j, "Folders") as JsonObject;
        var bad = Path.GetInvalidFileNameChars();
        foreach (var prop in typeof(FolderNames).GetProperties())
        {
            if (Str(Get(folders, prop.Name)) is not { } v)
            {
                continue;
            }
            var x = v.Trim();
            if (x.Length == 0 || x.IndexOfAny(bad) >= 0 || Ci.IsMatch(x, "[<>:\"/\\\\|?*]") || Ci.IsMatch(x, "^\\.+$"))
            {
                s.Warnings.Add(("Folders.{0} = \"{1}\" is not a valid folder name: \"{2}\" is used.", [prop.Name, v, prop.GetValue(s.Folders)!]));
            }
            else
            {
                prop.SetValue(s.Folders, x);
            }
        }
        if (Str(Get(j, "PreferredCOMPort")) is { } com)
        {
            if (Ci.IsMatch(com.Trim(), "^(?i)COM\\d{1,3}$")) { s.PreferredCOMPort = com.Trim().ToUpperInvariant(); }
            else { s.Warnings.Add(("PreferredCOMPort = \"{0}\" is not a COM port: COM3 is used.", [com])); }
        }
        var push = Get(j, "Push") as JsonObject;
        foreach (var prop in typeof(PushDefaults).GetProperties())
        {
            if (Str(Get(push, prop.Name)) is { } v)
            {
                prop.SetValue(s.Push, v);
            }
        }
        if (Get(j, "ExtraAllowedCommands") is { } extra)
        {
            var list = new List<string>();
            foreach (var x in extra is JsonArray a ? a.Select(Str) : [Str(extra)])
            {
                if (string.IsNullOrWhiteSpace(x))
                {
                    continue;
                }
                try
                {
                    _ = new Regex(x, RegexOptions.None, TimeSpan.FromSeconds(1));
                    list.Add(x);
                }
                catch (ArgumentException)
                {
                    s.Warnings.Add(("ExtraAllowedCommands: \"{0}\" is not a valid regular expression and is ignored.", [x]));
                }
            }
            s.ExtraAllowedCommands = list;
        }
        return s;
    }

    private JsonObject ToJson() => new()
    {
        ["Language"] = Language,
        ["DataFolder"] = DataFolder,
        ["Folders"] = new JsonObject(typeof(FolderNames).GetProperties().Select(p => KeyValuePair.Create(p.Name, (JsonNode?)JsonValue.Create((string)p.GetValue(Folders)!)))),
        ["PreferredCOMPort"] = PreferredCOMPort,
        ["Push"] = new JsonObject(typeof(PushDefaults).GetProperties().Select(p => KeyValuePair.Create(p.Name, (JsonNode?)JsonValue.Create((string)p.GetValue(Push)!)))),
        ["ExtraAllowedCommands"] = new JsonArray(ExtraAllowedCommands.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
    };

    /// <summary>Writes the whole file (UTF-8 without BOM).</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson().ToJsonString(Indented), new UTF8Encoding(false));
    }

    /// <summary>Changes only "Language" in settings.json and keeps the rest as it is in the file.</summary>
    public static void SaveLanguage(string path, string language)
    {
        if (language is not ("en" or "es"))
        {
            throw new ArgumentException("en / es", nameof(language));
        }
        JsonObject obj;
        if (File.Exists(path))
        {
            try
            {
                obj = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? throw new JsonException("not an object");
            }
            catch (JsonException ex)
            {
                throw new FormatException(path, ex);
            }
            var key = obj.FirstOrDefault(p => Ci.Eq(p.Key, "Language")).Key ?? "Language";
            obj[key] = language;
        }
        else
        {
            obj = new AppSettings { Language = language }.ToJson();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, obj.ToJsonString(Indented), new UTF8Encoding(false));
    }
}

/// <summary>Where every kind of data lives, next to the program or under DataFolder (security rule 9).</summary>
public sealed record DataPaths(string Base, string Data, string Restores, string FactoryResets, string Upgrades, string Backups, string Fingerprints, string Inventory, string InventoryCsv, string Pushes)
{
    public static DataPaths From(string baseFolder, AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var data = s.DataFolder.Length == 0 ? baseFolder : Path.IsPathRooted(s.DataFolder) ? s.DataFolder : Path.Combine(baseFolder, s.DataFolder);
        var f = s.Folders;
        var inventory = Path.Combine(data, f.Inventory);
        return new DataPaths(baseFolder, data, Path.Combine(data, f.Restores), Path.Combine(data, f.FactoryResets), Path.Combine(data, f.Upgrades), Path.Combine(data, f.Backups),
            Path.Combine(data, f.SSHFingerprints, "ssh_fingerprints.csv"), inventory, Path.Combine(inventory, "switches.csv"), Path.Combine(data, f.Pushes));
    }
}
