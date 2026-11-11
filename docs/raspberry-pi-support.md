# Raspberry Pi — ARM paketleri

## Değişiklik

Servis, CLI ve Avalonia arayüzü artık `linux-arm64` ve `linux-arm` hedeflerine yayımlanır.
Üç uygulama da kendi .NET 8 çalışma ortamını içerir.
Üretici her hedef için Desktop ve Server DEB paketleri oluşturur.
Mevcut Linux x64 hedefi korunur.

Bulut API, Socket.IO, kimlik doğrulama, baskı sırası ve CUPS iş mantığı değiştirilmedi.
Bu kod yönetilen .NET kodudur ve x64'e bağlı veri yapısı içermez.
Linux süreç kilidindeki `flock(int, int)` çağrısı ARM için de aynı arayüzü kullanır.
Yeni CPU hedefleri, uygulamanın işletim sistemi bağımlılıklarında veya protokolünde değişiklik gerektirmedi.

## Değişen dosyalar

- `packaging/deb/build_deb.py`: RID → Debian mimarisi eşleştirmesi, ELF sınıfı/makine/byte sırası ve ARM hard-float kontrolü.
- `packaging/deb/verify_deb.py`: paket metadatası, dosya adı ve tüm yerel ELF dosyalarının bağımsız mimari kontrolü.
- `packaging/deb/preinst`: ARMv6 ve desteklenmeyen ARM32 dağıtımlarını kurulumdan önce reddetme.
- `scripts/publish.sh`: x64, ARM64 ve ARM32 yayın hedefleri.
- `scripts/install.sh`: işletim sistemi ile paket mimarisinin eşleşmesini denetleme.
- `scripts/build.sh`, `tests/Packaging/test_deb.py`: paket sınırında 4 regresyon testi.
- `README.md`: paket seçimi, Pi komutları, kısa cümleler ve güncel sonuçlar.
- `.gitattributes`, `.gitignore`: yeni dizinde Linux satır sonları ve Python geçici dosyaları.

Kullanıcı tarafından istenen taşıma Git'e kaydedilir: `linux-print-agent/` → `linux-print-agent-main/`.
Masaüstündeki mevcut `referance/` ve taşınan `reference/` kaynakları değiştirilmez.
README'nin üstteki iki büyük başlığı korunur.

## Mimari seçimi

| `dpkg --print-architecture` | RID | ELF | Kullanım |
|---|---|---|---|
| `arm64` | `linux-arm64` | ELF64, AArch64 (183) | Raspberry Pi OS 64 bit Bookworm/Trixie |
| `armhf` | `linux-arm` | ELF32, ARM (40), hard-float | Raspberry Pi OS 32 bit Bookworm, ARMv7+ |
| `amd64` | `linux-x64` | ELF64, x86-64 (62) | Mevcut x64 hedefi |

64 bit işlemcide 32 bit işletim sistemi çalışıyorsa `armhf` seçilir.
Paket seçimi yalnız `uname -m` sonucuna göre yapılmaz.
Pi 2 ARMv7 modelleri, Pi 3/4/5 ve Zero 2 W hedeflenir.
Özgün Pi, Pi Zero ve Zero W, ARMv6 nedeniyle desteklenmez.
32 bit Debian/Raspberry Pi OS 13 Trixie, .NET 8 ARM32 time64 ABI kısıtı nedeniyle desteklenmez.
Bu sistemlerde ARM64 işletim sistemi veya 32 bit Bookworm kullanılır.

Desktop için X11 veya XWayland gerekir; paket XWayland bağımlılığını içerir.
Pi OS Lite için Server paketi kullanılır.
Ekransız DRM/framebuffer arayüzü veya yeni ekran akışı eklenmedi.

## Paketler

`artifacts/deb/` altında her mimari için şu dosyalar bulunur:

```text
nuxprint-agent-desktop_1.9.4-1_arm64.deb
nuxprint-agent-server_1.9.4-1_arm64.deb
nuxprint-agent-desktop_1.9.4-1_armhf.deb
nuxprint-agent-server_1.9.4-1_armhf.deb
nuxprint-agent-desktop_1.9.4-1_amd64.deb
nuxprint-agent-server_1.9.4-1_amd64.deb
```

Paketler servis/CLI apphost'larını, CoreCLR'ı ve Desktop için Skia/HarfBuzz kütüphanelerini içerir.
Üretici tüm ELF dosyalarını kontrol eder; farklı mimaride kütüphane içeren yayını reddeder.
Server paketi Avalonia ve arayüz içermez.
Paket bağımlılıkları CUPS, ICU, OpenSSL ve gerekli Linux kütüphanelerini kurar.
ARM native dosyalarının `DT_NEEDED` girdileri de incelendi; çalışma ortamı ve çizim kütüphaneleri doğru ARM yükleyicisini kullanır.
Tam dosya bütünlük değerleri `artifacts/deb/SHA256SUMS` içindedir.

## Doğrulama

- Release çözümü: 0 hata, 0 uyarı.
- .NET testleri: Core 46, Linux/Service 34, Desktop 9; **89/89 geçti**.
- TRX sayı denetimi: üç grubun tüm testleri yürütüldü.
- Python paketleme testleri: **4/4 geçti**.
- Testler üç mimaride Desktop/Server çıktısını, yanlış native mimariyi, ARM soft-float ABI'sini ve eksik UI kütüphanesini kapsar.
- ARM64 ve ARM32: Service/CLI/UI self-contained publish tamamlandı.
- Her hedefte 47 native ELF dosyası doğrulandı.
- Altı DEB için ar/tar, metadata, dosya yerleşimi, script izinleri ve native mimari denetimleri geçti.
- Bash scriptlerinin sözdizimi denetlendi.
- Önceki Windows Uygulama Denetimi engeli bu normal derleme/test çalışmasında oluşmadı; güvenlik ayarı değiştirilmedi.

Gerçek Pi kurulumu, X11/XWayland, Socket.IO sunucusu ve fiziksel yazdırma kabul testini kullanıcı yapacaktır.
Windows üzerinde ARM kodu çalıştırıldığı iddia edilmez.

## Teknik kaynaklar

- [Microsoft: ARM cihazlara .NET yayını](https://learn.microsoft.com/en-us/dotnet/iot/deployment): ARM64/ARM32 RID seçimi ve self-contained yayın.
- [Microsoft: .NET 8 işletim sistemi ve ARM32 ABI koşulları](https://github.com/dotnet/core/blob/main/release-notes/8.0/supported-os.md): ARM32 time64 kısıtı.
- [Avalonia: Embedded Linux yayını](https://docs.avaloniaui.net/docs/deployment/embedded-linux): `linux-arm` ve `linux-arm64` hedefleri.

## Standards

Bağımsız incelemede giderilmesi gereken hata veya önemli tasarım bulgusu bulunmadı.

## Spec

Bağımsız incelemede eksik veya hatalı uygulanmış gereksinim bulunmadı.
İki inceleme de C# kaynaklarının yalnız taşındığını ve bulut/baskı iş mantığının korunmasını doğruladı.

Standards: 0 bulgu. Spec: 0 bulgu.
