#ifndef MyAppVersion
  #define MyAppVersion "1.9.5"
#endif

[Setup]
; AppId sabit kalmali: Inno bu kimlikle mevcut kurulumu bulup yerinde yukseltir.
; Ilk surumler AppName="Cloud Print Agent" ile kuruldugu icin eski kimlikli
; kurulumlar asagida [Code] icinde kaldiriliyor.
AppId=NuxPrint Agent
AppName=NuxPrint Agent
AppVersion={#MyAppVersion}
AppPublisher=NuxPrint Print
DefaultDirName={autopf}\NuxPrintAgent
OutputDir=installer
OutputBaseFilename=NuxPrintAgent-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
SetupIconFile=src\PrintAgent.UI\Resources\icon.ico
UninstallDisplayIcon={app}\PrintAgent.UI.exe
; Agent calisirken kurulum baslarsa: mutex serbest kalana kadar bekle / kapatmaya zorla
AppMutex=Global\CloudPrintAgentUI
CloseApplications=yes

[InstallDelete]
; Yerinde yukseltmede Inno eski dosyalari kendiliginden silmez; yeni surumde artik
; gonderilmeyen DLL'ler {app} altinda birikirdi. Kopyalamadan hemen once temizlenir,
; ardindan [Files] guncel seti yazar. Sadece bizim uygulama klasorumuz (certs ve
; veri klasoru etkilenmez).
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "publish\ui\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "certs\*"; DestDir: "{app}\certs"; Flags: ignoreversion

[Dirs]
Name: "{app}\tools"

[Run]
; Spooler kurtarma politikasi.
;
; Windows'un varsayilaninda SCM, ikinci cokmeden sonra "hicbir sey yapma" der ve
; yazdirma servisi kapali kalir. Bu durumda agent WMI'dan hic yazici goremez,
; hicbir is basilmaz ve POS ortaminda bunu kimse fark etmez -- sessiz kesinti.
; Sahada bunun bir ornegi yasandi: kirilgan bir POS surucusu (SPRTUNI_POS)
; spoolsv'yi dusurdu, servis geri gelmedi ve bir saat sonraki baskilar da
; sessizce kayboldu.
;
; Bu satir uc denemeyi de "5 sn sonra yeniden baslat" yapar; sayac bir gun hatasiz
; gectiginde sifirlanir. Makine geneli bir Windows ayaridir (yalnizca bizim
; uygulamamizi degil), bu yuzden bilincli bir tercih: kaldirma sirasinda geri
; alinmaz -- daha dayanikli bir spooler birakmak zarar vermez ve onceki degeri
; saklamak icin ek durum tutmak gerekirdi.
; Kaldirmak isteyen bu satiri silebilir; mevcut degeri gormek icin: sc qfailure Spooler
Filename: "{sys}\sc.exe"; Parameters: "failure Spooler reset= 86400 actions= restart/5000/restart/5000/restart/5000"; Flags: runhidden; StatusMsg: "Yazdirma servisi kurtarma ayari yapiliyor..."

; Let's Encrypt kok sertifikalari (ISRG Root X1/X2): kok deposu guncellenmemis veya
; otomatik kok guncellemesi kapali makinelerde TLS dogrulamasi dusmesin diye
; kurulumda LocalMachine\Root deposuna eklenir (idempotent, setup zaten admin).
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f Root ""{app}\certs\isrgrootx1.pem"""; Flags: runhidden; StatusMsg: "Kok sertifikalar yukleniyor..."
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f Root ""{app}\certs\isrg-root-x2.pem"""; Flags: runhidden; StatusMsg: "Kok sertifikalar yukleniyor..."

; Etkilesimli kurulum: kullanici bitis sayfasindaki kutucukla baslatir
Filename: "{app}\PrintAgent.UI.exe"; Description: "Start NuxPrint Cloud Agent"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Sessiz kurulum (otomatik guncelleme): agent'i yukseltilmemis kullanici olarak yeniden baslat
Filename: "{app}\PrintAgent.UI.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Icons]
Name: "{commondesktop}\NuxPrint Agent"; Filename: "{app}\PrintAgent.UI.exe"
Name: "{commonstartup}\NuxPrint Agent"; Filename: "{app}\PrintAgent.UI.exe"
Name: "{group}\NuxPrint Agent"; Filename: "{app}\PrintAgent.UI.exe"

[Code]
// "Cloud Print Agent" adiyla kurulmus eski surumu (farkli AppId) sessizce kaldirir;
// yoksa eski kurulum ayri klasorde kalir ve baslangic kisayolu eski surumu acmaya devam eder.
procedure UninstallLegacyCloudPrintAgent();
var
  UninstKey, UninstStr: string;
  ResultCode: Integer;
begin
  UninstKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Cloud Print Agent_is1';
  UninstStr := '';
  if not RegQueryStringValue(HKLM32, UninstKey, 'UninstallString', UninstStr) then
    RegQueryStringValue(HKLM64, UninstKey, 'UninstallString', UninstStr);
  if UninstStr <> '' then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PrintAgent.UI.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(RemoveQuotes(UninstStr), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  UninstallLegacyCloudPrintAgent();
  Result := '';
end;
procedure CurStepChanged(CurStep: TSetupStep);
var
  LegacyDirectory: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    LegacyDirectory := ExpandConstant('{commonappdata}\CloudPrintAgent');
    if DirExists(LegacyDirectory) then
      if not Exec(ExpandConstant('{sys}\icacls.exe'), '"' + LegacyDirectory + '" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /remove:g *S-1-5-32-545 *S-1-5-11 *S-1-1-0 /T /C', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
        RaiseException('Cannot secure legacy agent data. Installation cannot complete safely.');
  end;
end;
