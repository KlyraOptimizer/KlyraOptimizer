; =============================================================================
;  Klyra Optimizer - instalador oficial (Inno Setup)
;  Requisitos: Windows 10 21H2 (build 19044) ou superior, x64
; =============================================================================

#define AppName        "Klyra Optimizer"
#define AppVersion     "1.6.2"
#define AppPublisher   "Klyra Optimizer"
#define AppExe         "KlyraOptimizer.exe"
#define AppId          "{{7B4C9E12-8D51-4A7E-9B24-3E9F1C40A815}"

#ifndef SelfContainedBuild
#define SelfContainedBuild 0
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Klyra Optimizer
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=Output
OutputBaseFilename=KlyraOptimizer-Setup-{#AppVersion}
SetupIconFile=..\Assets\KlyraOptimizer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} - instalador
VersionInfoProductName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19044
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
SetupLogging=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Pasta de logs"; Filename: "{commonappdata}\KlyraOptimizer\logs"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden skipifdoesntexist; RunOnceId: "killapp"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""KlyraOptimizer_Startup"" /F"; Flags: runhidden skipifdoesntexist; RunOnceId: "delstartuptask"
Filename: "reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""KlyraOptimizer"" /f"; Flags: runhidden skipifdoesntexist; RunOnceId: "delstartuprun"

[Code]
const
  UrlDotNetRuntime = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  UrlVCLibs        = 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx';
  UrlUIXaml        = 'https://aka.ms/Microsoft.UI.Xaml.2.8.appx';
  UrlAppInstaller  = 'https://aka.ms/getwinget';

var
  NeedDotNet: Boolean;
  NeedWinget: Boolean;
  DotNetVersion: String;
  DependencyNotes: String;

function GetWindowsBuild: Cardinal;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Build;
end;

function DetectDotNetDesktop8: Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if FindFirst(Root + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          DotNetVersion := FindRec.Name;
        end;
      until Result or (not FindNext(FindRec));
    finally
      FindClose(FindRec);
    end;
  end;
end;

function HasWinget: Boolean;
var
  ResultCode: Integer;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe'));

  if not Result then
  begin
    Result :=
      Exec('powershell.exe',
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
        '"if (Get-AppxPackage -AllUsers Microsoft.DesktopAppInstaller) { exit 0 } else { exit 1 }"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  end;
end;

function RunPowerShellHidden(const CommandLine: String): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec('powershell.exe',
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + CommandLine + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function InstallDotNetRuntime(var ErrorMessage: String): Boolean;
var
  Installer: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';

  try
    WizardForm.StatusLabel.Caption := 'Baixando o .NET 8 Desktop Runtime...';
    DownloadTemporaryFile(UrlDotNetRuntime, 'windowsdesktop-runtime-8-win-x64.exe', '', nil);
    Installer := ExpandConstant('{tmp}\windowsdesktop-runtime-8-win-x64.exe');
  except
    ErrorMessage := 'Nao foi possivel baixar o .NET 8 Desktop Runtime: ' + GetExceptionMessage;
    Exit;
  end;

  if not FileExists(Installer) then
  begin
    ErrorMessage := 'O download do .NET Desktop Runtime falhou.';
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Instalando o .NET 8 Desktop Runtime...';
  if not Exec(Installer, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrorMessage := 'Nao foi possivel executar o instalador do .NET 8 Desktop Runtime.';
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638) then
  begin
    ErrorMessage := Format('O instalador do .NET 8 Desktop Runtime retornou o codigo %d.', [ResultCode]);
    Exit;
  end;

  Result := DetectDotNetDesktop8;
  if not Result then
    ErrorMessage := 'O .NET 8 Desktop Runtime foi instalado, mas nao foi detectado. Reinicie e tente novamente.';
end;

function AddAppxPackage(const FileName, FriendlyName: String): Boolean;
var
  Path: String;
begin
  Path := ExpandConstant('{tmp}\') + FileName;
  if not FileExists(Path) then
  begin
    Result := False;
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Instalando ' + FriendlyName + '...';
  Result := RunPowerShellHidden('Add-AppxPackage -Path ''' + Path + ''' -ForceApplicationShutdown -ErrorAction SilentlyContinue; exit 0');
end;

function InstallWinget(var ErrorMessage: String): Boolean;
begin
  Result := False;
  ErrorMessage := '';

  try
    WizardForm.StatusLabel.Caption := 'Baixando dependencias do winget (1/3)...';
    DownloadTemporaryFile(UrlVCLibs, 'KlyraPrereq.VCLibs.appx', '', nil);

    WizardForm.StatusLabel.Caption := 'Baixando dependencias do winget (2/3)...';
    DownloadTemporaryFile(UrlUIXaml, 'KlyraPrereq.UIXaml.appx', '', nil);

    WizardForm.StatusLabel.Caption := 'Baixando o App Installer (3/3)...';
    DownloadTemporaryFile(UrlAppInstaller, 'KlyraPrereq.AppInstaller.msixbundle', '', nil);
  except
    ErrorMessage := 'Nao foi possivel baixar as dependencias do winget: ' + GetExceptionMessage;
    Exit;
  end;

  AddAppxPackage('KlyraPrereq.VCLibs.appx', 'Microsoft.VCLibs');
  AddAppxPackage('KlyraPrereq.UIXaml.appx', 'Microsoft.UI.Xaml');
  AddAppxPackage('KlyraPrereq.AppInstaller.msixbundle', 'App Installer (winget)');

  Result := HasWinget;
  if not Result then
    ErrorMessage :=
      'O winget nao pode ser instalado automaticamente. Instale o "App Installer" pela Microsoft Store ' +
      'para usar a Loja do Klyra Optimizer.';
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  {$IF SelfContainedBuild = 1}
  NeedDotNet := False;
  DependencyNotes := ' .NET 8 embutido na publicacao (nao requer runtime no cliente)' + #13#10;
  {$ELSE}
  NeedDotNet := not DetectDotNetDesktop8;
  if NeedDotNet then
    DependencyNotes := DependencyNotes + ' .NET 8 Desktop Runtime (sera instalado agora)' + #13#10
  else
    DependencyNotes := DependencyNotes + ' .NET 8 Desktop Runtime ' + DotNetVersion + ' detectado' + #13#10;
  {$ENDIF}

  NeedWinget := not HasWinget;

  if NeedWinget then
    DependencyNotes := DependencyNotes + ' winget / App Installer: nao encontrado (sera instalado agora)' + #13#10
  else
    DependencyNotes := DependencyNotes + ' winget / App Installer detectado' + #13#10;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if MemoDirInfo <> '' then Result := Result + MemoDirInfo + NewLine + NewLine;
  Result := Result + 'Dependencias:' + NewLine + DependencyNotes + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorMessage: String;
begin
  Result := '';

  if GetWindowsBuild < 19044 then
  begin
    Result :=
      'O Klyra Optimizer requer Windows 10 21H2 (build 19044) ou superior.' + #13#10 +
      'Este computador esta com o build ' + IntToStr(GetWindowsBuild) + '.';
    Exit;
  end;

  if NeedDotNet then
  begin
    if not InstallDotNetRuntime(ErrorMessage) then
    begin
      Result :=
        'Esta dependencia e obrigatoria para o aplicativo abrir.' + #13#10#13#10 +
        ErrorMessage + #13#10#13#10 +
        'Instale manualmente em https://dotnet.microsoft.com/download/dotnet/8.0/runtime e execute o instalador novamente.';
      Exit;
    end;
  end;

  if NeedWinget then
  begin
    if not InstallWinget(ErrorMessage) then
    begin
      MsgBox(
        ErrorMessage + #13#10#13#10 +
        'O Klyra Optimizer sera instalado normalmente, mas a instalacao de aplicativos pela Loja ' +
        'ficara indisponivel ate o winget ser instalado.',
        mbInformation, MB_OK);
    end;
  end;
end;
