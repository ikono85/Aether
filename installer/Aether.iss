; Installeur AETHER (Inno Setup 6).
;
; 1. Publier l'application :
;      dotnet publish Aether.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
; 2. (Recommandé) Signer publish\win-x64\Aether.exe avec signtool et un certificat Authenticode.
; 3. Compiler ce script :  iscc installer\Aether.iss
;
; L'installation dans Program Files est une exigence de sécurité : AETHER s'exécute en
; administrateur, et son démarrage automatique (tâche planifiée élevée) est refusé si
; l'exécutable se trouve dans un dossier modifiable sans droits administrateur.

#define AppVersion "1.2.0"

[Setup]
AppId={{6B3E2E7C-2B0B-4C39-9E42-8B7A9D6C1F10}
AppName=AETHER
AppVersion={#AppVersion}
AppPublisher=AETHER
DefaultDirName={autopf}\AETHER
DefaultGroupName=AETHER
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\installer
OutputBaseFilename=AETHER-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\Aether.exe
WizardStyle=modern
; AETHER ouvert pendant la mise à jour : l'installeur demande de le fermer.
AppMutex=Global\Aether.SingleInstance

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\AETHER"; Filename: "{app}\Aether.exe"

[Run]
Filename: "{app}\Aether.exe"; Description: "Lancer AETHER"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; La tâche de démarrage automatique pointe vers l'exécutable supprimé : on la retire.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN AetherAutoStart /F"; Flags: runhidden; RunOnceId: "DeleteAutoStartTask"

[Code]
var
  RestoreBeforeUninstall: Boolean;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Answer := MsgBox(
    'Rétablir la configuration d''origine de Windows avant de désinstaller ?' + #13#10 + #13#10 +
    'Oui : AETHER annule les optimisations, les changements de services et de DNS qu''il a appliqués ' +
    '(équivalent de « Tout restaurer » dans l''onglet Historique).' + #13#10 +
    'Non : les modifications restent en place ; leurs sauvegardes sont conservées dans %ProgramData%\Aether.',
    mbConfirmation, MB_YESNOCANCEL);

  RestoreBeforeUninstall := Answer = IDYES;
  Result := Answer <> IDCANCEL;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if (CurUninstallStep = usUninstall) and RestoreBeforeUninstall then
  begin
    if not Exec(ExpandConstant('{app}\Aether.exe'), '--restore-all', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      MsgBox('La restauration n''a pas pu être lancée. Les sauvegardes restent dans %ProgramData%\Aether.', mbError, MB_OK)
    else if ResultCode = 3 then
      MsgBox('AETHER est encore ouvert : la restauration n''a pas été faite. Fermez-le puis relancez la désinstallation.', mbError, MB_OK)
    else if ResultCode <> 0 then
      MsgBox('Certaines modifications n''ont pas pu être restaurées (détails dans %LocalAppData%\Aether\logs). ' +
             'Leurs sauvegardes restent dans %ProgramData%\Aether.', mbInformation, MB_OK);
  end;
end;
