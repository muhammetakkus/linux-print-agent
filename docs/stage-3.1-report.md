# Aşama 3.1 — kapsam, uyumluluk ve doğrulama

## Talep ve son kullanıcı kararları

Talep: bağımsız .NET 8 Common/Core çözümü; mevcut veri modelleri, HTTP kimlik doğrulama, token yönetimi, cihaz kimliği/kaydı, Socket.IO handshake/olaylar/ACK ve yeniden bağlantı. Endpoint, namespace, olay adı, JSON tip/alan/varsayılanları ve kaynak hata davranışı korunur. Gerçek baskı, CUPS, PDF motoru, UI, CLI, systemd ve paketleme kapsam dışıdır. Kaynak hataları kendiliğinden düzeltilmez. Referans projelere derleme bağı kurulmaz.

Uygulama sırasında kullanıcı iki belirsizliği yanıtladı:

1. Heartbeat için «buna gerek yok local'de çalışacak.» dedi. Bu aşamada uygulama heartbeat gönderimi ve heartbeat süresi testi kapsam dışına alındı. Yeni heartbeat olayı/yükü tanımlanmadı. Kaynakta uygulama heartbeat'i yoktur. Test sunucusunun Engine.IO ping aralıkları uygulama heartbeat sözleşmesi değildir.
2. Linux farkları için «Şimdilik yalnız kaynakla aynı sözleşmeyi hazırla; Linux farklarını beklet» seçildi. `DeviceInfo.Platform` varsayılanı `windows` olarak korundu. Linux platformuna geçiş, `/var/lib` varsayılanı ve gerçek Linux credential store uygulanmadı. Credential erişimi zorunlu `ICredentialStore` üzerinden sağlanır; yalnız test projesinde bellek taklidi vardır. Şifresiz dosya saklama veya Windows DPAPI fallback yoktur.

Bu kararlar nedeniyle bu çıktı üretim Linux Agent kurulumu değildir. Bu aşamada istenen kütüphane altyapısı ve yerel uyumluluk doğrulamasıdır. Bir sonraki host, platform/saklama/yol kararları onaylandıktan sonra bağlanabilir.

## Oluşturulan yapı

```text
NuxPrint.Linux.sln
global.json
src/
  PrintAgent.Common/
    Constants.cs
    Models/ (mevcut altı model dosyası + PrinterIdMapping + UpdateInfo)
    PrintAgent.Common.csproj
  PrintAgent.Core/
    CloudSession.cs
    CloudConnection.cs
    DeviceIdentity.cs
    ICredentialStore.cs
    IAgentSocket.cs
    SocketIoAgentSocket.cs
    PrintAgent.Core.csproj
tests/
  PrintAgent.Core.Tests/
    ModelContractTests.cs
    CloudSessionTests.cs
    DeviceIdentityTests.cs
    CloudConnectionTests.cs
    SocketTransportTests.cs
```

Common/Core yalnız `net8.0` hedefler. Core, Common ve kaynakla aynı SocketIOClient 3.1.1 paketini kullanır. Çözümde referans Windows projesi yoktur. Windows `Constants.cs` ve altı model dosyası içerik olarak birebir kopyadır; dolayısıyla `MutexName`, Windows yazıcı filtreleri ve `GetWindowsUrl` gibi kaynak metinleri korunur. Bunlar Windows API çağrısı değildir ve Linux'ta çalıştırılan mutex/filtre/updater oluşturmaz. Kullanılmayan sabitler bu aşamada temizlenmedi.

## Uygulanan davranış

- `CloudSession.LoginAsync`: aynı session isteğini döndürür; kaynakta olduğu gibi otomatik SetAuth yapmaz. Çağıran session yanıtını `SetAuth` ile saklar. UI giriş kontrolü ve parola politikası eklenmedi.
- `LoadSavedAuth`, `SetAuth`, `ClearAuth`, legacy adopt, beş dakikalık exp penceresi, refresh/revoke, offline koruma ve logout korunur. Depolama biçimini host belirler; JSON auth modeli PascalCase ve `IsLegacy` alanını korur.
- Origin `https://nuxprint.com`; HTTP uçları `/api/agent/session`, `/adopt`, `/refresh`, `/logout`, `/api/agent/version`. JSON seçenekleri kaynakla aynı CamelCase politikasını kullanır; ekstra case-insensitive veya string-number dönüşümü eklenmedi.
- TLS giriş hatasında HTTP Date üzerinden saat tanısı korunur. Windows'a atıf yapan kaynak hata metni Linux farkları bekletildiği için değiştirilmedi.
- `DeviceIdentity.GetOrCreate(filePath)`: GUID üretme, dosyadan trim ederek okuma ve boşsa yenileme algoritması korunur. Varsayılan işletim sistemi yolu seçilmez. Testler benzersiz geçici yollar kullanır.
- `CloudConnection.RunAsync(ct)`: host tarafından iptal edilen ve await edilen bağlantı döngüsüdür. Namespace `/agent`, path `/api/socket.io`, auth `{token}`, yerleşik reconnection kapalıdır. Socket ayarı 10 saniye, dış bağlantı beklemesi 15 saniye korunur.
- `agent:register` ve `printer:status-update` tam kaynak projeksiyonunu kullanır. Kayıtta agentVersion, portName veya externalId eklenmez. `printers:registered` doğrudan listeyi tam adla eşler.
- `print:job`, aynı DTO'yu zorunlu host işleyicisine iletir; sonuç aynı ACK üzerinden döner. Gerçek baskı veya varsayılan sahte başarı/başarısızlık işleyicisi yoktur. Hata callback davranışı korunur.
- Yazıcı kaynağı host tarafından sağlanır. WMI keşfi/ağ probes Linux'a taşınmadı; CUPS veya yapay keşif verisi eklenmedi. Gerçek keşif sonraki aşamadadır.
- Üstel retry, jitter, 30 saniye üst sınır, refresh hatasında jitter olmaması ve oturumsuz durumda 1 saniye bekleme korunur. Testler yalnız zaman/rastgelelik ve dış transport Seam'lerinde taklit kullanır.
- Kaynak 100 ms StateChanged debounce korunur. Core nesneleri, WPF/UI singleton yerine host tarafından oluşturulur; bağımsız servis/ikinci Agent başlatmaz.

## Düzeltilmeden korunan kaynak sınırlamaları

- `Invalid token` socket hatası yerel exp gelecekteyse aynı token'ın yeniden kullanılmasına yol açabilir.
- Bağlantı kurulurken iptal, dış 15 saniyelik bekleme tamamlanana kadar gözlenmeyebilir; bağlantı başarısızlığında eski socket sonraki denemede veya Dispose'ta temizlenir.
- RunAsync iptalinden sonra IsConnected, kaynak iptal yolunda olduğu gibi eski true değerini taşıyabilir. Host bunu tamamlanmış çalışma olarak ele almalı; davranış bu aşamada değiştirilmedi.
- Aynı nesnede iki RunAsync çağrısına karşı yeni süreç/çalışma kilidi eklenmedi. Host tek döngü başlatmalı, iptal edip await ettikten sonra Dispose çağırmalıdır. Kaynak Windows singleton tek başlatıcıyı sağlıyordu; Linux host sonraki aşamadadır.
- İlk kimlik dosyası oluşturma atomik değildir; mevcut boş olmayan kimlik GUID olarak doğrulanmaz.
- `session_revoked` gövdede metin aramasıyla belirlenir. Düz HTTP Date güvenilir zaman doğrulaması değildir.
- Kalıcı iş kuyruğu/deduplication, fiziksel baskı takibi veya sonuç outbox eklenmedi.
- Kaynaktaki login/version/logout HttpResponseMessage yaşam döngüsü davranışı optimize edilmedi.

## Doğrulama

Kullanılan SDK: 8.0.426, geçici SDK klasöründe. Çalıştırma makinesi Windows x64; WSL/Linux çalışma ortamı kurulu değil. Linux x64 hedef derlemesi, Linux üzerinde test çalıştırma kanıtı sayılmaz.

```text
dotnet build NuxPrint.Linux.sln -c Release
dotnet test NuxPrint.Linux.sln -c Release --no-build
dotnet build src/PrintAgent.Core/PrintAgent.Core.csproj -c Release -r linux-x64 --self-contained false
```

Testler model JSON altın örneklerini, HTTP istek/yanıtlarını, oturum akışlarını, kimlik dosyası yeniden kullanımını, cihaz/yazıcı kayıt alanlarını, Socket.IO olay/ACK biçimini ve retry sürelerini doğrular. Ek transport testi gerçek SocketIOClient 3.1.1'i loopback Kestrel üzerinde minimal Engine.IO polling taklidiyle çalıştırır; auth, namespace/path, register, iş ve ACK baytlarını karşılaştırır. Test sunucusu NuxPrint sunucu sözleşmesinin bağımsız doğrulaması değildir. Gerçek NuxPrint hesabı, token'ı veya sunucusu kullanılmadı.

Sonuç: Release çözüm derlemesi başarılı: 0 uyarı, 0 hata. 46 test başarılı; 0 başarısız, 0 atlanan. Linux x64 hedef derlemesi başarılı: 0 uyarı, 0 hata. 28 referans dosyasının SHA-256 değeri değişmedi; altı model dosyası ve Constants.cs referansla birebir aynı.

## Standards

Matt Pocock Code Review akışının bağımsız standart incelemesinde belgelenmiş kural ihlali veya bu kapsamda işlem gerektiren kod kokusu bulunmadı. Common/Core .NET 8 hedefler; Windows API veya referans proje bağımlılığı yoktur. Kaynakla uyum amacıyla korunan sınırlamalar yeni hata olarak değerlendirilmedi.

## Spec

Bağımsız kapsam incelemesinde son kullanıcı kararlarına göre eksik gereksinim, kapsam genişlemesi veya aktarım hatası bulunmadı. HTTP oturumu, JSON, kayıt yükleri, Socket.IO adapter'ı, ACK ve retry davranışı incelendi. İncelemeciler derleme/testleri yeniden çalıştırmadı ve canlı sunucu doğrulaması yapmadı; yukarıdaki yürütme sonuçları uygulama oturumuna aittir.

İnceleme özeti: Standards 0 bulgu; Spec 0 bulgu. Depoda ilk commit henüz bulunmadığından yalnız bu aşamaya ait staged yeni dosyalar inceleme temeli olarak kullanıldı; önceki aşamanın untracked referansları ve belgeleri değişiklik kapsamına alınmadı.

Windows referansları ayrıca önceki aşamanın SHA-256 envanteriyle karşılaştırılır. Hiçbir referans dosyası değiştirilmez. CUPS, systemd, Avalonia, CLI veya DEB/RPM çalışması yapılmaz. Aşama 3.2 başlatılmaz.
