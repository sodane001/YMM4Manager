using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YmmExplorer;

namespace YMM4Manager;

public sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public string Text { get; init; } = "";
    public string Attached { get; init; } = "";
    public bool IsUser => Role == "user";
    public bool IsAssistant => Role == "assistant";
    public bool IsError => Role == "error";
    public bool HasAttached => Attached.Length > 0;
}

public sealed class ChatAttachment
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 「AI質問」ページ。YMM4 マネージャーから AI に質問できる。
/// 専用ストレージ・素材データベース・プロジェクト・YMM4 の情報を一緒に送り、選んだプロジェクト(.ymmp)の中身も読ませられる。
/// AI の種類・APIキー・モデルは、YMM4 の中の YmmExplorer の「AIエディット」と共有する
/// </summary>
public sealed class AiChatVm : NotifyBase
{
    readonly MainVm main;
    readonly AppSettings settings = AppSettings.Instance;
    CancellationTokenSource? cts;

    public AiChatVm(MainVm main)
    {
        this.main = main;
        SendCommand = new RelayCommand(async _ => await SendAsync(), _ => !busy && (input.Trim().Length > 0 || Attachments.Count > 0));
        CancelCommand = new RelayCommand(_ => cts?.Cancel(), _ => busy);
        NewChatCommand = new RelayCommand(_ => { Messages.Clear(); chat = null; Raise(nameof(HasNoMessages)); Raise(nameof(ChatTitle)); Status = "新しい会話を始めました"; });
        ContinueChatCommand = new RelayCommand(p => { if (p is AiChatRecord r) ContinueChat(r); }, _ => !busy);
        DeleteChatCommand = new RelayCommand(p =>
        {
            if (p is not AiChatRecord r) return;
            if (MessageBox.Show($"会話「{r.Title}」の記録を削除しますか?", "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AiChatLog.Delete(r);
            if (chat?.Id == r.Id) chat = null;
            LoadHistory();
        });
        OpenMemoryCommand = new RelayCommand(_ => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AiMemory.EnsureFile()) { UseShellExecute = true }); } catch { } });
        ClearMemoryCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show("AI が覚えていることを、すべて忘れさせますか?\n(会話の記録は消えません)", "YMM4 マネージャー", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            AiMemory.Clear(); Raise(nameof(MemoryInfo)); Status = "AI の記憶を消しました";
        });
        OpenChatFolderCommand = new RelayCommand(_ => { Directory.CreateDirectory(AiChatLog.Dir); Ymm4.OpenFolder(AiChatLog.Dir); });
        CopyCommand = new RelayCommand(p => { if (p is ChatMessage m) try { Clipboard.SetText(m.Text); Status = "コピーしました"; } catch { } });
        RemoveAttachmentCommand = new RelayCommand(p => { if (p is ChatAttachment a) { Attachments.Remove(a); Raise(nameof(HasAttachments)); } });
        ToggleSettingsCommand = new RelayCommand(_ => ShowSettings = !ShowSettings);
        OpenKeyPageCommand = new RelayCommand(_ => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Provider.KeyUrl) { UseShellExecute = true }); } catch { } });
        FetchModelsCommand = new RelayCommand(async _ => await FetchModelsAsync(false));
        QuickCommand = new RelayCommand(async p => { if (p is string s) { Input = s; await SendAsync(); } }, _ => !busy);
        AiClient.Retrying += msg => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (busy) Status = msg; }));
        AiClient.Progress += msg => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (busy) Status = msg; }));
        showSettings = ApiKey.Length == 0 && AiClient.NeedsKey(ProviderId);
        ResetModels();
    }

    // ---------------------------------------------------------------- AI の設定(YmmExplorer と共有)
    public IReadOnlyList<AiProviderInfo> Providers => AiClient.Providers;
    string ProviderId => PluginJson.GetString("AiProvider") is { Length: > 0 } p ? p : "claude";
    public AiProviderInfo Provider => Providers.FirstOrDefault(p => p.Id == ProviderId) ?? Providers[0];
    public int ProviderIndex
    {
        get => Math.Max(0, Providers.ToList().FindIndex(p => p.Id == ProviderId));
        set
        {
            if (value < 0 || value >= Providers.Count || Providers[value].Id == ProviderId) return;
            PluginJson.Set("AiProvider", Providers[value].Id);
            foreach (var n in new[] { nameof(ProviderIndex), nameof(Provider), nameof(Model), nameof(KeyState), nameof(IsCompatible), nameof(KeyHint), nameof(IsLocalStudio), nameof(ModelNote), nameof(HasModelNote), nameof(ModelBadge), nameof(CurrentAi) }) Raise(n);
            ResetModels();
        }
    }
    public bool IsCompatible => ProviderId == "compatible";
    /// <summary>Local AI Studio のモデルの性能・重さ(ほかの AI では空)</summary>
    public string ModelNote => AiClient.ModelNote(ProviderId, Model);
    public bool HasModelNote => ModelNote.Length > 0;
    /// <summary>性能・重さを1行で(画面の上に出す)</summary>
    public string ModelBadge => ModelNote.Split('\n')[0];
    ICommand? openLocalStudioCommand;
    /// <summary>Local AI Studio を開く(モデルのインストールなど)</summary>
    public ICommand OpenLocalStudioCommand => openLocalStudioCommand ??= new RelayCommand(async _ =>
    {
        try { Status = await LocalAiStudio.OpenAppAsync() ? "Local AI Studio を起動しました" : "Local AI Studio は起動中です(タスクバーから開けます)"; }
        catch (Exception ex) { Status = ex.Message; }
    });
    public bool IsLocalStudio => ProviderId == "localstudio";
    /// <summary>いま選んでいる AI とモデル(入力欄の下に出す)</summary>
    public string CurrentAi => $"送り先: {Provider.Name} ・ {(string.IsNullOrWhiteSpace(Model) ? "(モデル未選択)" : Model)}";

    /// <summary>AI がインターネットを検索・閲覧する(YMM4 の中の AIエディットと共通)</summary>
    public bool WebOn
    {
        get => PluginJson.Get("AiWebAccess") is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        set
        {
            PluginJson.Set("AiWebAccess", JsonValue.Create(value)); Raise(nameof(WebOn));
            Status = value ? (ProviderId switch
            {
                "claude" => "ネット検索をオンにしました。Claude が必要な時に検索・ページ閲覧をします(Claude の検索は、1回ごとに少し料金がかかります)",
                "gemini" => "ネット検索をオンにしました。Gemini が必要な時に Google 検索をします",
                "localstudio" => "ネット検索をオンにしました。Local AI Studio が検索・ページ閲覧をして、AI に渡します",
                _ => "ネット検索をオンにしました。このソフトが検索・ページ閲覧の道具を用意して、AI に使ってもらいます",
            }) : "ネット検索をオフにしました";
        }
    }
    public string KeyHint => Provider.KeyHint;
    public string ApiKey => SecretBox.Unprotect(PluginJson.GetMap("AiKeys", ProviderId));
    public void SaveKey(string key)
    {
        PluginJson.SetMap("AiKeys", ProviderId, SecretBox.Protect((key ?? "").Trim()));
        Raise(nameof(KeyState));
        Status = "APIキーを保存しました(YMM4 の中の AIエディットと共通です)";
        _ = FetchModelsAsync(true);
    }
    public string KeyState => ApiKey.Length > 0 ? "✓ APIキーを保存しています(このPCで暗号化。YMM4 の AIエディットと共通)" : !AiClient.NeedsKey(ProviderId) ? "" : "APIキーが未設定です";
    public string Model
    {
        get => PluginJson.GetMap("AiModels", ProviderId) is { Length: > 0 } m ? m : Provider.DefaultModel;
        set { var v = (value ?? "").Trim(); if (v.Length == 0 || v == Model) return; PluginJson.SetMap("AiModels", ProviderId, v); Raise(nameof(Model)); Raise(nameof(ModelNote)); Raise(nameof(HasModelNote)); Raise(nameof(ModelBadge)); Raise(nameof(CurrentAi)); }
    }
    public string BaseUrl
    {
        get => PluginJson.GetString("AiBaseUrl") ?? "http://localhost:11434/v1";
        set { PluginJson.Set("AiBaseUrl", (value ?? "").Trim()); Raise(nameof(BaseUrl)); }
    }
    public ObservableCollection<string> ModelChoices { get; } = new();
    readonly Dictionary<string, List<string>> fetched = new();

    void ResetModels()
    {
        ModelChoices.Clear();
        IEnumerable<string> src = fetched.TryGetValue(ProviderId, out var got) ? got : Provider.KnownModels;
        if (!src.Contains(Model)) ModelChoices.Add(Model);
        foreach (var m in src) ModelChoices.Add(m);
        Raise(nameof(Model));
        Raise(nameof(ModelNote));
        Raise(nameof(HasModelNote));
        Raise(nameof(ModelBadge)); Raise(nameof(CurrentAi));
        // 一覧を作り直すと、入力できる ComboBox は表示の文字を消してしまうので、少し後でもう一度出す
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => { Raise(nameof(Model)); Raise(nameof(CurrentAi)); }));
        if (!fetched.ContainsKey(ProviderId) && (ApiKey.Length > 0 || !AiClient.NeedsKey(ProviderId))) _ = FetchModelsAsync(true);
    }

    async Task FetchModelsAsync(bool quiet)
    {
        var id = ProviderId;
        try
        {
            using var c = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var list = await AiClient.ListModelsAsync(id, ApiKey, BaseUrl, c.Token);
            if (list.Count == 0) return;
            fetched[id] = list;
            if (id == ProviderId) ResetModels();
            if (!quiet) Status = $"{list.Count} 個のモデルが見つかりました";
        }
        catch (Exception ex) { if (!quiet) Status = "モデルの一覧を読めませんでした: " + ex.Message; }
    }

    bool showSettings;
    public bool ShowSettings { get => showSettings; set => Set(ref showSettings, value); }

    // ---------------------------------------------------------------- 一緒に送るもの
    public bool SendStorage { get => settings.AiSendStorage; set { settings.AiSendStorage = value; settings.Save(); Raise(nameof(SendStorage)); } }
    public bool SendMaterials { get => settings.AiSendMaterials; set { settings.AiSendMaterials = value; settings.Save(); Raise(nameof(SendMaterials)); } }
    public bool SendProjects { get => settings.AiSendProjects; set { settings.AiSendProjects = value; settings.Save(); Raise(nameof(SendProjects)); } }
    public bool SendYmm4 { get => settings.AiSendYmm4; set { settings.AiSendYmm4 = value; settings.Save(); Raise(nameof(SendYmm4)); } }

    /// <summary>中身を読ませるプロジェクト(.ymmp)</summary>
    public ObservableCollection<ProjectFile> ProjectChoices => main.Projects.Files;
    ProjectFile? project;
    public ProjectFile? Project { get => project; set { if (Set(ref project, value)) Raise(nameof(HasProject)); } }
    public bool HasProject => project != null;
    ICommand? clearProject;
    public ICommand ClearProjectCommand => clearProject ??= new RelayCommand(_ => Project = null);

    public ObservableCollection<ChatAttachment> Attachments { get; } = new();
    public bool HasAttachments => Attachments.Count > 0;
    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (File.Exists(p) && !Attachments.Any(a => string.Equals(a.Path, p, StringComparison.OrdinalIgnoreCase))) Attachments.Add(new ChatAttachment { Path = p });
        Raise(nameof(HasAttachments));
        CommandManager.InvalidateRequerySuggested();
    }

    public IReadOnlyList<string> QuickPrompts { get; } = new[]
    {
        "専用ストレージの素材を見て、動画づくりに足りなそうな素材を教えて",
        "素材データベースのタグの付け方を整理するアドバイスをして",
        "選んだプロジェクトの構成(テンポ・字幕・素材の使い方)の改善点を教えて",
        "YMM4 の動作を軽くするための設定やコツを教えて",
        "入れているプラグインで、できることをまとめて",
    };

    // ---------------------------------------------------------------- 会話の記録(専用ストレージの「AI会話」。YMM4 の AIエディットと共通)
    AiChatRecord? chat;
    public string ChatTitle => chat == null ? "" : "続きの会話: " + chat.Title;
    bool showHistory;
    public bool ShowHistory { get => showHistory; set { if (Set(ref showHistory, value) && value) LoadHistory(); } }
    public ObservableCollection<AiChatRecord> History { get; } = new();
    public bool HasNoHistory => History.Count == 0;
    public string ChatFolder => AiChatLog.Dir;
    public ICommand ContinueChatCommand { get; }
    public ICommand DeleteChatCommand { get; }
    public ICommand OpenChatFolderCommand { get; }
    public ICommand OpenMemoryCommand { get; }
    public ICommand ClearMemoryCommand { get; }
    /// <summary>AI が会話をまたいで覚える(YmmExplorer の AIエディットと共有。専用ストレージの「AI記憶」)</summary>
    public bool UseMemory { get => settings.AiUseMemory; set { settings.AiUseMemory = value; settings.Save(); Raise(nameof(UseMemory)); } }
    public string MemoryInfo => $"覚えていること {AiMemory.Load().Count} 件 ・ 保存先 {AiMemory.FilePath}";

    void LoadHistory()
    {
        History.Clear();
        foreach (var r in AiChatLog.List()) History.Add(r);
        Raise(nameof(HasNoHistory)); Raise(nameof(ChatFolder));
    }

    void ContinueChat(AiChatRecord r)
    {
        Messages.Clear();
        foreach (var m in r.Messages) Messages.Add(new ChatMessage { Role = m.Role, Text = m.Text, Attached = m.Attached });
        chat = r;
        ShowHistory = false;
        Status = $"「{r.Title}」の続きから話せます(これまでのやりとりを AI に送ります)";
        Raise(nameof(HasNoMessages)); Raise(nameof(ChatTitle));
        MessageAdded?.Invoke();
    }

    void SaveChat()
    {
        if (Messages.Count == 0) return;
        chat ??= new AiChatRecord { Source = "AI質問(YMM4 マネージャー)" };
        chat.Provider = Provider.Name;
        chat.Model = Model;
        if (chat.Project.Length == 0 && project != null) chat.Project = project.Name;
        chat.Messages = Messages.Select(m => new AiChatLine { Role = m.Role, Text = m.Text, Attached = m.Attached }).ToList();
        AiChatLog.Save(chat);
        Raise(nameof(ChatTitle));
    }

    // ---------------------------------------------------------------- 会話
    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public bool HasNoMessages => Messages.Count == 0;
    string input = "";
    public string Input { get => input; set { if (Set(ref input, value ?? "")) CommandManager.InvalidateRequerySuggested(); } }
    bool busy;
    public bool IsBusy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    string status = "";
    public string Status { get => status; set => Set(ref status, value); }
    public event Action? MessageAdded;

    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NewChatCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand OpenKeyPageCommand { get; }
    public ICommand FetchModelsCommand { get; }
    public ICommand QuickCommand { get; }

    string SystemPrompt() => BaseSystemPrompt() + (UseMemory ? "\n\n" + SafeMemoryPrompt() : "");
    string SafeMemoryPrompt() { try { return AiMemory.Prompt(chat?.Id, project?.Name ?? ""); } catch { return ""; } }

    static string BaseSystemPrompt() =>
        "あなたは「ゆっくりMovieMaker4」(YMM4)と、その管理ソフト「YMM4 マネージャー」・プラグイン「YmmExplorer」に詳しいアシスタントです。\n" +
        "ユーザーの質問と一緒に、YMM4専用ストレージの中身・素材データベース・プロジェクトの一覧・YMM4 の情報、選んだプロジェクトの内容が届くことがあります。\n" +
        "それらを根拠に、日本語で、具体的で実行しやすく答えてください。分からないことは推測と分かるように書いてください。\n" +
        "タイムラインへの配置や項目の変更は、YMM4 の中の YmmExplorer の「AIエディット」(操作の提案をオン)で行えることも、必要なら案内してください。" +
        "\n\n" + Ymm4Knowledge.Text +
        (AiStorage.Root != null ? "\n\n" + AiStorage.Instructions : "");

    async Task SendAsync()
    {
        var question = input.Trim();
        if (busy) return;
        if (question.Length == 0) { if (Attachments.Count == 0) return; question = "添付したものを見て、役に立つことを教えて"; }
        var key = ApiKey;
        if (key.Length == 0 && AiClient.NeedsKey(ProviderId)) { Status = $"{Provider.Name} の APIキーを、⚙ 設定に入れてください"; ShowSettings = true; return; }

        IsBusy = true;
        cts = new CancellationTokenSource();
        Input = "";
        try
        {
            Status = "一緒に送るものを集めています…";
            var notes = new List<string>();
            var images = new List<(string mime, string base64, string caption)>();
            var context = await Task.Run(() => BuildContext(notes));
            var files = Attachments.ToList();
            Attachments.Clear();
            Raise(nameof(HasAttachments));
            var attachText = files.Count > 0 ? await Task.Run(() => BuildAttachments(files, images, notes)) : "";

            var text = new StringBuilder();
            if (context.Length > 0) text.AppendLine("# いまの状態\n" + context);
            if (attachText.Length > 0) text.AppendLine("# 添付したファイル\n" + attachText);
            text.AppendLine("# 質問\n" + question);
            var turn = new AiTurn { Role = "user", Text = text.ToString(), Images = images };
            var userMsg = new ChatMessage { Role = "user", Text = question, Attached = string.Join("・", notes) };
            Messages.Add(userMsg);
            Raise(nameof(HasNoMessages));
            MessageAdded?.Invoke();

            var turns = new List<AiTurn>();
            foreach (var m in Messages.Where(m => m.Role is "user" or "assistant"))
                turns.Add(ReferenceEquals(m, userMsg) ? turn : new AiTurn { Role = m.Role, Text = m.Role == "user" ? "# 質問\n" + m.Text : m.Text });

            Status = $"{Provider.Name} に送っています…";
            var reply = await AiClient.SendAsync(ProviderId, Model, key, BaseUrl, SystemPrompt(), turns, cts.Token, web: WebOn && IsLocalStudio);
            var answer = string.IsNullOrWhiteSpace(reply) ? "(返事が空でした)" : reply.Trim();

            // AI が専用ストレージのファイルを読みたいと言ったら(ymm-read)、読んで続けて送る(3回まで)
            var readNames = new List<string>();
            var memoryNotes = new List<string>();
            var proj = project?.Name ?? "";
            for (int round = 0; round < 3; round++)
            {
                var probe = answer;
                if (UseMemory) { var mn = AiMemory.ExtractAndApply(ref probe, proj); if (mn.Length > 0) memoryNotes.Add(mn); }
                var reads = AiStorage.ExtractReads(ref probe);
                if (reads.Count == 0) break;
                Status = $"AI が専用ストレージのファイルを読んでいます…({reads.Count} 個)";
                var rimages = new List<(string mime, string base64, string caption)>();
                var rnotes = new List<string>();
                var rfiles = await Task.Run(() => ExpandReads(reads));
                var rtext = await Task.Run(() => BuildAttachments(rfiles, rimages, rnotes));
                readNames.AddRange(rfiles.Select(f => f.Name));
                turns.Add(new AiTurn { Role = "assistant", Text = probe.Length > 0 ? probe : "(ファイルを読みます)" });
                turns.Add(new AiTurn { Role = "user", Text = "# 読んだファイル(ymm-read で頼まれたもの)\n" + rtext + "\n\n上のファイルを読んだうえで、さっきの質問に答えてください。", Images = rimages });
                Status = $"{Provider.Name} に送っています…(読んだファイルと一緒に)";
                reply = await AiClient.SendAsync(ProviderId, Model, key, BaseUrl, SystemPrompt(), turns, cts.Token, web: WebOn && IsLocalStudio);
                answer = string.IsNullOrWhiteSpace(reply) ? "(返事が空でした)" : reply.Trim();
            }
            AiStorage.ExtractReads(ref answer);
            if (UseMemory) { var mn = AiMemory.ExtractAndApply(ref answer, proj); if (mn.Length > 0) memoryNotes.Add(mn); Raise(nameof(MemoryInfo)); }
            if (memoryNotes.Count > 0) answer += "\n\n" + string.Join("\n", memoryNotes);
            if (readNames.Count > 0) answer += "\n\n📂 読んだファイル: " + string.Join("、", readNames.Distinct().Take(20));
            Messages.Add(new ChatMessage { Role = "assistant", Text = answer });
            Status = "";
        }
        catch (OperationCanceledException) { Status = "取りやめました"; }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (AiClient.SuggestedModel(msg) is { } sm && sm != Model) { Model = sm; msg += $"\n\n→ モデルを「{sm}」に切り替えました。もう一度送ってください。"; }
            Messages.Add(new ChatMessage { Role = "error", Text = msg });
            Status = "";
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(HasNoMessages));
            MessageAdded?.Invoke();
            SaveChat();
        }
    }

    // ---------------------------------------------------------------- 送るものを作る
    string BuildContext(List<string> notes)
    {
        var sb = new StringBuilder();
        try
        {
            if (SendYmm4 && Ymm4.IsReady)
            {
                sb.AppendLine($"## YMM4\n場所 {Ymm4.Dir} / バージョン {Ymm4.Version}" + (Ymm4.IsRunning ? " / 起動中" : ""));
                var plugins = main.Plugins.Items.ToList();
                if (plugins.Count > 0) sb.AppendLine("入れているプラグイン: " + string.Join("、", plugins.Take(60).Select(p => p.Name + (p.Version.Length > 0 ? " " + p.Version : "") + (p.IsEnabled ? "" : "(無効)"))));
                if (settings.Ymm4Presets.Count > 1) sb.AppendLine("YMM4プリセット: " + string.Join("、", settings.Ymm4Presets.Select(p => p.Name)));
                notes.Add("YMM4の情報");
            }
            if (SendStorage && AiStorage.Root != null)
            {
                var idx = AiStorage.Index();
                if (idx.Length > 0) { sb.AppendLine().AppendLine(idx); notes.Add("専用ストレージのすべての中身"); }
            }
            if (SendMaterials)
            {
                var all = MaterialStore.Instance.All().Where(e => !e.IsDeleted).ToList();
                if (all.Count > 0)
                {
                    sb.AppendLine($"\n## 素材データベース({all.Count} 個)");
                    var tags = all.SelectMany(e => e.Tags).GroupBy(t => t).OrderByDescending(g => g.Count()).Take(60).Select(g => $"{g.Key}({g.Count()})");
                    sb.AppendLine("よく使うタグ: " + string.Join("、", tags));
                    foreach (var e in all.Take(300))
                    {
                        var info = new List<string> { e.Kind };
                        if (e.Width > 0) info.Add($"{e.Width}×{e.Height}");
                        if (e.DurationSec > 0) info.Add($"{e.DurationSec:0.#}秒");
                        if (e.Tags.Count > 0) info.Add("タグ: " + string.Join(",", e.Tags.Take(6)));
                        if (e.Folder.Length > 0) info.Add("フォルダー: " + e.Folder);
                        sb.AppendLine($"「{e.Name}」 " + string.Join(" | ", info));
                    }
                    if (all.Count > 300) sb.AppendLine($"(ほか {all.Count - 300} 個は省略)");
                    notes.Add("素材データベース");
                }
            }
            if (SendProjects && main.Projects.Files.Count > 0)
            {
                sb.AppendLine($"\n## プロジェクト({main.Projects.Files.Count} 個。新しい順)");
                foreach (var p in main.Projects.Files.OrderByDescending(p => p.Modified).Take(80)) sb.AppendLine($"{p.Name}({p.ModifiedText} 更新・{p.SizeText})");
                notes.Add("プロジェクトの一覧");
            }
            if (project != null)
            {
                sb.AppendLine($"\n## 選んだプロジェクト「{project.Name}」の中身");
                sb.AppendLine(SummarizeProject(project.Path));
                notes.Add($"プロジェクト「{project.Name}」");
            }
        }
        catch (Exception ex) { sb.AppendLine("(一部の情報を読めませんでした: " + ex.Message + ")"); }
        return sb.ToString();
    }

    /// <summary>.ymmp(JSON)から、シーンとアイテム(位置・長さ・種類・文字・ファイル)を読み出す</summary>
    static string SummarizeProject(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            var sb = new StringBuilder();
            int items = 0;
            void Walk(JsonNode? n, string where)
            {
                if (sb.Length > 80000) return;
                switch (n)
                {
                    case JsonObject o:
                    {
                        var name = o["Name"] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : null;
                        if (o["Items"] is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject first && first["Frame"] != null)
                        {
                            var fps = o["VideoInfo"]?["FPS"]?.GetValue<double>() ?? 60;
                            sb.AppendLine($"### シーン「{name ?? where}」(アイテム {arr.Count} 個)");
                            foreach (var it in arr.OfType<JsonObject>().OrderBy(i => i["Frame"]?.GetValue<int>() ?? 0))
                            {
                                if (sb.Length > 80000) { sb.AppendLine("(長いため省略)"); break; }
                                int f = it["Frame"]?.GetValue<int>() ?? 0, len = it["Length"]?.GetValue<int>() ?? 0, layer = it["Layer"]?.GetValue<int>() ?? 0;
                                var type = (it["$type"]?.GetValue<string>() ?? "").Split(',')[0].Split('.').Last().Replace("Item", "");
                                var text = it["Serif"]?.GetValue<string>() ?? it["Text"]?.GetValue<string>();
                                var file = it["FilePath"]?.GetValue<string>();
                                var chara = it["CharacterName"]?.GetValue<string>();
                                var line = $"[L{layer:00}] {f / fps:0.0}〜{(f + len) / fps:0.0}秒 {type}";
                                if (!string.IsNullOrEmpty(chara)) line += $"({chara})";
                                if (!string.IsNullOrEmpty(text)) line += $"「{text.Replace("\r", "").Replace("\n", " ")}」";
                                if (!string.IsNullOrEmpty(file)) line += " " + Path.GetFileName(file);
                                sb.AppendLine(line);
                                items++;
                            }
                        }
                        foreach (var (k, v) in o) if (k != "Items") Walk(v, name ?? k);
                        break;
                    }
                    case JsonArray a:
                        foreach (var v in a) Walk(v, where);
                        break;
                }
            }
            Walk(root, "シーン");
            return items == 0 ? "(アイテムを読み取れませんでした)" : sb.ToString();
        }
        catch (Exception ex) { return "(プロジェクトを読めませんでした: " + ex.Message + ")"; }
    }

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".json", ".srt", ".ass", ".vtt", ".ini", ".log", ".xml", ".yaml", ".yml", ".ymmp" };

    /// <summary>ymm-read で頼まれたパスを添付の形にする(フォルダーは中のファイルを 30 個まで)</summary>
    static List<ChatAttachment> ExpandReads(List<string> reads)
    {
        var list = new List<ChatAttachment>();
        foreach (var r in reads)
        {
            if (File.Exists(r)) list.Add(new ChatAttachment { Path = r });
            else if (Directory.Exists(r))
                try
                {
                    foreach (var f in Directory.EnumerateFiles(r, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(30))
                        list.Add(new ChatAttachment { Path = f });
                }
                catch { }
        }
        return list.Take(40).ToList();
    }

    static string BuildAttachments(List<ChatAttachment> files, List<(string, string, string)> images, List<string> notes)
    {
        var sb = new StringBuilder();
        foreach (var a in files)
        {
            var ext = Path.GetExtension(a.Path);
            try
            {
                if (ImageExt.Contains(ext))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(a.Path); bi.EndInit(); bi.Freeze();
                    double scale = Math.Min(1.0, 1280.0 / Math.Max(bi.PixelWidth, bi.PixelHeight));
                    BitmapSource img = scale < 1 ? new TransformedBitmap(bi, new ScaleTransform(scale, scale)) : bi;
                    var enc = new JpegBitmapEncoder { QualityLevel = 85 };
                    enc.Frames.Add(BitmapFrame.Create(img));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    images.Add(("image/jpeg", Convert.ToBase64String(ms.ToArray()), "[添付画像] " + a.Name));
                    sb.AppendLine($"- 画像 {a.Name}(画像として添付)");
                }
                else if (string.Equals(ext, ".ymmp", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"### プロジェクト {a.Name}\n" + SummarizeProject(a.Path));
                else if (TextExt.Contains(ext))
                {
                    var t = File.ReadAllText(a.Path);
                    if (t.Length > 40000) t = t.Substring(0, 40000) + "\n(長いため途中まで)";
                    sb.AppendLine($"### ファイル {a.Name}\n```\n{t}\n```");
                }
                else sb.AppendLine($"- {a.Name}({FileItem.FormatSize(new FileInfo(a.Path).Length)}。この種類は中身を送れません)");
            }
            catch (Exception ex) { sb.AppendLine($"- {a.Name}(読めませんでした: {ex.Message})"); }
        }
        notes.Add($"添付 {files.Count} 件");
        return sb.ToString();
    }
}
