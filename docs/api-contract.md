# NuxPrint kaynak kodundan çıkarılan API sözleşmesi

Tarih: 2026-10-21. Bu belge başlangıçta Windows Agent 1.9.4 kaynağından çıkarılan API sözleşmesini kaydeder. Windows kaynakları artık projenin parçası değildir. Canlı sunucu veya sunucu kaynak kodu doğrulaması yapılmadı. «Kaynakta doğrulandı», sunucunun bugün aynı davranışı garanti ettiği anlamına gelmez.

## Adresler ve kodlama

- Origin: `https://nuxprint.com` (`Constants.cs:7`). HTTP uçları `/api` ile başlar.
- HTTP JSON: `System.Text.Json`, `PropertyNamingPolicy = CamelCase`; `PostAsJsonAsync` / `ReadFromJsonAsync` kullanılır.
- HTTP istemci zaman aşımı: 15 saniye. Logout iptali: 5 saniye.
- Socket.IO bağlantı adresi: `https://nuxprint.com/agent`. Namespace: `/agent`. Transport path: `/api/socket.io`. Bu iki değer birbirinin yerine geçmez.
- SocketIOClient paket sürümü: 3.1.1. Sunucunun Socket.IO/Engine.IO sürümü ve transport zorunlulukları: **Doğrulanmadı**.

Tablolardaki AS satırları tarihsel kaynak notlarıdır; artık mevcut bir dosyaya bağlantı değildir. Güncel Linux uygulaması `src/PrintAgent.Core/CloudSession.cs` ve `src/PrintAgent.Core/CloudConnection.cs` dosyalarındadır.

## HTTP uçları

| Metot / tam yol | Kimlik doğrulama | İstek JSON alanları | İstemcinin beklediği yanıt | Kanıt |
|---|---|---|---|---|
| POST `/api/agent/session` | Ek Authorization başlığı yok; email/password gövdede | `email`, `password`, `deviceId`, `deviceName`, `platform`, `agentVersion` | `AgentSessionResponse`: `sessionToken`, `accessToken`, `customer` | AS:76–104 |
| POST `/api/agent/session/adopt` | `Authorization: Bearer <legacy JWT>` | `deviceId`, `deviceName`, `platform`, `agentVersion` | `AgentSessionResponse` | AS:142–177 |
| POST `/api/agent/session/refresh` | Session token gövdede; ek Bearer başlığı yok | `sessionToken`, `agentVersion` | `AgentRefreshResponse`: `accessToken`, `customer` | AS:188–260 |
| POST `/api/agent/session/logout` | Session token gövdede | `sessionToken` | Gövde okunmuyor; şema **Doğrulanmadı** | AS:288–304 |
| GET `/api/agent/version` | Ek kimlik başlığı yok | Yok | `latestVersion`, `downloadUrls`, `releaseNotes`, `mandatory` | AS:1311–1327, 1386–1407 |

Başarılı HTTP durum kodlarının tam listesi ve hata JSON şemaları: **Doğrulanmadı**. Session girişi `EnsureSuccessStatusCode` çağırır. UI, 401'i hatalı giriş olarak gösterir. Adopt başarısızlığı `false` döner. Refresh 401 gövdesinde büyük/küçük harf duyarsız `session_revoked` metni varsa yerel kimlik bilgisi silinir; diğer 401, 5xx ve ağ hatalarında korunur. Bu kontrol yapılandırılmış hata kodu okuma yerine metin aramasıdır.

### Yanıt modelleri

`customer`, `CustomerInfo?` olarak okunur: `id`, `email`, `name`, `apiKey` string alanları. Null olabilmesi ve istemci varsayılanları sunucu alan zorunluluğunu kanıtlamaz.

`AgentSessionResponse` ve `AgentRefreshResponse` alanları yukarıdaki tabloda verilmiştir. `AuthData.Token`, ağdaki `accessToken` değerinin yereldeki adıdır. `AuthData.IsLegacy`, `SessionToken`, `Token` yerel saklama modelidir; tamamı doğrudan bir HTTP istek gövdesi değildir. `LoginRequest` ve `LoginResponse` tanımları vardır, fakat aktif giriş yolu bunları kullanmaz.

`UpdateInfo`: `latestVersion: string`, `downloadUrls: JsonElement?`, `releaseNotes: string`, `mandatory: bool`. İstemci yalnızca `downloadUrls.windows` string değerini okur. Linux indirme anahtarları ve zorunlu güncelleme politikası: **Doğrulanmadı**. `mandatory` okunur fakat UI'da zorunlu uygulama kontrolü yoktur.

### Diğer HTTP erişimleri

- `TryGetClockSkewAsync` (AS:108): yalnızca TLS giriş hatasında `GET http://nuxprint.com`, yönlendirme kapalı, 5 saniye; HTTP `Date` başlığından saat farkı tahmini. Kimlik bilgisi göndermez. Bu yanıt doğrulanmış zaman kaynağı değildir.
- `EnsureSumatraAsync` (AS:919): `GET https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64.exe`. Windows bağımlılığıdır, NuxPrint sözleşmesi değildir.
- `DownloadAndInstallUpdateAsync` (AS:1336): sürüm yanıtından gelen URL'ye GET; Windows EXE indirir. Linux'ta doğrudan kullanılamaz. URL kabul politikası ve indirme bütünlük doğrulaması kaynakta yoktur.

## Oturum ve cihaz akışı

1. `DeviceIdHelper.GetOrCreate` dosyadan kimlik okur; yoksa `Guid.NewGuid().ToString()` ile üretir. Windows konumu `%ProgramData%/CloudPrintAgent/device_id`.
2. Giriş isteği cihaz alanlarını da gönderir. Kaynakta `platform = "windows"`, `agentVersion = "1.9.4"`, cihaz adı `Environment.MachineName`.
3. UI, boş olmayan `sessionToken` ister. Session token, access token ve customer DPAPI ile saklanır.
4. Yeniden bağlantıda `EnsureAccessTokenAsync`, JWT `exp` alanını yerelde okur. Eksik/okunamayan token veya bitişine beş dakikadan az süre varsa refresh ister. Burada imza doğrulaması yapılmaz; bu kontrol yalnızca yenileme kararıdır.
5. Eski yalnız-JWT kaydı varsa adopt denenir. Başarısızsa eski JWT ile bağlantı denenebilir.
6. Socket handshake: `Auth = { token = Auth.Token }`. Bu HTTP session token değildir.
7. Bağlantıda `agent:register` gönderilir; ardından yazıcı kimlik eşlemesi alınır.
8. Logout, sunucuya en iyi çabayla bildirilir; yerel kayıt her durumda temizlenir.

Session token'ın süresiz oluşu kaynak yorumunda belirtilir; sunucu TTL/rotasyon garantisi **Doğrulanmadı**. `platform: "linux"` kabulü **Doğrulanmadı**. Ayrı HTTP cihaz kaydı, eşleştirme kodu veya kullanıcı girişi dışında cihaz eşleştirme ucu bulunmadı.

## Socket.IO olayları

| Yön | Olay | Gövde / yanıt | Kaynak |
|---|---|---|---|
| Agent → sunucu | `agent:register` | `{ deviceId, deviceName, platform, printers: [...] }` | AS:459 |
| Sunucu → Agent | `printers:registered` | Doğrudan `[{ name, externalId }]`; wrapper okunmuyor | AS:541 |
| Agent → sunucu | `printer:status-update` | `{ printers: [...] }` | AS:719 |
| Sunucu → Agent | `print:job` | `PrintJobData`; sonuç aynı olayın callback/ACK yanıtıdır | AS:527 |

Kayıt ve durum bildiriminde her yazıcı: `name`, `status`, `connectionType`, `driverName`, `capabilities`. `portName`, `externalId`, `isActive` modelde vardır fakat bu iki emit gövdesine dahil edilmez. Durum güncellemesine `deviceId` eklenmez. Kayıt mesajına `agentVersion` eklenmez.

Windows durum değerleri: `online`, `offline`, ilk ağ kontrolünde `unknown`. Bağlantı türü: `USB` veya `Network`. Ağ kontrolü sonradan paralel çalışır ve gerekirse durum bildirimi gönderilir. IP bulunamazsa kaynak `online` kabul eder; erişilebilirlik fiziksel hazır olmayı kanıtlamaz.

`printers:registered` eşlemesi tam yazıcı adına göre yapılır. Kalıcı eşleme saklanmaz. Yazıcının sonraki yeniden keşfinde UI, önceki `externalId` değerini ad üzerinden korumaya çalışır.

### Capabilities şeması

`capabilities` null olabilir. Doluysa kaynak model alanları:

| Alan | Tip / anlam |
|---|---|
| `bins` | `[{ id: int, name: string }]` |
| `papers` | `[{ id: int, name: string, width: int, height: int }]`; ölçü 0.1 mm |
| `duplex`, `color`, `collate` | bool |
| `maxCopies` | int |
| `dpis` | string listesi; Windows üretimi `"XxY"` biçiminde |
| `fields` | int; Win32 `DC_FIELDS` sonucu |

`id` değerleri Windows sürücüsünden gelir. IPP anahtarlarına eşdeğer oldukları varsayılmamalı. Sunucunun bu alanları hangi anlamla kullandığı ve Linux eşlemesi: **Doğrulanmadı**.

## Yazdırma işi sözleşmesi

Alan adları `PrintJobData.cs` içindeki `JsonPropertyName` özniteliklerinden doğrulandı. Bunlar model varsayılanlarıdır; sunucu zorunluluk kuralları değildir.

| Alan | Tip | İstemci varsayılanı / davranışı |
|---|---|---|
| `jobId` | string | boş; log/geçmiş için kullanılır, tekrar baskıyı engellemez |
| `printerId` | string | boş; yazıcı seçimi için kullanılmaz |
| `printerName` | string | boş; gerçek seçim adla yapılır |
| `documentType` | string | `pdf`; küçük harfe çevrilir; `pdf` dışındaki her değer raw yoluna gider |
| `documentData` | string | boş; Base64 olarak çözülür, belge URL'si indirilmez |
| `documentName` | string | boş |
| `copies` | int | 1 |
| `options` | `PrintJobOptions?` | null; yalnızca PDF yolunda uygulanır |

`options` alanları:

- `duplex: string?`: `one-sided`, `long-edge`, `short-edge` için eşleme var.
- `paperSize: string?`: Sumatra `paper=` değerine dönüşür.
- `tray: string?`: Sumatra `bin=` değerine dönüşür.
- `color: bool?`: true renkli, false monochrome, null sürücü varsayılanı.
- `orientation: string?`: `portrait` / `landscape`; yoksa ilk 8192 baytta `/MediaBox` regex tahmini.
- `fitToPage: bool?`: false → noscale, diğerleri → fit.
- `pages: string?`: yalnızca rakam, virgül ve tire tutulur.

PDF için kopya sayısı 1'den büyükse Sumatra ayarına eklenir. Raw için 1'den küçük değer 1 yapılır ve her kopya ayrı spool işi olarak gönderilir. Üst boyut/kopya sınırı, MIME doğrulama, hash, iş süresi, zaman aşımından sonra uzlaştırma ve raw belge türlerinin izin listesi: kaynakta yok / **Doğrulanmadı**.

### İş sonucu ve güvenilirlik

`print:job` dinleyicisi `PrintAsync` sonucunu `response.CallbackAsync(result)` ile döndürür. Şema: `status: string`, `errorMessage: string?`. `status` değerleri `done` veya `failed`. Serializer null alanı atmazsa başarılı yanıtta `errorMessage: null` bulunur; Socket.IO serializer'ın gerçek bayt çıktısı **Doğrulanmadı**. ACK gövdesinde `jobId` yoktur; ilişki gelen olaya aittir. Ayrı sonuç HTTP ucu veya sonuç emit olayı bulunmadı.

Yerel geçmiş durumları `printing`, `done`, `failed`. PDF `done`: Sumatra işlemi sıfır çıkış koduyla sona erdi. Raw `done`: tüm WritePrinter çağrıları beklenen bayt sayısını yazdı. Kaynak CUPS/Windows spool iş kimliğiyle fiziksel çıktı takibi yapmaz. `done` fiziksel tamamlanma garantisi değildir.

Tek `SemaphoreSlim` tüm yazıcılar için baskıyı sıralar. Kilit en fazla 30 saniye beklenir; PDF işlemi zaman aşımı 60 saniyedir. İş geçmişi bellekte en fazla 100, log en fazla 200 kayıt tutar. Kalıcı kuyruk, deduplication ve sonuç outbox yoktur. ACK kaybında yeniden teslim, ACK zaman aşımı, aynı işin yeniden gönderimi ve sunucu tarafı durum makinesi: **Doğrulanmadı**.

## Bağlantı, heartbeat ve yoklama

- Yerleşik Socket.IO reconnection kapalı; `ConnectionLoopAsync` kendi gecikmesini uygular.
- Yeniden bağlantı gecikmesi üstel artış + jitter ile 30 saniyede sınırlandırılır. Refresh başarısızlığı kolunda jitter yoktur. İlk başarısız deneme sayacına bağlı süre 2 saniye olabilir.
- Socket bağlantı ayarı 10 saniye; dış bekleme 15 saniye.
- `OnError`: tam `Session revoked` mesajı yerel oturumu siler. `Invalid token` yeniden denemeyi tetikler; mevcut kod token'ı açıkça geçersiz kılmaz. Sonraki döngü yerel `exp` kontrolüne göre yeniler.
- README heartbeat için 30 saniye der. Kaynakta uygulama heartbeat metodu/olayı/mesajı bulunmadı. Socket.IO taşıma ping/pong davranışı ayrı konudur; uygulama heartbeat sözleşmesi **Doğrulanmadı**.
- WMI ekleme/silme sorguları 30 saniyelik aralık kullanır. Durum fallback döngüsü başlangıçta 3 saniye sonra, ardından 60 saniyede bir çalışır. `Constants.PrinterPollIntervalMs = 30000` tanımlı fakat bu döngüde kullanılmaz. Baskı sırasında yenileme 5 saniye ertelenir; değişim bildirimi 1 saniye debounce kullanır.

## Sonraki aşamadan önce doğrulanacaklar

Linux platform kabulü; capabilities alanlarının Windows dışı anlamı; yeni yazıcının status-update ile kayıt edilip edilmediği; silinen yazıcı davranışı; heartbeat; ACK süreleri; tekrar teslim; yeniden bağlantıda kaçırılan işler; sonuç uzlaştırma; token süresi ve hata şemaları; desteklenen raw türleri; Linux güncelleme alanları. İstemcide bulunmayan uçlar veya olaylar bu belgeye eklenmedi.
