# NuxPrint güvenlik incelemesi — 2 Kasım 2026

> 3 Kasım 2026: Aşağıdaki bulgular inceleme tarihindeki kodu kaydeder. Düzeltmeler `1.9.5` kaynaklarına eklendi. Güncel uygulama ve doğrulama bilgileri için [düzeltme raporunu](security-fixes-2026-11-03.md) okuyun. Eski paketler bu düzeltmeleri içermez.

İncelenen konum: `C:\Users\ed554\Desktop\nuxprint-linux\linux-print-agent-main`.

## Kapsam ve yöntem

Tüm alt dizinlerin envanteri çıkarıldı: inceleme başlangıcında 5.283 dosya, 2.206.809.405 bayt. `src`, `tests`, `scripts`, `packaging`, `docs`, `reference/windows`, `referance/windows` ve `artifacts` kapsama alındı. Uygulamanın güven sınırları, kimlik doğrulama, yerel IPC, baskı girdileri, süreç çalıştırma, dosya izinleri, kurulum/güncelleme akışları ve bağımlılık manifestleri incelendi; kaynaklarda gizli anahtar ve tehlikeli yürütme/TLS kalıpları arandı.

Bu, her DLL/SO dosyasının tersine mühendislikle incelendiği anlamına gelmez. İkili çıktılarda bağımlılık manifestleri ve örnek çalışma ortamı dosya sürümü incelendi; altı DEB'in SHA256 değerleri mevcut SHA256SUMS ile karşılaştırıldı. Canlı bulut API'sine veya gerçek yazıcıya pentest uygulanmadı. Strix üzerinde tarama başlatılmadı; aşağıdaki bulgular yerel kaynak incelemesine ve kontrollü testlere aittir.

Standart `dotnet list ... package --vulnerable --include-transitive` kontrolü, makinede .NET SDK bulunmadığı için çalışmadı. Bunun yerine gerçek kaynak dosyalarının ilgili bölümleri PowerShell'in C# derleyicisiyle bellekte derlenerek sahte CUPS ve sahte süreç yürütücüsüyle test edildi. Üretim kaynakları değiştirilmedi. Bu testler .NET 10 ana bilgisayarında çalıştı; Linux/.NET 8 üzerinde uçtan uca çalışma doğrulaması değildir.

Önem dereceleri bu incelemeye ait nitel değerlendirmelerdir; ölçülmüş CVSS puanı değildir. Bulut girdilerini kullanan sorunlar, hedef agent'a iş gönderebilen bir hesap, sunucunun ilettiği hatalı iş veya ele geçirilmiş bulut bileşeni gerektirir. İnternet üzerinden kimlik doğrulamasız erişim kanıtlanmadı. Bulut sunucusunun boyut/kopya sınırları bu depoda bulunmadığı için doğrulanamadı.

## Linux bulguları

### L1 — Yüksek: kopya sayısında üst sınır yok

- Kanıt: `src/PrintAgent.Linux/LinuxPrintEngine.cs:82-89`.
- Raw işlerde yalnız alt sınır uygulanıyor: `Math.Max(1, job.Copies)`. Her kopya ayrı CUPS gönderimi oluyor. Yazıcının bildirilen `MaxCopies` özelliği veya uygulama kotası burada kontrol edilmiyor.
- Test: `copies = 2147483647` kabul edildi. Sahte CUPS üçüncü gönderimde kontrollü hata döndürdü; gerçek baskı yapılmadı ve milyarlarca döngü çalıştırılmadı.
- Etki: iş gönderebilen bir taraf, kuyruk ve sarf malzemelerini tüketebilir; tek global baskı kilidi nedeniyle diğer yazıcıların işlerini de engelleyebilir. CUPS'ın PDF kopyalarına uygulayabileceği sınırlar raw yolundaki ayrı ayrı gönderimleri sınırlamaz.
- Düzeltme: iş kabul edilirken kopya sayısına makul ve yapılandırılabilir üst sınır koyun; yazıcı kapasitesi ve hesap/cihaz kotasını birlikte uygulayın. İş başına toplam süreyi de sınırlayın.

### L2 — Orta: raw yazdırma süresiz bekleyebilir, iptal aktarılmıyor

- Kanıt: `src/PrintAgent.Linux/CupsClient.cs:74`, `src/PrintAgent.Linux/LinuxPrintEngine.cs:29-54,85`, `src/PrintAgent.Service/AgentWorker.cs`.
- PDF için 60 saniye sınırı varken raw için süreç zaman aşımı `null`. PrintAsync iptal belirteci almıyor ve CUPS çağrısına servis iptali aktarılmıyor.
- Test: sahte yürütücüye aktarılan raw zaman aşımı `null`, sahte CUPS'a aktarılan belirteç `CanBeCanceled=false`. CUPS tamamlanmadığında PrintAsync beklemede ve `IsPrinting=true` kaldı; test sonrasında sahte işlem serbest bırakıldı.
- Etki: CUPS/lp takılırsa tüm baskılar tek kilit arkasında kalabilir. Yazıcı yoklaması da baskının bitmesini bekliyor. İş göndericisinin tek başına her sistemde lp'yi takabileceği kanıtlanmadı; bozuk/erişilemeyen CUPS ile etkisi büyüyen bir kullanılabilirlik sorunudur.
- Düzeltme: PDF ve raw için süreç zaman aşımı, işin tamamını kapsayan süre bütçesi ve servis kapanış iptali uygulayın.

### L3 — Orta: sınırsız belge adı yerel yönetim yanıtını bozabiliyor

- Kanıt: `src/PrintAgent.Linux/LinuxPrintEngine.cs:43,107-110,117-120`, `src/PrintAgent.Service/AgentLogBuffer.cs`, `src/PrintAgent.Service/LocalAgentController.cs:60-66`, `src/PrintAgent.Common/Local/LocalProtocol.cs:48-53`.
- `documentName` için uzunluk sınırı yok; aynı değer geçmiş ve loglarda tekrar saklanıyor. Tüm durum tek yanıtta dönüyor. Protokol JSON'u tamamen oluşturduktan sonra 4 MiB sınırını kontrol ediyor.
- Test: 2 MiB ASCII belge adı içeren bir PDF işi sahte CUPS'a gönderildi. Geçmiş ve loglardan oluşturulan yanıt 6.293.196 bayt oldu; gerçek LocalProtocol.WriteAsync bu yanıtı reddetti.
- Etki: bu büyüklükte bir başlık agent'a ulaştığında UI/CLI durum, geçmiş ve tanılama yanıtlarını alamayabilir. Kayıtların temizlenmesi/değişmesi veya servisin yeniden başlaması gerekebilir. Gerçek lp bu kadar uzun bir başlığı reddetse bile başlık gönderimden önce geçmişe/loga alınıyor. Bulutun bu boyutta başlığı iletip iletmeyeceği doğrulanmadı.
- Düzeltme: alan uzunluklarını girişte sınırlayın; log mesajlarını kısaltın; geçmiş/loglar için sayfalama ve çıktı boyutu bütçesi kullanın. Belge verisine de çözmeden önce/sonra boyut sınırı ve kaynak bütçesi koyun; mevcut Base64 çözümü tüm veriyi belleğe alıyor.

### L4 — Düşük: yerel IPC eşzamanlı bağlantı ve yanıt süresi sınırı yok

- Kanıt: `src/PrintAgent.Service/LocalIpcServer.cs:35-39,53-58`.
- `Listen(16)` yalnız bekleyen bağlantı kuyruğudur; kabul edilmiş istemci sayısını sınırlamaz. Her bağlantı için ayrı görev açılıyor. İlk okuma 15 saniye ile sınırlı; işlem yürütme ve yanıt yazma için istemciye özel bir son süre yok.
- Etki: socket'e erişebilen yerel kullanıcı sürekli bağlantı açarak dosya tanıtıcısı/bellek tüketebilir; yanıt okumayan istemciler görevleri açık tutabilir. Varsayılan 0660/0750 izinleri nedeniyle sıradan tüm yerel kullanıcılar erişemez; grup üyeleri zaten yönetim yetkisine sahiptir. Bu nedenle bağımsız yetki yükseltme olarak değerlendirilmedi. Dinamik tüketim testi yapılmadı.
- Düzeltme: kabul edilmiş istemci sayısını sınırlayın, istemci başına işlem/yazma süre bütçesi ve bağlantı kapanışında iptal uygulayın.

### L5 — Düşük: başarılı girişten sonra parola UI modelinde kalıyor

- Kanıt: `src/PrintAgent.UI/MainViewModel.cs:15,79-87,107`.
- `request.Password = null` yapılıyor fakat ViewModel.Password yalnız logout sonrasında temizleniyor. Başarılı login sonrasında gizli formla birlikte bellekte kalabiliyor.
- Etki: UI sürecinin belleğini okuyabilen tarafın parolayı bulabileceği süre uzuyor. Bu, tek başına başka kullanıcının belleğini okuma yetkisi sağlamaz.
- Düzeltme: login denemesinden sonra Password alanını temizleyin ve görünür parola durumunu kapatın. .NET string'lerinin geçmiş bellek kopyalarının sıfırlanmasını garanti etmediğini dikkate alın.

## Yalnız Windows referansında bulunan sorunlar

`reference/windows` Linux çözümüne/paketine dahil edilmiyor. Aşağıdaki sorunlar Linux'ta çalışıyormuş gibi yorumlanmamalı.

### W1 — Yüksek: başka yerel kullanıcı tarafından değiştirilebilir program çalıştırılıyor

- Kanıt: `reference/windows/setup.iss:44`, `reference/windows/src/PrintAgent.UI/Services/AgentService.cs:919-934,1091-1105`.
- Installer, `%ProgramData%\CloudPrintAgent` dizinine `users-modify` veriyor. SumatraPDF.exe aynı dizinde bulunuyor ve varsa yalnız varlık kontrolüyle kabul edilip PDF baskısında çalıştırılıyor. Dosya imzası veya güvenilir hash doğrulanmıyor.
- Etki: referans installer ile kurulmuş Windows sisteminde başka standart kullanıcı dosyayı değiştirerek bir sonraki PDF baskısında agent kullanıcısının yetkileriyle kod çalıştırabilir. Agent yönetici çalışmıyorsa otomatik olarak SYSTEM yetkisi elde edilmez. Çapraz kullanıcı Windows kurulumu üzerinde istismar denenmedi; izin ve çalıştırma zinciri kaynakta açık.
- Düzeltme: yürütülebilir dosyayı standart kullanıcıların değiştiremediği Program Files altına kurun; kullanıcı verileriyle programları ayırın ve imza doğrulaması uygulayın.

### W2 — Yüksek: Windows kimlik bilgileri kullanıcılar arasında çözülebilir

- Kanıt: `reference/windows/src/PrintAgent.Common/Helpers/CredentialStore.cs:10-19,24-29`, `reference/windows/setup.iss:44`.
- Kimlik bilgileri paylaşılan ProgramData dizininde ve DPAPI `LocalMachine` kapsamıyla saklanıyor. Installer'ın users-modify izni başka yerel kullanıcılara dosyayı okuma/değiştirme olanağı veriyor.
- Etki: bu kurulum koşullarında başka yerel kullanıcı saklanan session/access token ve müşteri API anahtarını elde edebilir. Token'ın bulutta tam olarak hangi işlemleri yapabildiği sunucu kodu olmadan belirlenemiyor.
- Düzeltme: kullanıcı uygulaması için kullanıcıya özel dizin/ACL ve DPAPI CurrentUser; makine servisi için servis hesabına özel ACL ve saklama mekanizması kullanın.
- Birincil kaynak: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope — LocalMachine kapsamı, aynı makinedeki diğer hesapların veriyi çözmesini engellemez.

### W3 — Orta, koşullu: güncelleme dosyası bağımsız doğrulanmadan çalıştırılıyor

- Kanıt: `reference/windows/src/PrintAgent.UI/Services/AgentService.cs:1336-1382`, `reference/windows/src/PrintAgent.UI/ViewModels/MainViewModel.cs:291-334`.
- API'den dönen indirme adresinde HTTPS/izinli host kontrolü ve indirilen installer için beklenen yayıncı imzası kontrolü yok. API sürüm metni geçici dosya adına, oradan cmd.exe komutuna giriyor; sürüm için dar biçim doğrulaması da yok.
- Önkoşul: güncelleme metadatasının veya indirme kaynağının saldırgan tarafından kontrol edilmesi, ya da indirilen dosyanın çalıştırılmadan önce değiştirilebilmesi. API ele geçirilmiş veya uzaktan erişim açığı bulunmuş değildir; bu inceleme böyle bir sonuç göstermiyor. Kullanıcı güncellemeyi onaylıyor ve işletim sisteminin UAC kuralları ayrıca geçerli.
- Etki: güncelleme güven zinciri bozulduğunda sahte installer'ın çalıştırılmasına karşı istemcide ek doğrulama yok.
- Düzeltme: izinli HTTPS kaynağı, güvenilir anahtarla doğrulanan güncelleme metadatası ve yayıncı imzası kontrolü kullanın; sürümü Version.TryParse ile doğrulayın ve cmd.exe üzerinden komut üretmeyin.

## Ek güvenilirlik / saldırı yüzeyi notları

- Aynı jobId ikinci kez gelince tekrar basılıyor. Kontrollü testte iki gönderim oluştu; mevcut PrintEngineTests de bunu bekliyor. Bu, tek başına kimlik doğrulama atlatma değil; yeniden teslim/ACK kaybı veya iş gönderebilen tarafın tekrarıyla mükerrer fiziksel çıktı riski. Kalıcı, boyutu/süresi sınırlı idempotency kaydı ve CUPS sonuç uzlaştırması düşünülmeli.
- Bilinmeyen documentType değerleri de raw yoluna gidiyor; testte doğrulandı. Raw yazdırma ürün özelliği olduğundan tek başına RCE sayılmadı. İzin verilen yazıcı dilleri açıkça tanımlanmalı ve gerekmeyen kurulumlarda raw kapatılabilmeli.
- Linux credentials.json şifrelenmeden saklanıyor ama 0600 ve üst dizin 0700 ile korunuyor. Bu depoda belgelendirilmiş bir tercih; yalnız bu sebeple başka kullanıcıların okuyabildiği bir açık olarak raporlanmadı. Disk/yedek hırsızlığı tehdidi için ek koruma gerekebilir.
- API anahtarının yetkili yerel kullanıcıya gösterilmesi de açıkça ürün davranışı. Yetkisiz erişim kanıtı olmadan ayrı bir açık sayılmadı.

## Doğrulama çıktısı

```json
{
  "SnapshotBytesAfterSingleLargeTitle": 6293196,
  "LocalProtocolRejectsState": true,
  "MaxCopiesAccepted": 2147483647,
  "SubmissionsBeforeControlledStop": 3,
  "DuplicateJobSubmissions": 2,
  "UnknownDocumentTypeAccepted": true,
  "RawTaskCanRemainPending": true,
  "PrintEnginePassesCancellation": false,
  "RawProcessTimeoutSeconds": null,
  "FilesLeft": 0
}
```

Sahte CUPS ve sahte süreç yürütücüsü kullanıldı; gerçek lp, yazıcı veya bulut çağrısı yapılmadı. Test sadece geçici dizin kullandı ve baskı dosyalarını temizledi.

Altı DEB'in SHA256 değeri mevcut SHA256SUMS ile eşleşti. Bu yalnız mevcut manifestle tutarlılığı gösterir; manifestin güvenilir yayıncı tarafından imzalandığını, paketlerin kaynakla birebir aynı derleme olduğunu veya zararsız olduklarını kanıtlamaz.

## Bağımlılık denetiminin sınırı

Manifestlerde System.Text.Json/7.0.3 görünüyor; buna karşılık incelenen `artifacts/publish/linux-x64/Service/System.Text.Json.dll` dosyasının ürün sürümü 8.0.32. Bu nedenle manifestteki satır tek başına dağıtılan JSON DLL'inin savunmasız olduğunu göstermez. CVE-2024-30105 ayrıca DeserializeAsyncEnumerable kullanımına ilişkin; projede bu API'ye doğrudan çağrı saptanmadı. Bu CVE doğrulanmış proje açığı olarak raporlanmadı.

Birincil bildirim: https://github.com/dotnet/runtime/security/advisories/GHSA-hh2w-p6rv-4g7w

Tam bağımlılık/CVE denetimi için SDK bulunan ortamda restore ve NuGet audit çalıştırılmalı, self-contained çıktılarındaki gerçek yönetilen/native dosya sürümleriyle sonuçlar uzlaştırılmalı. Bu inceleme tüm üçüncü taraf DLL/SO'ları için güvenlik garantisi vermez.

## Olumlu kontroller

- Linux CUPS komutunda shell kapalı, argümanlar ArgumentList ile ayrı aktarılıyor; belge yolundan önce `--` var. İncelenen bu yolda shell komut enjeksiyonu saptanmadı.
- Bulut adresi HTTPS; sertifika doğrulamasını devre dışı bırakan bir callback saptanmadı. Saat tanılaması için HTTP isteği yapılması, login parolasının HTTP ile gönderildiği anlamına gelmiyor.
- Linux kimlik bilgileri için 0600/0700 ve yeni dosyada CreateNew/UnixCreateMode kullanılıyor; son yol için symlink reddi var.
- Yerel soket varsayılan olarak 0660 ve runtime dizini 0750; erişim gruba sınırlı.
- systemd servisi root olmayan hesap, NoNewPrivileges, ProtectSystem=strict ve ProtectHome ile çalışıyor.
- Kaynak metin aramasında doğrulanmış gerçek özel anahtar/erişim anahtarı sızıntısı bulunmadı; test örnekleri gerçek kimlik bilgisi sayılmadı.
