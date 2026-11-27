; YMM4 マネージャー のインストーラー(Inno Setup 6 用)
; build.bat から呼ばれます。手で作る時は、先に dotnet publish してから、このファイルを Inno Setup で開いて「Compile」してください。

#define AppName "YMM4 マネージャー"
#define AppExe "YMM4Manager.exe"
; バージョンは、作った exe から読む(csproj の <Version> を上げれば、インストーラーも上がる)
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\bin\publish\YMM4Manager.exe")
#define AppPublisher "YMM4 Manager"
#define PublishDir "..\bin\publish"

[Setup]
AppId={{6E1E3C8A-3B7D-4C55-9C2E-7A41D5B1F0A9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\YMM4Manager
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 管理者でなくても入れられる(自分だけ / すべてのユーザー を選べる)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; できたインストーラーは D:\自作ソフト\YmmManager\インストーラー にまとめる(版のフォルダー\YMM4Manager\Installer から3つ上)
OutputDir=..\..\..\インストーラー
OutputBaseFilename=YMM4Manager_Setup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; ---- アップデート: 同じ AppId なので、新しいインストーラーを実行すると、前と同じ場所へ上書きで入る
DisableDirPage=auto
DisableReadyPage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
AppMutex=YMM4ManagerRunning
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
SetupIconFile=..\Assets\app.ico
; インストールの時に利用規約を見せて、同意してもらう
LicenseFile=..\利用規約.txt

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作る"; GroupDescription: "ショートカット:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\利用規約.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{autoprograms}\{#AppName} 利用規約"; Filename: "{app}\利用規約.txt"

[Messages]
japanese.WelcomeLabel2=[name/ver] をインストールします。%n%n以前のバージョンが入っている場合は、設定や登録内容はそのままで、新しいバージョンに上書きされます。

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} を起動する"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; このソフトの設定(%AppData%\YMM4Manager)は、アンインストールしても残します(素材やバックアップも消しません)
