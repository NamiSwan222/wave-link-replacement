; WaveFix installer (Inno Setup 6).
; Build: iscc /DAppVersion=0.3.0 /DPublishDir=..\publish installer\WaveFix.iss
; Per-user install (no admin prompt), so WaveFix can update itself silently.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; Never change AppId: it's how updates find the existing install.
AppId={{B9BA8B3C-6D94-4548-BE2D-B32E361FEB14}
AppName=WaveFix
AppVersion={#AppVersion}
AppVerName=WaveFix {#AppVersion}
AppPublisher=NamiSwan222
AppPublisherURL=https://github.com/NamiSwan222/wave-link-replacement
AppSupportURL=https://github.com/NamiSwan222/wave-link-replacement/issues
AppUpdatesURL=https://github.com/NamiSwan222/wave-link-replacement/releases
DefaultDirName={localappdata}\Programs\WaveFix
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=WaveFix-Setup-{#AppVersion}
SetupIconFile=..\src\WaveFix\Assets\WaveFix.ico
UninstallDisplayIcon={app}\WaveFix.exe
UninstallDisplayName=WaveFix
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
InfoBeforeFile=before-install.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start WaveFix with &Windows (in the tray)"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WaveFix"; Filename: "{app}\WaveFix.exe"
Name: "{autodesktop}\WaveFix"; Filename: "{app}\WaveFix.exe"; Tasks: desktopicon

[Registry]
; Same value the app's "Start with Windows" switch uses; removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WaveFix"; ValueData: """{app}\WaveFix.exe"" --tray"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WaveFix"; Flags: dontcreatekey uninsdeletevalue

[Run]
; Normal install: offer to open it. Silent update (/UPDATE): start the new version in the tray.
Filename: "{app}\WaveFix.exe"; Description: "Open WaveFix now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\WaveFix.exe"; Parameters: "--updated"; Flags: nowait; Check: IsUpdate

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM WaveFix.exe /F"; Flags: runhidden; RunOnceId: "StopWaveFix"
; Takes WaveFix's audio engine off every mic (original effects back) and removes it from
; Program Files. It's machine-wide, so Windows asks for permission once.
Filename: "{app}\WaveFix.exe"; Parameters: "--uninstall"; Flags: waituntilterminated; RunOnceId: "RemoveEngine"

[Code]
function IsUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
      Result := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Updates go straight through without questions. }
  Result := IsUpdate;
end;
