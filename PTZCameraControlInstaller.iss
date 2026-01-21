;PTZ Camera Control Installer
;Author: Robert Foster
;Version: 1.0.0

#define MyAppName "PTZ Camera Operator"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Robert Foster"
#define MyAppURL "https://github.com/devildog5x5/PTZ_Interface"
#define MyAppExeName "PTZCameraOperator.exe"
#define MyAppIcon "warrior_icon.ico"

[Setup]
AppId={{F8A9B3C5-2D4E-4F1A-9876-5B4C3A2D1E0F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=.\Installer\Output
OutputBaseFilename=PTZCameraOperatorSetup-{#MyAppVersion}
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppIcon}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
LicenseFile=LICENSE.txt
AppCopyright=Copyright © 2025 Robert Foster

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\release\PTZCameraOperator.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "warrior_icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIcon}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIcon}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  MsgBox('PTZ Camera Operator by Robert Foster' + #13#10 + 'Professional ONVIF PTZ Camera Controller', mbInformation, MB_OK);
end;

