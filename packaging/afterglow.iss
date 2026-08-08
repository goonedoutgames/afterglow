; Afterglow Windows installer (Inno Setup 6)
; Built from packaging/publish-windows.ps1 output in publish\windows

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\windows"
#endif

#ifndef IconFile
  #define IconFile "..\src\Afterglow\Assets\afterglow.ico"
#endif

#ifndef OutputDir
  #define OutputDir "..\publish"
#endif

[Setup]
AppId={{A7F3C2E1-8B4D-4E9A-9C1F-2D6E5B8A0F31}
AppName=Afterglow
AppVersion={#AppVersion}
AppVerName=Afterglow {#AppVersion}
AppPublisher=Gooned Out Games
AppPublisherURL=https://github.com/goonedoutgames/afterglow
AppSupportURL=https://github.com/goonedoutgames/afterglow/issues
DefaultDirName={autopf}\Afterglow
DefaultGroupName=Afterglow
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Afterglow-Setup-x64
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Afterglow.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Portable publish tree (Afterglow.exe + deps + sidecar)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Afterglow"; Filename: "{app}\Afterglow.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Afterglow"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Afterglow"; Filename: "{app}\Afterglow.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Afterglow.exe"; Description: "{cm:LaunchProgram,Afterglow}"; Flags: nowait postinstall skipifsilent

[Code]
function DotNetDesktopRuntimeInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := False;
  { .NET 8 Desktop Runtime (x64) release key }
  if RegQueryDWordValue(HKLM64,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
       '8.0.0', Installed) then
  begin
    Result := True;
    Exit;
  end;
  { Any 8.x desktop shared framework folder is enough }
  Result := DirExists(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.0'))
         or DirExists(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App'));
  if Result then
  begin
    { Prefer confirming an 8.* directory exists }
    Result := True;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not DotNetDesktopRuntimeInstalled then
  begin
    if MsgBox('Afterglow needs the .NET 8 Desktop Runtime (x64).' + #13#10#13#10 +
              'Open the download page now?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open',
        'https://dotnet.microsoft.com/download/dotnet/8.0',
        '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    { Allow continuing — user may install runtime afterwards }
    Result := True;
  end;
end;
