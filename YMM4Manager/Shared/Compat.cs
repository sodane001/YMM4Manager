using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YmmExplorer;

/// <summary>
/// YmmExplorer プラグインの設定(%AppData%\YmmExplorer\settings.json)のうち、
/// このソフトが共有する項目(素材データベースの保存先)だけを読み書きする。ほかの項目には触らない
/// </summary>
public sealed class ExplorerSettings
{
    public static ExplorerSettings Instance { get; } = new();

    public static readonly string PluginDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YmmExplorer");
    static string FilePath => Path.Combine(PluginDataDir, "settings.json");

    public string? MaterialLibraryPath
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var node = JsonNode.Parse(File.ReadAllText(FilePath));
                return node?["MaterialLibraryPath"]?.GetValue<string>();
            }
            catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(PluginDataDir);
                JsonObject obj = File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o ? o : new JsonObject();
                obj["MaterialLibraryPath"] = value;
                File.WriteAllText(FilePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}

/// <summary>プラグイン側の FileItem にある、大きさの表示だけ</summary>
public static class FileItem
{
    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}

/// <summary>YmmExplorer の設定ファイル(settings.json)の1項目を読み書きする(AI のキー・モデルを、YMM4 の中の AIエディットと共有するため)</summary>
public static class PluginJson
{
    static string FilePath => Path.Combine(ExplorerSettings.PluginDataDir, "settings.json");

    public static JsonNode? Get(string key)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonNode.Parse(File.ReadAllText(FilePath))?[key]?.DeepClone();
        }
        catch { return null; }
    }

    public static void Set(string key, JsonNode? value)
    {
        try
        {
            Directory.CreateDirectory(ExplorerSettings.PluginDataDir);
            JsonObject obj = File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o ? o : new JsonObject();
            obj[key] = value;
            File.WriteAllText(FilePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static string? GetString(string key) => Get(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>{"claude": "…", …} の形の項目の1つ</summary>
    public static string? GetMap(string key, string sub) => Get(key) is JsonObject o && o[sub] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static void SetMap(string key, string sub, string value)
    {
        var o = Get(key) as JsonObject ?? new JsonObject();
        o[sub] = value;
        Set(key, o);
    }
}
