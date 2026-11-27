using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using YmmExplorer;

namespace YMM4Manager;

/// <summary>YMM4のバージョン(GitHub のリリース、またはこのソフトが保存した以前のバージョン)</summary>
public sealed class Ymm4Release
{
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime Date { get; init; }
    public string? Url { get; init; }          // ダウンロードする zip
    public string? LocalZip { get; init; }     // このPCに保存した以前のバージョン
    public bool IsLocal => LocalZip != null;
    public string Info => IsLocal ? $"このPCに保存したバージョン  /  {Date:yyyy/MM/dd}" : $"公開日 {Date:yyyy/MM/dd}";
    public string Label => $"{Version}  {Title}".Trim();
}

/// <summary>
/// YMM4のアップデート・以前のバージョンへの切り替え。
/// 入れ替える前に、今のバージョン(user フォルダー以外)を保存しておくので、あとで戻せる。user フォルダー(設定・プラグイン)は変えない
/// </summary>
public sealed class Ymm4UpdaterVm : NotifyBase
{
    const string ReleasesApi = "https://api.github.com/repos/manju-summoner/YukkuriMovieMaker4/releases?per_page=60";
    const string LatestFull = "https://files.manjubox.net/YukkuriMovieMaker_v4.zip";
    const string LatestLite = "https://files.manjubox.net/YukkuriMovieMaker_v4_Lite.zip";
    public static string DefaultVersionsDir => Path.Combine(AppSettings.Dir, "ymm4-versions");
    public static string VersionsDir => string.IsNullOrWhiteSpace(AppSettings.Instance.VersionsDir) ? DefaultVersionsDir : AppSettings.Instance.VersionsDir!;

    readonly MainVm main;
    static readonly HttpClient http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("YMM4Manager/1.1");
        return h;
    }

    public Ymm4UpdaterVm(MainVm main)
    {
        this.main = main;
        CheckCommand = new RelayCommand(async _ => await LoadReleasesAsync(), _ => !busy);
        UpdateLatestCommand = new RelayCommand(async _ => await InstallAsync(IsLite ? LatestLite : LatestFull, "最新版"), _ => !busy);
        InstallCommand = new RelayCommand(async p =>
        {
            if (p is not Ymm4Release r) return;
            if (r.IsLocal) await InstallFromZipAsync(r.LocalZip!, r.Version);
            else if (r.Url != null) await InstallAsync(r.Url, r.Version);
        }, _ => !busy);
        CancelCommand = new RelayCommand(_ => cts?.Cancel());
        LoadLocal();
    }

    public ObservableCollection<Ymm4Release> Releases { get; } = new();
    public ICommand CheckCommand { get; }
    public ICommand UpdateLatestCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    public string CurrentVersion => Ymm4.Version is { Length: > 0 } v ? v : "不明";
    /// <summary>いまの YMM4 のプリセット(登録してあれば)</summary>
    static Ymm4PresetEntry? CurrentPreset => Ymm4.Dir == null ? null : AppSettings.Instance.Ymm4Presets.FirstOrDefault(p =>
    {
        try { return string.Equals(Path.GetFullPath(p.Path).TrimEnd('\\'), Path.GetFullPath(Ymm4.Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    });

    /// <summary>Lite 版か(プリセットごとに覚える)</summary>
    public bool IsLite
    {
        get
        {
            var e = CurrentPreset?.Edition ?? AppSettings.Instance.Ymm4Edition;
            if (e == "lite") return true;
            if (e == "full") return false;
            return (Ymm4.Dir ?? "").Contains("Lite", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            var v = value ? "lite" : "full";
            if (CurrentPreset is { } p) p.Edition = v; else AppSettings.Instance.Ymm4Edition = v;
            AppSettings.Instance.Save();
            Raise(nameof(IsLite)); Raise(nameof(EditionIndex));
        }
    }
    public IReadOnlyList<string> Editions { get; } = new[] { "通常版", "Lite 版" };
    public int EditionIndex { get => IsLite ? 1 : 0; set => IsLite = value == 1; }

    /// <summary>対象の YMM4(プリセット)が変わった</summary>
    public void OnTargetChanged()
    {
        Raise(nameof(CurrentVersion)); Raise(nameof(IsLite)); Raise(nameof(EditionIndex));
    }

    bool busy;
    public bool IsBusy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    string status = "「バージョンの一覧を読み込む」で、公開されているバージョンを確認できます";
    public string Status { get => status; private set => Set(ref status, value); }
    double progress;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    CancellationTokenSource? cts;

    void LoadLocal()
    {
        try
        {
            if (!Directory.Exists(VersionsDir)) return;
            foreach (var f in new DirectoryInfo(VersionsDir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTime))
                if (!Releases.Any(r => r.LocalZip == f.FullName))
                    Releases.Add(new Ymm4Release { Version = Path.GetFileNameWithoutExtension(f.Name), Title = "(保存したもの)", Date = f.LastWriteTime, LocalZip = f.FullName });
        }
        catch { }
    }

    /// <summary>GitHub から、公開されているバージョンの一覧を読む</summary>
    async Task LoadReleasesAsync()
    {
        IsBusy = true;
        Status = "バージョンの一覧を読み込んでいます…";
        try
        {
            var json = await http.GetStringAsync(ReleasesApi);
            using var doc = JsonDocument.Parse(json);
            var list = new List<Ymm4Release>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                var tag = rel.GetProperty("tag_name").GetString() ?? "";
                var name = rel.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                DateTime.TryParse(rel.TryGetProperty("published_at", out var pa) ? pa.GetString() : null, out var date);
                string? url = null;
                if (rel.TryGetProperty("assets", out var assets))
                {
                    var zips = assets.EnumerateArray()
                        .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? ""))
                        .Where(a => a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
                    // Lite 版なら名前に Lite を含むもの、通常版なら含まないもの
                    var pick = zips.FirstOrDefault(a => a.name.Contains("Lite", StringComparison.OrdinalIgnoreCase) == IsLite);
                    url = pick.url is { Length: > 0 } ? pick.url : zips.FirstOrDefault().url;
                }
                if (string.IsNullOrEmpty(url)) continue;
                list.Add(new Ymm4Release { Version = tag.TrimStart('v', 'V'), Title = name == tag ? "" : name, Date = date, Url = url });
            }
            Releases.Clear();
            foreach (var r in list) Releases.Add(r);
            LoadLocal();
            Status = list.Count > 0 ? $"{list.Count} 個のバージョンが見つかりました。いまのバージョン: {CurrentVersion}" : "公開されているバージョンが見つかりませんでした";
        }
        catch (Exception ex)
        {
            LoadLocal();
            Status = "バージョンの一覧を読み込めませんでした(ネットにつながっているか確認してください): " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    bool Confirm(string label)
    {
        if (Ymm4.IsRunning)
        {
            MessageBox.Show("YMM4を閉じてから、もう一度お試しください。", "YMM4のバージョン変更", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return MessageBox.Show($"YMM4を「{label}」にします。\n\n・今のバージョン({CurrentVersion})は保存しておくので、あとで戻せます\n・user フォルダー(設定・キャラクター・プラグイン)は、そのまま残します\n\nよろしいですか?",
            "YMM4のバージョン変更", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    async Task InstallAsync(string url, string label)
    {
        if (!Ymm4.IsReady || !Confirm(label)) return;
        IsBusy = true;
        cts = new CancellationTokenSource();
        var tmp = Path.Combine(Path.GetTempPath(), "YMM4Manager_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Status = $"「{label}」をダウンロードしています…";
            using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? -1;
                await using var src = await res.Content.ReadAsStreamAsync(cts.Token);
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    done += n;
                    if (total > 0) { Progress = (double)done / total * 0.6; Status = $"ダウンロード中… {FileItem.FormatSize(done)} / {FileItem.FormatSize(total)}"; }
                }
            }
            await ApplyAsync(tmp, label);
        }
        catch (OperationCanceledException) { Status = "取りやめました(YMM4は変えていません)"; }
        catch (Exception ex) { Status = "変更できませんでした: " + ex.Message; }
        finally
        {
            try { File.Delete(tmp); } catch { }
            IsBusy = false;
            Progress = 0;
        }
    }

    /// <summary>手元の zip から入れる</summary>
    public async Task InstallFromZipAsync(string zip, string label)
    {
        if (!Ymm4.IsReady || !Confirm(label)) return;
        IsBusy = true;
        try { await ApplyAsync(zip, label); }
        catch (Exception ex) { Status = "変更できませんでした: " + ex.Message; }
        finally { IsBusy = false; Progress = 0; }
    }

    /// <summary>今のバージョンを保存してから、zip の中身を YMM4 のフォルダーへ上書きする(user フォルダーは除く)</summary>
    async Task ApplyAsync(string zipPath, string label)
    {
        var dir = Ymm4.Dir!;
        // zip の中の、YukkuriMovieMaker.exe があるフォルダー(先頭にフォルダーが1段あることが多い)
        string root;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var exe = zip.Entries.FirstOrDefault(e => e.Name.Equals(Ymm4.ExeName, StringComparison.OrdinalIgnoreCase));
            if (exe == null) throw new InvalidDataException("zip の中に YukkuriMovieMaker.exe が見つかりませんでした");
            root = exe.FullName.Substring(0, exe.FullName.Length - exe.Name.Length);
        }

        // 1) 今のバージョンを保存
        Status = $"今のバージョン({CurrentVersion})を保存しています…";
        Directory.CreateDirectory(VersionsDir);
        var keep = Path.Combine(VersionsDir, $"{(CurrentVersion == "不明" ? DateTime.Now.ToString("yyyyMMdd_HHmmss") : CurrentVersion)}.zip");
        if (!File.Exists(keep))
        {
            await Task.Run(() =>
            {
                using var z = ZipFile.Open(keep, ZipArchiveMode.Create);
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, f);
                    if (rel.StartsWith("user" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    try { z.CreateEntryFromFile(f, "YukkuriMovieMaker/" + rel.Replace('\\', '/'), CompressionLevel.Fastest); } catch { }
                }
            });
        }
        Progress = 0.75;

        // 2) 上書き(user フォルダーは触らない)
        Status = $"「{label}」に入れ替えています…";
        int count = await Task.Run(() =>
        {
            int c = 0;
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(root, StringComparison.Ordinal) || e.Name.Length == 0) continue;
                var rel = e.FullName.Substring(root.Length).Replace('/', Path.DirectorySeparatorChar);
                if (rel.StartsWith("user" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.GetFullPath(Path.Combine(dir, rel));
                if (!dest.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
                c++;
            }
            return c;
        });
        Progress = 1;
        Raise(nameof(CurrentVersion));
        LoadLocal();
        Status = $"YMM4を「{label}」にしました({count} 個のファイル)。いまのバージョン: {CurrentVersion}";
        main.Toast(Status);
    }
}
