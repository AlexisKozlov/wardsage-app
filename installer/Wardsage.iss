; Wardsage-Setup.exe — the Windows installer of the desktop app (Inno Setup 7).
;
;   ISCC installer\Wardsage.iss /DAppVersion=1.4.0      (tools/release-app.mjs does it) -> dist\Wardsage-Setup.exe
;   ISCC installer\Wardsage.iss /DAppVersion=1.4.0 /DTestBuild
;                                                       the test build (tools/release-app.mjs --test) -> dist\Wardsage-Test-Setup.exe:
;                                                       its own AppId, folder, shortcuts and exe (Wardsage-Test.exe), so it installs next to the normal one
;
; Installs for the current user (no administrator rights for the installer itself) into
; %LOCALAPPDATA%\Programs\Wardsage: Wardsage.exe and README.txt, a Start menu shortcut and an optional desktop one,
; and an entry in Windows «Apps» for removal. The app then keeps itself up to date (app/Updater.cs), so this installer
; is needed once. The app asks for administrator rights when it starts (the game runs elevated).
;
; The product was called RealmForge before: the AppId stays, so running this installer over an old install keeps its
; folder (UsePreviousAppDir), replaces RealmForge.exe with Wardsage.exe and renames the shortcuts. The per-user data
; folders keep the old name (%APPDATA%\RealmForge, %LOCALAPPDATA%\RealmForge): the settings and the sync code stay.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifdef TestBuild
  #define AutostartName "Wardsage Test"
  #define AppTitle "Wardsage Test"
  #define ExeName "Wardsage-Test.exe"
  #define SetupName "Wardsage-Test-Setup"
  #define UserData "WardsageTest"
  #define OptionsDir "WardsageTest"
#else
  #define AutostartName "Wardsage"
  #define AppTitle "Wardsage"
  #define ExeName "Wardsage.exe"
  #define SetupName "Wardsage-Setup"
  #define UserData "RealmForge"
  ; OptionsDir: the app's settings folder (Channel.FolderName); the app copies the old RealmForge one into it
  #define OptionsDir "Wardsage"
#endif

[Setup]
#ifdef TestBuild
AppId={{76D2E3CA-9344-4DB6-8F70-AEB2A9C21A48}
#else
AppId={{8B7E4C5A-3F21-4D8E-9B61-5A2C7E0D4F13}
#endif
AppName={#AppTitle}
AppVersion={#AppVersion}
AppVerName={#AppTitle} {#AppVersion}
AppPublisher=Wardsage (fan project)
AppPublisherURL=https://wardsage.com
AppSupportURL=https://github.com/AlexisKozlov/wardsage-app
DefaultDirName={localappdata}\Programs\{#AppTitle}
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename={#SetupName}
SetupIconFile=..\app\res\icon.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppTitle}
VersionInfoVersion={#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; the game's look: dark art behind every page, white text (Inno Setup 7 'stellar' style), the game's battle art with a
; gold frame on the welcome / finish pages and a gold crest (installer/make-art.py draws them from the site's game art)
WizardStyle=modern stellar excludelightcontrols
WizardBackImageFile=art\back.png
WizardImageFile=art\side.png
WizardSmallImageFile=art\small.png
WizardBackColor=#0b0e14
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=yes

[Languages]
; the installer asks for the language first; the app starts in the same one (install-lang.txt, read once by the app)
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.TaskAutostart=Запускать вместе с Windows
en.TaskAutostart=Start with Windows
ru.TaskErrorReports=Отправлять отчёты об ошибках
en.TaskErrorReports=Send error reports
ru.TasksOptions=Параметры программы (можно изменить позже в настройках):
en.TasksOptions=App options (can be changed later in the settings):
ru.WebViewInstalling=Устанавливается компонент Microsoft Edge WebView2 (нужен для окна программы)...
en.WebViewInstalling=Installing Microsoft Edge WebView2 (the app's window needs it)...

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; both on by default; the choice is applied below ([Registry], installer-options.json)
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:TasksOptions}"
Name: "errorreports"; Description: "{cm:TaskErrorReports}"; GroupDescription: "{cm:TasksOptions}"

[Registry]
; the start with Windows exactly as the app writes it itself (app/Tray.cs Autostart): the value "<exe>" --tray
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AutostartName}"; ValueData: """{app}\{#ExeName}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AutostartName}"; Tasks: not autostart; Flags: deletevalue dontcreatekey
#ifndef TestBuild
; the entry of the old name (RealmForge.exe is replaced by Wardsage.exe)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "RealmForge"; Flags: deletevalue dontcreatekey
#endif

[Files]
Source: "..\dist\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
#ifndef TestBuild
; an install made under the old name (RealmForge): its exe and shortcuts are replaced by the new ones
Type: files; Name: "{app}\RealmForge.exe"
Type: files; Name: "{app}\RealmForge.exe.old"
Type: files; Name: "{autoprograms}\RealmForge.lnk"
Type: files; Name: "{autodesktop}\RealmForge.lnk"
#endif

[Icons]
Name: "{autoprograms}\{#AppTitle}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppTitle}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
; shellexec: the app's manifest asks for administrator rights, Windows shows its prompt
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppTitle}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; left by the self-update; the unpacked interface, WebView2 cache and downloaded updates. The settings with the sync
; code (%APPDATA%\{#UserData}) stay, so a reinstall keeps the link to the site.
Type: files; Name: "{app}\{#ExeName}.old"
#ifndef TestBuild
Type: files; Name: "{app}\RealmForge.exe"
Type: files; Name: "{app}\RealmForge.exe.old"
#endif
#ifndef TestBuild
Type: filesandordirs; Name: "{localappdata}\RealmForge"
#endif
Type: filesandordirs; Name: "{localappdata}\{#OptionsDir}"

[Code]
function JsonBool(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

// The choices for the app: it reads this file once at its start, applies "errorReports" to its config.json and deletes it
// (the installer never edits config.json). "autostart" is informational: the Run value is written by [Registry].
procedure WriteOptions;
var
  Dir, Lang: String;
begin
  if ActiveLanguage = 'ru' then Lang := 'ru' else Lang := 'en';
  Dir := ExpandConstant('{userappdata}\{#OptionsDir}');
  ForceDirectories(Dir);
  SaveStringToFile(Dir + '\installer-options.json',
    '{"errorReports": ' + JsonBool(WizardIsTaskSelected('errorreports')) +
    ', "autostart": ' + JsonBool(WizardIsTaskSelected('autostart')) +
    ', "lang": "' + Lang + '"}', False);
end;

// The app's window is a WebView2: without its runtime the app only offers Microsoft's download page and quits
// (app/Program.cs). Windows 11 and an updated Windows 10 have it; stripped-down ones (LTSC, «lite» builds) may not.
// Then the installer downloads Microsoft's bootstrapper on «Install» and runs it after the files are in place: per user
// without administrator rights, for the whole PC when the installer runs elevated. A failed download or install does not
// stop the setup: the app still explains it at its start.
const
  WebViewClient = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebViewSetup = 'MicrosoftEdgeWebview2Setup.exe';

var
  DownloadPage: TDownloadWizardPage;
  WebViewReady: Boolean;

// Microsoft's documented check: the client's «pv» value, set and not 0.0.0.0, machine-wide (32-bit view) or per user
function WebViewKey(RootKey: Integer; SubKey: String): Boolean;
var
  V: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', V) and (V <> '') and (V <> '0.0.0.0');
end;

function WebViewInstalled: Boolean;
begin
  Result := WebViewKey(HKLM32, 'SOFTWARE\' + WebViewClient) or WebViewKey(HKCU, 'Software\' + WebViewClient);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or WebViewInstalled then Exit;
  DownloadPage.Clear;
  // the Evergreen bootstrapper (~2 MB, signed by Microsoft; it fetches the runtime itself)
  DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', WebViewSetup, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      WebViewReady := True;
    except
      Log('WebView2 bootstrapper download failed: ' + GetExceptionMessage);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure InstallWebView;
var
  Code: Integer;
begin
  if not WebViewReady or WebViewInstalled then Exit;
  WizardForm.StatusLabel.Caption := CustomMessage('WebViewInstalling');
  if Exec(ExpandConstant('{tmp}\' + WebViewSetup), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('WebView2 bootstrapper exit code ' + IntToStr(Code))
  else
    Log('WebView2 bootstrapper did not start: ' + SysErrorMessage(Code));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\install-lang.txt'), ActiveLanguage, False);
    WriteOptions;
    InstallWebView;
  end;
end;
