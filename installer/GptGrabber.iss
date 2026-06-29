; ============================================================================
;  GPT Grabber — установщик (Inno Setup 6, версия на WebView2).
;  Компилируется НЕ напрямую, а через tools\build-installer.ps1 — он публикует
;  приложение, собирает портативный .NET runtime, тянет WebView2-бутстраппер и
;  передаёт сюда /D-define'ы (AppVersion, StagingApp, опц. WebView2Setup/AppIcon).
;
;  Запуск приложения — через ВЛОЖЕННЫЙ подписанный Microsoft dotnet.exe + наша DLL.
;  Свой apphost (.exe) не собираем: Smart App Control блокирует неподписанный exe
;  (CLAUDE.md §6.14). dotnet.exe подписан Microsoft → SAC его пропускает.
; ============================================================================

#define AppName "GPT Grabber"
#define AppPublisher "Radiy Sladkov"
#define ExeRel "dotnet\dotnet.exe"
#define DllName "WebView2Poc.dll"

#ifndef AppVersion
  #define AppVersion "0.1.2"
#endif

#ifndef StagingApp
  #error "StagingApp (папка опубликованного приложения) не задан. Собирай через tools\build-installer.ps1."
#endif

; Клаузула иконки для ярлыков — только если передан AppIcon (installer\app.ico).
#ifdef AppIcon
  #define IcoClause "; IconFilename: ""{app}\app.ico"""
#else
  #define IcoClause ""
#endif

[Setup]
AppId={{8B2F3C1A-7D49-4E6B-9F2A-1C5E0A7B3D64}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\GptGrabber
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#SourcePath}\..\dist
OutputBaseFilename=GptGrabber-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Язык мастера выбираем автоматически по системе — без отдельного шага выбора.
; (Язык самой программы детектится в рантайме независимо от этого.)
ShowLanguageDialog=no
; Никаких шагов с выбором: ни приветствия, ни выбора папки, ни «всё готово к установке».
; Запустили → поставилось → «Готово». Папка фиксирована (DefaultDirName), per-user, без админа.
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes
UninstallDisplayName={#AppName}
#ifdef AppIcon
; Иконка в списке «Приложения» (Параметры/Установка и удаление) и для самого setup.exe.
UninstallDisplayIcon={app}\app.ico
SetupIconFile={#AppIcon}
#else
UninstallDisplayIcon={app}\{#ExeRel}
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.LaunchAfter=Launch GPT Grabber
ru.LaunchAfter=Запустить GPT Grabber
de.LaunchAfter=GPT Grabber starten
en.InstallingWv2=Installing Microsoft Edge WebView2 Runtime…
ru.InstallingWv2=Установка Microsoft Edge WebView2 Runtime…
de.InstallingWv2=Microsoft Edge WebView2 Runtime wird installiert…

[Files]
; Приложение + портативный .NET runtime (подпапка dotnet\) + загрузчик WebView2 + шрифты.
Source: "{#StagingApp}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#ifdef AppIcon
Source: "{#AppIcon}"; DestDir: "{app}"; DestName: "app.ico"; Flags: ignoreversion
#endif
#ifdef WebView2Setup
; Bootstrapper Evergreen WebView2 — выполнится только если рантайма в системе ещё нет.
Source: "{#WebView2Setup}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeRel}"; Parameters: """{app}\{#DllName}"""; WorkingDir: "{app}"{#IcoClause}
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeRel}"; Parameters: """{app}\{#DllName}"""; WorkingDir: "{app}"{#IcoClause}

[Run]
#ifdef WebView2Setup
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWv2}"; Check: NeedsWebView2; Flags: waituntilterminated
#endif
Filename: "{app}\{#ExeRel}"; Parameters: """{app}\{#DllName}"""; WorkingDir: "{app}"; Description: "{cm:LaunchAfter}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Данные, созданные программой в рантайме (профиль ChatGPT/WebView2 + лог) — лежат в {app}\data.
; Inno удаляет только установленные файлы; рантайм-папку сносим явно, чтобы не оставлять хвостов.
Type: filesandordirs; Name: "{app}\data"

[Code]
{ Проверка наличия Edge WebView2 Runtime по ключу клиента Evergreen в реестре. }
function Wv2Installed: Boolean;
var
  v: String;
begin
  Result := False;
  { Per-machine x64 (под WOW6432Node). }
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
    if (v <> '') and (v <> '0.0.0.0') then
      Result := True;
  { Per-user. }
  if (not Result) and RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) then
    if (v <> '') and (v <> '0.0.0.0') then
      Result := True;
end;

function NeedsWebView2: Boolean;
begin
  Result := not Wv2Installed;
end;
