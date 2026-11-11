# Güvenlik düzeltmeleri — 1.9.5

Tarih: 3 Kasım 2026. Kaynak bulgular: [2 Kasım incelemesi](security-review-2026-11-02.md).

## Uygulanan değişiklikler

| Bulgu | Çözüm | Doğrulama |
|---|---|---|
| L1: sınırsız kopya | Uygulama sınırı ile geçerli yazıcı sınırının düşüğü kullanılır. Varsayılan uygulama sınırı 100. Geçersiz işler dosya/CUPS işleminden önce reddedilir. | Alt/üst sınır ve yazıcı kapasitesi testleri |
| L2: raw süresiz bekleme | PDF/raw süreç süresi 60 saniye; tüm iş için kuyruk beklemesini de içeren 60 saniyelik bütçe. Host ve bulut iptali baskıya aktarılır. | Takılan sahte CUPS, iptal, dosya temizliği ve sonraki işin kilidi alması |
| L3: büyük girdiler/yanıtlar | Belge adı/yazıcı adı 256, iş kimliği 128, seçenekler 32–256 karakterle sınırlı. Kağıt/tepsi değerlerinde yalnız güvenli anahtar karakterleri, yön/duplex için izinli değerler kabul edilir; ek CUPS seçeneği enjekte edilemez. Belge varsayılan 20 MiB. Loglar 1024 karakter. Sayfalı geçmiş/log ve boyutu sınırlı yanıt yazımı. | 2 MiB başlık, fazla Base64 ve 5 MiB durum yanıtı testleri |
| L4: sınırsız IPC | En fazla 16 aktif istemci, 16 KiB istek, okuma için en fazla 15 saniye, toplam 90 saniye, yazma için 5 saniye. Bağlantı kapanışı yürütülen işlemi iptal eder. | Gerçek Unix socket ile yavaş istemci, büyük başlık ve geçerli küçük hata yanıtı |
| L5: UI parolası | Başarılı/başarısız giriş sonunda parola alanı ve görünür parola seçimi temizlenir. Yerel sunucu istek parolasını da bırakır. | UI başarı ve başarısızlık testleri |
| W1: değiştirilebilir program | SumatraPDF 3.6.1, Windows paketine `Program Files/.../tools` altında eklenir. Runtime indirmesi kaldırıldı. Sabit arşiv hash'i derlemede, korumalı manifestteki EXE hash'i baskı öncesinde doğrulanır. | Windows çözümü derlemesi; kullanıcı yazılabilir konumun reddi |
| W2: paylaşılan kimlik bilgisi | Windows kimlik bilgisi ve cihaz kimliği LocalAppData/NuxPrintAgent altında. Dizin ACL'si yalnız mevcut kullanıcı/SYSTEM; DPAPI CurrentUser. Eski ortak veri dizinindeki kullanıcı izinleri installer yükseltmesinde kaldırılır. | Gerçek Windows ACL testi; Windows çözümü derlemesi |
| W3: güvensiz güncelleme | Yalnız nuxprint.com HTTPS origin'i; yönlendirme yok; geçerli yeni sürüm; 200 MiB/5 dakika sınırı; rastgele ve kullanıcıya özel indirme yolu; WinVerifyTrust, beklenen sertifika SHA256 pini ve imzalı ürün/sürüm eşleşmesi. cmd.exe kaldırıldı. | Kötü origin/port/sürüm testleri ve Windows derlemesi |

Windows kodu Linux paketine eklenmez. Windows raw baskısı ayrıca sonlandırılabilir alt sürece taşındı. PDF ve raw aynı iş süresi bütçesini kullanır. Komut argümanları ayrı aktarılır. Alt süreç çıktıları bellekte 8192 karakterle sınırlıdır; tıkanmayı önlemek için kalan çıktı okunup atılır.

## Tekrar baskı ve kota

- İş kimliği zorunludur. Kabul edilen iş, fiziksel gönderimden önce kalıcı kayda alınır.
- Aynı kimlik ve içerik yeniden gelirse kayıtlı sonuç döner. İçerik değişmişse iş reddedilir.
- Kayıt servisin yeniden başlamasından sonra da korunur. Belirsiz sonuçlu işler otomatik tekrar basılmaz; kullanıcı yazıcı kuyruğunu kontrol etmelidir.
- Linux kayıt dosyası `/var/lib/nuxprint-agent/job-receipts.json`; Windows kayıt dosyası kullanıcıya özel veri dizinindedir. Belgelerin kendisi saklanmaz; içerik parmak izi ve sonuç saklanır.
- Kayıt süresi 7 gün, kapasite 10.000 iş. Kapasite dolarsa süresi dolmamış kayıtlar silinerek koruma zayıflatılmaz; yeni işler reddedilir. Yedi günden eski bir kimlik yeniden kullanılabilir.
- Cihaz başına kayan bir dakikalık pencerede varsayılan 60 iş ve 1000 kopya sınırı vardır. Kota uygulama belleğindedir; servis yeniden başlayınca sıfırlanır. Kalıcı kimlik kaydı ayrı olarak korunur.
- Bulut işleyicisinde aynı anda en fazla 8 bekleyen iş tutulur.

## Linux ayarları

`/etc/nuxprint-agent/agent.json` içindeki `Agent.PrintSafety` alanı:

```json
{
  "MaxCopiesPerJob": 100,
  "MaxDocumentBytes": 20971520,
  "JobTimeoutSeconds": 60,
  "MaxCopiesPerMinute": 1000,
  "MaxJobsPerMinute": 60,
  "AllowRaw": true,
  "AllowedRawTypes": ["raw", "zpl", "escpos", "escp", "pcl", "postscript", "ps", "epl", "tspl", "cpcl", "text"]
}
```

`Agent.MaxLocalClients` varsayılan 16, `Agent.LocalRequestTimeoutSeconds` varsayılan 90. Değişiklikten sonra servisi yeniden başlatın. Raw gerekmiyorsa `AllowRaw=false` kullanın. Bilinmeyen belge türleri artık raw olarak kabul edilmez. PDF/raw verilerinde boş veya geçersiz Base64 reddedilir. `Copies` için 0/negatif değerler artık 1'e çevrilmez.

IPC `Offset` ve `PageSize` alanlarını kabul eder. Sayfa boyutu 1–100; varsayılan 100. Durum `TotalJobs` ve `TotalLogs` sayılarını içerir. Arayüz varsayılan olarak ilk sayfayı gösterir. Daha büyük yanıt, bağlantı kopması yerine küçük bir hata yanıtı üretir.

## Windows geçişi ve yayınlama

- Güvenilmeyen eski makine kapsamındaki kimlik bilgileri yeni kullanıcı deposuna otomatik taşınmaz. Kullanıcı yeniden giriş yapar. Önceden sızmış olabilecek eski oturumlar NuxPrint panelinden iptal edilmelidir; yerel kod değişikliği eski token kopyalarını uzaktan geçersiz kılamaz.
- Program Files dışındaki bir Windows kurulumunda yardımcı program ve güncelleme çalıştırılması reddedilir.
- `build-installer.ps1`, resmi SumatraPDF 3.6.1 portable arşivini sabit SHA256 ile kontrol eder ve EXE/manifesti installer'a ekler. Runtime ProgramData'daki eski programı çalıştırmaz.
- İmzalı Windows yayınında `-SignThumbprint` veya tek sertifikayla eşleşen `-SignSubject` verilir. Derleme, beklenen sertifikanın SHA256 pinini pakete ekler. Güvenilir signer pini bulunmayan geliştirme paketleri otomatik güncelleme çalıştırmaz; imza kontrolü atlanmaz.
- Sertifika değişimi mevcut güven zinciri üzerinden planlanmalıdır; yeni sertifikayla imzalamak tek başına eski pini değiştirmez.
- Güncellemede ayrı metadata imzası gerektiren sunucu protokolü eklenmedi. Bunun yerine sabit HTTPS origin, sürüm doğrulama ve indirilen dosyada beklenen yayıncı imzası ile imzalı ürün/sürüm eşleşmesi zorunlu tutuldu. Sunucu/API üzerinde değişiklik yapılmadı.

Resmi yardımcı program kaynağı: [SumatraPDF indirme sayfası](https://www.sumatrapdfreader.org/download-free-pdf-viewer).

## Bağımlılıklar

- SocketIOClient'in getirdiği System.Text.Json 7.0.3 yerine doğrudan 8.0.6 seçildi.
- Linux UI, Tmds.DBus.Protocol 0.21.3 güvenlik yamasını seçer. [Üreticinin bildirimi](https://github.com/tmds/Tmds.DBus/security/advisories/GHSA-xrw6-gwf8-vvr9), eski sürümlerde yerel D-Bus sinyal sahteciliği ve kaynak tüketimi sorunlarını açıklar.
- Kaynak güvenlik taraması çıktısı `artifacts/security-dependency-audit.json` içine kaydedilir. Bilinen paket uyarısının olmaması, uygulamanın tüm saldırılara kapalı olduğunu kanıtlamaz.

## Kontroller ve sınırlar

Linux çözümündeki 107 .NET testi ve Windows güvenlik grubundaki 14 test çalıştırıldı. Python paketleme grubunda 4 test bulunur. Sonuç dosyaları test projelerinin `TestResults/security*.trx` yollarındadır.

Testlerde gerçek bulut hesabı veya fiziksel baskı kullanılmadı. Windows çözümü derlenir; imzalı gerçek installer ile yükseltme, UAC, gerçek Linux kurulumu, CUPS filtreleri ve fiziksel cihaz kabul testleri bu bilgisayarda doğrulanmadı. Üretim imzalama sertifikası bu çalışma sırasında oluşturulmadı veya değiştirilmedi.

Mevcut paket yerine düzeltmeleri içeren `1.9.5` paketi kurulmalıdır. Eski `1.9.4` dosyaları güvenlik düzeltmesi içermez.
