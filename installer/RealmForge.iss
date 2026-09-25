; RealmForge-Setup.exe — the Windows installer of the desktop app (Inno Setup 7).
;
;   ISCC installer\RealmForge.iss /DAppVersion=1.4.0      (tools/release-app.mjs does it) -> dist\RealmForge-Setup.exe
;
; Installs for the current user (no administrator rights for the installer itself) into
; %LOCALAPPDATA%\Programs\RealmForge: RealmForge.exe and README.txt, a Start menu shortcut and an optional desktop one,
; and an entry in Windows «Apps» for removal. The app then keeps itself up to date (app/Updater.cs), so this installer
; is needed once. The app asks for administrator rights when it starts (the game runs elevated).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8B7E4C5A-3F21-4D8E-9B61-5A2C7E0D4F13}
AppName=RealmForge
AppVersion={#AppVersion}
AppVerName=RealmForge {#AppVersion}
AppPublisher=RealmForge (fan project)
AppPublisherURL=https://realmforge-wor.vercel.app
AppSupportURL=https://github.com/AlexisKozlov/realmforge-extractor
DefaultDirName={localappdata}\Programs\RealmForge
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=RealmForge-Setup
SetupIconFile=..\app\res\icon.ico
UninstallDisplayIcon={app}\RealmForge.exe
UninstallDisplayName=RealmForge
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

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\RealmForge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RealmForge"; Filename: "{app}\RealmForge.exe"
Name: "{autodesktop}\RealmForge"; Filename: "{app}\RealmForge.exe"; Tasks: desktopicon

[Run]
; shellexec: the app's manifest asks for administrator rights, Windows shows its prompt
Filename: "{app}\RealmForge.exe"; Description: "{cm:LaunchProgram,RealmForge}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; left by the self-update; the unpacked interface, WebView2 cache and downloaded updates. The settings with the sync
; code (%APPDATA%\RealmForge) stay, so a reinstall keeps the link to the site.
Type: files; Name: "{app}\RealmForge.exe.old"
Type: filesandordirs; Name: "{localappdata}\RealmForge"
