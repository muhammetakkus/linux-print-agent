# Aşama 3.3 — teslim ve doğrulama raporu

## Kapsam

Temel `5fcf44d` üzerine Avalonia UI, CLI, Unix socket IPC, iki Linux x64 DEB, Bash scriptleri ve Linux README eklendi. Cloud API/DTO/olay/retry ve CUPS baskı sırası değiştirilmez. Yalnız host'ta yerel giriş/çıkış için cloud döngüsünün Stop → Logout → Start yaşam döngüsü ve mevcut veri snapshot erişimi eklenir. UI/CLI yalnız Common'a referans verir; Core, Service veya SocketIOClient kullanmaz.

Kullanıcı en son aşamada ilk paketin x64 olmasını açıkça istedi. Dolayısıyla bu DEB'ler Raspberry Pi ARM paketi değildir. Önceki ayrı heartbeat eklememe kararı korunur. Windows referansları değişmez ve üretim/paketleme onlara bağımlı değildir.

## Dosya grupları

- `src/PrintAgent.UI/`: Avalonia X11/Skia 11.3.8, MVVM, giriş ve Account/Printers/Jobs/Log, tray ve bildirimler. Referanstaki ikon bağımsız Assets kopyasıdır.
- `src/PrintAgent.CLI/`: status, printers, connection, history, diagnostics, login ve logout. Parola argv yerine gizli terminal isteminden veya açık `--password-stdin` ile alınır.
- `src/PrintAgent.Common/Local/`: version 1 uzunluk çerçeveli IPC, client ve ayrı snapshot DTO'ları. Mevcut cloud DTO'ları değişmedi.
- `src/PrintAgent.Service/LocalIpcServer.cs`, `LocalAgentController.cs`, `AgentLogBuffer.cs`, `CloudRuntime.cs`: mevcut servis üzerinde yerel kontrol ve bellekte 200 log.
- `LinuxPrintEngine` ekleri: kilit altında geçmiş snapshot'ı ve mevcut done/failed temizleme işlemi. Baskı koşulları değişmedi.
- `packaging/systemd/nuxprint-agent.service`: yeni publish yerleşimi, runtime 0750 ve erişim grubu.
- `packaging/deb/`: ar/tar DEB üretici ve bağımsız okuyucu, kontrol/kurulum/kaldırma dosyaları, configuration ve desktop launcher.
- `scripts/build.sh`, `publish.sh`, `install.sh`, `verify_tests.py`: derleme/test/publish/paket/kurulum ve eksik test keşfi kontrolü.
- `tests/PrintAgent.Desktop.Tests/`: gerçek UDS host testi, CLI, IPC çerçeve/sır sınırı ve headless UI testleri.
- `README.md`: Linux'a ait 19 zorunlu konu; gerçek komutlar, dizinler, sonuçlar ve sınırlar.

## Kullanıcı akışı ve zorunlu Linux farkları

Windows kaynakta ayrı Dashboard veya Ayarlar ekranı yok; yalnız giriş ve dört sekme var. Bunlar korundu; yeni ekran eklenmedi. Printer test baskısı servis tarafında referanstaki aynı test PDF'iyle yürür. Refresh, kaynaktaki gibi mevcut snapshot'ı yeniler; yeni donanım taraması kuralı değildir. Jobs Clear yalnız done/failed kayıtlarını temizler. Account API key kopyalama, parola gösterme, bağlantı ve sürüm bilgileri korunur.

UI kapanışı/Quit servisi durdurmaz; bu Linux teknik ayrımının açık gereğidir. Tray backend'i varsa pencere gizlenir ve Show/Quit menüsü vardır; yoksa normal kapanır. Bildirim `/usr/bin/notify-send` ile, shell açmadan gönderilir. Gerçek masaüstü ve tray doğrulanmadı.

**Eksik taşınabilir işlev:** Windows'un otomatik güncelleyicisi EXE indirip çalıştırır. Linux update endpoint/indirme anahtarı doğrulanmadığı için Windows URL'si kullanılmaz. Sürüm gösterilir; Update mevcut arayüzde DEB kurulumu gerektiğini bildirir. Otomatik Linux güncellemesi veya güncelleme onay dialog'u tam taşındı diye raporlanmaz. Bu, yeni cloud protokolü uydurmamak içindir.

## IPC erişimi

Servis cihaz kilidini tuttuğu sürece `/run/nuxprint-agent/control.sock` açar. Dizin 0750, socket 0660 ve grup `nuxprint-agent`. State/cache 0700 ve credentials 0600 kalır. Grup üyeliği Agent yönetim yetkisidir; paket kullanıcıları kendiliğinden gruba eklemez, install.sh sudo ile çağıran gerçek kullanıcıyı ekler. README tekrar oturum açmayı belirtir.

İstekler 4 MiB ile sınırlı çerçevelidir; okunmayan bağlantı 15 saniyede kesilir. Tek istek/yanıt sonrası yerel stream kapanır; cloud devam eder. Yalnız tanımlı komutlar çalışır; keyfi yöntem çağırma, shell veya belge yükleme yoktur. Login isteği parola taşır; istek loglanmaz. Yanıt access/session token içermez. API key, referanstaki Account davranışı için yetkili kullanıcılara gösterilir. Yerel protokolün alanları cloud protokolü değildir.

## Paketler

`artifacts/deb/nuxprint-agent-desktop_1.9.4-1_amd64.deb`: self-contained servis+CLI+UI; desktop launcher, autostart ve CUPS/masaüstü bağımlılıkları.

`artifacts/deb/nuxprint-agent-server_1.9.4-1_amd64.deb`: self-contained servis+CLI; UI, Avalonia veya desktop launcher yok.

İki paket Conflicts/Replaces ile birlikte kurulamaz. Apphost'lar Linux ELF64, machine 62 (x86-64) olmalıdır; paket üretici bunu denetler. Bağımsız denetçi ar başlığı, Debian 2.0 sürümü, tar yolları, maintainer script izinleri, ELF, unit ExecStart ve Server'da UI bulunmamasını kontrol eder. Gerçek `dpkg/apt` kurulumu veya systemd başlatılması Windows'ta yapılamadı. Paketler imzalı APT deposu değildir. Maintainer alanı proje adıdır; doğrulanmamış bir destek e-postası uydurulmadı.

## Doğrulama

- Release çözüm derlemesi: **0 hata, 0 uyarı**.
- Core: **46/46 geçti**; Desktop: **9/9 geçti**. Son sonuçlar her test projesinin `TestResults/final.trx` dosyasındadır.
- Linux/Service: **34 test çalıştırılamadı**. Windows Uygulama Denetimi assembly yüklemesini `0x800711C7` ile engelledi; keşif 0 test döndürdü. Başarılı sayılmadı. `verify_tests.py --name final.trx` beklenen şekilde exit 1 döndürdü.
- Gerçek UDS + production DI + mock cloud/CUPS: CLI stdin giriş, tek cloud bağlantısı, ikinci authenticated girişin reddi, token içermeyen snapshot, istemci bağlantısı kapandığında servis devamı, geçersiz IPC ve logout doğrulandı.
- Service/CLI/UI için son kaynaklardan self-contained `linux-x64` publish başarılı.
- İki DEB oluşturuldu; bağımsız ar/tar/ELF/izin/içerik denetimi geçti. SHA-256 değerleri `artifacts/deb/SHA256SUMS` içindedir.
- Bash syntax kontrolü geçti. Referans manifestindeki 28 Windows dosyasının hash'i aynı kaldı. Windows kaynaklarına derleme bağımlılığı yok.

Test sayıları: Core 46, Linux/Service 34, Desktop 9 (toplam 89 beklenir). Windows Uygulama Denetimi test keşfini engelleyebildiğinden `dotnet test` exit 0 tek başına başarı kabul edilmez; build.sh TRX içindeki gerçek yürütülen test sayısını ayrıca doğrular. Güvenlik politikası değiştirilmez veya aşılmaz.

Headless UI görselleri `artifacts/preview/login.png` ve `printers.png` olarak üretildi ve incelendi. Bu Windows/Skia headless görüntüleridir; Linux masaüstü testi değildir. Örnek yazıcı/kimlik test verisidir.

README'deki Bash ve maintainer scriptleri syntax kontrolünden geçirilir. README'de verilen dotnet, publish ve paket araçları gerçek proje adlarını kullanır. `install.sh`/apt/systemctl gerçek hedefte çalıştırılmadı; kurulum başarılı gösterilmez.

## Son kontrol tablosu

| Kontrol | Sonuç |
|---|---|
| Linux çözümü | Release: 0 hata, 0 uyarı |
| API/Socket.IO | Core aynı; mevcut 46 mock/loopback testi |
| CUPS motoru | Önceki uygulama korundu; gerçek CUPS/yazıcı hâlâ doğrulanmadı |
| Bağımsız servis | Generic Host + systemd unit; gerçek systemd doğrulaması yok |
| UI/CLI → servis | Ortak IPC client; gerçek UDS ile mock host entegrasyonu |
| UI kapanırken servis | UI Stop/Logout çağırmaz; yerel bağlantılar kapanırken servis devam eder |
| Desktop/Server DEB | Dosyalar oluşturulur, binary içerik denetlenir; hedef kurulum bekler |
| README | Linux uygulamasını anlatır; otomatik update/donanım sınırlarını saklamaz |
| Windows bağımlılığı | ProjectReference/Compile Include yok; referanslar paketlenmez |

Gerçek Linux izin/grup denemesi, CUPS çıktısı, Pi, X11/XWayland masaüstü/tray, DEB kurulumu ve systemd kabul testi hâlâ gerekir. Bu sonuçlar üretim veya donanım uyumluluk onayı değildir.

## Standards

Bağımsız inceleme, açık oturum varken ikinci CLI girişinin mevcut socket ile farklı hesap gösterebildiğini buldu. Referans UI açık oturumda giriş sunmadığı için yerel login artık önce logout yapılmasını ister. Gerçek UDS entegrasyon testinde kimlik nesnesinin ve tek bağlantının korunması doğrulandı. Başka somut standart ihlali bulunmadı; önceki çalışan cloud kodu yeniden tasarlanmadı.

## Spec

Bağımsız kapsam incelemesi aynı ikinci giriş sorununu doğruladı; düzeltildi. Paketleme, varsayılan izinler ve logout yaşam döngüsünde ek somut kapsam hatası bulunmadı. Linux otomatik güncellemesinin eksikliği ve gerçek hedef testlerinin yapılamaması açık teslim sınırlamalarıdır. Bu incelemeler gerçek donanım kabul testi yerine geçmez.

Teknik dayanaklar: [Avalonia 11 Linux](https://v11.docs.avaloniaui.net/docs/overview/supported-platforms/), [headless test platformu](https://v11.docs.avaloniaui.net/docs/concepts/headless/), [CUPS IPP](https://openprinting.github.io/cups/doc/spec-ipp.html). Windows README veya kaynak dosyası değiştirilmedi.
