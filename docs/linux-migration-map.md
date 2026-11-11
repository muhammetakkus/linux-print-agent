# Windows → Linux geçiş planı

Bu belge yalnızca plan içerir. Linux kaynak projesi, servis, ekran, CUPS motoru veya paket oluşturulmadı. Windows kaynakları `reference/windows` içinde değişmeden tutulur. Bu klasör Linux derlemesine ve üretim paketine bağlanmayacak.

## Matt Pocock Codebase Design değerlendirmesi

`AgentService` yaklaşık 1400 satırda oturum, socket, yazıcı keşfi, süreç çalıştırma, güncelleme ve UI bildirimi topluyor. Ana sorun yalnızca uzunluk değildir: WPF Dispatcher, cihaz bilgileri ve yazıcı durumları iş akışına sızar. UI doğrudan Agent singleton'ını başlatır. Bu davranış Linux tasarımına taşınmamalı.

Her Module, küçük bir Interface arkasında ilgili karmaşıklığı toplamalı. Örneğin JobCoordinator kalıcı kabul, tekrar teslim ve baskı durumunu yönetirken UI bunların sırasını bilmemeli. PrintingEngine, CUPS seçeneklerini ve iş takibini saklamalı. CloudSession, yenileme ve bağlantı tekrarını saklamalı. Bu öneri her sınıfa ayrı interface yazılması anlamına gelmez. Gerçek değişim ihtiyacı olan Seam'lerde Adapter kullan; yalnızca geçiş yapan katmanlar ekleme.

## Hedef görevler

| Proje | Planlanan sorumluluk / önerilen yapı | Kaynak referansı |
|---|---|---|
| `PrintAgent.Common` | Bulut DTO'ları, IPC DTO'ları ve ortak durum değerleri. UI nesneleri ve işletim sistemi erişimi içermez. `PrintJobData`, `PrintJobOptions`, `PrintJobResult`, `CustomerInfo`, `PrinterSnapshot` | Common modelleri; AgentService içindeki `PrinterIdMapping`, `UpdateInfo` |
| `PrintAgent.Core` | `CloudSession`, `CloudConnection`, `PrinterSynchronizer`, `JobCoordinator`, `JobJournal`. HTTP, Socket.IO, tekrar teslim ve kalıcı sonuç akışı | AgentService auth/socket/iş akışı |
| `PrintAgent.Linux` | `CupsPrintingEngine`, `IppCapabilitiesReader`, `LinuxCredentialStore`, `DeviceIdentityStore`, `LinuxDiagnostics`. CUPS gönderim, iş kimliği ve durum gözlemi | WMI, Winspool, Sumatra, DPAPI ve yollar |
| `PrintAgent.Service` | `AgentWorker`, `LocalControlServer`: tek süreç, yaşam döngüsü, bağımlılık kurulumu ve Unix socket sunucusu. Core modüllerini çalıştırır | Start/Stop, Windows App mutex/lifecycle |
| `PrintAgent.UI` | Avalonia MVVM; `AgentControlClient` üzerinden giriş, durum, yazıcılar, geçmiş ve desteklenen sistem tepsisi | MainViewModel, MainWindow, App, tema ve dönüştürücüler |
| `PrintAgent.CLI` | Aynı yerel kontrol Interface'i üzerinden durum, yazıcılar, eşleştirme/giriş, geçmiş ve tanılama | Windows eşdeğeri ZIP'te yok |

Önerilen bağımlılık yönü: Common bağımsızdır; Core Common kullanır. Linux, Core'un ihtiyaç duyduğu Seam'lere Adapter sağlar. Service Core ve Linux'u birleştirir. UI/CLI yalnızca Common IPC sözleşmesi ve yerel istemci üzerinden konuşur. Cloud token UI/CLI'ya geri verilmez. Proje bağlantıları bu aşamada oluşturulmadı.

## İşlev eşlemesi

| Mevcut işlev | Linux planı | Korunacak / değişecek davranış |
|---|---|---|
| Login/session/adopt/refresh/logout | Core CloudSession | Alan adları ve mevcut uçlar korunur; Linux platform kabulü sunucudan doğrulanır. Yeni Linux cihazının Windows DPAPI kaydını devralması planlanmaz. |
| Socket register/print/status | Core CloudConnection + PrinterSynchronizer | `/agent`, `/api/socket.io` ve callback ilişkisi korunur. Sunucu desteği olmadan yeni olay eklenmez. |
| WMI printer discovery | Linux CupsPrintingEngine | Önce CUPS'a kayıtlı kuyrukların listelenmesi; yeni ağ cihazı keşfi/otomatik kuyruk kurma ayrı kapsam kararıdır. |
| Win32 capabilities | Linux IppCapabilitiesReader | Alan ve ölçü uyumu incelenir. Windows bin/paper ID ve `fields` için sahte eşdeğer üretilmez. |
| Sumatra PDF | Linux CupsPrintingEngine | CUPS iş kimliği saklanır; gönderim, spool tamamlanması ve fiziksel çıktı kanıtı ayrılır. |
| Winspool raw | Linux PrintingEngine | Windows raw desteği PDF-only ilk tanımından geniştir. Linux raw kapsamı, CUPS/yazıcı desteği ve bulut türleri doğrulanmadan destek sözü verilmez. |
| `BuildPdfPrintSettings` | Linux seçenek eşlemesi | İş seçenekleri korunur; Sumatra komut dizisi kopyalanmaz. Desteklenmeyen seçenek için açık sonuç gerekir. |
| `DetectPdfOrientation` | PDF işleme kararı | İlk 8 KB regex'i kesin PDF parser değildir; varsayılan yön davranışı ayrıca kararlaştırılır. |
| `_printLock`, Jobs, RecentLogs | Core JobCoordinator + JobJournal | Bellek listesi yerine kalıcı iş kaydı; kuyruk limiti, saklama süresi ve tekrar teslim politikası tanımlanır. |
| DPAPI LocalMachine | LinuxCredentialStore | Headless uyumlu, servis hesabına sınırlı erişim; UI masaüstü anahtarlığına zorunlu bağımlılık kurulmaz. Kesin saklama yöntemi sonraki tasarım kararıdır. |
| ProgramData/device_id | DeviceIdentityStore | Kalıcı kurulum kimliği `/var/lib/nuxprint-agent/`; atomik ilk oluşturma ve izinler. |
| UI Start/Stop singleton | AgentWorker | UI kapanınca Agent sürer. Tek aktif servis için süreç kilidi gerekir. |
| WPF event/Dispatcher | IPC durum snapshot'ı ve bildirimler | UI/CLI kendi Agent başlatmaz. IPC sürümü, mesaj çerçevesi, hata ve erişim kuralları tanımlanır. |
| Sürüm kontrolü/EXE updater | Core metadata + Linux paket yönetimi | `downloadUrls.windows` kullanılmaz. DEB/RPM güncelleme yolu ve imza kontrolü sonraki aşamaya aittir. |
| Inno startup/spooler/root-store değişiklikleri | Linux kurulum politikası | Windows sistem ayarları aynen uygulanmaz; systemd/paket izinleri ayrıca tasarlanır. |

## Planlanan veri akışı

UI veya CLI → Unix socket → AgentWorker → CloudSession/JobCoordinator.

Bulut `print:job` → doğrulama → kalıcı iş kabulü → tekrar iş kontrolü → CUPS gönderimi → CUPS iş kimliğini kalıcı kayda bağlama → durum uzlaştırma → mevcut protokolün desteklediği sonuç bildirimi.

CUPS kabulü ile yerel kayıt arasındaki çökme aralığı ayrı ele alınmalı. Yalnız `jobId` kaydı tutmak tam «bir kez fiziksel baskı» garantisi vermez. Belirsiz iş otomatik tekrar basılmadan önce uzlaştırılmalı. Sunucunun ACK süresi bilinmediği için uzun süreli sonuç modelinin mevcut callback ile uyumu henüz kararlaştırılamaz. Yeni outbox/sonuç mekanizması sunucu desteği olmadan uydurulmaz.

## Sonraki tasarım kararları

1. Bulutun Linux platformu, heartbeat, raw ve capabilities desteğini doğrula.
2. Kalıcı durum makinesi, kabul/ACK noktası, çökme kurtarması ve saklama sürelerini belirle.
3. Unix socket grup/izinleri, peer kimliği, komut yetkileri, çerçeveleme ve protokol sürümünü belirle.
4. PDF boyutu, kopya üst sınırı, disk kotası ve cache temizliğini belirle.
5. Yazıcı kimliğini CUPS kuyruğuna kararlı biçimde eşleştir; yeniden adlandırma/silme davranışını belirle.
6. 30 saniyelik planlanan kontrolü Windows'un 60 saniyelik mevcut uygulamasından ayır. Heartbeat olayını sunucudan öğren.

Sonraki geliştirmede doğrulama Interface üzerinden yapılmalı: tekrar gelen iş; CUPS kabulünden sonra çökme; ACK kaybı; refresh ağ hatası; oturum iptali; desteklenmeyen seçenek; UI kapanması. Harici Adapter'lar kontrollü taklitlerle değiştirilebilir; UI üzerinden gerçek baskı testine bağımlı kalınmamalı.
