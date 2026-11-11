# Windows masaüstü sürümüyle davranış karşılaştırması

Karşılaştırılan kaynak: masaüstündeki `windows-print-agent-master (1)/windows-print-agent-master`, sürüm 1.9.4. Windows projesi değiştirilmedi ve Linux projesine eklenmedi.

## Baskı davranışı

| Davranış | Linux karşılığı |
|---|---|
| `print:job` gelir, sonuç aynı Socket.IO callback ile döner | Aynı sözleşme; gerçek Socket.IO istemcisi ve sahte taşıyıcı testleri |
| Aynı iş kimliği tekrar gönderilebilir | Kalıcı iş defteri ve tekrar engeli yok |
| Yazıcı adı boşluk/büyük-küçük harf farkıyla eşleşir | Yerel CUPS adını çözümleyerek gönderir |
| Tüm yazıcılar aynı baskı kilidini paylaşır | Tek `SemaphoreSlim`; en fazla 30 saniye kuyruk bekleme |
| PDF baskısında yazdırma süresi kuyruk beklemesinden ayrıdır | Kilit alındıktan sonra varsayılan 60 saniye |
| Uygulama ayrıca kopya veya belge boyutu kotası koymaz | 100 kopya ve 20 MiB kotaları kaldırıldı; yazıcı kapasite bilgisi iş reddetmek için kullanılmaz |
| PDF dışındaki belgeler RAW gönderilir | Tür izin listesi kaldırıldı; RAW verisi CUPS'a filtrelenmeden gönderilir |
| RAW için sıfır/negatif kopya bir kabul edilir | Her baskıda en az bir kopya |
| Eksik belge türü PDF kabul edilir | Eksik veya null tür PDF |
| İş kimliği baskının zorunlu şartı değildir | Eksik/boş kimlik baskıyı durdurmaz |
| İşler/geçmiş ve hatalar arayüzde görünür | Bellekte son 100 iş; çözülemeyen yazıcı ve bozuk Base64 hatası başarısız geçmiş kaydı üretir |

Windows Spooler/SumatraPDF yerine Linux'ta CUPS ve `lp` kullanılır. Geçersiz CUPS seçenekleri ve kontrol karakterleri yerelde reddedilir; bunlar işletim sistemine özgü girdi kontrolleridir. Servis kapatılırken işler iptal edilebilir; RAW işlemleri Linux'ta da sonlandırılabilir süre bütçesi kullanır.

## Giriş ve yazıcılar

Mevcut HTTP oturum açma/yenileme/çıkış, Socket.IO kayıt ve durum mesajları korunur. Cihaz platformu `linux` olarak bildirilir. Yazıcılar Windows WMI yerine CUPS/IPP üzerinden keşfedilir; bulut yazıcı kimliklerini ve özellik değişikliklerini koruyan önceki düzeltmeler geri alınmaz. Windows yazıcı bildirimleri yerine mevcut Linux yenileme döngüsü kullanılır; donanım algılama gecikmesinin birebir aynı olduğu iddia edilmez.

## Açık farklar ve doğrulama sınırı

- 11 Kasım 2026 tarihinde `GET https://nuxprint.com/api/agent/version` yanıtı `latestVersion=1.9.6`, dolu Windows indirme adresi ve boş Linux adresi döndürdü. Linux otomatik güncellemesi tamamlanmış değildir; API kaynak kodu bu kontrol için gerekmedi.
- Windows ve Linux kurulum mekanizmaları farklıdır; Linux DEB paketini kullanır.
- Gerçek Linux yazıcısında uçtan uca baskı denenmedi. Başarılı CUPS gönderimi, kağıdın çıktığını kanıtlamaz.
- Testler mesaj sözleşmesini, dokuz eşzamanlı gelen işin işleyiciye ulaşmasını, eski belge/kopya sınırlarının kalkmasını, varsayılanları ve kuyruk/yazdırma sürelerinin ayrılmasını doğrular.

Bu karşılaştırma tüm platform davranışlarının birebir aynı olduğu iddiası değildir; baskı akışındaki fazladan uygulama kurallarını kaldırır ve kalan platform farklarını açıkça kaydeder.
