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
  #define AppTitle "Wardsage Test"
  #define ExeName "Wardsage-Test.exe"
  #define SetupName "Wardsage-Test-Setup"
  #define UserData "WardsageTest"
#else
  #define AppTitle "Wardsage"
  #define ExeName "Wardsage.exe"
  #define SetupName "Wardsage-Setup"
  #define UserData "RealmForge"
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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

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
Type: files; Name: "{app}\RealmForge.exe.old"
#endif
Type: filesandordirs; Name: "{localappdata}\{#UserData}"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\install-lang.txt'), ActiveLanguage, False);
end;
