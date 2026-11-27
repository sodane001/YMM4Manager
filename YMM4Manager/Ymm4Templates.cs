using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YmmExplorer;

namespace YMM4Manager;

/// <summary>YMM4 標準のテンプレート1つ</summary>
public sealed class Ymm4TemplateRow : NotifyBase
{
    public Ymm4TemplateRow(JsonObject node, JsonArray parent, string group)
    {
        Node = node; Parent = parent; Group = group;
        ItemCount = CountItems(node);
        var kinds = new SortedSet<string>();
        CollectKinds(node, kinds);
        KindsText = string.Join("・", kinds.Take(6));
    }

    public JsonObject Node { get; }
    public JsonArray Parent { get; }
    public string Group { get; }
    public int ItemCount { get; }
    public string KindsText { get; }
    public string InfoText => (Group.Length > 0 ? Group + " / " : "") + $"アイテム {ItemCount} 個" + (KindsText.Length > 0 ? $"({KindsText})" : "");

    bool isChecked;
    public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }

    public string Name
    {
        get => Node["Name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "(名前なし)";
        set { var t = (value ?? "").Trim(); if (t.Length == 0 || t == Name) return; Node["Name"] = t; Raise(nameof(Name)); Renamed?.Invoke(); }
    }
    public event Action? Renamed;

    public static int CountItems(JsonNode? n) => n switch
    {
        JsonObject o => (o["Frame"] != null && o["Layer"] != null ? 1 : 0) + o.Sum(kv => CountItems(kv.Value)),
        JsonArray a => a.Sum(CountItems),
        _ => 0,
    };

    static void CollectKinds(JsonNode? n, SortedSet<string> kinds)
    {
        switch (n)
        {
            case JsonObject o:
                if (o["Frame"] != null && o["$type"] is JsonValue t && t.TryGetValue<string>(out var s))
                    kinds.Add(s.Split(',')[0].Split('.').Last().Replace("Item", ""));
                foreach (var kv in o) CollectKinds(kv.Value, kinds);
                break;
            case JsonArray a:
                foreach (var c in a) CollectKinds(c, kinds);
                break;
        }
    }
}

/// <summary>
/// 「YMM4テンプレート」ページ。YMM4 標準のテンプレート(アイテムを右クリック→テンプレートに保存したもの)を、プリセットごとに一覧・名前変更・削除・書き出し・読み込み・ほかのプリセットへコピーする。
/// テンプレートは、各 YMM4 の user\setting の中の設定ファイル(名前に Template を含む JSON)に入っている
/// </summary>
public sealed class Ymm4TemplatesVm : NotifyBase
{
    readonly MainVm main;
    JsonNode? doc;
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Ymm4TemplatesVm(MainVm main)
    {
        this.main = main;
        ReloadCommand = new RelayCommand(_ => Load());
        SaveCommand = new RelayCommand(_ => Save(), _ => dirty);
        DeleteCommand = new RelayCommand(_ => DeleteChecked(), _ => Items.Any(i => i.IsChecked));
        ExportCommand = new RelayCommand(_ => Export(), _ => Items.Any(i => i.IsChecked));
        ImportCommand = new RelayCommand(_ => Import(), _ => doc != null);
        CopyToCommand = new RelayCommand(_ => CopyTo(), _ => copyTarget != null && Items.Any(i => i.IsChecked));
        CheckAllCommand = new RelayCommand(_ => { var all = Items.All(i => i.IsChecked); foreach (var i in Items) i.IsChecked = !all; CommandManager.InvalidateRequerySuggested(); });
        OpenFolderCommand = new RelayCommand(_ => { if (FilePath != null) Ymm4.OpenFolder(Path.GetDirectoryName(FilePath)); });
    }

    // ---------------------------------------------------------------- プリセット
    public ObservableCollection<PluginsVm.PresetChoice> PresetChoices { get; } = new();
    PluginsVm.PresetChoice? target;
    public PluginsVm.PresetChoice? Target
    {
        get => target;
        set
        {
            if (dirty && value != target && MessageBox.Show("保存していない変更があります。捨てて切り替えますか?", "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { Raise(nameof(Target)); return; }
            if (!Set(ref target, value)) return;
            RefreshCopyTargets();
            Load();
        }
    }
    public ObservableCollection<PluginsVm.PresetChoice> CopyTargets { get; } = new();
    PluginsVm.PresetChoice? copyTarget;
    public PluginsVm.PresetChoice? CopyTarget { get => copyTarget; set { Set(ref copyTarget, value); CommandManager.InvalidateRequerySuggested(); } }

    public void OnShown()
    {
        PresetChoices.Clear();
        foreach (var p in AppSettings.Instance.Ymm4Presets.Where(p => Ymm4.IsValid(p.Path)))
            PresetChoices.Add(new PluginsVm.PresetChoice(p.Name, p.Path, Ymm4.Dir != null && Same(p.Path, Ymm4.Dir)));
        if (Ymm4.Dir != null && !PresetChoices.Any(c => c.IsCurrent)) PresetChoices.Insert(0, new PluginsVm.PresetChoice("いつもの YMM4", Ymm4.Dir, true));
        var keep = target != null ? PresetChoices.FirstOrDefault(c => Same(c.Dir, target.Dir)) : null;
        target = keep ?? PresetChoices.FirstOrDefault(c => c.IsCurrent) ?? PresetChoices.FirstOrDefault();
        Raise(nameof(Target));
        RefreshCopyTargets();
        if (!dirty) Load();
    }

    void RefreshCopyTargets()
    {
        CopyTargets.Clear();
        foreach (var c in PresetChoices.Where(c => target == null || !Same(c.Dir, target.Dir))) CopyTargets.Add(c);
        CopyTarget = null;
    }

    static bool Same(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 読み込み
    public ObservableCollection<Ymm4TemplateRow> Items { get; } = new();
    public bool HasNoItems => Items.Count == 0;
    string? filePath;
    public string? FilePath { get => filePath; private set { Set(ref filePath, value); Raise(nameof(FileText)); } }
    public string FileText => filePath ?? "(テンプレートの設定ファイルが見つかりません)";
    string message = "";
    public string Message { get => message; set => Set(ref message, value); }
    bool dirty;
    public bool IsDirty { get => dirty; private set { Set(ref dirty, value); Raise(nameof(DirtyText)); CommandManager.InvalidateRequerySuggested(); } }
    public string DirtyText => dirty ? "● 保存していない変更があります" : "";

    public ICommand ReloadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CopyToCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand OpenFolderCommand { get; }

    /// <summary>YMM4 のフォルダーから、テンプレートの設定ファイルを探す(user\setting の中で、名前に Template を含む JSON。いちばん新しいもの)</summary>
    public static string? FindFile(string ymm4Dir)
    {
        var setting = Path.Combine(ymm4Dir, "user", "setting");
        if (!Directory.Exists(setting)) return null;
        try
        {
            return Directory.GetFiles(setting, "*.json", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Contains("Template", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>テンプレート(アイテムを持つ、名前つきのもの)を探す。グループの中のテンプレートも探す</summary>
    static void Collect(JsonNode? n, string group, List<Ymm4TemplateRow> found)
    {
        switch (n)
        {
            case JsonArray arr:
                foreach (var c in arr.ToList())
                {
                    if (c is JsonObject o && o["Name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && Ymm4TemplateRow.CountItems(o) > 0 && !HasNamedChildren(o))
                        found.Add(new Ymm4TemplateRow(o, arr, group));
                    else if (c is JsonObject g)
                        Collect(g, g["Name"] is JsonValue gv && gv.TryGetValue<string>(out var gn) ? gn : group, found);
                    else Collect(c, group, found);
                }
                break;
            case JsonObject obj:
                foreach (var kv in obj.ToList()) Collect(kv.Value, group, found);
                break;
        }
    }

    /// <summary>中に「名前つきでアイテムを持つもの」の一覧がある(= グループ)</summary>
    static bool HasNamedChildren(JsonObject o) => o.Any(kv => kv.Value is JsonArray a && a.OfType<JsonObject>().Any(c => c["Name"] != null && Ymm4TemplateRow.CountItems(c) > 0));

    public void Load()
    {
        Items.Clear();
        doc = null;
        IsDirty = false;
        FilePath = target == null ? null : FindFile(target.Dir);
        if (FilePath == null)
        {
            Message = target == null ? "YMM4 が選ばれていません" : "テンプレートの設定ファイルが見つかりません(YMM4 でテンプレートを1つ保存すると作られます)";
            Raise(nameof(HasNoItems));
            return;
        }
        try
        {
            doc = JsonNode.Parse(File.ReadAllText(FilePath));
            var found = new List<Ymm4TemplateRow>();
            Collect(doc, "", found);
            foreach (var r in found) { r.Renamed += () => IsDirty = true; Items.Add(r); }
            Message = $"{found.Count} 個のテンプレートが見つかりました({File.GetLastWriteTime(FilePath):yyyy/MM/dd HH:mm} 更新)";
        }
        catch (Exception ex) { Message = "読めませんでした: " + ex.Message; }
        Raise(nameof(HasNoItems));
    }

    // ---------------------------------------------------------------- 書き込み
    bool CheckClosed()
    {
        if (!Ymm4.IsRunning) return true;
        return MessageBox.Show("YMM4 が起動しています。YMM4 は閉じる時に設定を書き込むため、ここでの変更が消えることがあります。YMM4 を閉じてから保存するのがおすすめです。このまま保存しますか?",
            "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    static void WriteWithBackup(string path, JsonNode doc)
    {
        var bak = path + $".manager-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (File.Exists(path)) File.Copy(path, bak, true);
        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".manager-backup-*").OrderByDescending(f => f).Skip(5))
            try { File.Delete(old); } catch { }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, doc.ToJsonString(WriteOpts));
        File.Move(tmp, path, true);
    }

    void Save()
    {
        if (doc == null || FilePath == null || !CheckClosed()) return;
        try
        {
            WriteWithBackup(FilePath, doc);
            Message = "保存しました(前のファイルは、同じ場所に manager-backup として残しています)";
            Load();
        }
        catch (Exception ex) { Message = "保存できませんでした: " + ex.Message; }
    }

    void DeleteChecked()
    {
        var list = Items.Where(i => i.IsChecked).ToList();
        if (MessageBox.Show($"{list.Count} 個のテンプレートを削除します(「保存」を押すまでは、ファイルは変わりません)。よろしいですか?", "YMM4 マネージャー",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var r in list) { r.Parent.Remove(r.Node); Items.Remove(r); }
        IsDirty = true;
        Raise(nameof(HasNoItems));
    }

    void Export()
    {
        var list = Items.Where(i => i.IsChecked).ToList();
        var dlg = new SaveFileDialog { Title = "テンプレートを書き出す", Filter = "YMM4テンプレート(JSON)|*.json", FileName = list.Count == 1 ? list[0].Name : "YMM4テンプレート" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var arr = new JsonArray(list.Select(r => (JsonNode?)r.Node.DeepClone()).ToArray());
            File.WriteAllText(dlg.FileName, new JsonObject { ["YMM4ManagerTemplates"] = arr }.ToJsonString(WriteOpts));
            Message = $"{list.Count} 個を書き出しました: {dlg.FileName}";
        }
        catch (Exception ex) { Message = "書き出せませんでした: " + ex.Message; }
    }

    /// <summary>テンプレートを入れる一覧(いまのテンプレートが入っている一覧。無ければ、ファイルの中でいちばん上の一覧)</summary>
    static JsonArray? TargetArray(JsonNode? doc, IEnumerable<Ymm4TemplateRow> rows)
        => rows.FirstOrDefault()?.Parent ?? (doc as JsonArray) ?? (doc as JsonObject)?.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault();

    void Import()
    {
        var dlg = new OpenFileDialog { Title = "読み込むテンプレート(書き出した JSON)を選んでください", Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(dlg.FileName));
            var incoming = (node?["YMM4ManagerTemplates"] as JsonArray)?.OfType<JsonObject>().ToList();
            if (incoming == null)
            {
                var found = new List<Ymm4TemplateRow>();
                Collect(node, "", found);
                incoming = found.Select(f => f.Node).ToList();
            }
            if (incoming.Count == 0) { Message = "テンプレートが見つかりませんでした"; return; }
            var arr = TargetArray(doc, Items) ?? throw new InvalidOperationException("テンプレートを入れる場所が分かりません(YMM4 でテンプレートを1つ保存してから試してください)");
            foreach (var t in incoming) arr.Add(t.DeepClone());
            var keepDirty = true;
            var found2 = new List<Ymm4TemplateRow>();
            Collect(doc, "", found2);
            Items.Clear();
            foreach (var r in found2) { r.Renamed += () => IsDirty = true; Items.Add(r); }
            IsDirty = keepDirty;
            Message = $"{incoming.Count} 個を読み込みました。「保存」を押すと YMM4 に反映されます";
            Raise(nameof(HasNoItems));
        }
        catch (Exception ex) { Message = "読み込めませんでした: " + ex.Message; }
    }

    void CopyTo()
    {
        var dest = copyTarget;
        var list = Items.Where(i => i.IsChecked).ToList();
        if (dest == null || list.Count == 0) return;
        if (Ymm4.IsRunning && !CheckClosed()) return;
        try
        {
            var path = FindFile(dest.Dir);
            if (path == null)
            {
                // コピー先にまだテンプレートが無い: いまのファイルを、選んだものだけにして置く
                if (FilePath == null || doc == null) return;
                var rel = Path.GetRelativePath(Path.Combine(target!.Dir, "user", "setting"), FilePath);
                path = Path.Combine(dest.Dir, "user", "setting", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var copy = doc.DeepClone();
                var rows = new List<Ymm4TemplateRow>();
                Collect(copy, "", rows);
                var keep = list.Select(l => l.Node.ToJsonString()).ToHashSet();
                foreach (var r in rows.Where(r => !keep.Contains(r.Node.ToJsonString()))) r.Parent.Remove(r.Node);
                WriteWithBackup(path, copy);
            }
            else
            {
                var other = JsonNode.Parse(File.ReadAllText(path));
                var rows = new List<Ymm4TemplateRow>();
                Collect(other, "", rows);
                var arr = TargetArray(other, rows) ?? throw new InvalidOperationException("コピー先のファイルの形が分かりません");
                foreach (var r in list) arr.Add(r.Node.DeepClone());
                WriteWithBackup(path, other!);
            }
            Message = $"{list.Count} 個のテンプレートを「{dest.Name}」へコピーしました";
            main.Toast(Message);
        }
        catch (Exception ex) { Message = "コピーできませんでした: " + ex.Message; }
    }
}
