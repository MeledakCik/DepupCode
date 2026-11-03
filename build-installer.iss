#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be supplied by build-installer.ps1
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied by build-installer.ps1
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be supplied by build-installer.ps1
#endif
#ifndef VersionInfoVersion
  #error VersionInfoVersion must be supplied by build-installer.ps1
#endif

[Setup]
AppId={{54F7AF61-9788-40A1-9F36-E5022B31A6ED}
AppName=Depup Code
AppVersion={#AppVersion}
AppVerName=Depup Code {#AppVersion}
AppPublisher=Depup
AppPublisherURL=https://github.com/MeledakCik/DepupCode
AppSupportURL=https://github.com/MeledakCik/DepupCode/issues
AppUpdatesURL=https://github.com/MeledakCik/DepupCode/releases
AppCopyright=Copyright (C) 2026 Depup
VersionInfoCompany=Depup
VersionInfoDescription=Depup Code Setup
VersionInfoProductName=Depup Code
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#VersionInfoVersion}
DefaultDirName={autopf}\Depup Code
DefaultGroupName=Depup Code
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=DepupCode-Setup-v{#AppVersion}-win-x64
SetupIconFile={#ProjectRoot}\DepupCode\Assets\depup.ico
UninstallDisplayIcon={app}\DepupCode.exe
UninstallDisplayName=Depup Code
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
SetupMutex=DepupCodeSetup
ChangesAssociations=yes
AllowNoIcons=yes
InfoBeforeFile={#ProjectRoot}\Packaging\Installer\PRIVACY-NOTICE.txt
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "indonesian"; MessagesFile: "{#ProjectRoot}\Packaging\Installer\Indonesian.isl"

[CustomMessages]
english.LaunchProgram=Launch Depup Code
indonesian.LaunchProgram=Jalankan Depup Code
english.DesktopIcon=Create a &desktop shortcut
indonesian.DesktopIcon=Buat pintasan &desktop
english.ShortcutGroup=Additional shortcuts:
indonesian.ShortcutGroup=Pintasan tambahan:
english.ContextMenu=Add "Open with Depup Code" to the folder context menu
indonesian.ContextMenu=Tambahkan "Buka dengan Depup Code" ke menu klik kanan folder

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:ShortcutGroup}"; Flags: unchecked
Name: "contextmenu"; Description: "{cm:ContextMenu}"; GroupDescription: "{cm:ShortcutGroup}"

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProjectRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Depup Code"; Filename: "{app}\DepupCode.exe"
Name: "{group}\Uninstall Depup Code"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Depup Code"; Filename: "{app}\DepupCode.exe"; Tasks: desktopicon

[Registry]
; depup:// callback used by the optional Depup Cloud Google sign-in.
Root: HKCU; Subkey: "Software\Classes\depup"; ValueType: string; ValueName: ""; ValueData: "URL:Depup Code login"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\depup"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\depup\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\DepupCode.exe,0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\depup\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\DepupCode.exe"" ""%1"""; Flags: uninsdeletekey

; "Open with Depup Code" on folders and folder backgrounds.
Root: HKCU; Subkey: "Software\Classes\Directory\shell\DepupCode"; ValueType: string; ValueName: ""; ValueData: "Open with Depup Code"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\DepupCode"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\DepupCode.exe,0"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\DepupCode\command"; ValueType: string; ValueName: ""; ValueData: """{app}\DepupCode.exe"" ""%1"""; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\DepupCode"; ValueType: string; ValueName: ""; ValueData: "Open with Depup Code"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\DepupCode"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\DepupCode.exe,0"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\DepupCode\command"; ValueType: string; ValueName: ""; ValueData: """{app}\DepupCode.exe"" ""%V"""; Tasks: contextmenu; Flags: uninsdeletekey

[Run]
Filename: "{app}\DepupCode.exe"; Description: "{cm:LaunchProgram}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
function WebView2RuntimePresent(): Boolean;
begin
  Result :=
    RegKeyExists(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKLM, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKLM, 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not WebView2RuntimePresent() then
    MsgBox('Microsoft Edge WebView2 Runtime was not detected.' + #13#10#13#10 +
           'Depup Code needs it to render the editor preview and terminal. ' +
           'Windows Update normally installs it automatically; otherwise download it from ' +
           'https://developer.microsoft.com/microsoft-edge/webview2/ before starting Depup Code.',
           mbInformation, MB_OK);
end;
