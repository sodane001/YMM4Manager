@echo off
setlocal
cd /d "%~dp0"

echo === YMM4 マネージャー: exe を作っています（.NET 10 SDK が必要です） ===
dotnet publish YMM4Manager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:ContinuousIntegrationBuild=true "-p:PathMap=%CD%=/_/YMM4Manager" -o bin\publish
if errorlevel 1 (
  echo.
  echo [エラー] exe を作れませんでした。上のメッセージを確認してください。
  pause
  exit /b 1
)

echo.
echo === インストーラーを作っています（Inno Setup 6 または 7 が必要です） ===
set ISCC=
rem Inno Setup 7 / 6 を、よくある場所と PATH から探す
for %%V in (7 6) do (
  if exist "%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe"
  if exist "%ProgramFiles%\Inno Setup %%V\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup %%V\ISCC.exe"
  if exist "%LocalAppData%\Programs\Inno Setup %%V\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup %%V\ISCC.exe"
)
if "%ISCC%"=="" for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do set "ISCC=%%I"
if "%ISCC%"=="" (
  echo.
  echo [お知らせ] Inno Setup が見つかりませんでした。
  echo   exe は bin\publish\YMM4Manager.exe にできています（そのままでも使えます）。
  echo   インストーラーを作るには、Inno Setup を入れてから、もう一度実行してください。
  echo   入れ方: winget install JRSoftware.InnoSetup （または jrsoftware.org の公式サイトから）
  pause
  exit /b 0
)
echo 使う Inno Setup: %ISCC%
"%ISCC%" Installer\YMM4Manager.iss
if errorlevel 1 (
  echo [エラー] インストーラーを作れませんでした。
  pause
  exit /b 1
)
echo.
echo できました: bin\installer の中の YMM4Manager_Setup_（バージョン）.exe
explorer ..\..\インストーラー
pause
