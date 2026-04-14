// AppSettings.cs – persistent application settings backed by a JSON file.
// Replaces Properties.Settings. Also stores PMIC vendor passwords.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnifiedDDRSPDFlasher",
        "settings.json");

    // ── Connection settings ──────────────────────────────────────────────────
    public static bool AutoConnect { get; set; }
    public static string LastPort { get; set; } = string.Empty;

    // ── PMIC vendor passwords ────────────────────────────────────────────────
    // Key = PMIC type string (e.g. "PMIC5030") or "default" for the fallback.
    // Value = (LSB, MSB).
    private static Dictionary<string, (byte lsb, byte msb)> _pmicPasswords
        = new Dictionary<string, (byte, byte)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the saved password for a PMIC type, falling back to "default", then to 0x73/0x94.</summary>
    public static (byte lsb, byte msb) GetPMICPassword(string pmicType)
    {
        if (!string.IsNullOrEmpty(pmicType) && _pmicPasswords.TryGetValue(pmicType, out var pw))
            return pw;
        if (_pmicPasswords.TryGetValue("default", out var def))
            return def;
        return (0x73, 0x94); // JEDEC default
    }

    /// <summary>Persists a password for a specific PMIC type (or "default").</summary>
    public static void SetPMICPassword(string pmicType, byte lsb, byte msb)
    {
        string key = string.IsNullOrEmpty(pmicType) ? "default" : pmicType;
        _pmicPasswords[key] = (lsb, msb);
    }

    /// <summary>Returns all stored PMIC passwords as a read-only snapshot.</summary>
    public static IReadOnlyDictionary<string, (byte lsb, byte msb)> GetAllPMICPasswords()
        => _pmicPasswords;

    // ── Load / Save ──────────────────────────────────────────────────────────

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data == null) return;

            AutoConnect = data.AutoConnect;
            LastPort = data.LastPort ?? string.Empty;

            _pmicPasswords.Clear();
            if (data.PmicPasswords != null)
            {
                foreach (var kv in data.PmicPasswords)
                    _pmicPasswords[kv.Key] = (kv.Value.Lsb, kv.Value.Msb);
            }
        }
        catch { /* ignore – use defaults */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var pwDict = new Dictionary<string, PasswordEntry>();
            foreach (var kv in _pmicPasswords)
                pwDict[kv.Key] = new PasswordEntry { Lsb = kv.Value.lsb, Msb = kv.Value.msb };

            var data = new SettingsData
            {
                AutoConnect = AutoConnect,
                LastPort = LastPort,
                PmicPasswords = pwDict
            };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, opts));
        }
        catch { /* ignore */ }
    }

    // ── JSON model ───────────────────────────────────────────────────────────

    private class SettingsData
    {
        public bool AutoConnect { get; set; }
        public string LastPort { get; set; } = string.Empty;
        public Dictionary<string, PasswordEntry> PmicPasswords { get; set; }
            = new Dictionary<string, PasswordEntry>();
    }

    private class PasswordEntry
    {
        public byte Lsb { get; set; }
        public byte Msb { get; set; }
    }
}