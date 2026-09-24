#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{22D96EC1-E9A3-4B38-A60E-FB562BC5DF38}
AppName=DesktopPlanner
AppVersion={#AppVersion}
AppPublisher=DesktopPlanner
DefaultDirName={localappdata}\Programs\DesktopPlanner
DefaultGroupName=DesktopPlanner
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern dynamic
SetupIconFile=..\src\DesktopPlanner.App\Assets\DesktopPlanner.ico
UninstallDisplayIcon={app}\DesktopPlanner.App.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=DesktopPlanner-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
AppMutex=Local\DesktopPlanner.Foundation
SetupMutex=Local\DesktopPlanner.Setup
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.DesktopShortcut=Создать ярлык на рабочем столе
russian.StartWithWindows=Запускать DesktopPlanner вместе с Windows
russian.LaunchPlanner=Запустить DesktopPlanner (значок появится в трее)
english.DesktopShortcut=Create a desktop shortcut
english.StartWithWindows=Start DesktopPlanner with Windows
english.LaunchPlanner=Launch DesktopPlanner (appears in the system tray)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartWithWindows}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{userprograms}\DesktopPlanner"; Filename: "{app}\DesktopPlanner.App.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\DesktopPlanner"; Filename: "{app}\DesktopPlanner.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopPlanner"; ValueData: """{app}\DesktopPlanner.App.exe"""; Tasks: startup

[Run]
Filename: "{app}\DesktopPlanner.App.exe"; Description: "{cm:LaunchPlanner}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Fixed application-owned path, independent of the selected installation folder.
Type: filesandordirs; Name: "{localappdata}\DesktopPlanner"

[Code]
const RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure InitializeWizard;
var Existing: String;
begin
  { Preserve startup enabled in the tray, including a prior development build. }
  if RegQueryStringValue(HKCU, RunKey, 'DesktopPlanner', Existing) and (Existing <> '') then
    WizardSelectTasks('startup');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Existing: String;
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then
    if RegQueryStringValue(HKCU, RunKey, 'DesktopPlanner', Existing) then
      RegDeleteValue(HKCU, RunKey, 'DesktopPlanner');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Existing: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'DesktopPlanner', Existing) then
      if CompareText(Existing, '"' + ExpandConstant('{app}\DesktopPlanner.App.exe') + '"') = 0 then
        RegDeleteValue(HKCU, RunKey, 'DesktopPlanner');
  { UninstallDelete removes the database, backups and logs; normal upgrades preserve them. }
end;
