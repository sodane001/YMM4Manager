using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using YmmExplorer;

namespace YMM4Manager;

/// <summary>選べるドライブ</summary>
public sealed class DriveChoice
{
    public DriveChoice(DriveInfo d)
    {
        Root = d.RootDirectory.FullName;
        string label = "";
        try { label = d.VolumeLabel; } catch { }
        var type = d.DriveType switch { DriveType.Removable => "USB など", DriveType.Fixed => "内蔵・外付け", DriveType.Network => "ネットワーク", _ => "" };
        Text = $"{Root.TrimEnd('\\')} {label}  空き {Size(d.AvailableFreeSpace)} / {Size(d.TotalSize)}" + (type.Length > 0 ? $"({type})" : "");
    }
    public string Root { get; }
    public string Text { get; }
    public override string ToString() => Text;
    public static string Size(long b) => b >= 1L << 40 ? $"{b / (double)(1L << 40):0.0} TB" : b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{b / (double)(1L << 20):0} MB";
}

/// <summary>専用ストレージへ入れるもの1つ(YMM4本体・素材データベース など)</summary>
public sealed class StorageItem : NotifyBase
{
    public StorageItem(string id, string name, string icon, string description, string subFolder)
    { Id = id; Name = name; Icon = icon; Description = description; SubFolder = subFolder; }

    public string Id { get; }
    public string Name { get; }
    public string Icon { get; }
    public string Description { get; }
    /// <summary>専用ストレージの中のフォルダー名</summary>
    public string SubFolder { get; }

    string current = "";
    public string Current { get => current; set { if (Set(ref current, value)) Raise(nameof(CurrentText)); } }
    public string CurrentText => string.IsNullOrEmpty(current) ? "(まだありません)" : current;
    bool isInside;
    /// <summary>もう専用ストレージの中にある</summary>
    public bool IsInside { get => isInside; set { if (Set(ref isInside, value)) { Raise(nameof(StateText)); Raise(nameof(CanMove)); } } }
    bool available = true;
    public bool Available { get => available; set { if (Set(ref available, value)) Raise(nameof(CanMove)); } }
    public bool CanMove => !isInside && available;
    public string StateText => isInside ? "✓ 専用ストレージに入っています" : "";
    string sizeText = "";
    public string SizeText { get => sizeText; set => Set(ref sizeText, value); }
}

/// <summary>
/// 「YMM4専用ストレージ」ページ。ドライブ(またはフォルダー)を YMM4 専用に登録して、
/// YMM4本体・素材データベース・プロジェクト・バックアップ・過去のバージョンを、すべてその中にまとめる
/// </summary>
public sealed class StorageVm : NotifyBase
{
    readonly MainVm main;
    readonly AppSettings settings = AppSettings.Instance;
    const string MarkerName = "YMM4専用ストレージ.json";

    public StorageVm(MainVm main)
    {
        this.main = main;
        Items = new ObservableCollection<StorageItem>
        {
            new("ymm4", "YMM4本体", "", "YukkuriMovieMaker.exe があるフォルダー(中の user フォルダーの設定・キャラクター・プラグインも一緒)", "YMM4"),
            new("materials", "素材データベース", "", "YmmExplorer・マネージャーの素材データベース(素材のファイルとタグなど)", "素材データベース"),
            new("projects", "プロジェクト", "", "新しいプロジェクトを作る場所。今あるプロジェクトのフォルダーも一緒に移せます", "プロジェクト"),
            new("backups", "バックアップ", "", "バックアップ(zip)の保存先", "バックアップ"),
            new("versions", "YMM4の過去のバージョン", "", "アップデートの前にとっておいた、YMM4 の前のバージョン(zip)", "YMM4の過去のバージョン"),
        };
        RefreshDrivesCommand = new RelayCommand(_ => RefreshDrives());
        UseDriveCommand = new RelayCommand(_ =>
        {
            if (SelectedDrive == null) { main.Toast("ドライブを選んでください"); return; }
            Register(Path.Combine(SelectedDrive.Root, "YMM4"));
        }, _ => !busy);
        UnregisterCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show("専用ストレージの登録を外しますか?(中のファイルはそのまま残ります。YMM4 などの場所も、今のまま変わりません)", "YMM4 マネージャー",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            settings.StorageRoot = null;
            settings.Save();
            Refresh();
        }, _ => !busy && IsRegistered);
        OpenCommand = new RelayCommand(p =>
        {
            var path = p is StorageItem it ? it.Current : Root;
            if (!string.IsNullOrEmpty(path)) Ymm4.OpenFolder(path);
        });
        MoveCommand = new RelayCommand(async p => { if (p is StorageItem it) await MoveAsync(new[] { it }); }, _ => !busy && IsReachable);
        MoveAllCommand = new RelayCommand(async _ => await MoveAsync(Items.Where(i => i.CanMove).ToList()), _ => !busy && IsReachable && Items.Any(i => i.CanMove));
        OpenMaterialsCommand = new RelayCommand(_ => { var d = MaterialsDir; if (d != null) { Directory.CreateDirectory(d); Ymm4.OpenFolder(d); } }, _ => IsReachable);
        BackupCommand = new RelayCommand(async _ => await RunBackupAsync(false), _ => !busy && IsReachable && !string.IsNullOrWhiteSpace(settings.StorageBackupDir));
        // 時間になったら、自動でバックアップ(マネージャーを開いている間)
        backupTimer.Tick += async (_, _) => { RaiseBackup(); if (StorageBackup.IsDue() && IsReachable) await RunBackupAsync(true); };
        backupTimer.Start();
        RefreshDrives();
    }

    /// <summary>専用ストレージの「素材」フォルダー(ここに入れた素材は、YMM4 の AIエディットの AI がいつも見られる・配置できる)</summary>
    public string? MaterialsDir => IsRegistered ? Path.Combine(Root!, "素材") : null;
    string materialsText = "";
    public string MaterialsText { get => materialsText; private set => Set(ref materialsText, value); }
    public ICommand OpenMaterialsCommand { get; }

    /// <summary>専用ストレージの「録画」フォルダー(ゲームの録画などを自由に入れる場所。AIエディットの AI からも見えます)</summary>
    public string? RecordingsDir => IsRegistered ? Path.Combine(Root!, "録画") : null;
    string recordingsText = "";
    public string RecordingsText { get => recordingsText; private set => Set(ref recordingsText, value); }
    ICommand? openRecordings;
    public ICommand OpenRecordingsCommand => openRecordings ??= new RelayCommand(_ => { var d = RecordingsDir; if (d != null) { Directory.CreateDirectory(d); Ymm4.OpenFolder(d); } }, _ => IsReachable);

    /// <summary>専用ストレージの「立ち絵」フォルダー(キャラクターの立ち絵の素材を入れる場所。AIエディットの AI からも見えます)</summary>
    public string? PortraitsDir => IsRegistered ? Path.Combine(Root!, "立ち絵") : null;
    string portraitsText = "";
    public string PortraitsText { get => portraitsText; private set => Set(ref portraitsText, value); }
    ICommand? openPortraits;
    public ICommand OpenPortraitsCommand => openPortraits ??= new RelayCommand(_ => { var d = PortraitsDir; if (d != null) { Directory.CreateDirectory(d); Ymm4.OpenFolder(d); } }, _ => IsReachable);

    // ---- バックアップから戻す
    public ObservableCollection<StorageBackup.BackupPoint> BackupPoints { get; } = new();
    StorageBackup.BackupPoint? selectedPoint;
    public StorageBackup.BackupPoint? SelectedPoint { get => selectedPoint; set { Set(ref selectedPoint, value); CommandManager.InvalidateRequerySuggested(); } }
    bool keepBeforeRestore = true;
    /// <summary>戻す前に、今の状態も保存先に残す</summary>
    public bool KeepBeforeRestore { get => keepBeforeRestore; set => Set(ref keepBeforeRestore, value); }
    public bool HasBackupPoints => BackupPoints.Count > 0;
    ICommand? restoreCommand;
    public ICommand RestoreCommand => restoreCommand ??= new RelayCommand(async _ => await RestoreAsync(), _ => !busy && IsReachable && selectedPoint != null);

    void LoadBackupPoints()
    {
        var keep = selectedPoint?.Path;
        BackupPoints.Clear();
        foreach (var b in StorageBackup.ListBackups()) BackupPoints.Add(b);
        SelectedPoint = BackupPoints.FirstOrDefault(b => b.Path == keep) ?? BackupPoints.FirstOrDefault();
        Raise(nameof(HasBackupPoints));
    }

    async Task RestoreAsync()
    {
        var point = selectedPoint;
        if (point == null) return;
        if (Ymm4.IsRunning) { main.Toast("YMM4 を閉じてから戻してください(YMM4 や YmmExplorer が中のファイルを使っているため)"); return; }
        if (MessageBox.Show($"専用ストレージを「{point.Label}」の状態に戻します。\n\n・それ以降に足したファイルは消え、変えたファイルは元に戻ります\n" +
                            (keepBeforeRestore ? "・戻す前の今の状態は、バックアップの保存先に「戻す前」として残します\n" : "・今の状態は残しません(元に戻せません)\n") +
                            "\nよろしいですか?", "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            var result = await StorageBackup.RestoreAsync(point, keepBeforeRestore, s => Application.Current.Dispatcher.BeginInvoke(new Action(() => Status = s)));
            Status = result;
            main.Toast(result);
            // 素材データベースなどを読み直す
            try { MaterialStore.Instance.ReloadFromDisk(); main.Materials.Refresh(); } catch { }
        }
        finally { IsBusy = false; Refresh(); }
    }

    public ICommand BackupCommand { get; }

    public ObservableCollection<StorageItem> Items { get; }
    public ObservableCollection<DriveChoice> Drives { get; } = new();
    DriveChoice? selectedDrive;
    public DriveChoice? SelectedDrive { get => selectedDrive; set => Set(ref selectedDrive, value); }

    public string? Root => settings.StorageRoot;
    public bool IsRegistered => !string.IsNullOrWhiteSpace(settings.StorageRoot);
    public bool IsNotRegistered => !IsRegistered;
    /// <summary>登録した場所に、今つながっている(ドライブが外れていない)</summary>
    public bool IsReachable => IsRegistered && Directory.Exists(Path.GetPathRoot(Root!)) && (Directory.Exists(Root!) || TryCreate(Root!));
    public string RootText => !IsRegistered ? "まだ登録していません" : IsReachable ? Root! : Root + "(見つかりません。ドライブがつながっているか確かめてください)";
    string spaceText = "";
    public string SpaceText { get => spaceText; private set => Set(ref spaceText, value); }
    double usedRatio;
    public double UsedRatio { get => usedRatio; private set => Set(ref usedRatio, value); }

    bool busy;
    public bool IsBusy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    string status = "";
    public string Status { get => status; set => Set(ref status, value); }

    public ICommand RefreshDrivesCommand { get; }
    public ICommand UseDriveCommand { get; }
    public ICommand UnregisterCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand MoveAllCommand { get; }

    static bool TryCreate(string dir) { try { Directory.CreateDirectory(dir); return true; } catch { return false; } }

    void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network) Drives.Add(new DriveChoice(d)); }
            catch { }
        }
        if (IsRegistered) SelectedDrive = Drives.FirstOrDefault(d => Root!.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>フォルダーを専用ストレージにする(ドライブを選んだ時は、そのドライブの「YMM4」フォルダー)</summary>
    public void Register(string dir)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (Ymm4.Dir != null && dir.StartsWith(Path.GetFullPath(Ymm4.Dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            { main.Toast("YMM4本体のフォルダーの中は、専用ストレージにできません"); return; }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerName), JsonSerializer.Serialize(new { 説明 = "YMM4 マネージャーの「YMM4専用ストレージ」です", 登録日 = DateTime.Now }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Directory.CreateDirectory(Path.Combine(dir, "素材"));
            Directory.CreateDirectory(Path.Combine(dir, "録画"));
            Directory.CreateDirectory(Path.Combine(dir, "立ち絵"));
            settings.StorageRoot = dir;
            settings.Save();
            Status = $"{dir} を YMM4専用ストレージにしました。下の「ここへ移す」で、YMM4本体や素材データベースをまとめられます";
            Refresh();
        }
        catch (Exception ex) { main.Toast("登録できませんでした: " + ex.Message); }
    }

    public void Refresh()
    {
        foreach (var n in new[] { nameof(Root), nameof(IsRegistered), nameof(IsNotRegistered), nameof(IsReachable), nameof(RootText) }) Raise(n);
        var root = IsReachable ? Path.GetFullPath(Root!).TrimEnd('\\') + "\\" : null;
        foreach (var it in Items)
        {
            it.Current = PathOf(it.Id) ?? "";
            it.IsInside = root != null && it.Current.Length > 0 && (Path.GetFullPath(it.Current).TrimEnd('\\') + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase);
            it.Available = it.Id != "ymm4" || Ymm4.IsReady;
            it.SizeText = "";
        }
        try
        {
            if (root != null)
            {
                var d = new DriveInfo(Path.GetPathRoot(root)!);
                SpaceText = $"{d.RootDirectory.FullName.TrimEnd('\\')} の空き {DriveChoice.Size(d.AvailableFreeSpace)} / {DriveChoice.Size(d.TotalSize)}";
                UsedRatio = d.TotalSize > 0 ? 1 - d.AvailableFreeSpace / (double)d.TotalSize : 0;
            }
            else { SpaceText = ""; UsedRatio = 0; }
        }
        catch { SpaceText = ""; }
        try
        {
            if (root != null)
            {
                var md = MaterialsDir!;
                Directory.CreateDirectory(md);
                var n = Directory.EnumerateFiles(md, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Count();
                MaterialsText = $"{md}(ファイル {n} 個)";
                var rd = RecordingsDir!;
                Directory.CreateDirectory(rd);
                var rec = new DirectoryInfo(rd).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
                RecordingsText = $"{rd}(ファイル {rec.Count} 個・{DriveChoice.Size(rec.Sum(f => f.Length))})";
                var pd = PortraitsDir!;
                Directory.CreateDirectory(pd);
                var chars = Directory.GetDirectories(pd).Length;
                var pn = Directory.EnumerateFiles(pd, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Count();
                PortraitsText = $"{pd}(キャラクター {chars} 人分のフォルダー・ファイル {pn} 個)";
            }
            else { MaterialsText = ""; RecordingsText = ""; PortraitsText = ""; }
        }
        catch { MaterialsText = ""; }
        Raise(nameof(MaterialsDir)); Raise(nameof(RecordingsDir)); Raise(nameof(PortraitsDir));
        LoadBackupPoints();
        RaiseBackup();
        _ = MeasureAsync();
    }

    // ---------------------------------------------------------------- 定期バックアップ(決めたフォルダーへ、決めた間隔で、中身をすべてコピー)
    public string BackupDir => string.IsNullOrWhiteSpace(settings.StorageBackupDir) ? "(まだ決めていません)" : settings.StorageBackupDir!;
    public void SetBackupDir(string dir)
    {
        if (IsRegistered)
        {
            var root = Path.GetFullPath(Root!).TrimEnd('\\') + "\\";
            if ((Path.GetFullPath(dir).TrimEnd('\\') + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase)) { main.Toast("専用ストレージの中は選べません。ほかのドライブやフォルダーを選んでください"); return; }
        }
        settings.StorageBackupDir = dir;
        settings.Save();
        RaiseBackup();
        try { LoadBackupPoints(); } catch { }
        Status = StorageBackup.IsRunning
            ? $"バックアップの保存先を「{dir}」にしました(いま動いているバックアップは前の保存先のまま終わります。次から新しい保存先に入ります)"
            : $"バックアップの保存先を「{dir}」にしました";
        main.Toast("バックアップの保存先を変えました");
        if (settings.StorageBackupTask) Status = StorageBackup.UpdateTask();
    }
    public IReadOnlyList<string> IntervalChoices { get; } = StorageBackup.Intervals.Select(i => i.Label).ToList();
    public int IntervalIndex
    {
        get => Math.Max(0, Array.FindIndex(StorageBackup.Intervals, i => Math.Abs(i.Hours - settings.StorageBackupHours) < 0.01));
        set
        {
            if (value < 0 || value >= StorageBackup.Intervals.Length) return;
            settings.StorageBackupHours = StorageBackup.Intervals[value].Hours;
            settings.Save();
            RaiseBackup();
            if (settings.StorageBackupTask) Status = StorageBackup.UpdateTask();
        }
    }
    public IReadOnlyList<string> KeepChoices { get; } = StorageBackup.KeepModes.Select(k => k.Label).ToList();
    public int KeepIndex
    {
        get => Math.Max(0, Array.FindIndex(StorageBackup.KeepModes, k => k.Keep == settings.StorageBackupKeep));
        set { if (value >= 0 && value < StorageBackup.KeepModes.Length) { settings.StorageBackupKeep = StorageBackup.KeepModes[value].Keep; settings.Save(); RaiseBackup(); } }
    }
    /// <summary>ZIP 1つにまとめる(クラウドストレージ向け)</summary>
    public bool BackupZip { get => settings.StorageBackupZip; set { settings.StorageBackupZip = value; settings.Save(); Raise(nameof(BackupZip)); } }

    // ---- クラウドストレージ(Dropbox など)に入れる
    List<CloudFolders.Cloud>? clouds;
    public List<CloudFolders.Cloud> CloudChoices => clouds ??= CloudFolders.Find();
    CloudFolders.Cloud? selectedCloud;
    public CloudFolders.Cloud? SelectedCloud { get => selectedCloud ?? CloudChoices.FirstOrDefault(); set => Set(ref selectedCloud, value); }
    public bool HasClouds => CloudChoices.Count > 0;
    public string CloudNote => HasClouds
        ? "選んだクラウドの同期フォルダーの中に「YMM4バックアップ」を作って、そこへバックアップします。クラウドのアプリが自動でアップロードします(クラウドの空き容量に気をつけてください)。"
        : "この PC に Dropbox・OneDrive・Google ドライブなどのアプリが見つかりませんでした。アプリを入れてから「探し直す」を押すか、同期フォルダーを上の「選ぶ…」で選んでください。";
    ICommand? useCloud, findClouds;
    public ICommand UseCloudCommand => useCloud ??= new RelayCommand(_ =>
    {
        var c = SelectedCloud;
        if (c == null) return;
        SetBackupDir(Path.Combine(c.Path, "YMM4バックアップ"));
        if (!settings.StorageBackupZip) { BackupZip = true; }
        Status = $"バックアップの保存先を {c.Name} にしました。クラウド向けに「ZIP 1つにまとめる」をオンにしています(ファイルが多くても同期が速くなります)";
    }, _ => SelectedCloud != null);
    public ICommand FindCloudsCommand => findClouds ??= new RelayCommand(_ =>
    {
        clouds = null; selectedCloud = null;
        Raise(nameof(CloudChoices)); Raise(nameof(SelectedCloud)); Raise(nameof(HasClouds)); Raise(nameof(CloudNote));
        Status = HasClouds ? $"クラウドストレージが {CloudChoices.Count} つ見つかりました" : "クラウドストレージのアプリが見つかりませんでした";
    });

    public bool ExcludeBackups { get => settings.StorageBackupExclude; set { settings.StorageBackupExclude = value; settings.Save(); Raise(nameof(ExcludeBackups)); } }
    public bool UseTask
    {
        get => settings.StorageBackupTask;
        set { settings.StorageBackupTask = value; settings.Save(); Raise(nameof(UseTask)); Status = StorageBackup.UpdateTask(); }
    }
    public string LastText => settings.StorageBackupResult ?? (settings.StorageBackupLast is { } t ? $"前回: {t:yyyy/MM/dd HH:mm}" : "まだバックアップしていません");
    public string NextText => StorageBackup.Next is { } n ? $"次のバックアップ: {(n <= DateTime.Now.AddMinutes(1) ? "まもなく" : n.ToString("yyyy/MM/dd HH:mm"))}" + (settings.StorageBackupTask ? "" : "(マネージャーを開いている間)") : "自動のバックアップはしません";
    /// <summary>設定ファイルがほかで変わった時に、バックアップの表示を新しくする</summary>
    public void SettingsReloaded()
    {
        RaiseBackup();
        Raise(nameof(BackupZip)); Raise(nameof(ExcludeBackups)); Raise(nameof(UseTask));
        LoadBackupPoints();
    }
    void RaiseBackup() { foreach (var n in new[] { nameof(BackupDir), nameof(IntervalIndex), nameof(KeepIndex), nameof(LastText), nameof(NextText) }) Raise(n); CommandManager.InvalidateRequerySuggested(); }

    readonly System.Windows.Threading.DispatcherTimer backupTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    /// <summary>今すぐ(または時間になったので)バックアップする</summary>
    async Task RunBackupAsync(bool auto)
    {
        if (StorageBackup.IsRunning || busy) return;
        if (!auto && string.IsNullOrWhiteSpace(settings.StorageBackupDir)) { main.Toast("先に、バックアップの保存先を選んでください"); return; }
        IsBusy = true;
        try
        {
            var result = await StorageBackup.RunAsync(s => Application.Current.Dispatcher.BeginInvoke(new Action(() => Status = s)));
            Status = result;
            if (!auto || !result.StartsWith("✓")) main.Toast(auto ? "専用ストレージの自動バックアップ: " + result : result);
        }
        finally { IsBusy = false; RaiseBackup(); LoadBackupPoints(); }
    }

    /// <summary>それぞれの大きさを、裏で数える</summary>
    async Task MeasureAsync()
    {
        foreach (var it in Items.ToList())
        {
            var path = it.Current;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { it.SizeText = ""; continue; }
            var size = await Task.Run(() => FileOps.FolderSize(path));
            it.SizeText = size < 0 ? "" : DriveChoice.Size(size);
        }
    }

    string? PathOf(string id) => id switch
    {
        "ymm4" => Ymm4.Dir,
        "materials" => main.Materials.LibraryPath,
        "projects" => settings.ProjectRoot,
        "backups" => settings.BackupRoot,
        "versions" => Ymm4UpdaterVm.VersionsDir,
        _ => null,
    };

    string Target(StorageItem it)
    {
        if (it.Id == "ymm4" && Ymm4.Dir != null) return Path.Combine(Root!, Path.GetFileName(Path.GetFullPath(Ymm4.Dir).TrimEnd('\\')));
        return Path.Combine(Root!, it.SubFolder);
    }

    // ---------------------------------------------------------------- 移す
    async Task MoveAsync(IReadOnlyList<StorageItem> list)
    {
        if (list.Count == 0 || !IsReachable) return;
        if (Ymm4.IsRunning) { main.Toast("YMM4を閉じてから移してください(YMM4 や、その中の YmmExplorer が使っているため)"); return; }
        var names = string.Join("・", list.Select(i => i.Name));
        if (MessageBox.Show($"{names} を、専用ストレージ({Root})へ移します。\n\n中身をコピーして、うまくいったら元の場所から消します。大きいものは時間がかかります。よろしいですか?",
                "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsBusy = true;
        var done = new List<string>();
        try
        {
            foreach (var it in list)
            {
                Status = $"{it.Name} を移しています…";
                var msg = it.Id switch
                {
                    "ymm4" => await MoveYmm4Async(it),
                    "materials" => await MoveMaterialsAsync(it),
                    "projects" => await MoveProjectsAsync(it),
                    "backups" => await MoveSimpleAsync(it, settings.BackupRoot, d => settings.BackupDir = d),
                    "versions" => await MoveSimpleAsync(it, Ymm4UpdaterVm.VersionsDir, d => settings.VersionsDir = d),
                    _ => null,
                };
                settings.Save();
                if (msg != null) { Status = $"{it.Name}: {msg}"; if (!msg.StartsWith("✓")) break; }
                done.Add(it.Name);
            }
            if (done.Count == list.Count) Status = $"✓ {string.Join("・", done)} を専用ストレージへ移しました";
            main.Toast(Status);
        }
        catch (Exception ex) { Status = "移せませんでした: " + ex.Message; }
        finally
        {
            IsBusy = false;
            Refresh();
            main.Backups.Reload();
            main.Projects.RefreshSettings();
        }
    }

    /// <summary>フォルダーをまるごと移す(同じドライブなら一瞬。ちがうドライブなら、コピーしてから元を消す)</summary>
    async Task MoveFolderAsync(string from, string to)
    {
        from = Path.GetFullPath(from).TrimEnd('\\');
        to = Path.GetFullPath(to).TrimEnd('\\');
        if (to.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("移す先が、元のフォルダーの中です");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) throw new InvalidOperationException($"移す先「{to}」が空ではありません");
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(to)) Directory.Delete(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            await Task.Run(() => Directory.Move(from, to));
            return;
        }
        var count = await FileOps.CopyFolderAsync(from, to, s => Status = s);
        // コピーできたか確かめてから、元を消す
        var copiedCount = await Task.Run(() => Directory.GetFiles(to, "*", SearchOption.AllDirectories).Length);
        if (copiedCount < count) throw new InvalidOperationException("コピーしたファイルの数が合いません。元の場所は消していません");
        Status = "元の場所を片付けています…";
        await Task.Run(() => { try { Directory.Delete(from, true); } catch { } });
    }

    async Task<string?> MoveYmm4Async(StorageItem it)
    {
        if (!Ymm4.IsReady || Ymm4.Dir == null) return "YMM4 の場所が決まっていません";
        var to = Target(it);
        await MoveFolderAsync(Ymm4.Dir, to);
        main.SetYmm4Dir(to);
        return "✓ 移しました。デスクトップなどの YMM4 のショートカットは、新しい場所を指すように作り直してください";
    }

    async Task<string?> MoveMaterialsAsync(StorageItem it)
    {
        var to = Target(it);
        await main.Materials.MoveLibraryAsync(to);
        return string.Equals(Path.GetFullPath(main.Materials.LibraryPath).TrimEnd('\\'), Path.GetFullPath(to).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? "✓ 移しました" : "移せませんでした(素材データベースのページのお知らせを見てください)";
    }

    async Task<string?> MoveProjectsAsync(StorageItem it)
    {
        var to = Target(it);
        var from = settings.ProjectRoot;
        Directory.CreateDirectory(to);
        if (!string.IsNullOrWhiteSpace(from) && Directory.Exists(from) && Directory.EnumerateFileSystemEntries(from).Any())
        {
            var ans = MessageBox.Show($"今のプロジェクトのフォルダー({from})の中身も、専用ストレージへ移しますか?\n\n" +
                                      "「いいえ」なら、これから作るプロジェクトだけが専用ストレージに入ります。\n" +
                                      "(移したプロジェクトは、YMM4 の「最近使ったプロジェクト」からは開けなくなるので、ここのプロジェクトのページから開いてください)",
                                      "YMM4 マネージャー", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Cancel) return "取りやめました";
            if (ans == MessageBoxResult.Yes)
            {
                await MoveFolderAsync(from!, to);
                settings.ProjectFolders.RemoveAll(f => string.Equals(Path.GetFullPath(f).TrimEnd('\\'), Path.GetFullPath(from!).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            }
        }
        settings.ProjectRoot = to;
        if (!settings.ProjectFolders.Any(f => string.Equals(f, to, StringComparison.OrdinalIgnoreCase))) settings.ProjectFolders.Add(to);
        return "✓ 新しいプロジェクトは、専用ストレージに作るようにしました";
    }

    /// <summary>バックアップ・過去のバージョン: 中のファイルを移して、保存先を変える</summary>
    async Task<string?> MoveSimpleAsync(StorageItem it, string from, Action<string> setDir)
    {
        var to = Target(it);
        if (Directory.Exists(from) && Directory.EnumerateFileSystemEntries(from).Any()) await MoveFolderAsync(from, to);
        else Directory.CreateDirectory(to);
        setDir(to);
        return "✓ 移しました";
    }
}

/// <summary>フォルダーのコピー(進み具合つき)</summary>
public static class FileOps
{
    /// <summary>from の中身を to へコピーする。返すのはファイルの数</summary>
    public static async Task<int> CopyFolderAsync(string from, string to, Action<string> progress)
    {
        var files = await Task.Run(() => Directory.GetFiles(from, "*", SearchOption.AllDirectories));
        long total = 0, copied = 0;
        foreach (var f in files) try { total += new FileInfo(f).Length; } catch { }
        await Task.Run(() =>
        {
            int n = 0;
            foreach (var f in files)
            {
                var dst = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
                try { copied += new FileInfo(f).Length; } catch { }
                if (++n % 20 == 0 || n == files.Length)
                {
                    var pct = total > 0 ? copied * 100.0 / total : 100;
                    var text = $"コピーしています… {pct:0}%({n}/{files.Length} ファイル・{DriveChoice.Size(copied)})";
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => progress(text)));
                }
            }
            // 空のフォルダーも作っておく
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        });
        return files.Length;
    }

    public static long FolderSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
        catch { return -1; }
    }
}
