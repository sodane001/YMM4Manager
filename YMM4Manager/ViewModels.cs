using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YmmExplorer;

namespace YMM4Manager;

// ====================================================================== 全体
public sealed class MainVm : NotifyBase
{
    readonly AppSettings settings = AppSettings.Instance;
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainVm()
    {
        if (!Ymm4.IsReady)
        {
            var found = Ymm4.AutoDetect();
            if (found != null) { settings.Ymm4Dir = found; settings.Save(); }
        }
        PluginManager.RootOverride = Ymm4.PluginDir;
        Home = new HomeVm(this);
        Materials = new MaterialsVm(this);
        Projects = new ProjectsVm(this);
        Plugins = new PluginsVm(this);
        Backups = new BackupVm(this);
        Updater = new Ymm4UpdaterVm(this);
        PluginSettings = new PluginSettingsVm(this);
        Storage = new StorageVm(this);
        Presets = new Ymm4PresetsVm(this);
        Ai = new AiChatVm(this);
        Ymm4Templates = new Ymm4TemplatesVm(this);
        page = Ymm4.IsReady ? (settings.LastPage ?? "home") : "setup";
        statusTimer.Tick += (_, _) => RefreshRunning();
        statusTimer.Start();
        RefreshRunning();
        NavigateCommand = new RelayCommand(p => { if (p is string s) Page = s; });
        OnShown(page);
        LaunchCommand = new RelayCommand(_ => Toast(Ymm4.Launch()), _ => Ymm4.IsReady);
        OpenYmmFolderCommand = new RelayCommand(_ => Ymm4.OpenFolder(Ymm4.Dir), _ => Ymm4.IsReady);
        AutoDetectCommand = new RelayCommand(_ =>
        {
            var found = Ymm4.AutoDetect();
            if (found == null) { Toast("YMM4が見つかりませんでした。「場所を選ぶ」で YukkuriMovieMaker.exe のあるフォルダーを選んでください"); return; }
            SetYmm4Dir(found);
        });
    }

    public HomeVm Home { get; }
    public MaterialsVm Materials { get; }
    public ProjectsVm Projects { get; }
    public PluginsVm Plugins { get; }
    public BackupVm Backups { get; }
    public Ymm4UpdaterVm Updater { get; }
    public PluginSettingsVm PluginSettings { get; }
    public StorageVm Storage { get; }

    /// <summary>通知領域(タスクバーの右下)にアイコンを出す</summary>
    public bool TrayIcon
    {
        get => settings.TrayIcon;
        set { settings.TrayIcon = value; settings.Save(); Raise(nameof(TrayIcon)); (System.Windows.Application.Current.MainWindow as MainWindow)?.ApplyTray(); }
    }
    /// <summary>× で閉じた時は、通知領域に残す</summary>
    public bool CloseToTray { get => settings.CloseToTray; set { settings.CloseToTray = value; settings.Save(); Raise(nameof(CloseToTray)); } }
    public Ymm4PresetsVm Presets { get; }
    public AiChatVm Ai { get; }
    public Ymm4TemplatesVm Ymm4Templates { get; }

    string page;
    public string Page
    {
        get => page;
        set
        {
            if (value != "setup" && !Ymm4.IsReady && value != "settings") value = "setup";
            if (!Set(ref page, value)) return;
            if (value != "setup") { settings.LastPage = value; settings.Save(); }
            OnShown(value);
        }
    }

    void OnShown(string p)
    {
        switch (p)
        {
            case "projects": Projects.Reload(); break;
            case "plugins": Plugins.Reload(keepTarget: true); break;
            case "backup": Backups.Reload(); break;
            case "home": Home.Refresh(); break;
            case "pluginsettings": PluginSettings.OnShown(); break;
            case "storage": Storage.Refresh(); break;
            case "ymm4templates": Ymm4Templates.OnShown(); break;
            case "presets": Presets.Reload(); Plugins.Reload(); break;
            case "ai": if (Projects.Files.Count == 0) Projects.Reload(); if (Plugins.Items.Count == 0) Plugins.Reload(); break;
        }
    }

    public ICommand NavigateCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand OpenYmmFolderCommand { get; }
    public ICommand AutoDetectCommand { get; }

    // ---- YMM4の場所
    public string Ymm4Dir => Ymm4.Dir ?? "(未設定)";
    public string Ymm4Version => Ymm4.IsReady ? (Ymm4.Version is { Length: > 0 } v ? "バージョン " + v : "") : "";
    public bool IsReady => Ymm4.IsReady;
    public string AppVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    /// <summary>YMM4の場所を決める。正しくなければ false</summary>
    public bool SetYmm4Dir(string picked)
    {
        var dir = Ymm4.Resolve(picked);
        if (dir == null)
        {
            Toast("選んだ場所に YukkuriMovieMaker.exe が見つかりませんでした。YMM4のフォルダーを選んでください");
            return false;
        }
        settings.Ymm4Dir = dir;
        settings.Save();
        PluginManager.RootOverride = Ymm4.PluginDir;
        Raise(nameof(Ymm4Dir)); Raise(nameof(Ymm4Version)); Raise(nameof(IsReady));
        // 対象の YMM4 が変わったので、バージョン・プラグインの表示も切り替える
        Updater?.OnTargetChanged();
        if (Plugins != null) Plugins.Reload();
        Toast("YMM4の場所を設定しました: " + dir);
        if (page == "setup") Page = "home";
        Home.Refresh();
        return true;
    }

    // ---- 起動中かどうか
    bool running;
    public bool IsRunning { get => running; private set { if (Set(ref running, value)) { Raise(nameof(RunningText)); Home.Refresh(); } } }
    public string RunningText => running ? "● YMM4 起動中" : "○ YMM4 は起動していません";
    void RefreshRunning() => IsRunning = Ymm4.IsReady && Ymm4.IsRunning;

    // ---- 下の通知
    string toast = "";
    public string ToastText { get => toast; private set { Set(ref toast, value); Raise(nameof(HasToast)); } }
    public bool HasToast => toast.Length > 0;
    DispatcherTimer? toastTimer;
    public void Toast(string text)
    {
        ToastText = text;
        toastTimer?.Stop();
        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        toastTimer.Tick += (_, _) => { toastTimer?.Stop(); ToastText = ""; };
        toastTimer.Start();
    }
}

// ====================================================================== ホーム
public sealed class HomeVm : NotifyBase
{
    readonly MainVm main;
    public HomeVm(MainVm main)
    {
        this.main = main;
        OpenRecentCommand = new RelayCommand(p => { if (p is ProjectFile f) main.Toast(Ymm4.Launch(f.Path)); });
    }

    public ObservableCollection<ProjectFile> Recent { get; } = new();
    public ICommand OpenRecentCommand { get; }

    public string MaterialSummary { get; private set; } = "";
    public string PluginSummary { get; private set; } = "";
    public string ProjectSummary { get; private set; } = "";

    public void Refresh()
    {
        Recent.Clear();
        foreach (var f in ProjectsVm.Scan(AppSettings.Instance.ProjectFolders, 8)) Recent.Add(f);
        var all = MaterialStore.Instance.All();
        MaterialSummary = $"{all.Count} 個(画像 {all.Count(e => e.Kind == "image")} / 動画 {all.Count(e => e.Kind == "video")} / 音声 {all.Count(e => e.Kind == "audio")})";
        // ホームは、いま使っている YMM4 のプラグインを数える(プラグインのページで別のプリセットを見ていても)
        var keepRoot = PluginManager.RootOverride;
        PluginManager.RootOverride = Ymm4.PluginDir;
        var plugins = Ymm4.IsReady ? PluginManager.Scan(out _) : new List<PluginInfo>();
        PluginManager.RootOverride = keepRoot;
        PluginSummary = $"{plugins.Count} 個(有効 {plugins.Count(p => p.IsEnabled)} / 無効 {plugins.Count(p => !p.IsEnabled)})";
        ProjectSummary = AppSettings.Instance.ProjectFolders.Count == 0 ? "プロジェクトのフォルダーを登録すると、ここに最近のプロジェクトが出ます" : "";
        Raise(nameof(MaterialSummary)); Raise(nameof(PluginSummary)); Raise(nameof(ProjectSummary));
        Raise(nameof(HasNoRecent));
    }

    public bool HasNoRecent => Recent.Count == 0;
}

// ====================================================================== 素材データベース
public sealed class MaterialCard : NotifyBase
{
    public MaterialCard(MaterialEntry e) => Entry = e;
    public MaterialEntry Entry { get; }
    public string Name => Entry.Name;
    public string Kind => Entry.Kind;
    public string KindGlyph => Entry.Kind switch { "image" => "", "video" => "", "audio" => "", _ => "" };
    public string Info
    {
        get
        {
            var parts = new List<string> { Entry.Ext.TrimStart('.').ToUpperInvariant() };
            if (Entry.Width > 0) parts.Add($"{Entry.Width}×{Entry.Height}");
            if (Entry.DurationSec > 0) parts.Add(TimeSpan.FromSeconds(Entry.DurationSec).ToString(Entry.DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
            parts.Add(FileItem.FormatSize(Entry.Size));
            return string.Join("  ", parts);
        }
    }
    public string Stars => Entry.Rating <= 0 ? "" : new string('★', Entry.Rating);
    public string TagsText => string.Join(" ", Entry.Tags.Select(t => "#" + t));

    ImageSource? thumb;
    bool thumbLoaded;
    public ImageSource? Thumb
    {
        get
        {
            if (!thumbLoaded)
            {
                thumbLoaded = true;
                var path = MaterialStore.Instance.ThumbPath(Entry);
                if (!File.Exists(path) && Entry.Kind == "image") path = MaterialStore.Instance.FullPath(Entry);
                if (File.Exists(path))
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 320;
                            bmp.UriSource = new Uri(path);
                            bmp.EndInit();
                            bmp.Freeze();
                            return (ImageSource)bmp;
                        }
                        catch { return null; }
                    }).ContinueWith(t => { thumb = t.Result; Raise(nameof(Thumb)); Raise(nameof(HasThumb)); }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
            return thumb;
        }
    }
    public bool HasThumb => thumb != null;
    public void RefreshAll() { thumbLoaded = false; thumb = null; Raise(""); }
}

public sealed class ChoiceItem
{
    public ChoiceItem(string key, string label) { Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public sealed class MaterialsVm : NotifyBase
{
    readonly MainVm main;
    readonly MaterialStore store = MaterialStore.Instance;
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MaterialsVm(MainVm main)
    {
        this.main = main;
        store.Changed += () => Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh));
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Refresh(); };

        TrashCommand = new RelayCommand(_ => { var n = store.Trash(SelectedIds()); main.Toast($"{n} 個をゴミ箱へ移しました"); }, _ => selected.Count > 0 && !ShowTrash);
        RestoreCommand = new RelayCommand(_ => { var n = store.Restore(SelectedIds()); main.Toast($"{n} 個をゴミ箱から戻しました"); }, _ => selected.Count > 0 && ShowTrash);
        DeleteForeverCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show($"{selected.Count} 個を完全に削除します(ファイルも消えます)。よろしいですか?", "完全に削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            var n = store.Remove(SelectedIds());
            main.Toast($"{n} 個を完全に削除しました");
        }, _ => selected.Count > 0 && ShowTrash);
        EmptyTrashCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show("ゴミ箱を空にします(ファイルも消えます)。よろしいですか?", "ゴミ箱を空にする", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            main.Toast($"{store.EmptyTrash()} 個を削除しました");
        });
        OpenFileCommand = new RelayCommand(_ =>
        {
            if (Current == null) return;
            try { Process.Start(new ProcessStartInfo(store.FullPath(Current.Entry)) { UseShellExecute = true }); } catch { }
        }, _ => Current != null);
        ShowInExplorerCommand = new RelayCommand(_ => { if (Current != null) Ymm4.OpenFolder(store.FullPath(Current.Entry)); }, _ => Current != null);
        CopyPathCommand = new RelayCommand(_ =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(c => store.FullPath(c.Entry)))); main.Toast("場所をコピーしました"); } catch { }
        }, _ => selected.Count > 0);
        SetRatingCommand = new RelayCommand(p => { if (int.TryParse(p?.ToString(), out var r)) { store.SetRating(SelectedIds(), r); } }, _ => selected.Count > 0);
        AddTagCommand = new RelayCommand(_ =>
        {
            var tags = (NewTag ?? "").Split(new[] { ' ', '　', ',', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('#')).ToList();
            if (tags.Count == 0) return;
            store.AddTags(SelectedIds(), tags);
            NewTag = "";
        }, _ => selected.Count > 0);
        RemoveTagCommand = new RelayCommand(p => { if (p is string t) store.RemoveTag(SelectedIds(), t); });
        MoveToFolderCommand = new RelayCommand(p => { if (p is string f) { store.SetFolder(SelectedIds(), f); main.Toast(f.Length == 0 ? "フォルダーから外しました" : $"「{f}」へ移しました"); } }, _ => selected.Count > 0);
        AddFolderCommand = new RelayCommand(_ =>
        {
            var name = (NewFolder ?? "").Trim();
            if (name.Length == 0) return;
            if (store.AddFolder(name)) { NewFolder = ""; main.Toast($"フォルダー「{name}」を作りました"); }
        });
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is not ChoiceItem c || c.Key.Length == 0 || c.Key == "*") return;
            if (MessageBox.Show($"フォルダー「{c.Label}」を削除します(中の素材は消えずに、フォルダーなしになります)。", "フォルダーの削除", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            store.RemoveFolder(c.Key);
        });
        ClearFiltersCommand = new RelayCommand(_ =>
        {
            search = ""; kind = "*"; folder = "*"; tag = null; color = null;
            foreach (var n in new[] { nameof(Search), nameof(KindFilter), nameof(FolderFilter), nameof(TagFilter), nameof(ColorFilter), nameof(HasColorFilter) }) Raise(n);
            Refresh();
        });
        SetColorCommand = new RelayCommand(p => { ColorFilter = p as string; });
        RebuildThumbsCommand = new RelayCommand(async _ =>
        {
            main.Toast("サムネイル・長さ・色を作り直しています…");
            await store.BackfillAsync();
            foreach (var c in Items) c.RefreshAll();
            main.Toast("作り直しました");
        });
        Refresh();
    }

    // ---- 絞り込み
    string search = "";
    public string Search { get => search; set { if (Set(ref search, value)) { searchTimer.Stop(); searchTimer.Start(); } } }

    public static IReadOnlyList<ChoiceItem> Kinds { get; } = new[]
    {
        new ChoiceItem("*", "すべて"), new ChoiceItem("image", "画像"), new ChoiceItem("video", "動画"),
        new ChoiceItem("audio", "音声"), new ChoiceItem("other", "その他"),
    };
    string kind = "*";
    public string KindFilter { get => kind; set { if (Set(ref kind, value ?? "*")) Refresh(); } }

    public ObservableCollection<ChoiceItem> FolderChoices { get; } = new();
    string folder = "*";
    public string FolderFilter { get => folder; set { if (rebuilding) return; if (Set(ref folder, value ?? "*")) Refresh(); } }
    bool rebuilding;     // 一覧(フォルダー・タグ)を作り直している間は、選択が外れても絞り込みを変えない

    public ObservableCollection<ChoiceItem> TagChoices { get; } = new();
    string? tag;
    public string? TagFilter { get => tag; set { if (rebuilding) return; if (Set(ref tag, value)) Refresh(); } }

    string? color;
    public string? ColorFilter { get => color; set { if (Set(ref color, value)) { Raise(nameof(HasColorFilter)); Refresh(); } } }
    public bool HasColorFilter => color != null;
    public static IReadOnlyList<string> ColorPresets { get; } = new[]
        { "E53935", "FB8C00", "FDD835", "43A047", "00ACC1", "1E88E5", "8E24AA", "EC407A", "6D4C41", "FFFFFF", "9E9E9E", "212121" };

    public static IReadOnlyList<ChoiceItem> Sorts { get; } = new[]
    {
        new ChoiceItem("added", "追加した日(新しい順)"), new ChoiceItem("name", "名前"), new ChoiceItem("size", "大きさ"),
        new ChoiceItem("rating", "評価"), new ChoiceItem("length", "長さ"),
    };
    string sort = "added";
    public string Sort { get => sort; set { if (Set(ref sort, value ?? "added")) Refresh(); } }

    bool showTrash;
    public bool ShowTrash { get => showTrash; set { if (Set(ref showTrash, value)) { Refresh(); CommandManager.InvalidateRequerySuggested(); } } }

    public int TileSize
    {
        get => AppSettings.Instance.MaterialTileSize;
        set { AppSettings.Instance.MaterialTileSize = Math.Clamp(value, 90, 320); AppSettings.Instance.Save(); Raise(nameof(TileSize)); Raise(nameof(ThumbHeight)); }
    }
    public double ThumbHeight => TileSize * 0.62;

    // ---- 一覧
    public ObservableCollection<MaterialCard> Items { get; } = new();
    public string CountText { get; private set; } = "";
    public string LibraryPath => store.Root;
    public bool HasNoItems => Items.Count == 0;

    public void Refresh()
    {
        var keepIds = selected.Select(c => c.Entry.Id).ToHashSet();
        IEnumerable<MaterialEntry> q = showTrash ? store.Trashed() : store.All();
        if (kind != "*") q = q.Where(e => e.Kind == kind);
        if (folder != "*") q = q.Where(e => (e.Folder ?? "") == folder);
        if (tag != null) q = q.Where(e => e.Tags.Contains(tag));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var words = search.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                          || e.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase))
                                          || (e.Memo ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)
                                          || e.Ext.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        List<MaterialEntry> list;
        if (color != null)
            list = q.Select(e => (e, d: e.Palette.Count == 0 ? 999.0 : e.Palette.Min(h => ColorTools.Distance(h, color))))
                    .Where(x => x.d < 35).OrderBy(x => x.d).Select(x => x.e).ToList();
        else
            list = (sort switch
            {
                "name" => q.OrderBy(e => e.Name, NaturalComparer.Instance),
                "size" => q.OrderByDescending(e => e.Size),
                "rating" => q.OrderByDescending(e => e.Rating).ThenByDescending(e => e.Added),
                "length" => q.OrderByDescending(e => e.DurationSec),
                _ => q.OrderByDescending(e => e.Added),
            }).ToList();

        Items.Clear();
        foreach (var e in list) Items.Add(new MaterialCard(e));
        CountText = $"{list.Count} 個" + (showTrash ? "(ゴミ箱)" : "");
        Raise(nameof(CountText)); Raise(nameof(HasNoItems)); Raise(nameof(LibraryPath));

        // フォルダー・タグの一覧(変わった時だけ作り直す)
        rebuilding = true;
        try
        {
            var folderItems = new List<ChoiceItem> { new("*", "すべてのフォルダー"), new("", "フォルダーなし") };
            folderItems.AddRange(store.Folders().Select(f => new ChoiceItem(f, f)));
            if (!folderItems.Select(f => f.Label).SequenceEqual(FolderChoices.Select(f => f.Label)))
            {
                FolderChoices.Clear();
                foreach (var f in folderItems) FolderChoices.Add(f);
            }
            var tagItems = store.All().SelectMany(e => e.Tags).GroupBy(t => t).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => new ChoiceItem(g.Key, $"#{g.Key}  {g.Count()}")).ToList();
            if (!tagItems.Select(t => t.Label).SequenceEqual(TagChoices.Select(t => t.Label)))
            {
                TagChoices.Clear();
                foreach (var t in tagItems) TagChoices.Add(t);
            }
        }
        finally { rebuilding = false; }
        Raise(nameof(FolderFilter));
        Raise(nameof(TagFilter));

        // 選び直し
        RestoreSelection?.Invoke(Items.Where(c => keepIds.Contains(c.Entry.Id)).ToList());
    }

    /// <summary>一覧を作り直した後に、前に選んでいたものを選び直す(画面側で行う)</summary>
    public Action<IReadOnlyList<MaterialCard>>? RestoreSelection;

    // ---- 選択
    List<MaterialCard> selected = new();
    public MaterialCard? Current => selected.Count == 1 ? selected[0] : null;
    public bool HasSelection => selected.Count > 0;
    public bool HasSingle => selected.Count == 1;
    public string SelectionText => selected.Count == 0 ? "素材を選ぶと、ここに詳しい情報が出ます" : selected.Count == 1 ? "" : $"{selected.Count} 個を選んでいます";
    IEnumerable<string> SelectedIds() => selected.Select(c => c.Entry.Id).ToList();

    public void SetSelection(IEnumerable<MaterialCard> list)
    {
        selected = list.ToList();
        foreach (var n in new[] { nameof(Current), nameof(HasSelection), nameof(HasSingle), nameof(SelectionText), nameof(EditName), nameof(EditMemo),
                                  nameof(EditUrl), nameof(CurrentTags), nameof(CurrentPalette), nameof(CurrentPreview), nameof(CurrentInfo), nameof(CurrentFolder) })
            Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- 詳しい情報(1つ選んだ時)
    public string EditName
    {
        get => Current?.Entry.Name ?? "";
        set { if (Current == null || value == Current.Entry.Name || string.IsNullOrWhiteSpace(value)) return; Current.Entry.Name = value.Trim(); store.Touch(); }
    }
    public string EditMemo
    {
        get => Current?.Entry.Memo ?? "";
        set { if (Current == null || value == Current.Entry.Memo) return; Current.Entry.Memo = value ?? ""; store.Touch(); }
    }
    public string EditUrl
    {
        get => Current?.Entry.Url ?? "";
        set { if (Current == null || value == Current.Entry.Url) return; store.SetUrl(Current.Entry.Id, value ?? ""); }
    }
    public string CurrentFolder => Current == null ? "" : (string.IsNullOrEmpty(Current.Entry.Folder) ? "フォルダーなし" : Current.Entry.Folder);
    public IReadOnlyList<string> CurrentTags => Current?.Entry.Tags.ToList() ?? new List<string>();
    public IReadOnlyList<string> CurrentPalette => Current?.Entry.Palette.ToList() ?? new List<string>();
    public string CurrentInfo
    {
        get
        {
            if (Current == null) return "";
            var e = Current.Entry;
            return $"{Current.Info}\n追加: {e.Added:yyyy/MM/dd HH:mm}" + (string.IsNullOrEmpty(e.SourcePath) ? "" : $"\n元の場所: {e.SourcePath}");
        }
    }
    public ImageSource? CurrentPreview
    {
        get
        {
            if (Current == null) return null;
            try
            {
                var path = Current.Entry.Kind == "image" ? store.FullPath(Current.Entry) : store.ThumbPath(Current.Entry);
                if (!File.Exists(path)) return Current.Thumb;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 640;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return Current.Thumb; }
        }
    }
    public string? MediaPath => Current != null && (Current.Entry.Kind is "video" or "audio") ? store.FullPath(Current.Entry) : null;

    string newTag = "", newFolder = "";
    public string NewTag { get => newTag; set => Set(ref newTag, value); }
    public string NewFolder { get => newFolder; set => Set(ref newFolder, value); }

    // ---- 取り込み
    bool importing;
    public bool IsImporting { get => importing; private set => Set(ref importing, value); }
    string importText = "";
    public string ImportText { get => importText; private set => Set(ref importText, value); }

    public async Task ImportAsync(IEnumerable<string> paths, bool move = false)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        IsImporting = true;
        try
        {
            var opt = new ImportOptions { Mode = move ? ImportMode.Move : ImportMode.Copy, TagsFromFolders = true, Folder = folder is "*" ? "" : folder };
            var r = await store.ImportAsync(list, opt, s => Application.Current.Dispatcher.BeginInvoke(new Action(() => ImportText = s)));
            main.Toast($"取り込み: 追加 {r.Added} / 同じものがあった {r.Duplicates}" + (r.Restored > 0 ? $" / ゴミ箱から戻した {r.Restored}" : "") + (r.Failed > 0 ? $" / 失敗 {r.Failed}" : ""));
        }
        catch (Exception ex) { main.Toast("取り込めませんでした: " + ex.Message); }
        finally { IsImporting = false; ImportText = ""; }
    }

    // ---- ライブラリーの場所
    public void OpenLibrary(string dir)
    {
        store.Open(dir);
        ExplorerSettings.Instance.MaterialLibraryPath = dir;
        main.Toast("素材データベースを開きました: " + dir + "(YMM4の中の YmmExplorer も、次に開いた時からこの場所を使います)");
        Refresh();
    }

    public async Task MoveLibraryAsync(string dir)
    {
        if (Ymm4.IsRunning) { main.Toast("YMM4を閉じてから、引っ越してください(YMM4の中の YmmExplorer が使っているため)"); return; }
        IsImporting = true;
        try
        {
            var msg = await store.MoveLibraryAsync(dir, s => Application.Current.Dispatcher.BeginInvoke(new Action(() => ImportText = s)));
            if (string.Equals(store.Root, dir, StringComparison.OrdinalIgnoreCase)) ExplorerSettings.Instance.MaterialLibraryPath = dir;
            main.Toast(msg);
            Refresh();
        }
        finally { IsImporting = false; ImportText = ""; }
    }

    public ICommand TrashCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DeleteForeverCommand { get; }
    public ICommand EmptyTrashCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand SetRatingCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }
    public ICommand MoveToFolderCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand SetColorCommand { get; }
    public ICommand RebuildThumbsCommand { get; }

    public IEnumerable<string> SelectedFilePaths() => selected.Select(c => store.FullPath(c.Entry)).Where(File.Exists);
}

// ====================================================================== プロジェクト
public sealed class ProjectFile
{
    public ProjectFile(FileInfo f) { Path = f.FullName; Name = System.IO.Path.GetFileNameWithoutExtension(f.Name); Modified = f.LastWriteTime; Size = f.Length; Folder = f.DirectoryName ?? ""; }
    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    public DateTime Modified { get; }
    public long Size { get; }
    public string ModifiedText => Modified.ToString("yyyy/MM/dd HH:mm");
    public string SizeText => FileItem.FormatSize(Size);
}

public sealed class ProjectsVm : NotifyBase
{
    readonly MainVm main;
    readonly AppSettings settings = AppSettings.Instance;

    public ProjectsVm(MainVm main)
    {
        this.main = main;
        OpenCommand = new RelayCommand(p => { if (p is ProjectFile f) main.Toast(Ymm4.Launch(f.Path)); });
        ShowCommand = new RelayCommand(p => { if (p is ProjectFile f) Ymm4.OpenFolder(f.Path); });
        BackupCommand = new RelayCommand(p =>
        {
            if (p is not ProjectFile f) return;
            try
            {
                var dir = System.IO.Path.Combine(settings.BackupRoot, "プロジェクト");
                Directory.CreateDirectory(dir);
                var dest = System.IO.Path.Combine(dir, $"{f.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.ymmp");
                File.Copy(f.Path, dest);
                main.Toast("プロジェクトのコピーを保存しました: " + dest);
            }
            catch (Exception ex) { main.Toast("保存できませんでした: " + ex.Message); }
        });
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is not string d) return;
            settings.ProjectFolders.Remove(d);
            settings.Save();
            Folders.Remove(d);
            Reload();
        });
        foreach (var d in settings.ProjectFolders) Folders.Add(d);

        ShowCreateCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectRoot))
            {
                main.Toast("先に「設定」で、プロジェクトを作るフォルダーを決めてください");
                main.Page = "settings";
                return;
            }
            NewName = "";
            CreateError = "";
            IsCreating = true;
        });
        CancelCreateCommand = new RelayCommand(_ => IsCreating = false);
        CreateCommand = new RelayCommand(_ => Create());
    }

    // ---------- 新しいプロジェクト ----------
    bool creating;
    public bool IsCreating { get => creating; set => Set(ref creating, value); }
    string newName = "";
    public string NewName { get => newName; set { if (Set(ref newName, value)) { CreateError = ""; Raise(nameof(NewPathPreview)); } } }
    string createError = "";
    public string CreateError { get => createError; private set { Set(ref createError, value); Raise(nameof(HasCreateError)); } }
    public bool HasCreateError => createError.Length > 0;
    public string ProjectRoot => settings.ProjectRoot ?? "(未設定)";
    public string TemplateText => string.IsNullOrWhiteSpace(settings.ProjectTemplate) ? "ひな形: なし(空のプロジェクト)" : "ひな形: " + System.IO.Path.GetFileName(settings.ProjectTemplate);
    public bool OpenAfterCreate { get => settings.OpenAfterCreate; set { settings.OpenAfterCreate = value; settings.Save(); Raise(nameof(OpenAfterCreate)); } }
    public string NewPathPreview => string.IsNullOrWhiteSpace(newName) || string.IsNullOrWhiteSpace(settings.ProjectRoot) ? ""
        : System.IO.Path.Combine(settings.ProjectRoot!, newName.Trim(), newName.Trim() + ".ymmp");

    public ICommand ShowCreateCommand { get; }
    public ICommand CancelCreateCommand { get; }
    public ICommand CreateCommand { get; }

    public void RefreshSettings()
    {
        Raise(nameof(ProjectRoot)); Raise(nameof(TemplateText)); Raise(nameof(NewPathPreview));
    }

    /// <summary>
    /// .ymmp を取り込む: 新しく作ったプロジェクトと同じ形(作るフォルダー\名前\名前.ymmp)へコピーする。元のファイルはそのまま。
    /// 同じ名前がある時は「名前 (2)」のようにする
    /// </summary>
    public void Import(IEnumerable<string> files)
    {
        var root = settings.ProjectRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            main.Toast("先に「設定」で、プロジェクトを作るフォルダーを決めてください");
            main.Page = "settings";
            return;
        }
        int ok = 0;
        var errors = new List<string>();
        foreach (var src in files.Where(f => f.EndsWith(".ymmp", StringComparison.OrdinalIgnoreCase) && File.Exists(f)))
        {
            try
            {
                var baseName = System.IO.Path.GetFileNameWithoutExtension(src);
                var name = baseName;
                for (int n = 2; Directory.Exists(System.IO.Path.Combine(root, name)) || File.Exists(System.IO.Path.Combine(root, name, name + ".ymmp")); n++)
                    name = $"{baseName} ({n})";
                var dir = System.IO.Path.Combine(root, name);
                var dest = System.IO.Path.Combine(dir, name + ".ymmp");
                Directory.CreateDirectory(dir);
                ProjectMaker.CopyProject(src, dest);
                ok++;
            }
            catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(src)}({ex.Message})"); }
        }
        if (ok > 0 && !settings.ProjectFolders.Any(f => root.StartsWith(f, StringComparison.OrdinalIgnoreCase))) AddFolder(root);
        Reload();
        main.Toast(ok == 0 && errors.Count == 0 ? "取り込める .ymmp がありませんでした"
            : $"{ok} 個のプロジェクトを取り込みました(場所: {root})" + (errors.Count > 0 ? "。⚠ 取り込めなかったもの: " + string.Join("、", errors) : ""));
    }

    void Create()
    {
        var name = (newName ?? "").Trim();
        if (name.Length == 0) { CreateError = "プロジェクトの名前を入れてください"; return; }
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        { CreateError = "名前に使えない文字が入っています( \\ / : * ? \" < > | )"; return; }
        var root = settings.ProjectRoot;
        if (string.IsNullOrWhiteSpace(root)) { CreateError = "設定で、プロジェクトを作るフォルダーを決めてください"; return; }
        var dir = System.IO.Path.Combine(root, name);
        var file = System.IO.Path.Combine(dir, name + ".ymmp");
        if (File.Exists(file)) { CreateError = "同じ名前のプロジェクトが、もうあります"; return; }
        try
        {
            Directory.CreateDirectory(dir);
            var r = ProjectMaker.Create(file, settings.ProjectTemplate);
            if (r != null) { CreateError = r; return; }
            // 作ったフォルダーを、プロジェクトの一覧の探す場所に入れる
            if (!settings.ProjectFolders.Any(f => root.StartsWith(f, StringComparison.OrdinalIgnoreCase))) AddFolder(root);
            IsCreating = false;
            Reload();
            main.Toast($"プロジェクト「{name}」を作りました: {file}");
            if (OpenAfterCreate) main.Toast(Ymm4.Launch(file));
        }
        catch (Exception ex) { CreateError = "作れませんでした: " + ex.Message; }
    }

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<ProjectFile> Files { get; } = new();
    string search = "";
    public string Search { get => search; set { if (Set(ref search, value)) Reload(); } }
    public bool HasNoFiles => Files.Count == 0;
    public string CountText { get; private set; } = "";

    public ICommand OpenCommand { get; }
    public ICommand ShowCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RemoveFolderCommand { get; }

    public void AddFolder(string dir)
    {
        if (settings.ProjectFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
        settings.ProjectFolders.Add(dir);
        settings.Save();
        Folders.Add(dir);
        Reload();
    }

    public void Reload()
    {
        Files.Clear();
        foreach (var f in Scan(settings.ProjectFolders, 500))
            if (search.Length == 0 || f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) Files.Add(f);
        CountText = $"{Files.Count} 個のプロジェクト";
        Raise(nameof(CountText)); Raise(nameof(HasNoFiles));
    }

    /// <summary>登録したフォルダーの中の .ymmp を、新しい順に集める(サブフォルダーは4階層まで)</summary>
    public static List<ProjectFile> Scan(IEnumerable<string> folders, int max)
    {
        var list = new List<ProjectFile>();
        var opt = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
        foreach (var d in folders)
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.EnumerateFiles(d, "*.ymmp", opt))
                {
                    try { list.Add(new ProjectFile(new FileInfo(f))); } catch { }
                }
            }
            catch { }
        }
        return list.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                   .OrderByDescending(f => f.Modified).Take(max).ToList();
    }
}

// ====================================================================== プラグイン
public sealed class PluginsVm : NotifyBase
{
    readonly MainVm main;
    public PluginsVm(MainVm main)
    {
        this.main = main;
        ToggleCommand = new RelayCommand(p =>
        {
            if (p is not PluginInfo info) return;
            if (Ymm4.IsRunning) main.Toast("YMM4の起動中は、切り替えられないことがあります(YMM4を閉じてからがおすすめです)");
            main.Toast(PluginManager.Toggle(info));
        });
        DeleteCommand = new RelayCommand(p =>
        {
            if (p is not PluginInfo info) return;
            if (MessageBox.Show($"プラグイン「{info.Name}」を削除します。よろしいですか?", "プラグインの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            main.Toast(PluginManager.Delete(info));
            Reload();
        });
        ShowCommand = new RelayCommand(p => { if (p is PluginInfo info) Ymm4.OpenFolder(info.Path); });
        OpenRootCommand = new RelayCommand(_ => { if (PluginRoot != null) { Directory.CreateDirectory(PluginRoot); Ymm4.OpenFolder(PluginRoot); } });
        ReloadCommand = new RelayCommand(_ => Reload());
        CopyToCommand = new RelayCommand(p => { if (p is PluginInfo info) CopyTo(info); }, _ => copyTarget != null);
        CopyAllToCommand = new RelayCommand(_ => { foreach (var i in Items.Where(i => !i.IsSelf).ToList()) CopyTo(i, quiet: true); main.Toast($"{Items.Count} 個のプラグインを「{copyTarget?.Name}」へコピーしました"); }, _ => copyTarget != null && Items.Count > 0);
    }

    // ---------------------------------------------------------------- どのプリセットのプラグインを見るか
    public sealed record PresetChoice(string Name, string Dir, bool IsCurrent)
    {
        public override string ToString() => IsCurrent ? Name + "(いま使っている YMM4)" : Name;
    }

    public ObservableCollection<PresetChoice> PresetChoices { get; } = new();
    PresetChoice? target;
    /// <summary>プラグインを管理するプリセット(いま使っている YMM4 とは別のものも選べる)</summary>
    public PresetChoice? Target
    {
        get => target;
        set
        {
            if (!Set(ref target, value)) return;
            Raise(nameof(PluginRoot)); Raise(nameof(TargetText));
            RefreshCopyTargets();
            Reload(keepTarget: true);
        }
    }
    /// <summary>いま見ているプラグインフォルダー</summary>
    public string? PluginRoot => target != null ? System.IO.Path.Combine(target.Dir, "user", "plugin") : Ymm4.PluginDir;
    public string TargetText => target == null ? "" : target.IsCurrent ? "いま使っている YMM4 のプラグインです" : "このプリセットは、いま使っている YMM4 ではありません(起動する時に、このプラグインが読み込まれます)";

    public ObservableCollection<PresetChoice> CopyTargets { get; } = new();
    PresetChoice? copyTarget;
    public PresetChoice? CopyTarget { get => copyTarget; set { Set(ref copyTarget, value); CommandManager.InvalidateRequerySuggested(); } }
    public ICommand CopyToCommand { get; }
    public ICommand CopyAllToCommand { get; }

    /// <summary>プリセットの一覧を読み直して、いま使っている YMM4 を選ぶ</summary>
    public void ResetTarget()
    {
        PresetChoices.Clear();
        var presets = AppSettings.Instance.Ymm4Presets.Where(p => Ymm4.IsValid(p.Path)).ToList();
        foreach (var p in presets)
        {
            bool cur = Ymm4.Dir != null && SameDir(p.Path, Ymm4.Dir);
            PresetChoices.Add(new PresetChoice(p.Name, p.Path, cur));
        }
        if (Ymm4.Dir != null && !PresetChoices.Any(c => c.IsCurrent)) PresetChoices.Insert(0, new PresetChoice("いつもの YMM4", Ymm4.Dir, true));
        target = PresetChoices.FirstOrDefault(c => c.IsCurrent);
        Raise(nameof(Target)); Raise(nameof(PluginRoot)); Raise(nameof(TargetText));
        RefreshCopyTargets();
    }

    void RefreshCopyTargets()
    {
        CopyTargets.Clear();
        foreach (var c in PresetChoices.Where(c => target == null || !SameDir(c.Dir, target.Dir))) CopyTargets.Add(c);
        CopyTarget = null;
    }

    static bool SameDir(string a, string b)
    {
        try { return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'), System.IO.Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>プラグインを、別のプリセットへコピーする</summary>
    void CopyTo(PluginInfo info, bool quiet = false)
    {
        var dest = copyTarget;
        if (dest == null) return;
        try
        {
            var root = System.IO.Path.Combine(dest.Dir, "user", "plugin");
            Directory.CreateDirectory(root);
            var to = System.IO.Path.Combine(root, System.IO.Path.GetFileName(info.Path.TrimEnd('\\')));
            if (Directory.Exists(info.Path)) CopyDir(info.Path, to);
            else File.Copy(info.Path, to, true);
            if (!quiet) main.Toast($"「{info.Name}」を「{dest.Name}」へコピーしました");
        }
        catch (Exception ex) { main.Toast($"「{info.Name}」をコピーできませんでした: " + ex.Message); }
    }

    public ObservableCollection<PluginInfo> Items { get; } = new();
    public string Message { get; private set; } = "";
    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ShowCommand { get; }
    public ICommand OpenRootCommand { get; }
    public ICommand ReloadCommand { get; }

    public void Reload() => Reload(keepTarget: false);

    public void Reload(bool keepTarget)
    {
        if (!keepTarget || PresetChoices.Count == 0) ResetTarget();
        PluginManager.RootOverride = PluginRoot;
        Items.Clear();
        var list = PluginManager.Scan(out var msg);
        foreach (var p in list) Items.Add(p);
        Message = msg;
        Raise(nameof(Message));
    }

    /// <summary>プラグイン(.ymme・.zip・.dll・フォルダー)を入れる</summary>
    public string Install(string path)
    {
        var root = PluginRoot;
        if (root == null) return "YMM4の場所が設定されていません";
        try
        {
            Directory.CreateDirectory(root);
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (Directory.Exists(path))
            {
                CopyDir(path, System.IO.Path.Combine(root, System.IO.Path.GetFileName(path)));
            }
            else if (ext is ".zip" or ".ymme")
            {
                var dest = System.IO.Path.Combine(root, name);
                System.IO.Compression.ZipFile.ExtractToDirectory(path, dest, true);
            }
            else if (ext == ".dll")
            {
                var dest = System.IO.Path.Combine(root, name);
                Directory.CreateDirectory(dest);
                File.Copy(path, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(path)), true);
            }
            else return "入れられるのは、.ymme・.zip・.dll・フォルダーです";
            Reload();
            return $"「{name}」を入れました。YMM4を再起動すると読み込まれます";
        }
        catch (Exception ex) { return "入れられませんでした: " + ex.Message + "(YMM4を閉じてから、もう一度お試しください)"; }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
    }
}

// ====================================================================== バックアップ
public sealed class BackupFile
{
    public BackupFile(FileInfo f) { Path = f.FullName; Name = f.Name; Created = f.LastWriteTime; Size = f.Length; Contents = string.Join("・", Backup.NamesIn(f.FullName).Select(BackupVm.Label)); }
    public string Path { get; }
    public string Name { get; }
    public DateTime Created { get; }
    public long Size { get; }
    public string Contents { get; }
    public string Info => $"{Created:yyyy/MM/dd HH:mm}  /  {FileItem.FormatSize(Size)}  /  {Contents}";
}

public sealed class BackupVm : NotifyBase
{
    readonly MainVm main;
    readonly AppSettings settings = AppSettings.Instance;

    public BackupVm(MainVm main)
    {
        this.main = main;
        CreateCommand = new RelayCommand(async _ => await CreateAsync(), _ => !busy);
        RestoreCommand = new RelayCommand(async p => { if (p is BackupFile b) await RestoreAsync(b); }, _ => !busy);
        DeleteCommand = new RelayCommand(p =>
        {
            if (p is not BackupFile b) return;
            if (MessageBox.Show($"バックアップ「{b.Name}」を削除しますか?", "バックアップの削除", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try { File.Delete(b.Path); } catch { }
            Reload();
        });
        ShowCommand = new RelayCommand(p => Ymm4.OpenFolder(p is BackupFile b ? b.Path : settings.BackupRoot));
    }

    public bool IncludeYmmSettings { get; set; } = true;
    public bool IncludePlugins { get; set; } = true;
    public bool IncludeExplorer { get; set; } = true;
    public bool IncludeMaterials { get; set; }
    /// <summary>YMM4のフォルダーまるごと(本体・user フォルダーすべて)。選ぶと、設定・プラグインの項目は中に含まれる</summary>
    bool includeWhole;
    public bool IncludeWholeYmm4 { get => includeWhole; set { if (Set(ref includeWhole, value)) Raise(nameof(PartsEnabled)); } }
    public bool PartsEnabled => !includeWhole;

    public ObservableCollection<BackupFile> Items { get; } = new();
    bool busy;
    public bool IsBusy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    string progress = "";
    public string Progress { get => progress; private set => Set(ref progress, value); }
    public string BackupRoot => settings.BackupRoot;

    public ICommand CreateCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ShowCommand { get; }

    public static string Label(string name) => name switch
    {
        "ymm4-whole" => "YMM4のフォルダーまるごと",
        "ymm4-setting" => "YMM4の設定",
        "ymm4-plugin" => "プラグイン",
        "ymmexplorer" => "YmmExplorer の設定・登録",
        "materials" => "素材データベース",
        _ => name,
    };

    IEnumerable<(string name, string dir)> Targets(bool settingsOnly = false)
    {
        var user = Ymm4.UserDir;
        if (includeWhole && Ymm4.Dir != null) yield return ("ymm4-whole", Ymm4.Dir);
        else
        {
            if (IncludeYmmSettings && user != null) yield return ("ymm4-setting", System.IO.Path.Combine(user, "setting"));
            if (IncludePlugins && user != null) yield return ("ymm4-plugin", System.IO.Path.Combine(user, "plugin"));
        }
        if (IncludeExplorer) yield return ("ymmexplorer", ExplorerSettings.PluginDataDir);
        if (IncludeMaterials) yield return ("materials", MaterialStore.Instance.Root);
    }

    public void Reload()
    {
        Items.Clear();
        try
        {
            if (Directory.Exists(settings.BackupRoot))
                foreach (var f in new DirectoryInfo(settings.BackupRoot).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTime))
                    Items.Add(new BackupFile(f));
        }
        catch { }
        Raise(nameof(BackupRoot));
    }

    async Task CreateAsync()
    {
        IsBusy = true;
        try
        {
            var path = System.IO.Path.Combine(settings.BackupRoot, $"YMM4_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            var targets = Targets().ToList();
            if (includeWhole && Ymm4.Dir != null && System.IO.Path.GetFullPath(settings.BackupRoot).StartsWith(System.IO.Path.GetFullPath(Ymm4.Dir), StringComparison.OrdinalIgnoreCase))
            {
                main.Toast("バックアップの保存先が YMM4 のフォルダーの中にあるため、まるごとのバックアップはできません。設定で保存先を変えてください");
                return;
            }
            // 素材データベースが YmmExplorer のフォルダーの中にある時は、二重に入れない
            if (IncludeMaterials && IncludeExplorer && MaterialStore.Instance.Root.StartsWith(ExplorerSettings.PluginDataDir, StringComparison.OrdinalIgnoreCase))
                targets.RemoveAll(t => t.name == "materials");
            await Backup.CreateAsync(path, targets, new Progress<string>(s => Progress = s));
            main.Toast("バックアップを作りました: " + path);
            Reload();
        }
        catch (Exception ex) { main.Toast("バックアップを作れませんでした: " + ex.Message); }
        finally { IsBusy = false; Progress = ""; }
    }

    async Task RestoreAsync(BackupFile b)
    {
        if (Ymm4.IsRunning) { MessageBox.Show("YMM4を閉じてから、元に戻してください。", "バックアップから戻す"); return; }
        var names = Backup.NamesIn(b.Path);
        var text = "次の内容を、バックアップの時の状態に戻します(今の内容は上書きされます)。\n\n" + string.Join("\n", names.Select(n => "・" + Label(n))) + "\n\nよろしいですか?";
        if (MessageBox.Show(text, "バックアップから戻す", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        IsBusy = true;
        try
        {
            var user = Ymm4.UserDir;
            var map = new List<(string, string)>();
            foreach (var n in names)
            {
                var dir = n switch
                {
                    "ymm4-whole" => Ymm4.Dir,
                    "ymm4-setting" when user != null => System.IO.Path.Combine(user, "setting"),
                    "ymm4-plugin" when user != null => System.IO.Path.Combine(user, "plugin"),
                    "ymmexplorer" => ExplorerSettings.PluginDataDir,
                    "materials" => MaterialStore.Instance.Root,
                    _ => null,
                };
                if (dir != null) map.Add((n, dir));
            }
            Progress = "戻しています…";
            var count = await Backup.RestoreAsync(b.Path, map);
            MaterialStore.Instance.ReloadFromDisk();
            main.Toast($"{count} 個のファイルを戻しました");
        }
        catch (Exception ex) { main.Toast("戻せませんでした: " + ex.Message); }
        finally { IsBusy = false; Progress = ""; }
    }
}
