using System.Text.Json;
using System.IO;
using System.Runtime.CompilerServices;

namespace XEnchantmentPatcher;

public static class SettingsStore
{
    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XEnchantmentPatcher");
    public static readonly string DefaultPathName = Path.Combine(DefaultFolder, "settings.json");
    private static string? selectedPath;
    public static string PathName => selectedPath ?? DefaultPathName;
    public static string Folder => Path.GetDirectoryName(PathName)!;
    internal static string? InitialOutputFolder { get; set; }
    internal sealed class Revision(string path, string? version) { public string Path = path; public string? Version = version; }
    private static readonly ConditionalWeakTable<AppSettings, Revision> revisions = new();
    private static string? baseline;
    private static bool baselineRead;
    internal static void UseFile(string path)
    {
        selectedPath = Path.GetFullPath(path);
        using var lease = SettingsLease(); baseline = AtomicFile.Version(PathName); baselineRead = true;
    }
    private static FileLease SettingsLease() => FileLease.Acquire(PathName + ".xep-lock", "settings file " + PathName, TimeSpan.FromSeconds(10));

    public static AppSettings Load()
    {
        using var lease = SettingsLease();
        var version = AtomicFile.Version(PathName);
        var settings = version is not null ? ReadSnapshot(PathName) : Normalize(new());
        if (InitialOutputFolder is not null) settings.OutputFolder = InitialOutputFolder;
        revisions.Add(settings, new(PathName, version)); baseline = version; baselineRead = true;
        return settings;
    }

    internal static AppSettings ReadSnapshot(string path)
    {
        try { return Normalize(JsonSerializer.Deserialize(AtomicFile.ReadText(path), JsonContext.Default.AppSettings)
            ?? throw new InvalidDataException("Settings document is null.")); }
        catch (JsonException ex) { throw new InvalidDataException($"Settings are not valid JSON: {path}. Restore a valid file or select a separate settings file.", ex); }
    }

    public static void Save(AppSettings settings)
    {
        var text = JsonSerializer.Serialize(settings, JsonContext.Default.AppSettings);
        using var lease = SettingsLease();
        var current = AtomicFile.Version(PathName);
        if (!baselineRead) { baseline = current; baselineRead = true; }
        var revision = revisions.GetValue(settings, _ => new(PathName, baseline));
        if (revision.Path != PathName) throw new InvalidOperationException("These settings belong to a different session settings file.");
        if (revision.Version != current)
        {
            var recovery = Path.Combine(Folder, Path.GetFileNameWithoutExtension(PathName) + ".conflict-" + SessionRuntime.Id + ".json");
            try { AtomicFile.WriteText(recovery, text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Another session changed {PathName}. The shared file was not overwritten, and the recovery copy could not be written to {recovery}. Your choices remain in this window; keep it open and restore write access before retrying. {ex.Message}", ex);
            }
            throw new SettingsConflictException($"Another session changed {PathName} since this session loaded it. The shared file was not overwritten. Your current choices are saved in {recovery}. Reopen to load the latest settings, or launch --gui --settings-file with that recovery file and choose an independent output folder.");
        }
        AtomicFile.WriteText(PathName, text);
        // The rename has committed the exact text. A later read or metadata
        // failure must not turn a successful settings save into a false failure.
        revision.Version = baseline = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        try
        {
            SessionRuntime.Settings(PathName, settings.OutputFolder);
            SessionRuntime.EffectiveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsMetadataException($"Settings were saved to {PathName}, but session metadata could not be updated. {ex.Message}", ex);
        }
    }

    public static ProfileSettings SelectProfile(AppSettings settings, string? profileName = null)
    {
        var selected = string.IsNullOrWhiteSpace(profileName) ? settings.ProfileName : profileName;
        settings.ProfileName = string.IsNullOrWhiteSpace(selected) ? "Default" : selected.Trim();
        if (!settings.Profiles.TryGetValue(settings.ProfileName, out var profile))
            settings.Profiles[settings.ProfileName] = profile = new();
        NormalizeProfile(profile);
        return profile;
    }

    // Explicit GUI selection must not create a profile from an unknown name.
    public static void SelectExistingProfile(AppSettings settings, string name)
    {
        var selected = settings.Profiles.Keys.FirstOrDefault(k => k.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Select an existing profile, or use New to create one.");
        SelectProfile(settings, selected);
    }

    internal static string ValidateProfileName(AppSettings settings, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a profile name.");
        if (settings.Profiles.ContainsKey(name))
            throw new ArgumentException("A profile with that name already exists. Choose another name.");
        return name;
    }

    public static void NewProfile(AppSettings settings, string name)
    {
        name = ValidateProfileName(settings, name);
        var profile = new ProfileSettings();
        NormalizeProfile(profile);
        settings.Profiles.Add(name, profile);
        settings.ProfileName = name;
    }

    public static void DuplicateProfile(AppSettings settings, string name)
    {
        name = ValidateProfileName(settings, name);
        var profile = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings.CurrentProfile, JsonContext.Default.ProfileSettings),
            JsonContext.Default.ProfileSettings)!;
        NormalizeProfile(profile);
        profile.LastGenerationFingerprint = null;
        settings.Profiles.Add(name, profile);
        settings.ProfileName = name;
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Profiles = new Dictionary<string, ProfileSettings>(settings.Profiles ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var existing in settings.Profiles.Values) NormalizeProfile(existing);
        SelectProfile(settings, settings.ProfileName);
        return settings;
    }

    // Drops saved values that name nothing (retired policies and modes).
    private static void NormalizeProfile(ProfileSettings profile)
    {
        profile.Policies = new Dictionary<string, PatchPolicy>(
            (profile.Policies ?? []).Where(x => Enum.IsDefined(x.Value)), StringComparer.OrdinalIgnoreCase);
        profile.SpecializedLootOverrides = new Dictionary<string, SpecializedLootMode>(
            (profile.SpecializedLootOverrides ?? []).Where(x => Enum.IsDefined(x.Value)), StringComparer.OrdinalIgnoreCase);
        profile.ItemOverrides = new Dictionary<string, bool>(profile.ItemOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        profile.EnchantmentOverrides = new Dictionary<string, bool>(profile.EnchantmentOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        profile.MaterialFamilyOverrides = new Dictionary<string, string>(profile.MaterialFamilyOverrides ?? [], StringComparer.OrdinalIgnoreCase);
        profile.AuthorConflictAnswers = new Dictionary<string, string>(profile.AuthorConflictAnswers ?? [], StringComparer.Ordinal);
        profile.IgnoredAuthorEnchantmentLists = new HashSet<string>(profile.IgnoredAuthorEnchantmentLists ?? [], StringComparer.OrdinalIgnoreCase);
        profile.IgnoredAuthorDestinationLists = new HashSet<string>(profile.IgnoredAuthorDestinationLists ?? [], StringComparer.OrdinalIgnoreCase);
        profile.EnchantmentProviderPlugins = (profile.EnchantmentProviderPlugins ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        profile.EnchantmentProviderOrder = (profile.EnchantmentProviderOrder ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Concat(profile.EnchantmentProviderPlugins)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        profile.WarningListEntries = Math.Clamp(profile.WarningListEntries, 1, 255);
    }
}

public sealed class SettingsConflictException(string message) : IOException(message);

internal sealed class SettingsMetadataException(string message, Exception inner) : IOException(message, inner);

// Shared by the GUI and offline tests. Failures never replace the AppSettings
// object, so all unsaved choices remain available for a retry.
internal sealed class SettingsSaveState
{
    public bool HasUnsavedChanges { get; private set; }
    public string Notice { get; private set; } = "";
    public bool TrySave(AppSettings settings)
    {
        try { SettingsStore.Save(settings); HasUnsavedChanges = false; Notice = SessionRuntime.LogError ?? ""; return true; }
        catch (SettingsMetadataException ex) { HasUnsavedChanges = false; Notice = ex.Message; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            HasUnsavedChanges = true;
            Notice = ex is SettingsConflictException ? ex.Message
                : $"Settings were not saved to {SettingsStore.PathName}. Your choices remain in this window. Keep it open, restore write access and retry. {ex.Message}";
            return false;
        }
    }
}
