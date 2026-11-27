using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace YMM4Manager;

/// <summary>
/// 専用ストレージの定期バックアップ。決めたフォルダーへ、決めた間隔で、専用ストレージの中身をすべてコピーする。
/// ・1つを上書き: 変わったファイルだけコピーして、消えたファイルは消す(いつも最新の写し)
/// ・日付ごとに残す: その時の中身を日付のフォルダーへまるごとコピーし、古いものから消す
/// ・ZIP にまとめる: 上の2つを、フォルダーではなく ZIP ファイル1つにする(Dropbox などのクラウドに入れる時向け。ファイルが多くても同期が速い)
/// </summary>
public static class StorageBackup
{
    public static readonly (string Label, double Hours)[] Intervals =
    {
        ("自動ではしない", 0), ("1時間ごと", 1), ("3時間ごと", 3), ("6時間ごと", 6), ("12時間ごと", 12), ("毎日", 24), ("3日ごと", 72), ("毎週", 168),
    };

    public static readonly (string Label, int Keep)[] KeepModes =
    {
        ("1つを上書きして、いつも最新にする(変わったファイルだけコピー)", 0),
        ("日付ごとに残す(新しい3つ)", 3),
        ("日付ごとに残す(新しい5つ)", 5),
        ("日付ごとに残す(新しい10個)", 10),
    };

    const string MirrorName = "YMM4専用ストレージ";
    const string GenPrefix = "YMM4専用ストレージ_";
    const string Working = "_作成中";
    static int running;

    static AppSettings S => AppSettings.Instance;

    public static bool IsRunning => running != 0;

    /// <summary>次にバックアップする時刻(自動でしない時は null)</summary>
    public static DateTime? Next
    {
        get
        {
            if (S.StorageBackupHours <= 0 || string.IsNullOrWhiteSpace(S.StorageBackupDir) || string.IsNullOrWhiteSpace(S.StorageRoot)) return null;
            if (S.StorageBackupLast == null) return DateTime.Now;
            var t = S.StorageBackupLast.Value.AddHours(S.StorageBackupHours);
            return t < DateTime.Now ? DateTime.Now : t;
        }
    }

    /// <summary>そろそろバックアップする時か(tolerance = 間隔の何割を過ぎたら良しとするか)</summary>
    public static bool IsDue(double tolerance = 1.0)
    {
        if (S.StorageBackupHours <= 0 || string.IsNullOrWhiteSpace(S.StorageBackupDir) || string.IsNullOrWhiteSpace(S.StorageRoot)) return false;
        if (S.StorageBackupLast == null) return true;
        return (DateTime.Now - S.StorageBackupLast.Value).TotalHours >= S.StorageBackupHours * tolerance;
    }

    static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\');

    /// <summary>バックアップする。返すのは結果の説明</summary>
    public static async Task<string> RunAsync(Action<string>? progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return "いま、ほかのバックアップをしています";
        try
        {
            var rootRaw = S.StorageRoot;
            var destRaw = S.StorageBackupDir;
            if (string.IsNullOrWhiteSpace(rootRaw) || !Directory.Exists(rootRaw)) return Finish("専用ストレージが見つかりません(ドライブがつながっているか確かめてください)", false);
            if (string.IsNullOrWhiteSpace(destRaw)) return Finish("バックアップの保存先が決まっていません", false);
            var root = Full(rootRaw);
            var dest = Full(destRaw);
            if ((dest + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return Finish("保存先が専用ストレージの中です。ほかの場所を選んでください", false);
            if ((root + "\\").StartsWith(dest + "\\", StringComparison.OrdinalIgnoreCase)) return Finish("専用ストレージが保存先の中にあります。ほかの場所を選んでください", false);
            Directory.CreateDirectory(dest);

            var skip = S.StorageBackupExclude ? Path.Combine(root, "バックアップ") + "\\" : null;
            progress?.Invoke("バックアップするファイルを数えています…");
            var files = await Task.Run(() => Directory.GetFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                .Where(f => skip == null || !f.StartsWith(skip, StringComparison.OrdinalIgnoreCase)).ToArray(), ct);

            var began = DateTime.Now;
            string result;
            if (S.StorageBackupZip)
            {
                var name = S.StorageBackupKeep <= 0 ? MirrorName : GenPrefix + began.ToString("yyyyMMdd_HHmmss");
                var final = Path.Combine(dest, name + ".zip");
                var (added, failed) = await Task.Run(() => ZipTo(root, final, files, skip, progress, ct), ct);
                if (S.StorageBackupKeep > 0) PruneGenerations(dest);
                result = $"✓ {began:MM/dd HH:mm} に {final} を作りました({added} 個" + (failed > 0 ? $"・入れられなかった {failed} 個(使用中など)" : "") +
                         (S.StorageBackupKeep > 0 ? $"。新しい {S.StorageBackupKeep} つを残します" : "") + ")";
            }
            else if (S.StorageBackupKeep <= 0)
            {
                var target = Path.Combine(dest, MirrorName);
                var (copied, removed, failed) = await Task.Run(() => Mirror(root, target, files, skip, progress, ct), ct);
                result = $"✓ {began:MM/dd HH:mm} に {target} を最新にしました(コピー {copied} 個・削除 {removed} 個" + (failed > 0 ? $"・コピーできなかった {failed} 個" : "") + ")";
            }
            else
            {
                var name = GenPrefix + began.ToString("yyyyMMdd_HHmmss");
                var work = Path.Combine(dest, name + Working);
                var (copied, _, failed) = await Task.Run(() => Mirror(root, work, files, skip, progress, ct), ct);
                var final = Path.Combine(dest, name);
                Directory.Move(work, final);
                PruneGenerations(dest);
                result = $"✓ {began:MM/dd HH:mm} に {final} へコピーしました({copied} 個" + (failed > 0 ? $"・コピーできなかった {failed} 個" : "") + $"。新しい {S.StorageBackupKeep} つを残します)";
            }
            return Finish(result, true);
        }
        catch (OperationCanceledException) { return Finish("バックアップを取りやめました", false); }
        catch (Exception ex) { return Finish("バックアップできませんでした: " + ex.Message, false); }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    /// <summary>日付ごとのバックアップ(フォルダーと ZIP)を、新しい StorageBackupKeep 個だけ残す。作りかけも片付ける</summary>
    static void PruneGenerations(string dest)
    {
        try
        {
            var gens = Directory.GetDirectories(dest, GenPrefix + "*").Select(d => (path: d, dir: true))
                .Concat(Directory.GetFiles(dest, GenPrefix + "*.zip").Select(f => (path: f, dir: false)))
                .Where(g => !g.path.EndsWith(Working) && !Path.GetFileName(g.path).StartsWith(GenPrefix + "戻す前_"))
                .OrderByDescending(g => Path.GetFileNameWithoutExtension(g.path)).ToList();
            foreach (var old in gens.Skip(S.StorageBackupKeep))
                try { if (old.dir) Directory.Delete(old.path, true); else File.Delete(old.path); } catch { }
            foreach (var w in Directory.GetDirectories(dest, GenPrefix + "*" + Working)) try { Directory.Delete(w, true); } catch { }
            foreach (var w in Directory.GetFiles(dest, "*.zip" + Working)) try { File.Delete(w); } catch { }
        }
        catch { }
    }

    // 圧縮してもほとんど小さくならないもの(そのまま入れて速くする)
    static readonly HashSet<string> Packed = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".zip", ".7z", ".rar", ".gz" };

    /// <summary>files を 1つの ZIP にする(作りかけは別名で作って、できてから置きかえる)</summary>
    static (int added, int failed) ZipTo(string root, string zipPath, string[] files, string? skip, Action<string>? progress, CancellationToken ct)
    {
        var work = zipPath + Working;
        int added = 0, failed = 0, n = 0;
        try
        {
            using (var fs = new FileStream(work, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    try
                    {
                        using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var e = zip.CreateEntry(rel, Packed.Contains(Path.GetExtension(f)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                        var t = File.GetLastWriteTime(f);
                        if (t.Year >= 1980 && t.Year <= 2107) e.LastWriteTime = t;
                        using var dst = e.Open();
                        src.CopyTo(dst);
                        added++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { failed++; }
                    if (++n % 50 == 0 || n == files.Length) progress?.Invoke($"ZIP にまとめています… {n * 100 / Math.Max(1, files.Length)}%({n}/{files.Length} ファイル)");
                }
                // 空のフォルダーも入れる
                foreach (var d in Directory.GetDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    if ((skip == null || !(d + "\\").StartsWith(skip, StringComparison.OrdinalIgnoreCase)) && !Directory.EnumerateFileSystemEntries(d).Any())
                        try { zip.CreateEntry(Path.GetRelativePath(root, d).Replace('\\', '/') + "/"); } catch { }
            }
            File.Move(work, zipPath, true);
        }
        catch { try { File.Delete(work); } catch { } throw; }
        return (added, failed);
    }

    static string Finish(string text, bool ok)
    {
        if (ok) S.StorageBackupLast = DateTime.Now;
        S.StorageBackupResult = text;
        S.Save();
        return text;
    }

    /// <summary>root の files を target へ。変わっていないファイルはコピーしない。target にだけあるものは消す</summary>
    static (int copied, int removed, int failed) Mirror(string root, string target, string[] files, string? skip, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        int copied = 0, failed = 0, n = 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, f);
            keep.Add(rel);
            var dst = Path.Combine(target, rel);
            try
            {
                var src = new FileInfo(f);
                var old = new FileInfo(dst);
                if (!old.Exists || old.Length != src.Length || Math.Abs((old.LastWriteTimeUtc - src.LastWriteTimeUtc).TotalSeconds) > 2)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, true);
                    File.SetLastWriteTimeUtc(dst, src.LastWriteTimeUtc);
                    copied++;
                }
            }
            catch { failed++; }
            if (++n % 50 == 0 || n == files.Length) progress?.Invoke($"バックアップしています… {n * 100 / Math.Max(1, files.Length)}%({n}/{files.Length} ファイル・コピー {copied} 個)");
        }
        // 空のフォルダーも写す
        foreach (var d in Directory.GetDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            if (skip == null || !(d + "\\").StartsWith(skip, StringComparison.OrdinalIgnoreCase))
                try { Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(root, d))); } catch { }
        // 元に無くなったファイルを消す
        int removed = 0;
        foreach (var f in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
            if (!keep.Contains(Path.GetRelativePath(target, f))) try { File.Delete(f); removed++; } catch { }
        foreach (var d in Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            var rel = Path.GetRelativePath(target, d);
            if (!Directory.Exists(Path.Combine(root, rel)) || (skip != null && (Path.Combine(root, rel) + "\\").StartsWith(skip, StringComparison.OrdinalIgnoreCase)))
                try { Directory.Delete(d, true); } catch { }
        }
        return (copied, removed, failed);
    }

    // ---------------------------------------------------------------- 戻す(ロード)
    public sealed record BackupPoint(string Path, DateTime Time, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>保存先にあるバックアップの一覧(新しい順)</summary>
    public static List<BackupPoint> ListBackups()
    {
        var list = new List<BackupPoint>();
        var dest = S.StorageBackupDir;
        if (string.IsNullOrWhiteSpace(dest) || !Directory.Exists(dest)) return list;
        try
        {
            var mirror = System.IO.Path.Combine(dest, MirrorName);
            if (Directory.Exists(mirror))
            {
                var t = S.StorageBackupKeep <= 0 && S.StorageBackupLast is { } l ? l : Directory.GetLastWriteTime(mirror);
                list.Add(new BackupPoint(mirror, t, $"最新の写し({t:yyyy/MM/dd HH:mm} ごろ)"));
            }
            var mirrorZip = System.IO.Path.Combine(dest, MirrorName + ".zip");
            if (File.Exists(mirrorZip))
            {
                var t = File.GetLastWriteTime(mirrorZip);
                list.Add(new BackupPoint(mirrorZip, t, $"最新の写し・ZIP({t:yyyy/MM/dd HH:mm})"));
            }
            foreach (var d in Directory.GetDirectories(dest, GenPrefix + "*").Concat(Directory.GetFiles(dest, GenPrefix + "*.zip")))
            {
                var isZip = d.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                var name = isZip ? System.IO.Path.GetFileNameWithoutExtension(d) : System.IO.Path.GetFileName(d);
                if (name.EndsWith(Working)) continue;
                var stamp = name.Substring(GenPrefix.Length);
                var before = stamp.StartsWith("戻す前_");
                if (before) stamp = stamp.Substring("戻す前_".Length);
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var time)) time = isZip ? File.GetLastWriteTime(d) : Directory.GetLastWriteTime(d);
                var zipNote = isZip ? "・ZIP" : "";
                list.Add(new BackupPoint(d, time, before ? $"{time:yyyy/MM/dd HH:mm}(戻す前に残した状態{zipNote})" : $"{time:yyyy/MM/dd HH:mm} のバックアップ{(isZip ? "(ZIP)" : "")}"));
            }
        }
        catch { }
        return list.OrderByDescending(b => b.Time).ToList();
    }

    /// <summary>
    /// 専用ストレージを、バックアップの状態に戻す(バックアップに無いファイルは消す)。
    /// keepCurrent なら、戻す前の今の状態を「YMM4専用ストレージ_戻す前_日時」として保存先に残す
    /// </summary>
    public static async Task<string> RestoreAsync(BackupPoint point, bool keepCurrent, Action<string>? progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return "いま、ほかのバックアップをしています";
        try
        {
            var rootRaw = S.StorageRoot;
            if (string.IsNullOrWhiteSpace(rootRaw) || !Directory.Exists(rootRaw)) return "専用ストレージが見つかりません";
            var isZip = point.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (isZip ? !File.Exists(point.Path) : !Directory.Exists(point.Path)) return "バックアップが見つかりません";
            var root = Full(rootRaw);
            var src = Full(point.Path);

            // バックアップに「バックアップ」フォルダーが無い時(除いてバックアップした時)は、今のものを消さずに残す
            bool hasBackupFolder;
            if (isZip) { using var z = ZipFile.OpenRead(src); hasBackupFolder = z.Entries.Any(e => e.FullName.StartsWith("バックアップ/", StringComparison.OrdinalIgnoreCase)); }
            else hasBackupFolder = Directory.Exists(System.IO.Path.Combine(src, "バックアップ"));
            var keepDir = hasBackupFolder ? null : System.IO.Path.Combine(root, "バックアップ") + "\\";

            if (keepCurrent && !string.IsNullOrWhiteSpace(S.StorageBackupDir))
            {
                progress?.Invoke("戻す前に、今の状態を残しています…");
                var safe = System.IO.Path.Combine(Full(S.StorageBackupDir!), GenPrefix + "戻す前_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                var now = await Task.Run(() => Directory.GetFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                    .Where(f => keepDir == null || !f.StartsWith(keepDir, StringComparison.OrdinalIgnoreCase)).ToArray(), ct);
                if (S.StorageBackupZip) await Task.Run(() => ZipTo(root, safe + ".zip", now, keepDir, progress, ct), ct);
                else await Task.Run(() => Mirror(root, safe, now, keepDir, progress, ct), ct);
            }

            progress?.Invoke("バックアップの状態に戻しています…");
            if (isZip)
            {
                var (zc, zr, zf) = await Task.Run(() => MirrorBackZip(src, root, keepDir, progress, ct), ct);
                return $"✓ {point.Label} の状態に戻しました(書き戻し {zc} 個・削除 {zr} 個" + (zf > 0 ? $"・戻せなかった {zf} 個(使用中など)" : "") + ")";
            }
            var files = await Task.Run(() => Directory.GetFiles(src, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }), ct);
            var (copied, removed, failed) = await Task.Run(() => MirrorBack(src, root, files, keepDir, progress, ct), ct);
            return $"✓ {point.Label} の状態に戻しました(コピー {copied} 個・削除 {removed} 個" + (failed > 0 ? $"・戻せなかった {failed} 個(使用中など)" : "") + ")";
        }
        catch (OperationCanceledException) { return "取りやめました"; }
        catch (Exception ex) { return "戻せませんでした: " + ex.Message; }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    /// <summary>バックアップ(src)の中身で、専用ストレージ(root)を上書きする。keepDir の中は触らない</summary>
    static (int copied, int removed, int failed) MirrorBack(string src, string root, string[] files, string? keepDir, Action<string>? progress, CancellationToken ct)
    {
        int copied = 0, failed = 0, n = 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = System.IO.Path.GetRelativePath(src, f);
            keep.Add(rel);
            var dst = System.IO.Path.Combine(root, rel);
            try
            {
                var a = new FileInfo(f);
                var b = new FileInfo(dst);
                if (!b.Exists || b.Length != a.Length || Math.Abs((b.LastWriteTimeUtc - a.LastWriteTimeUtc).TotalSeconds) > 2)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, true);
                    File.SetLastWriteTimeUtc(dst, a.LastWriteTimeUtc);
                    copied++;
                }
            }
            catch { failed++; }
            if (++n % 50 == 0 || n == files.Length) progress?.Invoke($"戻しています… {n * 100 / Math.Max(1, files.Length)}%({n}/{files.Length} ファイル)");
        }
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            try { Directory.CreateDirectory(System.IO.Path.Combine(root, System.IO.Path.GetRelativePath(src, d))); } catch { }
        int removed = 0;
        foreach (var f in Directory.GetFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (keepDir != null && f.StartsWith(keepDir, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = System.IO.Path.GetRelativePath(root, f);
            if (System.IO.Path.GetFileName(f) == "YMM4専用ストレージ.json") continue;
            if (!keep.Contains(rel)) try { File.Delete(f); removed++; } catch { failed++; }
        }
        return (copied, removed, failed);
    }

    /// <summary>ZIP のバックアップの中身で、専用ストレージ(root)を上書きする。keepDir の中は触らない</summary>
    static (int copied, int removed, int failed) MirrorBackZip(string zipPath, string root, string? keepDir, Action<string>? progress, CancellationToken ct)
    {
        int copied = 0, failed = 0, n = 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.ToList();
        var rootFull = root.TrimEnd('\\') + "\\";
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = e.FullName.Replace('/', '\\');
            var dst = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
            if (!dst.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;   // 外へ出る名前は無視
            if (e.FullName.EndsWith("/")) { try { Directory.CreateDirectory(dst); } catch { } continue; }
            keep.Add(System.IO.Path.GetRelativePath(root, dst));
            try
            {
                var b = new FileInfo(dst);
                if (!b.Exists || b.Length != e.Length || Math.Abs((b.LastWriteTime - e.LastWriteTime.DateTime).TotalSeconds) > 2)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                    e.ExtractToFile(dst, true);
                    try { File.SetLastWriteTime(dst, e.LastWriteTime.DateTime); } catch { }
                    copied++;
                }
            }
            catch { failed++; }
            if (++n % 50 == 0 || n == entries.Count) progress?.Invoke($"戻しています… {n * 100 / Math.Max(1, entries.Count)}%({n}/{entries.Count} ファイル)");
        }
        int removed = 0;
        foreach (var f in Directory.GetFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (keepDir != null && f.StartsWith(keepDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (System.IO.Path.GetFileName(f) == "YMM4専用ストレージ.json") continue;
            if (!keep.Contains(System.IO.Path.GetRelativePath(root, f))) try { File.Delete(f); removed++; } catch { failed++; }
        }
        return (copied, removed, failed);
    }

    // ---------------------------------------------------------------- Windows のタスクスケジューラー
    const string TaskName = "YMM4Manager\\専用ストレージのバックアップ";

    /// <summary>マネージャーを開いていない時もバックアップするよう、タスクスケジューラーに登録する(間隔が「しない」なら外す)</summary>
    public static string UpdateTask()
    {
        try
        {
            if (!S.StorageBackupTask || S.StorageBackupHours <= 0)
            {
                Schtasks($"/Delete /F /TN \"{TaskName}\"");
                return S.StorageBackupTask ? "自動でしない設定のため、タスクスケジューラーから外しました" : "タスクスケジューラーから外しました";
            }
            var exe = Environment.ProcessPath ?? "";
            var h = S.StorageBackupHours;
            var sc = h >= 168 ? "/SC WEEKLY /MO 1" : h >= 24 ? $"/SC DAILY /MO {(int)Math.Round(h / 24)}" : $"/SC HOURLY /MO {(int)Math.Max(1, Math.Round(h))}";
            var (code, output) = Schtasks($"/Create /F /TN \"{TaskName}\" {sc} /TR \"\\\"{exe}\\\" --storage-backup\"");
            return code == 0 ? "タスクスケジューラーに登録しました(マネージャーを開いていない時も、バックアップします)" : "タスクスケジューラーに登録できませんでした: " + output.Trim();
        }
        catch (Exception ex) { return "タスクスケジューラーを設定できませんでした: " + ex.Message; }
    }

    static (int code, string output) Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, o);
    }
}

/// <summary>この PC にある、クラウドストレージ(Dropbox・OneDrive・Google ドライブなど)の同期フォルダーを探す</summary>
public static class CloudFolders
{
    public sealed record Cloud(string Name, string Path)
    {
        public string Label => $"{Name}  ({Path})";
        // ComboBox の閉じた時の表示は、見た目のテンプレートによっては文字にした値を出すので、ここでも同じ文字を返す
        public override string ToString() => Label;
    }

    public static List<Cloud> Find()
    {
        var list = new List<Cloud>();
        void Add(string name, string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
                if (list.Any(c => string.Equals(c.Path, full, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(new Cloud(name, full));
            }
            catch { }
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Dropbox(アプリが info.json に同期フォルダーの場所を書いている)
        foreach (var info in new[] { System.IO.Path.Combine(appData, "Dropbox", "info.json"), System.IO.Path.Combine(local, "Dropbox", "info.json") })
        {
            try
            {
                if (!File.Exists(info)) continue;
                var j = JsonNode.Parse(File.ReadAllText(info));
                if (j is JsonObject o)
                    foreach (var kv in o)
                        Add(kv.Key == "business" ? "Dropbox(ビジネス)" : kv.Key == "personal" ? "Dropbox" : "Dropbox(" + kv.Key + ")", kv.Value?["path"]?.GetValue<string>());
            }
            catch { }
        }
        Add("Dropbox", System.IO.Path.Combine(home, "Dropbox"));

        // OneDrive
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive(職場・学校)", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));

        // Google ドライブ(パソコン版は、仮想ドライブの「マイドライブ」)
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                foreach (var n in new[] { "マイドライブ", "My Drive" })
                    Add("Google ドライブ", System.IO.Path.Combine(d.RootDirectory.FullName, n));
            }
        }
        catch { }
        Add("Google ドライブ", System.IO.Path.Combine(home, "Google Drive"));
        Add("Google ドライブ", System.IO.Path.Combine(home, "Google ドライブ"));

        // そのほか
        Add("iCloud Drive", System.IO.Path.Combine(home, "iCloudDrive"));
        Add("Box", System.IO.Path.Combine(home, "Box"));
        Add("MEGA", System.IO.Path.Combine(home, "MEGA"));
        return list;
    }
}
