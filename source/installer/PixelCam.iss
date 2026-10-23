#ifndef PayloadPath
  #error PayloadPath must point to the staged application folder
#endif
#ifndef ReleaseDir
  #define ReleaseDir "releases"
#endif
#define AppVersion "0.7.0"
#define FfmpegUrl "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-22-13-06/ffmpeg-n8.1.3-23-g5c4f5744cd-win64-lgpl-shared-8.1.zip"
#define FfmpegHash "c41df73a3f3259e81d5d53e6ce0c4c80b4841a30894fb77954650e445daa6c53"
#define FfmpegFolder "ffmpeg-n8.1.3-23-g5c4f5744cd-win64-lgpl-shared-8.1"
#define AdbUrl "https://dl.google.com/android/repository/platform-tools_r37.0.0-win.zip"
#define AdbHash "4fe305812db074cea32903a489d061eb4454cbc90a49e8fea677f4b7af764918"

[Setup]
AppId={{BC155159-65D8-4251-8B9E-83067EC2C102}
AppName=PixelCam
AppVersion={#AppVersion}
AppPublisher=PixelCam contributors
AppPublisherURL=https://github.com/frkatona/pixelcam
AppSupportURL=https://github.com/frkatona/pixelcam/issues
DefaultDirName={localappdata}\Programs\PixelCam
DefaultGroupName=PixelCam
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
DisableProgramGroupPage=yes
DisableDirPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchiveExtraction=full
OutputDir={#ReleaseDir}
OutputBaseFilename=PixelCam-{#AppVersion}-Setup
SetupIconFile=..\windows\assets\PixelCam.ico
UninstallDisplayIcon={app}\PixelCam.exe
LicenseFile=..\..\LICENSE.txt
InfoBeforeFile=INSTALL-NOTES.txt
CloseApplications=yes
CloseApplicationsFilter=PixelCam.exe,PixelCamMaxine.dll
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#PayloadPath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadPath}\PixelCam.exe"; Flags: dontcopy

[Icons]
Name: "{userprograms}\PixelCam"; Filename: "{app}\PixelCam.exe"; WorkingDir: "{app}"

[InstallDelete]
Type: files; Name: "{app}\runtime\ffmpeg.exe"
Type: files; Name: "{app}\runtime\ffprobe.exe"
Type: files; Name: "{app}\runtime\ffmpeg-LICENSE.txt"
Type: files; Name: "{app}\runtime\adb.exe"
Type: files; Name: "{app}\runtime\AdbWinApi.dll"
Type: files; Name: "{app}\runtime\AdbWinUsbApi.dll"

[Run]
Filename: "{app}\PixelCam.exe"; Description: "Open PixelCam"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\PixelCam.exe"; Parameters: "--unregister-camera"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterPixelCam"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\runtime\ffmpeg"
Type: filesandordirs; Name: "{app}\runtime\platform-tools"
Type: filesandordirs; Name: "{app}\virtual-camera"
Type: files; Name: "{app}\PixelCam-error.txt"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  HardwarePage: TOutputMsgWizardPage;
  ToolsPrepared: Boolean;

procedure CopyTree(const Source, Destination: String);
var
  Found: TFindRec;
begin
  if not ForceDirectories(Destination) then RaiseException('Could not create ' + Destination);
  if FindFirst(Source + '\*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          if Found.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            CopyTree(Source + '\' + Found.Name, Destination + '\' + Found.Name)
          else if not FileCopy(Source + '\' + Found.Name, Destination + '\' + Found.Name, False) then
            RaiseException('Could not install ' + Found.Name + '. Close video applications and retry.');
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function DownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
var
  ExitCode: Integer;
  Summary: AnsiString;
begin
  ExtractTemporaryFile('PixelCam.exe');
  Summary := 'The Background tab checks your NVIDIA GPU and downloads matching Maxine packages when you request them. Ordinary webcam controls work without Maxine.';
  if Exec(ExpandConstant('{tmp}\PixelCam.exe'), '--hardware-summary "' + ExpandConstant('{tmp}\gpu.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    if ExitCode = 0 then LoadStringFromFile(ExpandConstant('{tmp}\gpu.txt'), Summary);
  HardwarePage := CreateOutputMsgPage(wpInfoBefore, 'Graphics hardware', 'Optional NVIDIA effects', Utf8Decode(Summary) + #13#10#13#10 +
    'Set up Maxine and Eye contact from PixelCam''s Background tab after installation. These large downloads are cached and reused.' + #13#10#13#10 +
    'Setup downloads video tools (about 77 MiB) from BtbN/FFmpeg and USB tools (about 8 MiB) from Google. Internet access is required. A Pixel USB driver may also be needed; see the manual.');
  DownloadPage := CreateDownloadPage('Downloading required tools', 'Getting video and USB tools from their upstream publishers.', @DownloadProgress);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ToolsPrepared then exit;
  DownloadPage.Clear;
  DownloadPage.Add('{#FfmpegUrl}', 'ffmpeg.zip', '{#FfmpegHash}');
  DownloadPage.Add('{#AdbUrl}', 'platform-tools.zip', '{#AdbHash}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      ExtractArchive(ExpandConstant('{tmp}\ffmpeg.zip'), ExpandConstant('{tmp}\video-tools'), '', True, nil);
      ExtractArchive(ExpandConstant('{tmp}\platform-tools.zip'), ExpandConstant('{tmp}\usb-tools'), '', True, nil);
      ToolsPrepared := True;
    except
      Result := 'Required tools could not be prepared. Check your connection and run setup again.' + #13#10 + GetExceptionMessage;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    CopyTree(ExpandConstant('{tmp}\video-tools\{#FfmpegFolder}'), ExpandConstant('{app}\runtime\ffmpeg'));
    CopyTree(ExpandConstant('{tmp}\usb-tools\platform-tools'), ExpandConstant('{app}\runtime\platform-tools'));
    if not Exec(ExpandConstant('{app}\PixelCam.exe'), '--register-camera', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('Could not register the webcam. Run setup again.');
    if ExitCode <> 0 then RaiseException('Webcam registration failed. Close video apps and run setup again.');
  end;
end;
