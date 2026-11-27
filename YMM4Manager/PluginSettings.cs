using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using YmmExplorer;

namespace YMM4Manager;

/// <summary>YmmExplorer の設定1項目(画面の1行)</summary>
public sealed class PluginSettingEntry : NotifyBase
{
    readonly string loadedJson;

    public PluginSettingEntry(PluginSettingInfo info, JsonNode? value, int order)
    {
        Info = info;
        Order = order;
        var gi = Array.IndexOf(PluginSettingsCatalog.GroupOrder, info.Group);
        GroupIndex = gi >= 0 ? gi : PluginSettingsCatalog.GroupOrder.Length;
        loadedJson = value?.ToJsonString() ?? "null";
        SetFromNode(value);
    }

    public PluginSettingInfo Info { get; }
    public string Key => Info.Key;
    public string Label => Info.Label;
    public string Group => Info.Group;
    public string Note => Info.Note;
    public bool HasNote => Info.Note.Length > 0;
    public int Order { get; }
    public int GroupIndex { get; }

    public bool IsBool => Info.Kind == "bool";
    public bool IsChoice => Info.Kind == "choice";
    public bool IsJson => Info.Kind == "json";
    /// <summary>一覧・組み合わせを、入力欄で編集する(RawMode の時は JSON の文字で)</summary>
    public bool IsTree => IsJson && !rawMode;
    public bool IsRaw => IsJson && rawMode;
    bool rawMode;
    public bool RawMode
    {
        get => rawMode;
        set
        {
            if (rawMode == value || !IsJson) return;
            if (value)
            {
                // 入力欄 → JSON の文字
                var n = Tree?.ToNode(out _);
                textValue = n == null ? "" : n.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                Raise(nameof(TextValue));
            }
            else
            {
                // JSON の文字 → 入力欄(正しくない時は、文字のまま)
                JsonNode? n;
                try { n = textValue.Trim().Length == 0 ? null : JsonNode.Parse(textValue); }
                catch { Error = "JSON の形が正しくないため、入力欄に戻せません"; Raise(nameof(RawMode)); return; }
                SetTree(n);
            }
            rawMode = value;
            Raise(nameof(RawMode)); Raise(nameof(IsTree)); Raise(nameof(IsRaw));
            Changed();
        }
    }
    JsonEditNode? tree;
    public JsonEditNode? Tree { get => tree; private set => Set(ref tree, value); }
    void SetTree(JsonNode? n)
    {
        if (tree != null) tree.ChangedEvent -= Changed;
        Tree = JsonEditNode.BuildRoot(n?.DeepClone(), Info.Key, Info.Type);
        Tree.ChangedEvent += Changed;
    }
    public bool IsSecret => Info.Kind == "secret";
    public bool IsLine => Info.Kind is "int" or "double" or "text";
    public string[] Choices => Info.Choices ?? Array.Empty<string>();

    bool boolValue;
    public bool BoolValue { get => boolValue; set { if (Set(ref boolValue, value)) Changed(); } }
    string textValue = "";
    public string TextValue { get => textValue; set { if (Set(ref textValue, value ?? "")) Changed(); } }
    int choiceIndex;
    public int ChoiceIndex { get => choiceIndex; set { if (Set(ref choiceIndex, value)) Changed(); } }
    bool isNull;

    string error = "";
    public string Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => error.Length > 0;

    public string SecretText => isNull || textValue.Length == 0 ? "(保存していません)" : $"{textValue}(暗号化して保存しています。キーの変更は YMM4 の AIエディットで行ってください)";

    void SetFromNode(JsonNode? v)
    {
        isNull = v == null;
        switch (Info.Kind)
        {
            case "bool": boolValue = v is JsonValue b && b.TryGetValue<bool>(out var bb) && bb; break;
            case "choice": choiceIndex = v is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0; break;
            case "int":
            case "double": textValue = v?.ToJsonString() ?? ""; break;
            case "text": textValue = v is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : ""; break;
            case "secret": textValue = v is JsonObject o ? string.Join("・", o.Select(kv => kv.Key)) + $"({o.Count} 個)" : ""; break;
            default:
                textValue = v == null ? "" : v.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                SetTree(v);
                break;
        }
        foreach (var n in new[] { nameof(BoolValue), nameof(TextValue), nameof(ChoiceIndex), nameof(SecretText), nameof(Tree) }) Raise(n);
        Changed();
    }

    void Changed()
    {
        Validate();
        Raise(nameof(IsChanged));
        Raise(nameof(IsDefault));
        Raise(nameof(StateText));
        ValueChanged?.Invoke();
    }

    public event Action? ValueChanged;

    /// <summary>今の値を JSON にする。おかしな値なら、ok = false</summary>
    public JsonNode? ToNode(out bool ok)
    {
        ok = true;
        try
        {
            switch (Info.Kind)
            {
                case "bool": return JsonValue.Create(boolValue);
                case "choice": return JsonValue.Create(choiceIndex);
                case "int":
                    if (int.TryParse(textValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return JsonValue.Create(i);
                    ok = false; return null;
                case "double":
                    if (double.TryParse(textValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
                    ok = false; return null;
                case "text":
                    return isNull && textValue.Length == 0 ? null : JsonValue.Create(textValue);
                case "secret":
                    return JsonNode.Parse(loadedJson);
                default:
                    if (!rawMode && tree != null)
                    {
                        var n = tree.ToNode(out var tok);
                        ok = tok && !tree.HasError;
                        return n;
                    }
                    return textValue.Trim().Length == 0 ? null : JsonNode.Parse(textValue);
            }
        }
        catch { ok = false; return null; }
    }

    void Validate()
    {
        ToNode(out var ok);
        Error = ok ? "" : Info.Kind switch { "int" => "整数を入れてください", "double" => "数値を入れてください", _ => rawMode ? "JSON の形が正しくありません" : "正しくない値(数値の欄・空や重なった名前)があります" };
    }

    string CurrentJson => ToNode(out var ok) is var n && ok ? (n?.ToJsonString() ?? "null") : "?";
    static string Normalize(string json) { try { return JsonNode.Parse(json)?.ToJsonString() ?? "null"; } catch { return json; } }
    public bool IsChanged => CurrentJson != Normalize(loadedJson);
    public bool IsDefault => CurrentJson == Normalize(Info.DefaultJson);
    public string StateText => HasError ? "" : IsChanged ? "● 変更あり(未保存)" : IsDefault ? "" : "初期値から変えています";

    public void ResetToDefault()
    {
        isNull = false;
        SetFromNode(JsonNode.Parse(Info.DefaultJson));
    }

    public void ClearSecret()
    {
        isNull = true;
        textValue = "";
        Raise(nameof(SecretText));
        Changed();
    }
}

/// <summary>「プラグイン設定」ページ: YmmExplorer の設定(settings.json)を、ここからすべて変えられる</summary>
public sealed class PluginSettingsVm : NotifyBase
{
    readonly MainVm main;
    JsonObject root = new();

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YmmExplorer", "settings.json");

    public PluginSettingsVm(MainVm main)
    {
        this.main = main;
        View = CollectionViewSource.GetDefaultView(Entries);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PluginSettingEntry.Group)));
        View.SortDescriptions.Add(new SortDescription(nameof(PluginSettingEntry.GroupIndex), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(PluginSettingEntry.Order), ListSortDirection.Ascending));
        View.Filter = o => o is PluginSettingEntry e && Match(e);

        ReloadCommand = new RelayCommand(_ =>
        {
            if (ChangedCount > 0 && MessageBox.Show("保存していない変更があります。捨てて読み込み直しますか?", "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Reload();
        });
        SaveCommand = new RelayCommand(_ => Save(), _ => ChangedCount > 0 && ErrorCount == 0);
        ResetCommand = new RelayCommand(p => { if (p is PluginSettingEntry e) e.ResetToDefault(); });
        ClearSecretCommand = new RelayCommand(p =>
        {
            if (p is PluginSettingEntry e && MessageBox.Show("保存している APIキーをすべて消しますか?(保存すると消えます)", "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                e.ClearSecret();
        });
        ResetGroupCommand = new RelayCommand(_ =>
        {
            var targets = Entries.Where(e => View.Filter(e) && !e.IsSecret).ToList();
            if (targets.Count == 0) return;
            if (MessageBox.Show($"いま表示している {targets.Count} 項目を初期値に戻しますか?(「保存」を押すまでは反映されません)", "YMM4 マネージャー",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var e in targets) e.ResetToDefault();
        });
        OpenFolderCommand = new RelayCommand(_ =>
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        });
    }

    public ObservableCollection<PluginSettingEntry> Entries { get; } = new();
    public ICollectionView View { get; }

    public IReadOnlyList<string> Groups { get; } = new[] { "すべて" }.Concat(PluginSettingsCatalog.GroupOrder).ToList();
    string group = "すべて";
    public string Group { get => group; set { if (Set(ref group, value ?? "すべて")) View.Refresh(); } }
    string search = "";
    public string Search { get => search; set { if (Set(ref search, value ?? "")) View.Refresh(); } }
    bool changedOnly;
    public bool ChangedOnly { get => changedOnly; set { if (Set(ref changedOnly, value)) View.Refresh(); } }
    bool customizedOnly;
    /// <summary>初期値から変えている項目だけ</summary>
    public bool CustomizedOnly { get => customizedOnly; set { if (Set(ref customizedOnly, value)) View.Refresh(); } }

    bool Match(PluginSettingEntry e)
    {
        if (group != "すべて" && e.Group != group) return false;
        if (changedOnly && !e.IsChanged) return false;
        if (customizedOnly && e.IsDefault) return false;
        if (search.Trim().Length == 0) return true;
        var s = search.Trim();
        return e.Label.Contains(s, StringComparison.OrdinalIgnoreCase) || e.Key.Contains(s, StringComparison.OrdinalIgnoreCase)
               || e.Note.Contains(s, StringComparison.OrdinalIgnoreCase) || e.Group.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    string message = "";
    public string Message { get => message; set => Set(ref message, value); }
    public int ChangedCount => Entries.Count(e => e.IsChanged);
    public int ErrorCount => Entries.Count(e => e.HasError);
    public string ChangedText => ErrorCount > 0 ? $"⚠ 正しくない値が {ErrorCount} 個あります" : ChangedCount > 0 ? $"未保存の変更 {ChangedCount} 個" : "変更はありません";
    public bool FileExists => File.Exists(FilePath);

    public ICommand ReloadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ClearSecretCommand { get; }
    public ICommand ResetGroupCommand { get; }
    public ICommand OpenFolderCommand { get; }

    bool loaded;
    /// <summary>ページを開いた時(まだ読んでいないか、変更が無ければ読み直す)</summary>
    public void OnShown()
    {
        if (!loaded || ChangedCount == 0) Reload();
    }

    public void Reload()
    {
        loaded = true;
        try
        {
            root = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject() : new JsonObject();
            Message = File.Exists(FilePath)
                ? $"{FilePath} を読み込みました({File.GetLastWriteTime(FilePath):yyyy/MM/dd HH:mm} 更新)"
                : "YmmExplorer の設定ファイルがまだありません。初期値を表示しています(保存すると作られます)";
        }
        catch (Exception ex)
        {
            root = new JsonObject();
            Message = "設定ファイルを読めませんでした: " + ex.Message;
        }

        foreach (var e in Entries) e.ValueChanged -= OnEntryChanged;
        Entries.Clear();
        int order = 0;
        foreach (var info in PluginSettingsCatalog.All)
        {
            var node = root.TryGetPropertyValue(info.Key, out var v) ? v?.DeepClone() : JsonNode.Parse(info.DefaultJson);
            Entries.Add(new PluginSettingEntry(info, node, order++));
        }
        // 一覧に無い項目(新しい版で増えたものなど)も「その他」として出す
        foreach (var (key, v) in root)
        {
            if (PluginSettingsCatalog.Find(key) != null) continue;
            var kind = v switch
            {
                JsonValue jv when jv.TryGetValue<bool>(out _) => "bool",
                JsonValue jv when jv.TryGetValue<int>(out _) => "int",
                JsonValue jv when jv.TryGetValue<double>(out _) => "double",
                JsonValue jv when jv.TryGetValue<string>(out _) => "text",
                _ => "json",
            };
            var info = new PluginSettingInfo(key, key, "その他", kind, v?.ToJsonString() ?? "null", null, "(この版の YMM4 マネージャーが知らない項目です)");
            Entries.Add(new PluginSettingEntry(info, v?.DeepClone(), order++));
        }
        foreach (var e in Entries) e.ValueChanged += OnEntryChanged;
        View.Refresh();
        RaiseCounts();
        Raise(nameof(FileExists));
    }

    void OnEntryChanged()
    {
        RaiseCounts();
        if (changedOnly || customizedOnly) Application.Current?.Dispatcher.BeginInvoke(new Action(() => View.Refresh()), System.Windows.Threading.DispatcherPriority.Background);
    }

    void RaiseCounts()
    {
        Raise(nameof(ChangedCount)); Raise(nameof(ErrorCount)); Raise(nameof(ChangedText));
        CommandManager.InvalidateRequerySuggested();
    }

    void Save()
    {
        if (ErrorCount > 0) { Message = "正しくない値があるため、保存できません"; return; }
        if (Ymm4.IsRunning &&
            MessageBox.Show("YMM4 が起動しています。\n\nYMM4 の中の YmmExplorer は、起動した時に設定を読み込み、設定を変えるたびに上書き保存します。" +
                            "このまま保存すると、YMM4 を再起動するまで反映されず、YMM4 側で設定を変えると、ここで変えた内容が消えることがあります。\n\n" +
                            "YMM4 を閉じてから保存するのがおすすめです。このまま保存しますか?",
                            "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            // 前の設定をとっておく(最新の5つ)
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, Path.Combine(dir, $"settings.manager-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                foreach (var old in Directory.GetFiles(dir, "settings.manager-backup-*.json").OrderByDescending(f => f).Skip(5))
                    try { File.Delete(old); } catch { }
            }
            var next = root.DeepClone().AsObject();
            foreach (var e in Entries)
            {
                var node = e.ToNode(out var ok);
                if (!ok) throw new InvalidOperationException($"「{e.Label}」の値が正しくありません");
                next[e.Key] = node;
            }
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, next.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, FilePath, true);
            var count = ChangedCount;
            Reload();
            Message = $"{count} 項目を保存しました。" + (Ymm4.IsRunning ? "YMM4 を再起動すると反映されます" : "次に YMM4 を起動した時から使われます");
            main.Toast("プラグインの設定を保存しました");
        }
        catch (Exception ex) { Message = "保存できませんでした: " + ex.Message; }
    }
}
