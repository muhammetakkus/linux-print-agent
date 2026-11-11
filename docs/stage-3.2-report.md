# Aşama 3.2 — Linux yazdırma motoru ve Agent Service

## Kapsam ve onaylar

Kaynak: Aşama 3.1 (`2393fbf`) ile değişmemiş Windows referansları. Talep: Linux CUPS/IPP keşfi, varsayılan yazıcı, durum/özellik/kağıt/renk/duplex ve kuyruk okuma; kaynak sırasını koruyan PDF/raw baskı; headless .NET Worker ve systemd; tek aktif Agent; mock destekli testler. API, Socket.IO, DTO, job sırası, ACK koşulu ve kaynak retry davranışı korunur. UI, CLI, DEB ve başka aşamalar kapsam dışıdır.

Kullanıcı bu aşamada açıkça onayladı:

- Ayrı uygulama heartbeat'i eklenmeyecek; mevcut Socket.IO bağlantısı korunacak. Bu karar, görev metnindeki heartbeat satırının yerini alır.
- `platform="linux"`, `/var/lib/nuxprint-agent/device_id` ve servis hesabına sınırlı izinli kimlik dosyası kullanılacak. Dosya izinlerinin disk şifrelemesi sağlamadığı belirtildi ve onaylandı.
- Görevde belirtilen 30 saniyelik yazıcı kontrolü uygulanır. Windows fallback kodundaki 60 saniyeden bu fark talimat gereğidir; bağlantı retry süreleri değişmez.

## Dosyalar

| Konum | Görev |
|---|---|
| `src/PrintAgent.Linux/PrintAgent.Linux.csproj` | Bağımsız net8.0 Linux entegrasyon kütüphanesi |
| `src/PrintAgent.Linux/IppCodec.cs` | IPP binary istek/yanıt; grup, çoklu değer, collection, range ve resolution okuma |
| `src/PrintAgent.Linux/IppClient.cs` | Varsayılan yerel CUPS Unix socket'i üzerinden HTTP/IPP sorgusu; yapılandırmayla TCP seçilebilir |
| `src/PrintAgent.Linux/CupsClient.cs` | CUPS yazıcı, varsayılan, özellik ve aktif kuyruk sorguları; `/usr/bin/lp` ile gönderim |
| `src/PrintAgent.Linux/CupsPrinterMapper.cs` | IPP → mevcut PrinterInfo/PrinterCapabilities alanları |
| `src/PrintAgent.Linux/CupsPrintOptions.cs` | Kaynak seçenek ve yön algılama davranışının CUPS karşılığı |
| `src/PrintAgent.Linux/LinuxPrintEngine.cs` | Tek global baskı kilidi, kaynak iş sırası, PDF/raw, geçmiş ve geçici dosya temizliği |
| `src/PrintAgent.Linux/ProcessRunner.cs` | Shell kullanmadan ayrı argümanlarla lp çalıştırma; stdout/stderr ve PDF timeout |
| `src/PrintAgent.Linux/LinuxCredentialStore.cs` | JSON oturum dosyası, 0700 dizin/0600 dosya ve atomik değiştirme; Linux üzerinde |
| `src/PrintAgent.Linux/SingleInstanceLease.cs` | Aynı runtime lock dosyasında FileShare.None ve Linux flock |
| `src/PrintAgent.Service/PrintAgent.Service.csproj` | net8.0 Worker; Hosting/Systemd 8.0.1 |
| `src/PrintAgent.Service/Program.cs` | Linux işletim sistemi kontrolü; yalnız Generic Host başlatma |
| `src/PrintAgent.Service/AgentOptions.cs` | Linux dizinleri ve CUPS bağlantı ayarları |
| `src/PrintAgent.Service/AgentServices.cs` | Üretim bağımlılıkları; kimlik oluşturmadan önce tek Agent kilidi |
| `src/PrintAgent.Service/PrinterCatalog.cs` | Yazıcı snapshot'ı ve varsayılan yazıcı |
| `src/PrintAgent.Service/AgentWorker.cs` | Başlangıç, discovery, cloud döngüsü, 30 saniye kontrol ve durdurma |
| `packaging/systemd/nuxprint-agent.service` | Servis hesabı, notify, restart ve Linux dizin izinleri |
| `tests/PrintAgent.Linux.Tests/` | IPP, dönüşüm, PDF/raw, CUPS adaptörü ve headless host testleri |

3.1 Core'daki tek değişiklik, host'un discovery listesini değiştirebilmesi için `ReplacePrinterSnapshot` metodudur. HTTP/socket mesaj oluşturma, auth, ACK veya retry metotları değiştirilmedi. Common modelleri ve Windows referansları değiştirilmedi.

## Keşif ve durum

CUPS-Get-Printers `0x4002`; Get-Printer-Attributes `0x000B`; CUPS-Get-Default `0x4001`; Get-Jobs `0x000A` kullanılır. IPP sorgusu varsayılan olarak `/run/cups/cups.sock` üzerinden yerel CUPS'a gider. Kuyruk sorgusu aktif işleri döndürür; yeni «kuyruk doluysa basma» kuralı veya iş tamamlanmasını bekleme şartı eklemez.

Windows WMI gibi **işletim sistemine/CUPS'a kayıtlı kuyruklar** listelenir. USB ve bilinen ağ device-uri şemaları ayrılır. Henüz kurulmamış USB cihazlarını sisteme ekleme veya ağ taramasıyla otomatik yazıcı kurma yapılmaz. IPP-over-USB, CUPS tarafından IPP URI'siyle sunulursa fiziksel USB bağlantısı ayrıca kanıtlanamaz; Network olarak görünebilir.

Idle/processing online, stopped/rejecting veya açık offline sebebi offline olur; durum yoksa unknown. CUPS durumu fiziksel erişilebilirlik garantisi değildir. Eksik model adı veya bağlantı türü uydurulmaz. Kağıt ölçüsü bildirilen IPP collection'dan veya ölçüyü içeren PWG keyword'ünden alınır. IPP 0.01 mm → model 0.1 mm dönüşümü yapılır. Sayısal Windows bin/paper ID'leri ve DC_FIELDS üretilemez; mevcut int alanlarının 0 varsayılanı «bilgi yok» anlamında kalır. Model alanları nullable olmadığından bulunmayan bireysel bool/int yetenekler false/0 kalabilir; bunlar donanımın kesin desteklemediğinin kanıtı değildir. Hiç yetenek verisi yoksa capabilities null olur.

## Baskı uyumluluğu

İş sırası: global kilit (30 saniye bekleme) → geçmiş kaydı → yazıcı listesi/ad eşlemesi → tür ayrımı → Base64 → seçenekler → CUPS gönderim ve sonuç → geçici dosya temizliği → aynı socket ACK. Temizleme, işleyici dönmeden ve dolayısıyla ACK gönderilmeden önce tamamlanır. Gerçek baskı yöntemi CUPS'tır; Windows EXE veya DLL kullanılmaz.

| Kaynak seçenek | CUPS karşılığı |
|---|---|
| FitToPage varsayılan true | `print-scaling=fit` |
| FitToPage false / noscale | `print-scaling=none` |
| landscape / portrait | `orientation-requested=4` / `3` |
| Duplex long-edge / short-edge / one-sided | `sides=two-sided-long-edge` / `two-sided-short-edge` / `one-sided` |
| Color true / false | `print-color-mode=color` / `monochrome` |
| PaperSize / Tray | `media=<size>,<source>`; CUPS'un kabul ettiği ad/keyword gerekli |
| Pages | `page-ranges`, kaynakla aynı karakter temizliği |
| PDF Copies > 1 | `lp -n`; <=1 için varsayılan tek kopya |
| Raw copies | En az 1; kaynak gibi her kopya ayrı CUPS gönderimi |

Yön belirtilmediyse kaynakta olduğu gibi ilk 8192 baytta MediaBox regex'i kullanılır; tam PDF parser eklenmedi. `pdf` dışındaki her tür raw yoluna gider. Raw seçenekleri belge içinde kalır; PDF options uygulanmaz. `lp -o raw` CUPS/driver tarafından reddedilirse failed döner ve yeniden denenmez. Daha önce basılmış raw kopyalar geri alınamaz; bu kaynak davranışıyla aynıdır.

PDF gönderimi 60 saniyede timeout olur; lp işlemi sonlandırılır. Bu, CUPS'a daha önce kabul edilmiş işi iptal etmez. Raw yoluna kaynakta bulunmayan bir print timeout eklenmedi. Servis kapanış süresi dolarsa devam eden raw işin sonucu belirsiz kalabilir.

`done`: lp sıfır çıkış koduyla işi CUPS'a gönderdi. `failed`: gönderim hatası/timeout veya mevcut doğrulama hatası. Fiziksel çıktı tamamlanmasını beklemek yeni bir başarı koşulu olacağından eklenmedi. CUPS job numarası lp çıktısında loglanabilir; job-state sorgusu ayrıca mevcuttur. Sürekli fiziksel baskı izleyicisi veya otomatik tekrar yoktur.

Korunan kaynak sınırlamaları: jobId ile deduplication yok; kuyruk/geçmiş bellekte en fazla 100; PDF Base64/dosya yazımı try öncesinde olduğu için bu hata yerel geçmişi printing bırakabilir, socket katmanı failed ACK döner; hatalı token için yerel exp gelecekteyse aynı token yeniden denenebilir. Bunlar kendiliğinden düzeltilmedi.

Kapanışta socket bağlantısı, aktif baskı işleyicisi ACK dönmeden kapanabilir. Aktif gönderimin bitmesini beklemek ACK teslimini garanti etmez. Windows Stop ve mevcut Core iptal yolu da socket'i bu biçimde kapatır; bu kaynak sınırlaması yeni sonuç protokolü veya tekrar baskı kuralıyla değiştirilmedi.

## Servis ve Raspberry Pi

Servis grafik kütüphanesi yüklemez. Worker önce mevcut credential dosyasını yükler ve yazıcıları okur. Oturum yoksa çalışır durumda giriş bekler; sahte token veya otomatik e-posta/parola giriş akışı eklenmez. İlk kayıt/giriş UI/CLI/IPC'nin gelecekteki aşamasına aittir. Daha önce geçerli oturum elde edilmişse servis hesabına ait `credentials.json` kullanılabilir; dosya değerleri `AuthData` modelidir. Bu raporda gerçek kimlik bilgisi yoktur.

Kilit, kalıcı cihaz kimliği oluşturulmadan önce alınır ve host disposal'ına kadar tutulur. Varsayılan kilit `/run/nuxprint-agent/agent.lock` dosyasıdır; dosya silinmez. Aynı kurulumun tüm başlatıcıları aynı runtime dizinini kullanmalıdır. Durdurma cloud döngüsünü keser ve aktif baskı kilidinin boşalmasını bekler; yeni otomatik iş iptal/yeniden baskı kuralı yoktur.

Raspberry Pi modeli/OS bitliği henüz bilinmiyor. ARM64 (`linux-arm64`) ve 32 bit ARM (`linux-arm`) için framework-dependent yayınlar ayrı hazırlanır. Uygun .NET 8 runtime ve mimariyi destekleyen Pi gerekir; ARMv6/Pi Zero uyumluluğu iddia edilmez. Dizinler:

```text
src/PrintAgent.Service/bin/Release/net8.0/linux-arm64/publish/
src/PrintAgent.Service/bin/Release/net8.0/linux-arm/publish/
```

systemd unit'in ExecStart hedefi `/opt/nuxprint-agent/PrintAgent.Service` dosyasıdır. Unit, mevcut `nuxprint-agent` servis kullanıcısı/grubu ve CUPS gerektirir. Kullanıcı oluşturma, hedefe dosya yükleme, `systemctl enable/start` veya gerçek makine ayarı bu Windows oturumunda yapılmadı. `StateDirectory`, `CacheDirectory`, `RuntimeDirectory` izinleri 0700; umask 0077. CUPS istemci aracı `/usr/bin/lp` ve PDF filtreleri hedef sistemde kurulu olmalıdır.

İsteğe bağlı `/etc/nuxprint-agent/agent.json` dosyası `Agent` bölümünde dizinleri ve CUPS adresini tanımlar. TCP kullanılacaksa `CupsSocketPath` null yapılmalıdır. Varsayılan yerel Unix socket'tir. Gizli anahtarlar bu yapılandırmada tutulmaz. Üretim .NET runtime'ı framework-dependent çıktıya dahil değildir.

## Doğrulama sonucu ve sınırlar

- Release çözüm derlemesi `-warnaserror` ile başarılı: 0 hata, 0 uyarı.
- Framework-dependent `linux-arm64` ve `linux-arm` yayınları başarılı. ELF başlıkları ayrıca okundu: ARM64 e_machine=183/class=64 bit; ARM e_machine=40/class=32 bit. Bu, Pi üzerinde çalıştırma testi değildir.
- Son Release test koşusu: Core 46/46 başarılı; Linux/Service 28/34 başarılı. Toplam **74 başarılı, 6 başarısız (ortam tarafından engellenmiş), 0 atlanan**.
- Altı Worker testinin başarısızlık nedeni Windows Uygulama Denetimi'nin Release `PrintAgent.Service.dll` dosyasının yüklenmesini engellemesidir (`0x800711C7`). Güvenlik politikası değiştirilmedi veya aşılmadı. Bu testler başarılı gösterilmez.
- Geliştirme sırasındaki Debug hedefli koşularda headless host başlangıç/ACK/stop, 30 saniye poll, CUPS kesintisi, aktif baskıda stop ve çift instance testleri geçti. Son kilit yerleştirmesi sonrası çift instance testi ayrıca geçti. Bunlar engellenen son Release koşusunun yerine geçirilmez.
- 28 Windows referans dosyasının SHA-256 değerleri önceki envanterle aynı.

Komutlar:

```text
dotnet build NuxPrint.Linux.sln -c Release -warnaserror
dotnet test NuxPrint.Linux.sln -c Release --no-build
dotnet publish src/PrintAgent.Service/PrintAgent.Service.csproj -c Release -r linux-arm64 --self-contained false -warnaserror
dotnet publish src/PrintAgent.Service/PrintAgent.Service.csproj -c Release -r linux-arm --self-contained false -warnaserror
```

Yerel test host'unda üretim DI kaydı kullanılır; yalnız CUPS, socket transport ve credential store dış bağımlılıkları taklit edilir. Gerçek Generic Host, Linux platform kaydı, print handler/ACK, 30 saniye kontrol, tek instance, CUPS kesintisi ve durdurma sınanır. Taklit testleri gerçek CUPS filtresini, USB sürücüsünü, Linux flock/chmod'unu veya systemd yöneticisini doğrulamaz.

Bu makine Windows'tur; WSL/Linux, gerçek CUPS, yazıcı ve Raspberry Pi yoktur. Gerçek Pi baskısı, USB/ağ discovery'si, raw desteği, donanımdaki kağıt/tepsi/renk/duplex davranışı, servis kullanıcısının CUPS yetkileri ve `systemd-analyze verify`/systemd başlangıcı **donanım/ortam doğrulaması bekliyor**. Tamamlanmış donanım doğrulaması olarak raporlanmaz. Bulut canlı bağlantısı yapılmadı; 3.1'in socket/auth mock testleri korunur.

## Teknik kaynaklar

- [CUPS IPP işlemleri](https://openprinting.github.io/cups/doc/spec-ipp.html): işlem kodları ve yerel kuyruk kaynakları.
- [CUPS baskı seçenekleri](https://www.cups.org/doc/options.html): lp, kopya, media, sides ve sayfa seçenekleri. Destek, sürücüye bağlıdır.
- [IPP kayıtları](https://www.iana.org/assignments/ipp-registrations): attributes ve değer tipleri.
- [systemd execution ayarları](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html): runtime/state/cache dizinleri ve izinler.

## Standards

Bağımsız standart incelemesinde belgelenmiş kural ihlali, işlem gerektiren yeni kod kokusu veya somut yeni doğruluk/güvenlik kusuru bulunmadı. Windows kaynakları ve onaylanan bulut sözleşmesi korundu. İncelemeci kodu salt okunur inceledi; engellenmiş dosyaları veya testleri çalıştırmadı.

## Spec

Bağımsız kapsam incelemesinde yeni bir kapsam/uyumluluk hatası bulunmadı. CUPS kuyrukları, varsayılan/durum/yetenekler, PDF/raw dal sırası ve gönderim sonucu, Worker/systemd ve tek instance kapsamla uyumlu bulundu. Kapanışta ACK kaybı Windows/Core'dan devralınan sınırlama olarak ayrıldı. Gerçek Linux/CUPS/systemd ve engellenen altı Release Worker testinin doğrulanmadığı özellikle korundu.

İnceleme: Standards 0 yeni bulgu, Spec 0 yeni bulgu. Bu, eksik ortam/donanım doğrulamasını ortadan kaldırmaz. İnceleme temeli `2393fbf` üzerine bu aşamanın staged değişiklikleridir.

Aşama 3.3 başlatılmadı. Avalonia, CLI veya DEB paketi eklenmedi.
