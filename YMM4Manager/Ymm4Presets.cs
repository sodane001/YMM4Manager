using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using YmmExplorer;

namespace YMM4Manager;

/// <summary>YMM4プリセット1つ(YMM4本体のフォルダー。中の user フォルダーの設定・キャラクター・プラグインごと)</summary>
public sealed class Ymm4PresetRow : NotifyBase
{
    readonly Ymm4PresetEntry entry;
    public Ymm4PresetRow(Ymm4PresetEntry entry) { this.entry = entry; Refresh(); }
    public Ymm4PresetEntry Entry => entry;

    public string Name
    {
        get => entry.Name;
        set { var v = (value ?? "").Trim(); if (v.Length == 0 || v == entry.Name) return; entry.Name = v; AppSettings.Instance.Save(); Raise(nameof(Name)); }
    }
    public string Memo { get => entry.Memo; set { entry.Memo = value ?? ""; AppSettings.Instance.Save(); Raise(nameof(Memo)); } }
    public string Path => entry.Path;
    public bool Exists => Ymm4.IsValid(entry.Path);
    public bool IsCurrent => Ymm4.Dir != null && string.Equals(System.IO.Path.GetFullPath(Ymm4.Dir).TrimEnd('\\'), System.IO.Path.GetFullPath(entry.Path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public bool CanUse => Exists && !IsCurrent;
    public string StateText => !Exists ? "⚠ 見つかりません" : IsCurrent ? "● いま使っている YMM4" : "";
    string version = "";
    public string VersionText { get => version; private set => Set(ref version, value); }
    string size = "";
    public string SizeText { get => size; set => Set(ref size, value); }
    public string PluginText { get; private set; } = "";

    public void Refresh()
    {
        try
        {
            var exe = System.IO.Path.Combine(entry.Path, Ymm4.ExeName);
            VersionText = File.Exists(exe) ? "バージョン " + (FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?") : "";
            var plugin = System.IO.Path.Combine(entry.Path, "user", "plugin");
            PluginText = Directory.Exists(plugin) ? $"プラグイン {Directory.GetDirectories(plugin).Length + Directory.GetFiles(plugin, "*.dll").Length} 個" : "";
        }
        catch { }
        foreach (var n in new[] { nameof(Exists), nameof(IsCurrent), nameof(CanUse), nameof(StateText), nameof(PluginText) }) Raise(n);
    }
}

/// <summary>
/// 「YMM4プリセット」ページ。YMM4本体を user フォルダー(設定・キャラクター・プラグイン)ごと複製して、いくつも持ち、切り替えて使う
/// </summary>
public sealed class Ymm4PresetsVm : NotifyBase
{
    readonly MainVm main;
    readonly AppSettings settings = AppSettings.Instance;

    public Ymm4PresetsVm(MainVm main)
    {
        this.main = main;
        StartCopyCommand = new RelayCommand(_ => { NewName = $"YMM4 {DateTime.Now:MMdd-HHmm}"; IsNaming = true; }, _ => !busy && Ymm4.IsReady);
        CancelCopyCommand = new RelayCommand(_ => IsNaming = false);
        CopyCommand = new RelayCommand(async _ => await CopyCurrentAsync(NewName, null), _ => !busy && Ymm4.IsReady && NewName.Trim().Length > 0);
        UseCommand = new RelayCommand(p => { if (p is Ymm4PresetRow r) Use(r); }, _ => !busy);
        LaunchCommand = new RelayCommand(p => { if (p is Ymm4PresetRow r) Launch(r); });
        OpenCommand = new RelayCommand(p => { if (p is Ymm4PresetRow r) Ymm4.OpenFolder(r.Path); });
        RemoveCommand = new RelayCommand(async p => { if (p is Ymm4PresetRow r) await RemoveAsync(r); }, _ => !busy);
        OpenUserCommand = new RelayCommand(_ => { if (Ymm4.Dir != null) Ymm4.OpenFolder(Path.Combine(Ymm4.Dir, "user")); });
        CopyUserCommand = new RelayCommand(async _ => await CopyUserAsync(), _ => !busy && CopySource != null && Ymm4.IsReady);
    }

    // ---------------------------------------------------------------- 右側: いまのプリセットの管理(バージョン・プラグイン・ユーザー設定)
    public Ymm4PresetRow? Current => Items.FirstOrDefault(r => r.IsCurrent);
    public string CurrentTitle => Current is { } c ? $"「{c.Name}」の管理" : "いまの YMM4 の管理";
    string panel = "version";
    /// <summary>version = バージョン / plugins = プラグイン / user = ユーザー設定</summary>
    public string Panel { get => panel; set => Set(ref panel, value ?? "version"); }

    /// <summary>ユーザー設定を取り込める、ほかのプリセット</summary>
    public IEnumerable<Ymm4PresetRow> UserSources => Items.Where(r => !r.IsCurrent && r.Exists && Directory.Exists(Path.Combine(r.Path, "user")));
    Ymm4PresetRow? copySource;
    public Ymm4PresetRow? CopySource { get => copySource; set { Set(ref copySource, value); CommandManager.InvalidateRequerySuggested(); } }
    public string UserInfo
    {
        get
        {
            if (Ymm4.Dir == null) return "";
            var u = Path.Combine(Ymm4.Dir, "user");
            if (!Directory.Exists(u)) return "user フォルダーはまだありません(YMM4 を一度起動すると作られます)";
            var subs = Directory.GetDirectories(u).Select(Path.GetFileName).ToList();
            return $"{u}\n中身: {string.Join("・", subs)}";
        }
    }
    public ICommand OpenUserCommand { get; }
    public ICommand CopyUserCommand { get; }

    /// <summary>ほかのプリセットの user フォルダー(設定・キャラクターなど。プラグインは除く)を、いまのプリセットへ取り込む</summary>
    async Task CopyUserAsync()
    {
        var src = copySource;
        if (src == null || Ymm4.Dir == null) return;
        if (Ymm4.IsRunning) { main.Toast("YMM4 を閉じてから取り込んでください"); return; }
        if (MessageBox.Show($"「{src.Name}」の設定(user フォルダー。プラグインは除く)を、「{Current?.Name ?? "いまの YMM4"}」へ取り込みます。\n\n" +
                            "いまの user フォルダーは、取り込む前に同じ場所へ「user_取り込み前_日時」として残します。よろしいですか?",
                "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            var from = Path.Combine(src.Path, "user");
            var to = Path.Combine(Ymm4.Dir, "user");
            if (Directory.Exists(to))
            {
                Status = "いまの設定を残しています…";
                await FileOps.CopyFolderAsync(to, Path.Combine(Ymm4.Dir, $"user_取り込み前_{DateTime.Now:yyyyMMdd_HHmmss}"), s => Status = s);
            }
            Status = "設定を取り込んでいます…";
            await Task.Run(() =>
            {
                foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(from, f);
                    var top = rel.Split('\\')[0];
                    if (top.Equals("plugin", StringComparison.OrdinalIgnoreCase) || top.Equals("log", StringComparison.OrdinalIgnoreCase)) continue;
                    var dst = Path.Combine(to, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, true);
                }
            });
            Status = $"✓ 「{src.Name}」の設定を取り込みました(プラグインは、このプリセットのままです)";
            Raise(nameof(UserInfo));
        }
        catch (Exception ex) { Status = "取り込めませんでした: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public ObservableCollection<Ymm4PresetRow> Items { get; } = new();
    bool busy;
    public bool IsBusy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    string status = "";
    public string Status { get => status; set => Set(ref status, value); }
    bool isNaming;
    public bool IsNaming { get => isNaming; set => Set(ref isNaming, value); }
    string newName = "";
    public string NewName { get => newName; set { if (Set(ref newName, value ?? "")) CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>複製を置く場所(専用ストレージがあれば、その中の「YMM4プリセット」)</summary>
    public string PresetRoot => !string.IsNullOrWhiteSpace(settings.StorageRoot) && Directory.Exists(settings.StorageRoot)
        ? Path.Combine(settings.StorageRoot!, "YMM4プリセット")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YMM4プリセット");

    public ICommand StartCopyCommand { get; }
    public ICommand CancelCopyCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand UseCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }

    public void Reload()
    {
        // いま使っている YMM4 も、一覧に入れておく
        if (Ymm4.IsReady && !settings.Ymm4Presets.Any(p => SamePath(p.Path, Ymm4.Dir!)))
        {
            settings.Ymm4Presets.Insert(0, new Ymm4PresetEntry { Name = "いつもの YMM4", Path = Ymm4.Dir! });
            settings.Save();
        }
        Items.Clear();
        foreach (var e in settings.Ymm4Presets) Items.Add(new Ymm4PresetRow(e));
        Raise(nameof(PresetRoot));
        foreach (var n in new[] { nameof(Current), nameof(CurrentTitle), nameof(UserSources), nameof(UserInfo) }) Raise(n);
        CopySource = null;
        _ = MeasureAsync();
    }

    async Task MeasureAsync()
    {
        foreach (var r in Items.ToList())
        {
            if (!Directory.Exists(r.Path)) continue;
            var size = await Task.Run(() => FileOps.FolderSize(r.Path));
            r.SizeText = size < 0 ? "" : DriveChoice.Size(size);
        }
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>いまの YMM4 を、user フォルダーごとそのままコピーする(destDir が null なら、プリセットの場所に名前のフォルダーを作る)</summary>
    public async Task CopyCurrentAsync(string name, string? destDir)
    {
        if (!Ymm4.IsReady || Ymm4.Dir == null) return;
        name = string.Join("_", (name ?? "").Trim().Split(Path.GetInvalidFileNameChars()));
        if (name.Length == 0) name = $"YMM4 {DateTime.Now:yyyyMMdd-HHmmss}";
        var to = destDir ?? Path.Combine(PresetRoot, name);
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
        {
            if (destDir != null) to = Path.Combine(destDir, Path.GetFileName(Path.GetFullPath(Ymm4.Dir).TrimEnd('\\')) + $" のコピー {DateTime.Now:MMdd-HHmm}");
            else { main.Toast($"「{to}」はもう使われています。別の名前にしてください"); return; }
        }
        if (SamePath(to, Ymm4.Dir) || Path.GetFullPath(to).StartsWith(Path.GetFullPath(Ymm4.Dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        { main.Toast("YMM4 のフォルダーの中には、コピーできません"); return; }
        if (Ymm4.IsRunning && MessageBox.Show("YMM4 が起動しています。設定などが書き込み途中だと、正しくコピーできないことがあります。YMM4 を閉じてからがおすすめです。このまま続けますか?",
                "YMM4 マネージャー", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        IsBusy = true;
        IsNaming = false;
        try
        {
            Status = "YMM4 をコピーしています…";
            await FileOps.CopyFolderAsync(Ymm4.Dir, to, s => Status = s);
            var entry = new Ymm4PresetEntry { Edition = Current?.Entry.Edition, Name = destDir == null ? name : Path.GetFileName(to), Path = to, Memo = $"{DateTime.Now:yyyy/MM/dd} に「{Path.GetFileName(Ymm4.Dir.TrimEnd('\\'))}」からコピー" };
            settings.Ymm4Presets.Add(entry);
            settings.Save();
            Status = $"✓ コピーしました: {to}";
            main.Toast("YMM4 をコピーしました");
            Reload();
        }
        catch (Exception ex) { Status = "コピーできませんでした: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>すでにある YMM4 のフォルダーを、プリセットとして登録する</summary>
    public void AddExisting(string dir)
    {
        if (!Ymm4.IsValid(dir)) { main.Toast("YukkuriMovieMaker.exe があるフォルダーを選んでください"); return; }
        if (settings.Ymm4Presets.Any(p => SamePath(p.Path, dir))) { main.Toast("もう登録してあります"); return; }
        settings.Ymm4Presets.Add(new Ymm4PresetEntry { Name = Path.GetFileName(dir.TrimEnd('\\')), Path = dir });
        settings.Save();
        Reload();
    }

    void Use(Ymm4PresetRow r)
    {
        if (!r.Exists) return;
        if (Ymm4.IsRunning) { main.Toast("YMM4 を閉じてから切り替えてください"); return; }
        if (main.SetYmm4Dir(r.Path)) { Status = $"「{r.Name}」を使うようにしました(起動ボタン・プラグイン・アップデートなどは、この YMM4 が対象になります)"; Reload(); }
    }

    void Launch(Ymm4PresetRow r)
    {
        if (!r.Exists) { main.Toast("この YMM4 は見つかりません"); return; }
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(r.Path, Ymm4.ExeName)) { WorkingDirectory = r.Path, UseShellExecute = true });
            main.Toast($"「{r.Name}」の YMM4 を起動しました");
        }
        catch (Exception ex) { main.Toast("起動できませんでした: " + ex.Message); }
    }

    async Task RemoveAsync(Ymm4PresetRow r)
    {
        if (r.IsCurrent) { main.Toast("いま使っている YMM4 は外せません。先に別のプリセットに切り替えてください"); return; }
        var ans = MessageBox.Show($"「{r.Name}」を一覧から外します。\n\nフォルダー({r.Path})も削除しますか?\n\n「はい」= フォルダーごと削除 / 「いいえ」= 一覧から外すだけ",
            "YMM4 マネージャー", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (ans == MessageBoxResult.Cancel) return;
        if (ans == MessageBoxResult.Yes && Directory.Exists(r.Path))
        {
            IsBusy = true;
            Status = "削除しています…";
            try { await Task.Run(() => Directory.Delete(r.Path, true)); }
            catch (Exception ex) { Status = "フォルダーを削除できませんでした: " + ex.Message; IsBusy = false; return; }
            IsBusy = false;
        }
        settings.Ymm4Presets.Remove(r.Entry);
        settings.Save();
        Status = $"「{r.Name}」を外しました";
        Reload();
    }
}
