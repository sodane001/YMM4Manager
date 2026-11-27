using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Input;
using YmmExplorer;

namespace YMM4Manager;

/// <summary>
/// 一覧や組み合わせの設定(JSON)を、文字の構文ではなく、入力欄・チェックボックス・行の追加/削除で編集するための1項目。
/// 一覧(配列)・組み合わせ(オブジェクト)は、中に子の項目を持つ
/// </summary>
public sealed class JsonEditNode : NotifyBase
{
    /// <summary>string / number / bool / array / object / null(まだ設定していない)</summary>
    public string Kind { get; private set; }
    public JsonEditNode? Parent { get; private set; }
    /// <summary>どの設定の中か(表示名を決めるのに使う)</summary>
    public string SettingKey { get; }
    /// <summary>設定の C# の型(新しい行を足す時の形を決めるのに使う)</summary>
    public string SettingType { get; }
    /// <summary>キーを自由に足し引きできる組み合わせ(Dictionary)</summary>
    public bool IsDict { get; private set; }
    bool wasNull;
    bool isInteger;

    JsonEditNode(string kind, string settingKey, string settingType, JsonEditNode? parent)
    {
        Kind = kind; SettingKey = settingKey; SettingType = settingType; Parent = parent;
        AddCommand = new RelayCommand(_ => Add());
        RemoveCommand = new RelayCommand(_ => Parent?.RemoveChild(this));
        MoveUpCommand = new RelayCommand(_ => Parent?.MoveChild(this, -1), _ => Parent is { Kind: "array" } p && p.Children.IndexOf(this) > 0);
        CreateCommand = new RelayCommand(_ => Create());
    }

    // ---------------------------------------------------------------- 作る
    public static JsonEditNode BuildRoot(JsonNode? node, string settingKey, string settingType)
    {
        var root = Build(node, settingKey, settingType, null, null, settingType.StartsWith("Dictionary", StringComparison.Ordinal));
        if (node == null) root.Kind = "null";
        return root;
    }

    static JsonEditNode Build(JsonNode? node, string settingKey, string settingType, JsonEditNode? parent, string? key, bool isDict)
    {
        JsonEditNode n;
        switch (node)
        {
            case JsonArray arr:
                n = new JsonEditNode("array", settingKey, settingType, parent);
                foreach (var c in arr) n.Children.Add(Build(c, settingKey, settingType, n, null, false));
                break;
            case JsonObject obj:
                n = new JsonEditNode("object", settingKey, settingType, parent) { IsDict = isDict };
                foreach (var (k, v) in obj) n.Children.Add(Build(v, settingKey, settingType, n, k, false));
                break;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                n = new JsonEditNode("bool", settingKey, settingType, parent) { boolValue = b };
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                n = new JsonEditNode("string", settingKey, settingType, parent) { text = s };
                break;
            case JsonValue v:
                n = new JsonEditNode("number", settingKey, settingType, parent) { text = v.ToJsonString() };
                n.isInteger = v.TryGetValue<long>(out _);
                break;
            default:
                // 中の null(未設定の文字など)は、空の文字として出す
                n = new JsonEditNode("string", settingKey, settingType, parent) { wasNull = true };
                break;
        }
        n.key = key ?? "";
        return n;
    }

    // ---------------------------------------------------------------- 表示
    public bool IsValue => Kind is "string" or "number";
    public bool IsBool => Kind == "bool";
    public bool IsContainer => Kind is "array" or "object";
    public bool IsUnset => Kind == "null";
    public bool CanAdd => Kind == "array" || (Kind == "object" && IsDict);
    public bool CanRemove => Parent != null && (Parent.Kind == "array" || Parent.IsDict);
    public bool CanMoveUp => Parent?.Kind == "array";
    public bool ShowKey => Parent?.Kind == "object" && !Parent.IsDict;
    public bool KeyEditable => Parent?.Kind == "object" && Parent.IsDict;
    public bool ShowIndex => Parent?.Kind == "array";
    public string IndexText => Parent == null ? "" : $"{Parent.Children.IndexOf(this) + 1}.";
    public string AddText => Kind == "array" ? "＋ 行を足す" : "＋ 項目を足す";

    string key = "";
    public string Key { get => key; set { if (Set(ref key, value ?? "")) { Raise(nameof(KeyLabel)); Raise(nameof(KeyHint)); Changed(); } } }
    public string KeyLabel => JsonEditNames.KeyLabel(SettingKey, key);
    /// <summary>キーを自由に書ける時の、日本語の説明(「1」→「ウィンドウ1」など)</summary>
    public string KeyHint => JsonEditNames.DictKeyHint(SettingKey, key);

    string text = "";
    public string Text { get => text; set { if (Set(ref text, value ?? "")) { wasNull = false; Raise(nameof(Hint)); Validate(); Changed(); } } }
    bool boolValue;
    public bool BoolValue { get => boolValue; set { if (Set(ref boolValue, value)) Changed(); } }
    public string Hint => JsonEditNames.ValueHint(SettingKey, Parent?.IsDict == true ? Parent.SettingKey : "", text);
    public bool HasHint => Hint.Length > 0;
    string error = "";
    public string Error { get => error; private set => Set(ref error, value); }

    public ObservableCollection<JsonEditNode> Children { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand CreateCommand { get; }

    /// <summary>中のどこかが変わった(いちばん上へ伝える)</summary>
    public event Action? ChangedEvent;
    void Changed()
    {
        Raise(nameof(HasHint));
        if (Parent != null) Parent.Changed();
        else ChangedEvent?.Invoke();
    }

    void Validate()
    {
        if (Kind != "number") { Error = ""; return; }
        Error = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "" : "数値を入れてください";
    }

    public bool HasError => error.Length > 0 || Children.Any(c => c.HasError);

    // ---------------------------------------------------------------- 編集
    void RemoveChild(JsonEditNode c)
    {
        Children.Remove(c);
        foreach (var x in Children) x.Raise(nameof(IndexText));
        Changed();
    }

    void MoveChild(JsonEditNode c, int delta)
    {
        int i = Children.IndexOf(c), j = i + delta;
        if (i < 0 || j < 0 || j >= Children.Count) return;
        Children.Move(i, j);
        foreach (var x in Children) x.Raise(nameof(IndexText));
        CommandManager.InvalidateRequerySuggested();
        Changed();
    }

    /// <summary>新しい行・項目を足す(前の行と同じ形で、中身は空)</summary>
    void Add()
    {
        JsonNode? shape = Children.Count > 0 ? Blank(Children[^1].ToNode(out _)) : JsonEditNames.NewElement(SettingType);
        string? k = null;
        if (Kind == "object")
        {
            k = "新しい項目";
            for (int i = 2; Children.Any(c => c.key == k); i++) k = $"新しい項目{i}";
        }
        var n = Build(shape, SettingKey, SettingType, this, k, false);
        Children.Add(n);
        Changed();
    }

    /// <summary>まだ設定していない項目を、設定する(空の一覧・組み合わせを作る)</summary>
    void Create()
    {
        if (Kind != "null") return;
        var seed = JsonEditNames.Seed(SettingKey, SettingType);
        Kind = seed is JsonArray ? "array" : "object";
        IsDict = SettingType.StartsWith("Dictionary", StringComparison.Ordinal);
        Children.Clear();
        if (seed is JsonArray arr) foreach (var c in arr) Children.Add(Build(c, SettingKey, SettingType, this, null, false));
        else if (seed is JsonObject obj) foreach (var (k, v) in obj) Children.Add(Build(v, SettingKey, SettingType, this, k, false));
        foreach (var p in new[] { nameof(Kind), nameof(IsUnset), nameof(IsContainer), nameof(CanAdd), nameof(AddText), nameof(IsDict) }) Raise(p);
        Changed();
    }

    static JsonNode? Blank(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Key == "Visible" ? kv.Value?.DeepClone() : Blank(kv.Value)))),
        JsonArray => new JsonArray(),
        JsonValue v when v.TryGetValue<string>(out _) => JsonValue.Create(""),
        JsonValue v when v.TryGetValue<bool>(out var b) => JsonValue.Create(b),
        JsonValue => JsonValue.Create(0),
        _ => null,
    };

    // ---------------------------------------------------------------- JSON に戻す
    public JsonNode? ToNode(out bool ok)
    {
        ok = true;
        switch (Kind)
        {
            case "null": return null;
            case "bool": return JsonValue.Create(boolValue);
            case "string": return wasNull && text.Length == 0 ? null : JsonValue.Create(text);
            case "number":
                if (isInteger && long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return isInteger && d == Math.Floor(d) ? JsonValue.Create((long)d) : JsonValue.Create(d);
                ok = false; return null;
            case "array":
            {
                var arr = new JsonArray();
                foreach (var c in Children) { arr.Add(c.ToNode(out var cok)); ok &= cok; }
                return arr;
            }
            default:
            {
                var obj = new JsonObject();
                foreach (var c in Children)
                {
                    var v = c.ToNode(out var cok);
                    ok &= cok;
                    if (c.key.Length == 0 || obj.ContainsKey(c.key)) { ok = false; continue; }   // キーが空・重なっている
                    obj[c.key] = v;
                }
                return obj;
            }
        }
    }
}

/// <summary>JSON の設定に出てくる名前を、日本語にする</summary>
public static class JsonEditNames
{
    static readonly Dictionary<string, string> Keys = new()
    {
        ["Name"] = "名前", ["Visible"] = "表示する", ["StartPath"] = "開始フォルダー",
        ["Search"] = "検索の文字", ["Ext"] = "拡張子", ["Date"] = "日付", ["Sub"] = "サブフォルダーも", ["Tag"] = "タグ",
    };

    static readonly Dictionary<string, string> TabKinds = new()
    {
        ["Explorer"] = "エクスプローラー", ["Template"] = "テンプレート", ["Preset"] = "プリセット", ["Shape"] = "図形",
        ["VideoEffect"] = "映像エフェクト", ["AudioEffect"] = "音声エフェクト", ["Text"] = "テキスト", ["YmmSettings"] = "YMM4設定",
        ["Material"] = "素材データベース", ["Item"] = "アイテム", ["Shortcut"] = "ショートカット", ["AiEdit"] = "AIエディット",
    };

    static readonly Dictionary<string, string> DetailFields = new()
    {
        ["type"] = "種類", ["size"] = "サイズ", ["modified"] = "更新日時", ["created"] = "作成日時", ["accessed"] = "アクセス日時",
        ["ext"] = "拡張子", ["location"] = "場所", ["path"] = "フルパス", ["attrs"] = "属性", ["cloud"] = "クラウド",
        ["dimensions"] = "画像・動画のサイズ(解像度)", ["duration"] = "再生時間", ["framerate"] = "フレームレート",
        ["bitrate"] = "ビットレート", ["audio"] = "音声(サンプルレート・チャンネル)", ["pages"] = "PDFのページ数",
    };

    static readonly Dictionary<string, string> Providers = new()
    {
        ["claude"] = "Claude", ["gemini"] = "Gemini", ["openai"] = "ChatGPT(OpenAI)", ["compatible"] = "OpenAI 互換(ローカルなど)", ["localstudio"] = "Local AI Studio(このPCのAI)",
    };

    public static string KeyLabel(string setting, string key) => Keys.TryGetValue(key, out var v) ? v : key;

    /// <summary>自由なキー(「1」「claude」など)の説明</summary>
    public static string DictKeyHint(string setting, string key)
    {
        if (setting.StartsWith("Window", StringComparison.Ordinal) && int.TryParse(key, out var w))
            return w == 1 ? "YmmExplorer(1つ目のウィンドウ)" : $"YmmExplorer {w}(ウィンドウ)";
        if (setting.StartsWith("Explorer", StringComparison.Ordinal) && int.TryParse(key, out var t)) return $"エクスプローラー{t} のタブ";
        if (Providers.TryGetValue(key, out var p)) return p;
        return "";
    }

    static readonly Regex TabRef = new(@"^(\w+):(\d+)$");

    /// <summary>値の説明(「AiEdit:1」→「AIエディット 1」など)</summary>
    public static string ValueHint(string setting, string _, string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var m = TabRef.Match(value);
        if (m.Success && TabKinds.TryGetValue(m.Groups[1].Value, out var kind)) return $"→ {kind} {m.Groups[2].Value}";
        if (setting == "DetailFields" && DetailFields.TryGetValue(value, out var f)) return "→ " + f;
        if (setting == "ExplorerViewModes" && int.TryParse(value, out var vm))
            return vm switch { 0 => "→ 詳細", 1 => "→ 一覧", _ => $"→ アイコン(大きさ {vm - 1})" };
        return "";
    }

    /// <summary>行を足す時の形(まだ1行も無い時)</summary>
    public static JsonNode? NewElement(string type)
    {
        if (type.Contains("TabConfig")) return new JsonObject { ["Name"] = "", ["Visible"] = true, ["StartPath"] = "" };
        if (type.Contains("ExplorerFilterState")) return new JsonObject { ["Search"] = null, ["Ext"] = null, ["Date"] = 0, ["Sub"] = false, ["Tag"] = null };
        if (type.Contains("List<string>>")) return new JsonArray();
        if (type.Contains(", int>")) return JsonValue.Create(0);
        return JsonValue.Create("");
    }

    /// <summary>まだ設定していない項目を「設定する」時の、はじめの中身</summary>
    public static JsonNode Seed(string setting, string type)
    {
        if (setting == "DetailFields")
            return new JsonArray("type", "size", "modified", "location", "cloud", "dimensions", "duration", "pages");
        return type.StartsWith("Dictionary", StringComparison.Ordinal) ? new JsonObject() : new JsonArray();
    }
}
