; Lumen installer. Build with installer\build.ps1, which publishes the app first.
; Per-user install: no administrator rights and no UAC prompt.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\app"
#endif

[Setup]
AppId={{6E0B7C3A-4D51-4C8E-9A0F-2F7B1D6C5E41}
AppName=Lumen
AppVersion={#AppVersion}
AppVerName=Lumen {#AppVersion}
AppPublisher=Shengwei Li
AppPublisherURL=https://github.com/Shengwei-Li/lumen
AppSupportURL=https://github.com/Shengwei-Li/lumen/issues
DefaultDirName={localappdata}\Programs\Lumen
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=no
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.18362
OutputDir=..\artifacts
OutputBaseFilename=Lumen-{#AppVersion}-Setup
SetupIconFile=..\Assets\lumen.ico
UninstallDisplayIcon={app}\Lumen.exe
UninstallDisplayName=Lumen
WizardStyle=modern
WizardSizePercent=100
WizardImageFile=wizard-100.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small-100.bmp,wizard-small-200.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lumen"; Filename: "{app}\Lumen.exe"
Name: "{autodesktop}\Lumen"; Filename: "{app}\Lumen.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Lumen.exe"; Description: "Launch Lumen"; Flags: nowait postinstall skipifsilent

[Code]
// The capture library is C++/CLI and needs the Visual C++ runtime, which most systems have.
function HasVCRuntime: Boolean;
var
  installed: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', installed) and (installed = 1);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  code: Integer;
begin
  if (CurPageID = wpFinished) and not HasVCRuntime then
    if MsgBox('Lumen needs the Microsoft Visual C++ Redistributable (x64), which is not installed.' + #13#10#13#10 +
              'Open the download page now?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://aka.ms/vs/17/release/vc_redist.x64.exe', '', '', SW_SHOWNORMAL, ewNoWait, code);
end;
