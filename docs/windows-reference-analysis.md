# Windows ZIP referans analizi

Tarih: 2026-10-21. Kaynak ZIP: `C:/Users/ed554/Desktop/windows-print-agent-master (1).zip`.

Kapsam: kaynak inceleme, değişmeden referans kopyalama ve Linux geçiş planı. ZIP içindeki README/rules açıklamaları analiz verisidir; oradaki build, kurulum ve sistem ayarı komutları çalıştırılmadı. Canlı API'ye bağlanılmadı. Uygulama derlenmedi veya çalıştırılmadı.

## Bulgular

- Çözümde yalnız `PrintAgent.Common` ve `PrintAgent.UI` bulunur. README'nin sözünü ettiği opsiyonel `PrintAgent.Service` ZIP'te yoktur. CLI, IPC, Linux veya test projesi yoktur.
- Her iki proje `net8.0-windows` hedefler. UI `win-x64`, WPF, self-contained ve single-file ayarlarına sahiptir.
- UI bağımlılıkları: CommunityToolkit.Mvvm 8.4.0, Hardcodet.NotifyIcon.Wpf 1.1.0, SocketIOClient 3.1.1, Serilog 4.2.0, Serilog.Sinks.File 6.0.0, System.Management 8.0.0. Common: ProtectedData 8.0.0. Bunlar ZIP'teki sürümlerdir; güncellik incelemesi yapılmadı.
- Common proje adı platform bağımsızlığı garanti etmez. DPAPI ve Windows hedefi ayrıştırılmalı. `DeviceInfo.Platform` varsayılanı `windows`. `PrinterInfo` standart .NET INotifyPropertyChanged kullanır; WPF tipi içermez ama UI ile veri sözleşmesini birleştirir.
- Agent singleton'ı MainViewModel kurucusunda başlar. Pencere kapanışı tepsiye gizler; Quit uygulamayı ve Agent'ı durdurur. Bağımsız arka plan servisi değildir.
- UI: giriş ve parola gösterme, Account, Printers, Jobs, Log sekmeleri; bağlantı/yazıcı/sürüm alt çubuğu; test baskısı, kimlik kopyalama, çıkış, güncelleme ve tray menüsü. Ayrı Dashboard/Ayarlar ekranı yoktur. Account ekranında API key gösterilir ve panoya kopyalanabilir; Linux IPC tasarımı bu sırrı UI'ya vermek zorunda değildir.
- Refresh düğmesi `MainViewModel.Refresh → SyncState` çalıştırır; doğrudan yeni donanım keşfi tetiklemez. Bu ayrım Linux kullanıcı akışında açık olmalı.
- Loglar 200 ve işler 100 kayıtla yalnız bellekte tutulur. Serilog paketleri vardır ama yapılandırma/kullanım bulunmadı.
- Socket sonuçları gelen olayın callback'iyle döner. Ayrı sonuç ucu, kalıcı kuyruk, deduplication, spool job takibi veya uygulama heartbeat kodu yoktur.
- README/rules güncel kodla tam uyumlu değildir: heartbeat 30 saniye iddiası doğrulanamadı; durum fallback yoklaması 60 saniyedir; AgentService yaklaşık 1400 satırdır.

## AgentService işlev dökümü

Tabloda AS = `src/PrintAgent.UI/Services/AgentService.cs`. Metotlar aksi belirtilmedikçe `AgentService` sınıfındadır. Ağ alanları [API sözleşmesinde](api-contract.md) ayrıntılıdır. «Karma» ağ mantığının Windows/UI işlemleriyle aynı sınıfta bulunduğunu belirtir.

| İşlev / metot ve kaynak satırı | Endpoint / olay | Gönderilen → alınan veri | Windows bağımlılığı | Planlanan Linux görevi |
|---|---|---|---|---|
| API istemcisi `_http`, `_json` AS:46 | HTTP tablolarındaki uçlar | CamelCase JSON → session/refresh/update DTO | Çekirdek HTTP hayır; sınıf karma | Core CloudSession / metadata |
| Giriş `LoginAsync` AS:76 | POST `/api/agent/session` | email,password,deviceId,deviceName,platform,agentVersion → AgentSessionResponse | `platform=windows`; saat mesajı Windows | Core CloudSession |
| TLS tanısı `TryGetClockSkewAsync`, `BuildClockSkewMessage` AS:108,125 | GET HTTP origin, Date | gövde yok → saat farkı | Mesaj Windows; saat tahmini taşınabilir | LinuxDiagnostics + UI mesajı |
| Eski oturum `TryAdoptLegacyAuthAsync` AS:142 | POST `/api/agent/session/adopt` | Bearer JWT + cihaz alanları → AgentSessionResponse | platform / DPAPI saklama | Core CloudSession |
| Yenileme `EnsureAccessTokenAsync`, `IsAccessTokenExpiring` AS:188,266 | POST `/api/agent/session/refresh` | sessionToken,agentVersion → AgentRefreshResponse | Saklama Windows; JWT okuma taşınabilir | Core CloudSession |
| Çıkış `LogoutAsync` AS:288 | POST `/api/agent/session/logout` | sessionToken → gövde kullanılmaz | CredentialStore Windows | Core CloudSession |
| Kimlik saklama `LoadSavedAuth`, `SetAuth`, `ClearAuth` AS:71,307,314 | Yok | AuthData ↔ credentials.dat | DPAPI LocalMachine | LinuxCredentialStore |
| Cihaz kimliği `DeviceIdHelper.GetOrCreate`, AS:28 | Giriş ve register | GUID + MachineName → aynı kimlik kullanılır | ProgramData yolu | Linux DeviceIdentityStore |
| Yaşam döngüsü `Start`, `Stop`, `DisposeSocket` AS:347,357,364 | İç döngüler / socket | CTS, socket, watcher | Sumatra/WMI ile karma | Service AgentWorker |
| Bağlantı tekrarı `ConnectionLoopAsync` AS:381 | Refresh + socket connect | AuthData → bağlantı durumu | Döngü taşınabilir, saklama karma | Core CloudConnection |
| Socket kurma `ConnectSocketAsync` AS:433 | `/agent`, path `/api/socket.io` | auth `{token}` → connected/error/disconnected | Callback içinde WMI | Core CloudConnection |
| Cihaz/yazıcı kaydı OnConnected AS:449 | emit `agent:register` | cihaz alanları + yazıcı listesi → ayrı registered olayı | DiscoverPrinters/WMI | Core PrinterSynchronizer + Linux engine |
| Sunucu yazıcı eşlemesi callback AS:541 | on `printers:registered` | List<PrinterIdMapping> → ExternalId | Yok; isim eşlemesi | Core PrinterSynchronizer |
| İş alma callback AS:527 | on `print:job` | PrintJobData → PrintJobResult callback | PrintAsync Windows'a gider | Core JobCoordinator |
| Socket hata callback AS:507 | OnError | `Session revoked` / `Invalid token` metni | ClearAuth saklama Windows | Core CloudSession/Connection |
| Bağlantı kesme `DisconnectAsync` AS:593 | Socket disconnect | Yok → durum | Yok | Core CloudConnection |
| Yazıcı izleme `StartPrinterWatchers`, `StopPrinterWatchers`, `DebouncedRefresh` AS:607,646,660 | WMI instance creation/deletion | Win32_Printer → refresh | WMI | Linux keşif/izleme |
| Yoklama `PrinterFallbackLoopAsync`, `RefreshPrinters` AS:669,681 | 60 saniyelik döngü | Yerel liste → karşılaştırma | WMI ve durum probes | Linux engine; Core eşitleme |
| Durum gönderme `EmitPrinterStatusAsync` AS:719 | emit `printer:status-update` | printers seçilmiş alanları → ACK okunmaz | Hazırlanan veri taşınabilir | Core PrinterSynchronizer |
| Keşif `DiscoverPrinters` AS:746 | Win32_Printer SELECT | Name,DriverName,PortName,PrinterStatus,WorkOffline → PrinterInfo | WMI/Windows filtreleri | Linux CUPS kuyruk listesi |
| Ağ durumu `ResolveNetworkStatusesAsync`, `GetPrinterIp`, `GetPrinterIpFromWmi`, `IsReachable` AS:805,841,862,887 | ICMP / TCP 9100,631,515; WMI | host → online/offline | IP çözümü WMI; ping/TCP taşınabilir | Linux IPP durumu; salt port erişimini hazır sayma |
| Özellikler `GetCachedCapabilities` AS:825; `PrinterCapabilitiesHelper.Get` | Winspool DeviceCapabilities | ad,port → PrinterCapabilities? | DEVMODE/Winspool | Linux IppCapabilitiesReader |
| PDF yardımcı indirme `EnsureSumatraAsync` AS:919 | Sumatra public download URL | Yok → EXE bytes | EXE/ProgramData | Linux karşılığı gerekmez; CUPS kullanılır |
| İş geçmişi `TrackJob`, `CompleteJob` AS:937,956 | Yok | PrintJobData → PrintJobRecord | OS bağımlılığı yok; bellek/UI etkisi | Core JobJournal |
| Baskı sıralama `PrintAsync` AS:964 | İç PDF/raw dispatch | PrintJobData → PrintJobResult | Printer/WMI/Sumatra | Core JobCoordinator + Linux engine |
| Tanı `DescribeSpoolerState` AS:1036 | Win32_Service Spooler sorgusu | Yok → tanı metni | WMI Spooler | LinuxDiagnostics |
| Yazıcı çözme `ResolvePrinterName` AS:1064 | Yok | ad → tam ad / null | OS tipi yok | Core/engine ad-kimlik eşlemesi |
| Base64/PDF `PrintPdfAsync` AS:1077 | Ağ yok; Sumatra process | DocumentData → geçici PDF → done/failed | Sumatra EXE/process ayarları | Linux CUPS gönderimi ve izleme |
| Raw `PrintRaw` AS:1165; `RawPrintHelper.SendBytes` | Winspool RAW | Base64 bytes,copies,name → bool/error | winspool.drv | Linux raw kapsamı doğrulanacak |
| Seçenek `BuildPdfPrintSettings` AS:1195 | Ağ yok | PrintJobOptions → Sumatra ayar dizisi | Sumatra söz dizimi | Linux CUPS seçenek eşleme |
| Yön `DetectPdfOrientation`, `Tail`, `WaitForExitAsync` AS:1251,1056,1270 | Yok | PDF bytes / process / text → sonuç | Yön/Tail taşınabilir; process Sumatra yolunda | Core yardımcı / Linux engine |
| Test `TestPrintAsync`, `GenerateTestPdf` AS:1284,1299 | Yerel baskı | gömülü TEST PDF → PrintAsync | Baskı yolu Windows | IPC test komutu → Core job |
| Sürüm `FetchVersionInfoAsync`, `IsNewerVersion` AS:1311,1329 | GET `/api/agent/version` | Yok → UpdateInfo | Karşılaştırma taşınabilir | Core metadata |
| Güncelleme `DownloadAndInstallUpdateAsync`, `UpdateInfo.GetWindowsUrl` AS:1336,1400 | GET downloadUrls.windows | EXE bytes → cmd.exe installer | Windows EXE/cmd/Inno | Linux paket güncelleme planı |
| UI olayları `AddLog`, `OnStateChanged`, `SetConnected` AS:322,333,342 | Yerel event | LogEntry / durum | Event tüketicisi WPF Dispatcher | Core durum → Service IPC |

## Güvenilirlik ve taşınmaması gereken davranışlar

1. Aynı `jobId` iki kez geldiğinde tekrar baskı engellenmez. Semaphore yalnız eşzamanlılığı sınırlar.
2. Cihaz genelinde tek baskı kilidi vardır; yazıcı başına kuyruk yoktur. 30 saniyede kilit alamayan iş failed döner.
3. PDF Base64 çözümü ve geçici dosya yazımı `PrintPdfAsync` try bloğundan öncedir. Bu hatalar callback seviyesinde failed olabilirken yerel geçmiş `printing` kalabilir.
4. Sumatra sıfır çıkış kodu / raw yazma başarısı fiziksel çıktı kanıtı değildir. Zaman aşımı daha önce spool'a gönderilmiş işi iptal ettiğini kanıtlamaz.
5. PrinterId alınır fakat hedef seçiminde kullanılmaz. Eşleme isimlere dayanır; yeniden adlandırma kararı eksiktir.
6. Yeni yazıcı ve değişen capabilities için status-update sunucu davranışı doğrulanmalı. Değişiklik karşılaştırması yalnız sayı ve sıralı name:status değerlerini izler; capability/driver değişimi tek başına bildirim üretmeyebilir.
7. `Invalid token` hatası token'ı silmez. Yerel exp gelecekteyse aynı token tekrar denenebilir. Yorumdaki «her bağlanışta yeni token» iddiası kodun geçerli token'ı yeniden kullanmasıyla tam örtüşmez.
8. UI `ClearJobs` farklı kilit nesnesini kullanır; Jobs okumaları ile değişiklikleri tam senkronize değildir. Linux IPC snapshot modeli bu paylaşılan liste düzenini taşımamalı.
9. Sumatra ve updater indirmesinde hash/imza kontrolü yoktur. Inno imzalama seçeneği, istemcide indirme doğrulaması yapıldığı anlamına gelmez.
10. Kurulum ProgramData için users-modify izni verir, Spooler kurtarma ayarını değiştirir ve sistem kök sertifika deposuna sertifika ekler. Bunlar Linux kurulum politikası olarak kopyalanmamalı.

## Sınıflandırma ve kopyalama kuralları

A: yeniden kullanım adayı veri/iş mantığı. Otomatik üretim kodu aktarımı değildir; bağımlılık ve semantik kontrolü gerekir.
B: Linux'ta yeniden yazılacak işlev. Kopyası varsa yalnız `reference/windows` içindedir.
C: yalnız davranış/proje/arayüz referansı. Karma AgentService bu gruptadır; içindeki taşınacak işlevler yukarıda ayrıldı.
D: referansa alınmayan dosya. Bu ZIP'te derleme çıktısı bulunmadı. Public root sertifikaları özel anahtar değildir; Linux referansı için gereksiz olduklarından dışarıda bırakıldı.

Kaynak `src/` dizini korundu; örnek hedefteki düzleştirme uygulanmadı. Böylece ZIP yolu ve kopya birebir eşlenir. Aday modeller dahi üretim `src/` klasörüne aktarılmadı. Windows `.csproj` ve `.sln` dosyaları yalnız referanstır.

## Dosya envanteri

Her satır tek ZIP dosyasını gösterir. Hedefler `linux-print-agent/` köküne göredir. Dizin girdileri kaynak dosya sayısına dahil değildir. SHA-256 kayıtları `reference-manifest.json` içindedir.

| ZIP içindeki tam yol | Görev | Grup | Referans hedefi | Planlanan hedef | Gerekli değişiklik |
|---|---|---|---|---|---|
| `windows-print-agent-master/.gitignore` | Windows depo dışlama kuralları | D | — | — | Linux kuralları sonraki aşamada ayrı hazırlanır |
| `windows-print-agent-master/build-installer.ps1` | Windows publish ve Authenticode akışı | C | `reference/windows/build-installer.ps1` | Paketleme | DEB/RPM ve imzalama yeniden tasarlanır; çalıştırılmaz |
| `windows-print-agent-master/PrintAgent.sln` | İki projenin çözüm yapısı | C | `reference/windows/PrintAgent.sln` | Tüm proje planı | Yeni Linux çözümü ayrı oluşturulur |
| `windows-print-agent-master/README.md` | Kullanım ve mimari açıklaması | C | `reference/windows/README.md` | Belgeler | Kodla çelişen heartbeat, servis ve süre bilgileri ayrılır |
| `windows-print-agent-master/rules.md` | Windows teknik notları | C | `reference/windows/rules.md` | Belgeler | Referans verisidir; geliştirme talimatı olarak uygulanmaz |
| `windows-print-agent-master/setup.iss` | Windows kurulum, izin, başlangıç ve sertifika akışı | C | `reference/windows/setup.iss` | Paketleme / Service | Inno ve Windows sistem değişiklikleri taşınmaz |
| `windows-print-agent-master/certs/isrg-root-x2.pem` | ISRG Root X2 public sertifikası | D | — | — | Linux sistem CA politikasına gömülü Windows kökü taşınmaz |
| `windows-print-agent-master/certs/isrgrootx1.pem` | ISRG Root X1 public sertifikası | D | — | — | Linux sistem CA politikasına gömülü Windows kökü taşınmaz |
| `windows-print-agent-master/src/PrintAgent.Common/Constants.cs` | URL, sürüm, mutex, filtre ve süre sabitleri | B | `reference/windows/src/PrintAgent.Common/Constants.cs` | Common / Core / Linux | Windows mutex ve filtreler ayrılır; kullanılmayan poll sabiti düzeltilir |
| `windows-print-agent-master/src/PrintAgent.Common/PrintAgent.Common.csproj` | Windows Common bağımlılıkları | C | `reference/windows/src/PrintAgent.Common/PrintAgent.Common.csproj` | Common | net8.0-windows ve ProtectedData taşınmaz |
| `windows-print-agent-master/src/PrintAgent.Common/Helpers/CredentialStore.cs` | DPAPI kimlik saklama | B | `reference/windows/src/PrintAgent.Common/Helpers/CredentialStore.cs` | Linux | Headless uyumlu saklama ve izinler yeniden yazılır |
| `windows-print-agent-master/src/PrintAgent.Common/Helpers/DeviceIdHelper.cs` | Dosyada kalıcı GUID | B | `reference/windows/src/PrintAgent.Common/Helpers/DeviceIdHelper.cs` | Linux | Linux yolu, atomik oluşturma ve izinler tanımlanır |
| `windows-print-agent-master/src/PrintAgent.Common/Models/AuthData.cs` | Auth/session/customer ve giriş DTO modelleri | A | `reference/windows/src/PrintAgent.Common/Models/AuthData.cs` | Common | Aktif/legacy ayrımı; yerel auth ile ağ DTO ayrımı; sır IPC'ye açılmaz |
| `windows-print-agent-master/src/PrintAgent.Common/Models/DeviceInfo.cs` | Cihaz veri modeli | A | `reference/windows/src/PrintAgent.Common/Models/DeviceInfo.cs` | Common | windows varsayılanı ayrılır; linux sunucu kabulü doğrulanır |
| `windows-print-agent-master/src/PrintAgent.Common/Models/LogEntry.cs` | Log veri modeli | A | `reference/windows/src/PrintAgent.Common/Models/LogEntry.cs` | Common | UTC ve hassas veri temizleme politikası belirlenir |
| `windows-print-agent-master/src/PrintAgent.Common/Models/PrinterInfo.cs` | Yazıcı ve capabilities modelleri | A | `reference/windows/src/PrintAgent.Common/Models/PrinterInfo.cs` | Common | UI bildirimi ayrılabilir; Win32 ID/fields semantiği IPP için doğrulanır |
| `windows-print-agent-master/src/PrintAgent.Common/Models/PrintJobData.cs` | İş, seçenek ve callback sonucu DTO'ları | A | `reference/windows/src/PrintAgent.Common/Models/PrintJobData.cs` | Common | Alan adları korunur; boyut, tür ve seçenek doğrulama eklenmesi planlanır |
| `windows-print-agent-master/src/PrintAgent.Common/Models/PrintJobRecord.cs` | Yerel iş geçmişi | A | `reference/windows/src/PrintAgent.Common/Models/PrintJobRecord.cs` | Common / Core | Kalıcı durumlar, spool kimliği ve belirsiz sonuç planlanır |
| `windows-print-agent-master/src/PrintAgent.UI/App.xaml` | WPF kaynakları ve başlangıç görünümü | C | `reference/windows/src/PrintAgent.UI/App.xaml` | UI | Avalonia kaynakları ayrı yazılır |
| `windows-print-agent-master/src/PrintAgent.UI/App.xaml.cs` | Windows mutex ve süreç kapanışı | B | `reference/windows/src/PrintAgent.UI/App.xaml.cs` | Service / UI | Tek servis kilidi ve ayrı UI yaşam döngüsü |
| `windows-print-agent-master/src/PrintAgent.UI/PrintAgent.UI.csproj` | WPF, win-x64 ve NuGet ayarları | C | `reference/windows/src/PrintAgent.UI/PrintAgent.UI.csproj` | UI | Avalonia ve Linux hedefi ayrı hazırlanır |
| `windows-print-agent-master/src/PrintAgent.UI/Converters/Converters.cs` | WPF görünürlük ve giriş metni dönüştürücüleri | B | `reference/windows/src/PrintAgent.UI/Converters/Converters.cs` | UI | Avalonia karşılıkları; WPF tipleri taşınmaz |
| `windows-print-agent-master/src/PrintAgent.UI/Resources/icon.ico` | WPF tray/uygulama ikon referansı | C | `reference/windows/src/PrintAgent.UI/Resources/icon.ico` | UI | XAML/csproj bağımlılık referansını tamamlar; Linux simge formatı sonra seçilir |
| `windows-print-agent-master/src/PrintAgent.UI/Resources/icon.png` | Giriş ekranı marka görseli | C | `reference/windows/src/PrintAgent.UI/Resources/icon.png` | UI | Görsel referansı; sonraki kullanımda boyut/marka uygunluğu kontrol edilir |
| `windows-print-agent-master/src/PrintAgent.UI/Services/AgentService.cs` | Karma bulut, yazıcı, iş ve updater mantığı | C | `reference/windows/src/PrintAgent.UI/Services/AgentService.cs` | Core / Linux / Service | İşlevler ayrılır; sınıf doğrudan taşınmaz |
| `windows-print-agent-master/src/PrintAgent.UI/Services/PrinterCapabilitiesHelper.cs` | Winspool DEVMODE özellik okuma | B | `reference/windows/src/PrintAgent.UI/Services/PrinterCapabilitiesHelper.cs` | Linux | IPP karşılığı ve alan anlamları doğrulanır |
| `windows-print-agent-master/src/PrintAgent.UI/Services/RawPrintHelper.cs` | Winspool RAW gönderimi | B | `reference/windows/src/PrintAgent.UI/Services/RawPrintHelper.cs` | Linux | CUPS/yazıcı raw desteği doğrulanır; API çağrıları taşınmaz |
| `windows-print-agent-master/src/PrintAgent.UI/Themes/LightTheme.xaml` | WPF açık tema renkleri | C | `reference/windows/src/PrintAgent.UI/Themes/LightTheme.xaml` | UI | Avalonia tema kaynakları ayrı yazılır |
| `windows-print-agent-master/src/PrintAgent.UI/ViewModels/MainViewModel.cs` | MVVM, auth, listeler ve update UI akışı | C | `reference/windows/src/PrintAgent.UI/ViewModels/MainViewModel.cs` | UI | Singleton/cloud erişimi yerine IPC; Dispatcher değiştirilir |
| `windows-print-agent-master/src/PrintAgent.UI/Views/MainWindow.xaml` | Giriş, Account, Printers, Jobs, Log ve tray görünümü | C | `reference/windows/src/PrintAgent.UI/Views/MainWindow.xaml` | UI | Avalonia ekranları sonraki aşamada ayrı yazılır |
| `windows-print-agent-master/src/PrintAgent.UI/Views/MainWindow.xaml.cs` | Parola, tray, clipboard ve pencere olayları | B | `reference/windows/src/PrintAgent.UI/Views/MainWindow.xaml.cs` | UI | Avalonia olayları ve Agent'tan bağımsız kapanış |

## Kontrol sonucu

- ZIP: 31 dosya incelendi; 28 dosya referansa alındı; 3 dosya dışarıda bırakıldı.
- SHA-256: tüm kaynak dosyaları doğrudan ZIP girdileriyle; tüm kopyalar kaynakla eşleşti. Kaynak ZIP değişmedi.
- ZIP SHA-256: `3AAB30A492F9DFB20DF2E9F95E787E79E6EF5230C601F4BBB1181CCC5A335239`.
- Çıkarma: yeni ve benzersiz geçici dizin; hedef dışına çıkan yollar, yinelenen yollar, sembolik bağlantılar ve 20 MB üstü girdiler reddedilecek şekilde yapıldı. Bu ZIP toplamı küçüktür.
- Öncelikli dosyalar mevcut ve kopyalandı. Kaynak alt dizin yapısı korundu.
- Çıktı/EXE/DLL, bin/obj/publish/.git referansa alınmadı; bu ZIP'te bunlar zaten bulunmadı.
- Metin dosyaları kimlik bilgisi literal'leri, JWT/session token ve özel anahtar işaretleri için tarandı; eşleşme bulunmadı. Model alan adları gerçek kimlik bilgisi değildir. Tarama tüm olası sırları bulma garantisi değildir; kaynaklar ayrıca gözden geçirildi.
- İki PEM sertifikası X509 olarak okundu: ISRG Root X1/X2, HasPrivateKey=false. Referansa alınmadı.
- Linux üretim projesi ve paket tanımı yok; referansa dışarıdan Compile Include veya ProjectReference eklenmedi. Gelecekte paketleme yalnız açık üretim çıktısı listesi kullanmalı; reference dizinini paketlememeli.
- Üç analiz belgesi ve makinece okunur envanter hazır. Build, baskı veya canlı API testi yapılmadı; bu aşama statik incelemedir.

Eksikler: README'de anılan Service projesi ve uygulama heartbeat kodu yok. Sunucu kaynakları/örnek canlı mesajlar yok. Öncelikli dosya listesinde bulunan tüm dosyalar ZIP'te mevcut. Linux protokol belirsizlikleri [API sözleşmesinde](api-contract.md), görev ve Interface planı [geçiş haritasında](linux-migration-map.md) bulunur.

Bu aşama sonunda durulur. Avalonia, Worker Service, IPC, CUPS, systemd veya DEB/RPM geliştirmesi yapılmadı.
