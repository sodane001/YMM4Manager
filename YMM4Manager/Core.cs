using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace YMM4Manager;

/// <summary>このソフトの設定(%AppData%\YMM4Manager\settings.json)</summary>
public sealed class AppSettings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YMM4Manager");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Instance { get; } = Load();

    /// <summary>YMM4のフォルダー(YukkuriMovieMaker.exe があるところ)</summary>
    public string? Ymm4Dir { get; set; }
    /// <summary>プロジェクトを探すフォルダー</summary>
    public List<string> ProjectFolders { get; set; } = new();
    /// <summary>バックアップの保存先(空なら、ドキュメント\YMM4Manager バックアップ)</summary>
    public string? BackupDir { get; set; }
    public bool LaunchMinimize { get; set; }
    /// <summary>通知領域(タスクバーの右下)にアイコンを出す</summary>
    public bool TrayIcon { get; set; } = true;
    /// <summary>× で閉じた時は終わらずに、通知領域に残す(定期バックアップを続ける)</summary>
    public bool CloseToTray { get; set; } = true;
    public string LastPage { get; set; } = "home";
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public int MaterialTileSize { get; set; } = 150;
    /// <summary>新しいプロジェクトを作るフォルダー(この中に「プロジェクト名」のフォルダーを作る)</summary>
    public string? ProjectRoot { get; set; }
    /// <summary>新しいプロジェクトのひな形(.ymmp)。コピーして使う</summary>
    public string? ProjectTemplate { get; set; }
    public bool OpenAfterCreate { get; set; } = true;
    /// <summary>YMM4の種類("full" / "lite")。空なら、フォルダー名から判断する</summary>
    public string? Ymm4Edition { get; set; }
    /// <summary>YMM4専用ストレージ(ドライブ、またはフォルダー)。YMM4本体・素材データベース・プロジェクト・バックアップをまとめて置く場所</summary>
    public string? StorageRoot { get; set; }
    /// <summary>YMM4の過去のバージョン(zip)の保存先(空なら、標準の場所)</summary>
    public string? VersionsDir { get; set; }
    /// <summary>YMM4のプリセット(user フォルダーごと別々の YMM4 本体)</summary>
    public List<Ymm4PresetEntry> Ymm4Presets { get; set; } = new();
    /// <summary>AI質問: 一緒に送るもの</summary>
    public bool AiSendStorage { get; set; } = true;
    /// <summary>AI が会話をまたいで覚える(専用ストレージの「AI記憶」)</summary>
    public bool AiUseMemory { get; set; } = true;
    public bool AiSendMaterials { get; set; } = true;
    public bool AiSendProjects { get; set; } = true;
    public bool AiSendYmm4 { get; set; } = true;
    /// <summary>専用ストレージの定期バックアップ: 保存先・間隔(時間。0 = しない)・残し方(0 = 1つを上書き、n = 日付ごとに n 個)</summary>
    public string? StorageBackupDir { get; set; }
    public double StorageBackupHours { get; set; } = 24;
    public int StorageBackupKeep { get; set; }
    public bool StorageBackupExclude { get; set; } = true;
    /// <summary>バックアップを ZIP 1つにまとめる(クラウドストレージ向け)</summary>
    public bool StorageBackupZip { get; set; }
    public DateTime? StorageBackupLast { get; set; }
    public string? StorageBackupResult { get; set; }
    /// <summary>マネージャーを開いていない時も、Windows のタスクスケジューラーでバックアップする</summary>
    public bool StorageBackupTask { get; set; }

    // 読んだ時(または最後に保存した時)の中身。保存する時に「この画面で変えた所」だけを書くために使う
    System.Text.Json.Nodes.JsonObject? baseline;
    static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };
    static readonly object saveLock = new();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(text) ?? new();
                s.baseline = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject;
                return s;
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// 保存する。ファイルを丸ごと上書きせず、この画面で変えた項目だけを、いまのファイルの中身に重ねて書く。
    /// (マネージャーが2つ開いていた時や、タスクスケジューラーのバックアップが動いていた時に、
    ///  古い中身で上書きして、ほかで変えた設定(バックアップの保存先など)が元に戻ってしまうのを防ぐ)
    /// ほかで変わった項目は、この画面の値にも読み込む(文字・数・はい/いいえ・日時の項目だけ)。
    /// </summary>
    public void Save()
    {
        lock (saveLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var mine = JsonSerializer.SerializeToNode(this, JsonOpt) as System.Text.Json.Nodes.JsonObject ?? new();
                System.Text.Json.Nodes.JsonObject disk;
                try { disk = File.Exists(FilePath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath)) as System.Text.Json.Nodes.JsonObject ?? new() : new(); }
                catch { disk = new(); }
                foreach (var (key, value) in mine.ToList())
                {
                    var before = baseline?[key];
                    bool changedHere = baseline == null || !System.Text.Json.Nodes.JsonNode.DeepEquals(value, before);
                    if (changedHere || !disk.ContainsKey(key)) disk[key] = value?.DeepClone();
                }
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, disk.ToJsonString(JsonOpt));
                File.Move(tmp, FilePath, true);
                baseline = disk.DeepClone() as System.Text.Json.Nodes.JsonObject;
                PullSimple(disk);
            }
            catch { }
        }
    }

    /// <summary>
    /// ファイルを読み直して、ほかで変わった項目をこの画面の値に入れる(この画面で変えて、まだ保存していない項目はそのまま)。
    /// 画面が前に出た時に呼ぶ(タスクスケジューラーのバックアップの結果なども、すぐ見えるように)
    /// </summary>
    public bool ReloadFromDisk()
    {
        lock (saveLock)
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var disk = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath)) as System.Text.Json.Nodes.JsonObject;
                if (disk == null) return false;
                if (baseline != null && System.Text.Json.Nodes.JsonNode.DeepEquals(disk, baseline)) return false;
                var mine = JsonSerializer.SerializeToNode(this, JsonOpt) as System.Text.Json.Nodes.JsonObject ?? new();
                // この画面で変えた項目は、ファイルの方を変えずに残す
                var pull = new System.Text.Json.Nodes.JsonObject();
                foreach (var (key, value) in disk)
                {
                    var before = baseline?[key];
                    bool changedHere = baseline != null && !System.Text.Json.Nodes.JsonNode.DeepEquals(mine[key], before);
                    if (!changedHere) pull[key] = value?.DeepClone();
                }
                PullSimple(pull);
                baseline = disk.DeepClone() as System.Text.Json.Nodes.JsonObject;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>ファイルの値を、この画面の値に入れる(文字・数・はい/いいえ・日時の項目だけ。一覧はそのまま)</summary>
    void PullSimple(System.Text.Json.Nodes.JsonObject disk)
    {
        foreach (var prop in typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (!prop.CanWrite || !prop.CanRead) continue;
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!(t == typeof(string) || t.IsPrimitive || t == typeof(DateTime) || t == typeof(double) || t.IsEnum)) continue;
            if (!disk.TryGetPropertyValue(prop.Name, out var node)) continue;
            try
            {
                var v = node == null ? null : node.Deserialize(prop.PropertyType);
                if (!Equals(v, prop.GetValue(this))) prop.SetValue(this, v);
            }
            catch { }
        }
    }

    public string BackupRoot => string.IsNullOrWhiteSpace(BackupDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YMM4Manager バックアップ")
        : BackupDir!;
}

/// <summary>YMM4本体の場所・起動・状態</summary>
public static class Ymm4
{
    public const string ExeName = "YukkuriMovieMaker.exe";

    public static string? Dir => AppSettings.Instance.Ymm4Dir;
    public static string? Exe => Dir == null ? null : Path.Combine(Dir, ExeName);
    public static bool IsValid(string? dir) => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir!, ExeName));
    public static bool IsReady => IsValid(Dir);
    public static string? PluginDir => Dir == null ? null : Path.Combine(Dir, "user", "plugin");
    public static string? UserDir => Dir == null ? null : Path.Combine(Dir, "user");

    /// <summary>フォルダーを選んだ時、その中や1つ上・下に YMM4 があれば、その場所を返す</summary>
    public static string? Resolve(string picked)
    {
        try
        {
            if (File.Exists(picked) && Path.GetFileName(picked).Equals(ExeName, StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(picked);
            if (IsValid(picked)) return picked;
            var parent = Path.GetDirectoryName(picked);
            if (parent != null && IsValid(parent)) return parent;
            foreach (var d in Directory.EnumerateDirectories(picked))
                if (IsValid(d)) return d;
        }
        catch { }
        return null;
    }

    /// <summary>動いているYMM4や、よくある場所から探す</summary>
    public static string? AutoDetect()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("YukkuriMovieMaker"))
            {
                try { var f = p.MainModule?.FileName; if (f != null) return Path.GetDirectoryName(f); } catch { }
            }
        }
        catch { }
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName))
        {
            candidates.Add(Path.Combine(drive, "YukkuriMovieMaker4"));
            candidates.Add(Path.Combine(drive, "YukkuriMovieMaker_v4"));
            candidates.Add(Path.Combine(drive, "ymm4"));
            candidates.Add(Path.Combine(drive, "YMM4"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var sub in new[] { "Desktop", "Downloads", "Documents", "" })
        {
            var b = Path.Combine(home, sub);
            candidates.Add(Path.Combine(b, "YukkuriMovieMaker4"));
            candidates.Add(Path.Combine(b, "YukkuriMovieMaker_v4"));
            candidates.Add(Path.Combine(b, "YukkuriMovieMaker_v4_Lite"));
        }
        foreach (var c in candidates)
        {
            var r = Directory.Exists(c) ? Resolve(c) : null;
            if (r != null) return r;
        }
        return null;
    }

    public static string Version
    {
        get
        {
            try { return Exe != null && File.Exists(Exe) ? FileVersionInfo.GetVersionInfo(Exe).FileVersion ?? "" : ""; }
            catch { return ""; }
        }
    }

    public static Process[] Running()
    {
        try
        {
            return Process.GetProcessesByName("YukkuriMovieMaker").Where(p =>
            {
                try { return Dir == null || string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), Dir, StringComparison.OrdinalIgnoreCase); }
                catch { return true; }
            }).ToArray();
        }
        catch { return Array.Empty<Process>(); }
    }

    public static bool IsRunning => Running().Length > 0;

    /// <summary>YMM4を起動する(プロジェクトを指定すると、それを開く)</summary>
    public static string Launch(string? project = null)
    {
        if (!IsReady) return "YMM4の場所が設定されていません";
        try
        {
            var psi = new ProcessStartInfo(Exe!) { WorkingDirectory = Dir!, UseShellExecute = true };
            if (!string.IsNullOrEmpty(project)) psi.ArgumentList.Add(project);
            Process.Start(psi);
            return string.IsNullOrEmpty(project) ? "YMM4を起動しました" : $"「{Path.GetFileNameWithoutExtension(project)}」をYMM4で開きました";
        }
        catch (Exception ex) { return "起動できませんでした: " + ex.Message; }
    }

    public static void OpenFolder(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>ZIP でのバックアップ・復元</summary>
public static class Backup
{
    /// <summary>フォルダーをまとめて ZIP にする。sources は(ZIPの中の名前, 実際のフォルダー)</summary>
    public static async Task<string> CreateAsync(string zipPath, IEnumerable<(string name, string dir)> sources, IProgress<string>? progress)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (name, dir) in sources)
            {
                if (!Directory.Exists(dir)) continue;
                progress?.Report($"{name} をまとめています…");
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var rel = Path.GetRelativePath(dir, f);
                        // 使用中のファイルも読めるように、共有モードで開く
                        using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry(Path.Combine(name, rel).Replace('\\', '/'), CompressionLevel.Optimal);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    catch { }
                }
            }
        });
        return zipPath;
    }

    /// <summary>ZIP の中の name フォルダーを、dir へ戻す(上書き)</summary>
    public static async Task<int> RestoreAsync(string zipPath, IEnumerable<(string name, string dir)> targets)
    {
        return await Task.Run(() =>
        {
            int count = 0;
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var (name, dir) in targets)
            {
                var prefix = name.TrimEnd('/') + "/";
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0))
                {
                    var rel = e.FullName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    var dest = Path.GetFullPath(Path.Combine(dir, rel));
                    if (!dest.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;   // ZIP の外へは書かない
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, true);
                    count++;
                }
            }
            return count;
        });
    }

    public static IReadOnlyList<string> NamesIn(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Select(e => e.FullName.Split('/')[0]).Where(n => n.Length > 0).Distinct().ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>新しいプロジェクト(.ymmp)を作る</summary>
public static class ProjectMaker
{
    /// <summary>
    /// ひな形があれば、それをコピーする(中身はそのまま)。
    /// 無ければ、いちばん新しいプロジェクトの設定(解像度・fps・キャラクターなど)を使い、アイテムを空にしたものを作る。
    /// プロジェクトが1つも無い時は、最小限の内容で作る。成功なら null、失敗ならその理由
    /// </summary>
    public static string? Create(string file, string? template)
    {
        string? source = null;
        bool clearItems = false;
        if (!string.IsNullOrWhiteSpace(template) && File.Exists(template)) source = template;
        else
        {
            source = ProjectsVm.Scan(AppSettings.Instance.ProjectFolders, 1).FirstOrDefault()?.Path;
            clearItems = true;
        }

        if (source != null)
        {
            try
            {
                var text = File.ReadAllText(source);
                var node = System.Text.Json.Nodes.JsonNode.Parse(text);
                if (node is System.Text.Json.Nodes.JsonObject obj)
                {
                    if (clearItems) ClearItems(obj, 0);
                    if (obj.ContainsKey("FilePath")) obj["FilePath"] = file;
                    File.WriteAllText(file, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    return null;
                }
            }
            catch { }
            if (!clearItems)
            {
                try { File.Copy(source, file); return null; }
                catch (Exception ex) { return "ひな形をコピーできませんでした: " + ex.Message; }
            }
        }

        // 最小限のプロジェクト(YMM4が足りない設定を補って開く想定)
        var minimal = new System.Text.Json.Nodes.JsonObject
        {
            ["FilePath"] = file,
            ["SelectedTimelineIndex"] = 0,
            ["Timelines"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["ID"] = 0,
                ["Name"] = "タイムライン1",
                ["VideoInfo"] = new System.Text.Json.Nodes.JsonObject { ["FPS"] = 60, ["Hz"] = 48000, ["Width"] = 1920, ["Height"] = 1080 },
                ["Items"] = new System.Text.Json.Nodes.JsonArray(),
                ["CurrentFrame"] = 0,
                ["Length"] = 600,
            }),
        };
        try
        {
            File.WriteAllText(file, minimal.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return null;
        }
        catch (Exception ex) { return "作れませんでした: " + ex.Message; }
    }

    /// <summary>プロジェクトをコピーする(中の「FilePath」があれば、新しい場所に書き換える)。素材の場所はそのまま</summary>
    public static void CopyProject(string src, string dest)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(src));
            if (node is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey("FilePath"))
            {
                obj["FilePath"] = dest;
                File.WriteAllText(dest, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                return;
            }
        }
        catch { }
        File.Copy(src, dest, false);
    }

    /// <summary>タイムラインのアイテム("Items" の配列)を空にする</summary>
    static void ClearItems(System.Text.Json.Nodes.JsonNode? node, int depth)
    {
        if (node == null || depth > 4) return;
        if (node is System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var kv in o.ToList())
            {
                if (kv.Key == "Items" && kv.Value is System.Text.Json.Nodes.JsonArray a) a.Clear();
                else if (kv.Key == "CurrentFrame") o[kv.Key] = 0;
                else ClearItems(kv.Value, depth + 1);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
            foreach (var x in arr) ClearItems(x, depth + 1);
    }
}

public sealed class Ymm4PresetEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Memo { get; set; } = "";
    /// <summary>"full" / "lite"。空なら、フォルダー名から判断する</summary>
    public string? Edition { get; set; }
}
