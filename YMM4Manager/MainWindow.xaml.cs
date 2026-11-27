using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using YmmExplorer;

namespace YMM4Manager;

public partial class MainWindow : Window
{
    readonly MainVm vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = vm;
        var st = AppSettings.Instance;
        Width = Math.Max(MinWidth, st.WindowWidth);
        Height = Math.Max(MinHeight, st.WindowHeight);
        // ほかで設定が変わっていたら(タスクスケジューラーのバックアップなど)、前に出た時に読み直す
        Activated += (_, _) => { if (st.ReloadFromDisk()) try { vm.Storage.SettingsReloaded(); } catch { } };
        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal) { st.WindowWidth = Width; st.WindowHeight = Height; }
            st.Save();
        };
        vm.Materials.RestoreSelection = list =>
        {
            restoring = true;
            try
            {
                MaterialList.SelectedItems.Clear();
                foreach (var c in list) MaterialList.SelectedItems.Add(c);
            }
            finally { restoring = false; }
            vm.Materials.SetSelection(list);
        };
        vm.Home.Refresh();
    }

    // ---------------------------------------------------------------- YMM4の場所
    void PickYmm4_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "YMM4のフォルダー(YukkuriMovieMaker.exe があるフォルダー)を選んでください" };
        if (dlg.ShowDialog(this) == true) vm.SetYmm4Dir(dlg.FolderName);
    }

    void GoMaterials_Click(object sender, MouseButtonEventArgs e) => vm.Page = "materials";
    void GoPlugins_Click(object sender, MouseButtonEventArgs e) => vm.Page = "plugins";
    void GoBackup_Click(object sender, MouseButtonEventArgs e) => vm.Page = "backup";

    // ---------------------------------------------------------------- 素材データベース
    bool restoring;

    void MatKind_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChoiceItem c) vm.Materials.KindFilter = c.Key;
    }

    void MaterialList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoring) return;
        StopMedia();
        vm.Materials.SetSelection(MaterialList.SelectedItems.Cast<MaterialCard>());
    }

    void MaterialList_DoubleClick(object sender, MouseButtonEventArgs e) => vm.Materials.OpenFileCommand.Execute(null);

    void MaterialList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { vm.Materials.TrashCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { vm.Materials.OpenFileCommand.Execute(null); e.Handled = true; }
    }

    void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "取り込む素材を選んでください", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _ = vm.Materials.ImportAsync(dlg.FileNames);
    }

    void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "取り込むフォルダーを選んでください(中の素材をまとめて取り込みます)" };
        if (dlg.ShowDialog(this) == true) _ = vm.Materials.ImportAsync(new[] { dlg.FolderName });
    }

    void Materials_DragOver(object sender, DragEventArgs e)
    {
        // 自分の一覧から出したドラッグは受け取らない
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(OwnDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Materials_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(OwnDragFormat)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = vm.Materials.ImportAsync(files);
    }

    // 一覧から、YMM4のタイムラインやエクスプローラーへドラッグして出す
    const string OwnDragFormat = "YMM4Manager.Material";
    Point dragStart;
    bool dragArmed;

    void MaterialList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(MaterialList);
        dragArmed = (e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(MaterialList, d) is ListBoxItem;
    }

    void MaterialList_MouseMove(object sender, MouseEventArgs e)
    {
        if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(MaterialList);
        if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragArmed = false;
        var files = vm.Materials.SelectedFilePaths().ToArray();
        if (files.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, files);
        data.SetData(OwnDragFormat, true);
        try { DragDrop.DoDragDrop(MaterialList, data, DragDropEffects.Copy); } catch { }
    }

    void NewTag_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        vm.Materials.AddTagCommand.Execute(null);
        e.Handled = true;
    }

    void MoveFolder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ChoiceItem c) return;
        if (c.Key != "*") vm.Materials.MoveToFolderCommand.Execute(c.Key);
        cb.SelectedItem = null;
    }

    void RemoveMatFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = vm.Materials.FolderChoices.FirstOrDefault(f => f.Key == vm.Materials.FolderFilter);
        vm.Materials.RemoveFolderCommand.Execute(item);
    }

    // 動画・音声の再生(右の欄)
    void MatPlay_Click(object sender, RoutedEventArgs e)
    {
        var path = vm.Materials.MediaPath;
        if (path == null || !File.Exists(path)) { vm.Toast("再生できるのは、動画と音声です"); return; }
        try
        {
            if (MatMedia.Source == null || MatMedia.Source.LocalPath != path) MatMedia.Source = new Uri(path);
            MatMedia.Visibility = vm.Materials.Current?.Kind == "video" ? Visibility.Visible : Visibility.Collapsed;
            MatMedia.Play();
        }
        catch (Exception ex) { vm.Toast("再生できませんでした: " + ex.Message); }
    }

    void MatStop_Click(object sender, RoutedEventArgs e) => StopMedia();

    void StopMedia()
    {
        try { MatMedia.Stop(); MatMedia.Source = null; MatMedia.Visibility = Visibility.Collapsed; } catch { }
    }

    void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "開く素材データベースのフォルダー(materials.json があるフォルダー、または空のフォルダー)" };
        if (dlg.ShowDialog(this) == true) vm.Materials.OpenLibrary(dlg.FolderName);
    }

    void MoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "引っ越し先の、空のフォルダーを選んでください" };
        if (dlg.ShowDialog(this) == true) _ = vm.Materials.MoveLibraryAsync(dlg.FolderName);
    }

    // ---------------------------------------------------------------- プロジェクト
    void AddProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "YMM4のプロジェクトを保存しているフォルダーを選んでください" };
        if (dlg.ShowDialog(this) == true) vm.Projects.AddFolder(dlg.FolderName);
    }

    void ProjectList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is ProjectFile f) vm.Projects.OpenCommand.Execute(f);
    }

    void ImportProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "取り込むプロジェクト(.ymmp)を選んでください", Filter = "YMM4プロジェクト|*.ymmp", Multiselect = true };
        if (dlg.ShowDialog(this) == true) vm.Projects.Import(dlg.FileNames);
    }

    void Projects_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) vm.Projects.Import(files);
    }

    void NewProjectName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { vm.Projects.CreateCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.Projects.CancelCreateCommand.Execute(null); e.Handled = true; }
    }

    // 入力欄が出たら、すぐに打てるようにする
    void NewProjectName_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Dispatcher.BeginInvoke(new Action(() => { NewProjectName.Focus(); Keyboard.Focus(NewProjectName); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    void PickProjectRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "新しいプロジェクトを作るフォルダーを選んでください" };
        if (dlg.ShowDialog(this) != true) return;
        AppSettings.Instance.ProjectRoot = dlg.FolderName;
        AppSettings.Instance.Save();
        vm.Projects.AddFolder(dlg.FolderName);
        vm.Projects.RefreshSettings();
    }

    void PickTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "ひな形にするプロジェクト(.ymmp)を選んでください", Filter = "YMM4プロジェクト|*.ymmp" };
        if (dlg.ShowDialog(this) != true) return;
        AppSettings.Instance.ProjectTemplate = dlg.FileName;
        AppSettings.Instance.Save();
        vm.Projects.RefreshSettings();
    }

    void ClearTemplate_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.ProjectTemplate = null;
        AppSettings.Instance.Save();
        vm.Projects.RefreshSettings();
    }

    void InstallYmmZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "YMM4の zip を選んでください(配布ページからダウンロードしたもの)", Filter = "zip|*.zip" };
        if (dlg.ShowDialog(this) == true) _ = vm.Updater.InstallFromZipAsync(dlg.FileName, System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
    }

    // ---------------------------------------------------------------- プラグイン
    void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "入れるプラグインを選んでください", Filter = "YMM4プラグイン|*.ymme;*.zip;*.dll|すべてのファイル|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) vm.Toast(vm.Plugins.Install(f));
    }

    void Plugins_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files) vm.Toast(vm.Plugins.Install(f));
    }

    // ---------------------------------------------------------------- 専用ストレージ
    void StorageFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "YMM4専用ストレージにするフォルダーを選んでください(ドライブのいちばん上でもかまいません)" };
        if (dlg.ShowDialog(this) == true) vm.Storage.Register(dlg.FolderName);
    }

    void StorageBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "バックアップ(専用ストレージの写し)を置くフォルダーを選んでください(専用ストレージとは別のドライブがおすすめ)" };
        if (dlg.ShowDialog(this) == true) vm.Storage.SetBackupDir(dlg.FolderName);
    }

    // ---------------------------------------------------------------- YMM4プリセット
    void PresetCopyTo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "いまの YMM4 を、そのままコピーする先のフォルダーを選んでください(中に YMM4 のフォルダーができます)" };
        if (dlg.ShowDialog(this) != true || Ymm4.Dir == null) return;
        var to = System.IO.Path.Combine(dlg.FolderName, System.IO.Path.GetFileName(System.IO.Path.GetFullPath(Ymm4.Dir).TrimEnd('\\')));
        _ = vm.Presets.CopyCurrentAsync(System.IO.Path.GetFileName(to), to);
    }

    void PresetAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "登録する YMM4 のフォルダー(YukkuriMovieMaker.exe があるフォルダー)を選んでください" };
        if (dlg.ShowDialog(this) == true) vm.Presets.AddExisting(dlg.FolderName);
    }

    void PresetName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { vm.Presets.CopyCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.Presets.CancelCopyCommand.Execute(null); e.Handled = true; }
    }

    // ---------------------------------------------------------------- AI質問
    bool aiHooked;
    void AiPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (aiHooked) return;
        aiHooked = true;
        vm.Ai.MessageAdded += () => Dispatcher.BeginInvoke(new Action(() => AiScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);
    }

    void AiSaveKey_Click(object sender, RoutedEventArgs e)
    {
        vm.Ai.SaveKey(AiKeyBox.Password);
        AiKeyBox.Clear();
    }

    void AiInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.IsRepeat) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (e.ImeProcessedKey != Key.None || InputMethod.Current?.ImeState == InputMethodState.On && e.Key == Key.ImeProcessed) return;
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (vm.Ai.SendCommand.CanExecute(null)) vm.Ai.SendCommand.Execute(null);
        e.Handled = true;
    }

    void AiAttach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "AI に送るファイルを選んでください(画像・テキスト・字幕・プロジェクトなど)", Multiselect = true };
        if (dlg.ShowDialog(this) == true) vm.Ai.AddAttachments(dlg.FileNames);
    }

    void Ai_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void Ai_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { vm.Ai.AddAttachments(files); e.Handled = true; }
    }

    // ---------------------------------------------------------------- 設定
    void PickBackupDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "バックアップの保存先を選んでください" };
        if (dlg.ShowDialog(this) != true) return;
        AppSettings.Instance.BackupDir = dlg.FolderName;
        AppSettings.Instance.Save();
        vm.Backups.Reload();
    }
}
