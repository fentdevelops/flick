using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Flick;

public sealed class Settings
{
    public bool Record { get; set; } = true;
    public TargetKind Target { get; set; } = TargetKind.Area;
    public string Quality { get; set; } = "Good";
    public bool Sound { get; set; } = true;
    public bool QualityOpen { get; set; } = true;
    public bool SeenGuide { get; set; }
    public bool SeenPillHint { get; set; }
    public bool SeenTrayHint { get; set; }
    public int? BarX { get; set; }
    public int? BarY { get; set; }

    private static string FilePath => Path.Combine(Paths.AppData, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
    }
}

public static class StartWithWindows
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("Flick") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue("Flick", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Flick", false);
        }
    }
}
