; Inno Setup script for NFDFix. Builds one installer per architecture from a `dotnet publish` folder.
;
;   ISCC /DAppVersion=1.2.0 /DArch=x64 /DSourceDir=<publish folder> [/O<output folder>] installer\NFDFix.iss
;
; The output name, NFDFix-<version>-win-<arch>-setup.exe, is what the in-app updater looks for in a
; GitHub release (UpdateService.InstallerSuffix), so keep the two in sync.
; The updater runs the installer with /SILENT /SP- /NORESTART /RELAUNCH; /RELAUNCH starts the app again
; once a silent update finishes.

#ifndef AppVersion
  #error Pass the app version, for example /DAppVersion=1.2.0
#endif
#ifndef Arch
  #error Pass the architecture: /DArch=x64 or /DArch=arm64
#endif
#ifndef SourceDir
  #error Pass the dotnet publish folder: /DSourceDir=...
#endif

#define AppName "NFDFix"
#define AppExe "NFDFix.exe"
#define RepoUrl "https://github.com/jhyeok1023/NFDFix"

[Setup]
; Never change AppId: Setup uses it to find an existing install to upgrade.
AppId={{113DD512-63F5-4673-B673-185D305A5B9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Junhyeok Soung
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (c) 2026 Junhyeok Soung
VersionInfoVersion={#AppVersion}

; Per-user install under %LocalAppData%\Programs: no UAC prompt, and the in-app updater can
; replace the files without elevation.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
MinVersion=10.0.17763

#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
  #error Arch must be x64 or arm64
#endif

OutputBaseFilename={#AppName}-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\NFDFix\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; The updater exits the app before starting Setup, but a second window may still be open.
CloseApplications=force
RestartApplications=no

[Languages]
#if FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#else
  #pragma warning "Korean.isl is not in this Inno Setup install; building an English-only installer."
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.SendToTask=탐색기의 '보내기' 메뉴에 추가
english.SendToTask=Add to the Explorer "Send to" menu

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "sendto"; Description: "{cm:SendToTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{usersendto}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: sendto

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; After an update started from the app.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsRelaunchRequested

[UninstallDelete]
; Settings: language.txt and last-update-check.txt.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
function IsRelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;
