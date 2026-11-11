# NuxPrint Cloud Print Agent - Linux Native

gerekli proje son teknoloji bir şekilde yapılıp structure buraya ifade edilecek......

# windows derleme
wsl --install -d Ubuntu
scripts/publish.sh

# ubuntu için
sudo apt update && sudo apt install -y dotnet-sdk-8.0 python3
bash scripts/build.sh
bash scripts/publish.sh linux-x64
sudo apt install ./nuxprint-agent-desktop_<sürüm>-2_x64.deb

## Amaç

NuxPrint, Linux bilgisayarındaki yazıcıları NuxPrint Cloud hesabına bağlar.
Servis, buluttan gelen işleri CUPS'a gönderir.
Masaüstü arayüzü ve komut satırı aracı (CLI), yerel servisi yönetir.


## Hedef sistemler ve paket seçimi

Proje, Ubuntu 22.04/24.04, Debian 12 ve uygun Raspberry Pi OS sistemlerini hedefler.
Gerçek cihazda kurulum ve baskı testleri henüz doğrulanmadı.
Paket oluşturulması, bu testlerin geçtiği anlamına gelmez.

Hedef bilgisayarda işletim sistemi mimarisini kontrol edin:

```bash
dpkg --print-architecture
```

| Komut çıktısı | Paket mimarisi | Yayın hedefi |
|---|---|---|
| `amd64` | Linux x64 | `linux-x64` |
| `arm64` | ARM 64 bit | `linux-arm64` |
| `armhf` | ARM 32 bit, ARMv7 veya üstü | `linux-arm` |

Paket mimarisi işletim sistemiyle eşleşmelidir.
64 bit ARM işlemcide 32 bit ARM işletim sistemi varsa dosya adında `arm32` yazan paket gerekir.

ARM32 kurulum betiği Debian/Raspberry Pi OS 12 veya Ubuntu 22.04 gerektirir.
ARMv6 cihazlar ve 32 bit Debian/Raspberry Pi OS 13 desteklenmez.
Raspberry Pi için ayrıntılar: [ARM paketleri](docs/raspberry-pi-support.md).

## Kurulum paketleri

Kullanıcı, uygun DEB paketini indirerek kurulum yapabilir.
Kaynak proje klasörünü indirmesi gerekmez.
Kurulum, gerekli bağımlılıkları paket depolarından yükler.
Bu işlem için internet bağlantısı gerekebilir.

| Paket | İçerik |
|---|---|
| Desktop | Servis, masaüstü arayüzü ve CLI |
| Server | Servis ve CLI |

İki paket de CUPS kullanır.
Aynı anda yalnız bir paketi kurun.
Paketler kendi .NET çalışma ortamını içerir. Aynı mimaride servis, CLI ve arayüzün
birebir aynı dosyaları paket içinde hard link ile paylaşılır; kurulumda tek fiziksel
kopya yer kaplar. Bileşenler ayrı süreçler olarak çalışmaya devam eder.
Hedef bilgisayara ayrıca .NET kurmayın.

Uygulama sürümü: `1.9.5`; paket revizyonu: `2`. Paketler `artifacts/deb/` içindedir:

| Mimari | Desktop paketi | Server paketi |
|---|---|---|
| amd64 | `nuxprint-agent-desktop_1.9.5-2_x64.deb` | `nuxprint-agent-server_1.9.5-2_x64.deb` |
| arm64 | `nuxprint-agent-desktop_1.9.5-2_arm64.deb` | `nuxprint-agent-server_1.9.5-2_arm64.deb` |
| armhf | `nuxprint-agent-desktop_1.9.5-2_arm32.deb` | `nuxprint-agent-server_1.9.5-2_arm32.deb` |

Dosya bütünlüğünü kontrol etmek için proje klasöründe çalıştırın:

```bash
(cd artifacts/deb && sha256sum -c SHA256SUMS)
```

Bu kontrol dosyaları mevcut hash listesiyle karşılaştırır.
Yayıncı imzasını doğrulamaz.

Desktop paketi masaüstü başlatıcısı ve otomatik başlangıç dosyası içerir.
Server paketi Avalonia veya masaüstü arayüzü içermez.
Paketler imzalı bir APT deposu üzerinden sunulmaz.

## Kurulum

Aşağıdaki işlemleri hedef Linux bilgisayarında yapın.
Önce yazıcıyı CUPS'a kurun.
Gerekli yazıcı sürücülerini ve PDF filtrelerini yükleyin.

### Yalnız DEB dosyası ile kurulum

1. İndirdiğiniz DEB dosyasının bulunduğu klasörde terminal açın.
2. Kullanacağınız paket için aşağıdaki komutlardan birini çalıştırın.

Aşağıdaki örnekler Intel/AMD 64 bit bilgisayarlar içindir; dosya adında `x64` yazılır.
Linux paket yöneticisi aynı mimariyi `amd64` olarak bildirir.
ARM sistemlerde dosya adındaki `x64` yerine `arm64` veya `arm32` kullanın.
Dosya adındaki `arm32`, ARMv7 ve üzeri 32 bit ARM içindir; Linux paket yöneticisindeki karşılığı `armhf` olarak kalır.

Desktop:

```bash
sudo apt install ./nuxprint-agent-desktop_1.9.5-2_x64.deb
```

Server:

```bash
sudo apt install ./nuxprint-agent-server_1.9.5-2_x64.deb
```

3. Uygulama menüsünden **NuxPrint Agent** simgesini açın.
4. İlk açılışta Linux'un yönetici onayı penceresini onaylayın. Bu parola web hesabınızın parolası değildir.
5. Uygulamada mevcut NuxPrint web hesabınızla giriş yapın.

Terminalden grup eklemek veya oturumu kapatıp yeniden açmak gerekmez.
İzin yalnızca onay verilen yerel kullanıcıya kaydedilir. Uygulama yönetici olarak çalıştırılmaz.
Sonraki açılışlarda tekrar yönetici onayı gerekmez; izin kaldırılırsa yeniden onay istenir.
Başka bir yerel kullanıcıya otomatik erişim verilmez. Henüz yetkilendirilmemiş kullanıcıda
otomatik başlangıç parola penceresi açmaz; kullanıcı simgeye tıklayarak ilk onayı başlatır.

Bu akış desteklenen grafik masaüstünde bir polkit kimlik doğrulama ajanı gerektirir.
DEB dosyasını çift tıklayarak kurmak için dağıtımın grafik paket kurucusu da kurulu olmalıdır.
Server paketinde grafik arayüz yoktur; CLI ilk kullanımda uygun yerel oturumda yetki ister.
Uzak SSH oturumundan ilk erişim yönetici tarafından sağlanmalıdır; grafik açılış deneyimi Desktop paketlerine aittir.

### Proje klasöründen kurulum

Komutları bu README dosyasının bulunduğu klasörde çalıştırın.
Kullanacağınız paket için aşağıdaki komutlardan birini çalıştırın.
Örneklerdeki mimariyi işletim sisteminize göre değiştirin.

Desktop:

```bash
sudo bash scripts/install.sh artifacts/deb/nuxprint-agent-desktop_1.9.5-2_x64.deb
```

Server:

```bash
sudo bash scripts/install.sh artifacts/deb/nuxprint-agent-server_1.9.5-2_x64.deb
```

Kurulum betiği paketi yükler. Yerel kullanıcı yetkisi ilk istemci açılışında sistem onayıyla alınır; oturumu yeniden açmak gerekmez.

Paket, servis hesabını ve systemd servis dosyasını oluşturur.
Ardından servisi başlatmayı dener.
Kapsayıcı veya `policy-rc.d` politikası bu işlemi engelleyebilir.
`cups.service` çalışır durumda olmalıdır.

## Masaüstü kullanımı

Desktop paketini kurduktan sonra arayüzü açın:

```bash
nuxprint-agent-ui
```

Mevcut NuxPrint hesabınızla giriş yapın.
Girişten sonra şu sekmeler görünür:

| Sekme | İşlev |
|---|---|
| Account | Hesap, cihaz, sürüm ve API anahtarı |
| Printers | Yazıcı durumu, yazıcı kimliği ve test baskısı |
| Jobs | İş geçmişi ve tamamlanan kayıtları temizleme |
| Log | Son günlük kayıtları |

Bağlantı durumu alt çubukta görünür.
Desktop paketi X11 veya XWayland gerektirir.

Desteklenen masaüstlerinde sistem tepsisi ve bildirimler kullanılabilir.
Tepsi menüsündeki Show komutu pencereyi açar.
Quit komutu arayüzü kapatır.
Arayüz kapandığında servis ve yazdırma işlemleri devam eder.

Tepsi desteği varsa pencereyi kapatmak arayüzü gizler.
Tepsi desteği yoksa pencere normal kapanır.
GNOME ortamında ek tepsi desteği gerekebilir.
Gerçek masaüstünde tepsi ve bildirim testleri henüz yapılmadı.

Servis, buluttaki sürüm bilgisini arayüze iletir.
Update işlemi, DEB kurulumu gerektiğini bildirir.
Otomatik Linux güncellemesi yoktur.
Yeni sürümü uygun DEB paketiyle kurun.

## Arayüz olmadan kullanım

Server paketi masaüstü oturumu veya `DISPLAY` gerektirmez.
CLI, çalışan yerel servise bağlanır.
CLI ayrı bir servis veya bulut bağlantısı başlatmaz.

| İşlem | Komut |
|---|---|
| Yardım | `nuxprint-agent --help` |
| Servis durumu | `nuxprint-agent status` |
| Yazıcılar | `nuxprint-agent printers` |
| Bağlantı durumu | `nuxprint-agent connection` |
| İş geçmişi | `nuxprint-agent history` |
| Tanılama | `nuxprint-agent diagnostics` |
| Giriş | `nuxprint-agent login kullanici@example.com` |
| Çıkış | `nuxprint-agent logout` |

Giriş sırasında parola terminalde görünmez.
Parolayı komut satırı bağımsız değişkeni olarak vermeyin.
Başka hesapla giriş yapmadan önce mevcut oturumu kapatın.

Otomasyon için parolayı standart girişten (stdin) iletin:

```bash
nuxprint-agent login kullanici@example.com --password-stdin
```

Bu komut stdin üzerinden bir satır bekler.
Parolayı README dosyasına veya komut geçmişine yazmayın.
CLI sonuçları JSON biçimindedir.

| Çıkış kodu | Anlam |
|---|---|
| 0 | İşlem başarılı |
| 1 | Agent işlemi başarısız |
| 2 | Kullanım veya servis erişimi hatası |

## Servis yönetimi

Servis durumunu gösterin:

```bash
systemctl status nuxprint-agent.service
```

Servisi başlatın:

```bash
sudo systemctl start nuxprint-agent.service
```

Servisi durdurun:

```bash
sudo systemctl stop nuxprint-agent.service
```

Servisi yeniden başlatın:

```bash
sudo systemctl restart nuxprint-agent.service
```

Açılışta otomatik başlatmayı etkinleştirin:

```bash
sudo systemctl enable nuxprint-agent.service
```

Servis, `/opt/nuxprint-agent/service/PrintAgent.Service` dosyasını çalıştırır.
Servis dosyası `NoNewPrivileges` ve salt okunur sistem dizinleri kullanır.
Paketi kaldırmak, cihaz kimliğini veya kimlik bilgilerini otomatik silmez.

## Çalışma sırası

1. systemd servisi başlatır.
2. Servis, tek Agent için süreç kilidini alır.
3. Servis, kayıtlı cihaz kimliğini ve oturumu yükler.
4. Servis, CUPS yazıcı kuyruklarını okur.
5. Oturum varsa servis buluta bağlanır.
6. Oturum yoksa servis kullanıcı girişini bekler.
7. Servis, gelen yazdırma işlerini tek baskı kilidiyle sırayla işler.
8. Servis, sonucu mevcut bulut sözleşmesine göre bildirir.

Yazıcı kontrol aralığı 30 saniyedir.
Baskı sırasında yazıcı kontrolü ertelenir.
Bulut bağlantısını yalnız servis yönetir.
UI ve CLI, Unix Domain Socket üzerinden servise bağlanır.

## Cloud API ve Socket.IO

| Alan | Değer |
|---|---|
| API adresi | `https://nuxprint.com` |
| HTTP uçları | `/api/agent/...` |
| Socket.IO namespace | `/agent` |
| Socket.IO taşıma yolu | `/api/socket.io` |
| Olaylar | `agent:register`, `printers:registered`, `printer:status-update`, `print:job` |

Socket.IO bağlantısı, access token ile kimlik doğrular.
Session token yalnız servis tarafından saklanır.
Yazdırma sonucu, gelen `print:job` olayının callback/ACK yanıtıdır.
Ayrı bir sonuç endpoint'i eklenmedi.
Yeniden bağlantı davranışı Windows kaynağıyla aynıdır.

Kullanıcı kararıyla ayrı uygulama heartbeat mesajı eklenmedi.
Socket.IO kendi taşıma bağlantısını kontrol eder.

## CUPS ve yazdırma

Servis, CUPS'ta kurulu USB ve ağ yazıcısı kuyruklarını bulur.
Yazıcıları otomatik kurmaz.
IPP üzerinden durum, özellik, varsayılan yazıcı ve aktif kuyruk bilgilerini okur.
Hedef sistemde `cups`, `cups-client`, sürücüler ve PDF filtreleri gereklidir.

### PDF

1. Servis, Base64 belge verisini çözer.
2. Servis, veriyi geçici dosyaya yazar.
3. Servis, baskı seçeneklerini CUPS seçeneklerine dönüştürür.
4. Servis, işi `/usr/bin/lp` ile gönderir.
5. Servis, sonucu mevcut iş akışına göre bildirir.
6. Servis, geçici dosyayı temizler.

Seçenekler kağıt, tepsi, yön, renk ve çift taraflı baskıyı kapsar.
Sayfa aralığı, ölçek ve kopya sayısı da aktarılır.
PDF gönderim zaman aşımı 60 saniyedir.
Baskı kilidi için bekleme süresi 30 saniyedir.

### Raw

Yalnız ayarlarda izin verilen belge türleri raw yolunu kullanır.
Raw, ham yazdırma verisidir.
Servis her raw kopyayı ayrı gönderir.
CUPS veya sürücü veriyi reddederse servis hata bildirir.
Servis otomatik tekrar baskı yapmaz.
Raw gönderimi için de zaman aşımı ve iptal desteği vardır.
Kopya sayısı, uygulama sınırı ile yazıcının bildirdiği geçerli sınırdan düşük olanıyla sınırlıdır.

`done` durumu, CUPS'a gönderimin başarılı olduğunu belirtir.
Bu durum, fiziksel çıktının tamamlandığını doğrulamaz.
Seçenek ve filtre desteğini gerçek yazıcıda kontrol edin.

## Yapılandırma ve erişim

| Dizin veya dosya | İçerik |
|---|---|
| `/opt/nuxprint-agent/service/` | Servis ve .NET çalışma ortamı |
| `/opt/nuxprint-agent/cli/` | CLI ve .NET çalışma ortamı |
| `/opt/nuxprint-agent/ui/` | Desktop arayüzü ve .NET çalışma ortamı |
| `/etc/nuxprint-agent/agent.json` | Agent ayarları |
| `/var/lib/nuxprint-agent/` | `device_id` ve `credentials.json` |
| `/var/cache/nuxprint-agent/` | Geçici PDF ve raw dosyaları |
| `/run/nuxprint-agent/` | `control.sock` ve `agent.lock` |

Yerel bağlantı dosyası `/run/nuxprint-agent/control.sock` yolundadır.
Sahibi servis hesabı ve `nuxprint-agent` grubudur.

| Kaynak | İzin |
|---|---|
| Yerel bağlantı dosyası | 0660 |
| Çalışma dizini | 0750 |
| Kalıcı veri ve önbellek dizinleri | 0700 |
| Kimlik bilgisi dosyası | 0600 |

Grup üyeleri giriş, çıkış ve baskı işlemlerini yönetebilir.
Grup üyeliğini yalnız yetkili kullanıcılara verin.
Dosya izinleri disk şifrelemesi sağlamaz.
Yerel durum yanıtları access token veya session token içermez.
Account sekmesi, yetkili kullanıcıya API anahtarını gösterir.

Oturum bilgilerini veya parolayı ayar dosyasına yazmayın.
Varsayılan CUPS bağlantısı `/run/cups/cups.sock` yolunu kullanır.

TCP üzerinden CUPS kullanmak için:

1. `Agent.CupsSocketPath` değerini `null` yapın.
2. `Agent.CupsServer` alanına CUPS HTTP veya HTTPS adresini yazın.
3. Servisi yeniden başlatın.

## Teknolojiler

- C# ve .NET 8.
- Avalonia 11.3.8, X11/XWayland, Skia ve MVVM.
- .NET Worker Service ve Generic Host.
- systemd.
- SocketIOClient 3.1.1 ve HttpClient.
- CUPS ve IPP.
- Unix Domain Socket.
- Bash, Python ve DEB paketleme.

## Proje yapısı

```text
NuxPrint.Linux.sln
src/
  PrintAgent.Common/   # Veri modelleri ve yerel iletişim sözleşmesi
  PrintAgent.Core/     # Kimlik doğrulama, HTTP ve Socket.IO
  PrintAgent.Linux/    # CUPS, IPP, yazdırma ve Linux dosya erişimi
  PrintAgent.Service/  # Arka plan servisi ve yerel kontrol sunucusu
  PrintAgent.UI/       # Masaüstü arayüzü
  PrintAgent.CLI/      # Komut satırı aracı
tests/                # Core, Linux ve Desktop testleri
packaging/systemd/    # Servis dosyası
packaging/deb/        # DEB üretim ve doğrulama araçları
scripts/              # Derleme, yayın ve kurulum betikleri
artifacts/            # Yayınlar, paketler ve doğrulama çıktıları
docs/                 # Teknik raporlar ve API sözleşmesi
reference/windows/    # Derlemeye katılmayan Windows kaynağı
referance/windows/    # Hedef klasörde önceden bulunan referans
```

Linux projesinin Windows projesine derleme bağımlılığı yoktur.
Referans kaynakları kurulum paketlerine eklenmez.

## Kaynaktan derleme

Gereksinimler:

- .NET 8 SDK.
- Bash.
- Python 3.11 veya üstü.

Komutları bu README dosyasının bulunduğu klasörde çalıştırın.

Çözümü derleyin ve testleri çalıştırın:

```bash
bash scripts/build.sh
```

Hedef mimari için uygulamaları ve DEB paketlerini oluşturun.
Aşağıdaki komutlardan ihtiyacınız olanı çalıştırın:

```bash
bash scripts/publish.sh linux-x64
bash scripts/publish.sh linux-arm64
bash scripts/publish.sh linux-arm
```

Yayın betiği her çalıştırmada temiz, geçici bir yayın klasöründe servis, CLI ve UI oluşturur.
Ardından `artifacts/deb/` altında Desktop ve Server paketlerini üretir, doğrular
ve `SHA256SUMS` listesini günceller. Geçici yayın klasörü işlem sonunda kaldırılır.
Paketleyici her bileşende derlemenin oluşturduğu `.nuxprint-publish-manifest`
dosyasını zorunlu tutar. Listede olmayan veya eksik dosya varsa paket üretimi durur.
Testler ve kaynak derleme klasörleri pakete eklenmez.

`artifacts/`, `bin/`, `obj/`, `TestResults/` ve Python önbellekleri üretilmiş çıktılardır;
Git ile izlenmez. Eski Git geçmişinde bulunan çıktıların kaldırılması ayrı bir geçmiş
bakımı işlemidir. Kaynak kodla birlikte tüm mimarilerin kurulum paketlerini indirmeniz gerekmez.

## Günlükler ve hata ayıklama

Servis günlüklerini izleyin:

```bash
journalctl -u nuxprint-agent.service -f
```

Servis son 200 günlük kaydını bellekte tutar. Yerel durum yanıtı ilk 100 kaydı gösterir.
İş geçmişi bellekte en fazla 100 kayıt tutar.
Kalıcı iş geçmişi eklenmedi.
`diagnostics` komutu cihaz, bağlantı ve günlük bilgilerini gösterir.

| Sorun | Kontrol |
|---|---|
| Servise erişilemiyor | Servis durumu, grup üyeliği ve bağlantı dosyası izinleri |
| Yazıcı görünmüyor | `lpstat -p -d`, CUPS kuyrukları ve sürücüler |
| İş `done`, çıktı yok | CUPS kuyruğu ve filtre günlükleri |
| Bulut bağlantısı yok | İnternet, TLS, sistem saati ve servis günlükleri |

Test baskısı fiziksel çıktı üretebilir.
Donanım ve USB erişimini CUPS tarafında kontrol edin.

## Test sonuçları

Aşağıdaki sonuçlar 3 Kasım 2026 tarihli `security.trx` ve `security-windows.trx` kayıtlarına aittir.
Güvenlik düzeltmeleriyle birlikte derleme ve testler yeniden çalıştırıldı.

| Kontrol | Sonuç |
|---|---|
| Core testleri | 46/46 geçti |
| Desktop ve IPC testleri | 13/13 geçti |
| Linux/Service testleri | 48/48 geçti |
| Linux çözümündeki toplam .NET testi | 107/107 geçti |
| Windows güvenlik testleri | 14/14 geçti |
| Paketleme testleri | 4/4 geçti |
| Mevcut yayınlar | x64, ARM64 ve ARM32 için Service, CLI ve UI |
| Mevcut DEB paketleri | Her mimari için Desktop ve Server; toplam 6 paket |

Önceki rapordaki 34 testlik keşif engeli, bu test kayıtlarında yoktur.
Derleme ve paket doğrulama ayrıntıları için [ARM raporunu](docs/raspberry-pi-support.md) okuyun.

Gerçek Unix socket ile yerel iletişim testi yapıldı.
Testte bulut ve CUPS yerine taklit bileşenler kullanıldı.
Giriş, çıkış ve açık oturumda ikinci girişi reddetme doğrulandı.
İstemci bağlantısı kapandığında servisin çalışmaya devam ettiği doğrulandı.
Headless Avalonia görüntüleri oluşturuldu ve incelendi.
Bu sonuçlar gerçek Linux masaüstü veya yazıcı testini kapsamaz.

## Güvenlik durumu

2 Kasım incelemesindeki sorunlar `1.9.5` kaynaklarında düzeltildi.

- Varsayılan kopya sınırı 100. Yazıcı daha düşük bir sınır bildirirse o sınır uygulanır.
- Geçersiz kopya sayısı, büyük belge ve uzun alanlar baskıdan önce reddedilir.
- PDF ve raw işleri, kuyruk beklemesi dahil varsayılan 60 saniyeyle sınırlıdır.
- Aynı iş kimliği için 7 gün boyunca tekrar baskı engellenir. En fazla 10.000 kayıt tutulur.
- Belirsiz sonuçlu işler otomatik tekrar basılmaz. Yeni iş oluşturmadan önce yazıcı kuyruğunu kontrol edin.
- Cihaz başına dakikada en fazla 60 iş ve 1000 kopya kabul edilir.
- Raw türleri izin listesiyle sınırlıdır. Raw gerekmiyorsa kapatılabilir.
- Yerel iletişimde istemci sayısı, istek boyutu ve işlem süreleri sınırlıdır.
- Girişten sonra parola arayüz alanından temizlenir.

Bu sınırlar `/etc/nuxprint-agent/agent.json` içindeki `Agent.PrintSafety` alanından ayarlanır.
Dosyada yeni alanlar yoksa varsayılan değerler kullanılır.
Yerel bağlantı sınırları `Agent.MaxLocalClients` ve `Agent.LocalRequestTimeoutSeconds` alanlarındadır.
Ayar değişikliğinden sonra servisi yeniden başlatın.

Windows referansında program konumu, kullanıcıya özel kimlik bilgileri ve doğrulanmış güncelleme akışı da düzeltildi.
Güvenilir yayıncı yapılandırılmadıysa Windows otomatik güncellemesi çalışmaz.
Windows referansı Linux paketlerine dahil edilmez.

Ayarlar, geçiş adımları ve test sınırları: [güvenlik düzeltmeleri](docs/security-fixes-2026-11-03.md).
İlk bulgular: [2 Kasım güvenlik incelemesi](docs/security-review-2026-11-02.md).
## Bilinen sınırlamalar

- Gerçek Ubuntu veya Debian kurulumu henüz doğrulanmadı.
- Linux grup izinleri, `chmod`, `flock` ve systemd henüz gerçek hedefte doğrulanmadı.
- Gerçek CUPS, yazıcı, masaüstü, sistem tepsisi ve bildirim testleri yapılmadı.
- Raspberry Pi için mevcut amd64 paketlerini kullanmayın.
- Otomatik yazıcı kurulumu ve otomatik Linux güncellemesi yoktur.
- IPP-over-USB bağlantıları fiziksel USB olarak ayırt edilemeyebilir.
- Windows kağıt ve tepsi kimliklerine karşılık gelen IPP değerleri uydurulmaz.
- Eksik yazıcı bilgileri modelde `0` veya `false` kalabilir. Bu değerler donanım desteğinin bulunmadığını kesinleştirmez.
- Raw, ölçek, tepsi, renk ve çift taraflı baskı desteği sürücüye bağlıdır.
- Zaman aşımında veya servis kapanışında kabul edilmiş işin sonucu belirsiz kalabilir.
- Tekrar baskı koruması 7 günlük ve 10.000 kayıtlık pencereyle sınırlıdır. Çevrimdışı iş kuyruğu yoktur.
- Kapanışta ACK yanıtı kaybolabilir. Kalıcı iş kaydı belirsiz işi otomatik tekrar basmayı engeller.

## Teknik belgeler

- [ARM paketleri ve doğrulama sonuçları](docs/raspberry-pi-support.md)
- [Güvenlik düzeltmeleri ve geçiş adımları](docs/security-fixes-2026-11-03.md)
- [İlk güvenlik incelemesi](docs/security-review-2026-11-02.md)
- [Bulut API sözleşmesi](docs/api-contract.md)
- [Linux geçiş haritası](docs/linux-migration-map.md)
- [Önceki uygulama ve doğrulama raporu](docs/stage-3.3-report.md)
