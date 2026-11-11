# Terminal gerektirmeyen ilk açılış — 11 Kasım 2026

Paket sürümü: **1.9.5-2**. Uygulamanın web API giriş akışı ve C# kaynakları değiştirilmedi.

## Kullanıcı akışı

Desteklenen Ubuntu/Debian grafik masaüstünde uygun Desktop DEB paketi grafik paket kurucuyla kurulur. Uygulama menüsünden NuxPrint açılır. İlk erişimde gerekirse sistemin yönetici onayı alınır; ardından mevcut web hesabıyla uygulamaya giriş yapılır. Elle grup komutu veya oturumu kapatıp açma adımı yoktur. Daha önce yetkilendirilmiş yerel kullanıcı tekrar yönetici onayı almadan açabilir.

Kurulumda alınan yönetici parolası, ilk yerel erişim onayı ve web hesabı girişi ayrı işlemlerdir. Web hesabı servisi değiştirilmedi.

## Yetki sınırı

- Socket izinleri 0660 ve özel servis grubu korunur; herkese erişim açılmaz.
- Polkit yalnız etkin yerel oturumda yönetici doğrulaması ister. İlk Python argümanı sabit, root tarafından kurulan yetki yardımcısının tam yoluyla eşleşir.
- Yardımcı kullanıcı adı argümanı kabul etmez; yalnız pkexec tarafından verilen PKEXEC_UID hesabını sabit nuxprint-agent grubuna ekler.
- Uygulama root olarak çalıştırılmaz. Yeni grup üyeliği sg ile yeni istemci sürecine uygulanır; masaüstü oturumu yeniden başlatılmaz.
- Yetkisiz başka kullanıcıda otomatik başlangıç parola penceresi açmaz. Kullanıcının uygulama simgesine tıklaması gerekir.
- Web oturumu, parola ve token yardımcılara gönderilmez.

Polkit kimlik ve argüman eşleştirme davranışı: [resmi pkexec belgesi](https://polkit.pages.freedesktop.org/polkit/pkexec.1.html). Yeni grup altında komut başlatma: [Debian sg belgesi](https://manpages.debian.org/bookworm/login/sg.1.en.html).

## Paketler

| Paket | İşlemci / cihaz | Arayüz |
|---|---|---|
| Desktop amd64 | Intel/AMD 64 bit bilgisayar | Pencere ve menü simgesi |
| Server amd64 | Intel/AMD 64 bit sunucu | Komut satırı, pencere yok |
| Desktop arm64 | 64 bit ARM, uygun Raspberry Pi OS | Pencere ve menü simgesi |
| Server arm64 | 64 bit ARM sunucu | Komut satırı, pencere yok |
| Desktop arm32 | ARMv7 ve üzeri 32 bit ARM | Pencere ve menü simgesi |
| Server arm32 | ARMv7 ve üzeri 32 bit ARM | Komut satırı, pencere yok |

Bunlar altı farklı Linux dağıtımı değildir: üç işlemci mimarisi ve iki kullanım biçimidir. Aynı cihazda Desktop ve Server birlikte kurulmaz. Standart masaüstü kullanıcısı uygun işlemci mimarisindeki Desktop paketini seçer. ARM32 için mevcut Debian/Raspberry Pi OS 12 ve Ubuntu 22.04 sınırı korunur; ARMv6 desteklenmez.

Server paketleri bilinçli olarak grafik arayüz içermez. Grafik olarak kurulsalar bile uygulama kullanımı CLI ile yapılır. Yerel CLI ilk erişimi uygun etkin oturumda aynı yetkilendirme mekanizmasını kullanır. SSH/uzak oturumda yönetici tarafından erişim sağlanmalıdır.

## Doğrulama ve sınırlar

- 18 Python başlatma/yetki/paketleme testi geçti. Yetki verme testlerinde sistem kullanıcı/grup işlemleri taklit edildi; bu Windows bilgisayarındaki hesaplar değiştirilmedi.
- Yetki iptali, tekrar açılış, eski oturum, root UI reddi, otomatik başlangıç, kullanıcı argümanlarının güvenli taşınması ve yardımcıya sahte kullanıcı argümanı verilmesi denetlendi.
- Üç mimari için Service/CLI/UI yayınları uyarılar hata kabul edilerek derlendi; altı DEB üretildi ve doğrulandı.
- Altı gerçek paketteki launcher, yetki yardımcısı ve polkit policy dosyaları kaynaklarla birebir karşılaştırıldı.
- Hash listesi yenilendi. Eski -1 paketleri ve geçici derleme çıktıları kaldırıldı; altı güncel -2 paketi kaldı.
- Gerçek Linux masaüstünde grafik kurulum, polkit parola penceresi, sg grup geçişi, dpkg yükseltmesi ve fiziksel yazıcı testi bu ortamda yapılamadı. Bu son doğrulama hâlâ gereklidir.
- Grafik paket kurucusu ve polkit oturum ajanı bulunmayan minimal/headless sistemlerde Desktop için çift tıklama akışı beklenmemelidir. CUPS ve yazıcı sürücülerinin hazır olması gerekir.
