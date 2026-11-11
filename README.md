# NuxPrint Linux Desktop Agent

Bu proje yalnızca Linux masaüstü bilgisayarlarında çalışan NuxPrint yazdırma ajanıdır.
Web sitesi veya internet sunucusu bu projenin parçası değildir.

## Çalışma mantığı

1. API bağlantısı üzerinden baskı işi gelir.
2. Ajan işi, belgeyi ve hedef yazıcıyı doğrular.
3. Bilgisayarın CUPS yazdırma sistemi işi USB veya yerel ağ üzerinden yazıcıya gönderir.
4. Sonuç API bağlantısı üzerinden geri bildirilir.

Yazıcıya bağlanan kullanıcının bilgisayarıdır. API sunucusu fiziksel yazıcıya doğrudan bağlanmaz.
CUPS'ın işi kabul etmesi, kağıdın fiziksel olarak basıldığını garanti etmez.
Hesaba ait dakika, saat veya hafta kotalarını API yönetir; ajan ayrıca kullanım kotası uygulamaz.
Geçersiz belge, desteklenmeyen seçenek ve işlem zaman aşımı kontrolleri korunur.
Ajan kalıcı iş kayıt defteri tutmaz; aynı iş yeniden gönderilirse yeniden yazdırmayı dener.
İşin tekrar gönderilip gönderilmeyeceğini API yönetir.
Windows masaüstü sürümüyle aynı şekilde yerel kopya/belge boyutu kotası yoktur.
PDF dışındaki belge türleri RAW olarak gönderilir; sıfır veya negatif kopya sayısı bir kabul edilir.
Kuyruk beklemesi en fazla 30 saniyedir; 60 saniyelik yazdırma süresi kilit alındıktan sonra başlar.
Linux güncelleme adresi API’de henüz bulunmadığından otomatik güncelleme hazır değildir.

## Bileşenler

- Masaüstü arayüzü: hesapla giriş ve yazıcı yönetimi; C#/.NET 8 ve Avalonia.
- Arka plan servisi: API bağlantısı, iş doğrulama, CUPS'a gönderme ve sonuç bildirimi.
- Yerel iletişim: masaüstü arayüzü ve servis arasında Unix socket.
- CLI: aynı yerel servise erişen yardımcı komut satırı aracı.

Masaüstü penceresi kapansa da arka plan servisi çalışabilir. Bu servis masaüstü ajanının parçasıdır.
Windows projesi ve referans kaynaklarına derleme bağımlılığı yoktur.

## Kurulum

Hedef sistemler Ubuntu 22.04/24.04, Debian 12 ve uygun Raspberry Pi OS sürümleridir.
Her Linux dağıtımı için destek iddiası yoktur. Gerçek Linux cihazında kurulum ve fiziksel baskı testi ayrıca yapılmalıdır.

`artifacts/deb/` içindeki işletim sistemine uygun **desktop** paketini seçin:

| Dosya adındaki mimari | Kullanılacağı sistem |
|---|---|
| x64 | Intel/AMD 64 bit Linux |
| arm64 | ARM 64 bit Linux |
| arm32 | ARMv7 veya üstü 32 bit Linux |

ARMv6 desteklenmez. ARM32 için Debian/Raspberry Pi OS 12 veya Ubuntu 22.04 gerekir.
64 bit işlemci üzerinde 32 bit işletim sistemi kullanılıyorsa işletim sistemine uygun paket seçilir.

1. Yazıcıyı Linux'un yazıcı ayarlarından ekleyin ve çalıştığını kontrol edin.
2. Uygun `.deb` dosyasını paket yükleyicisiyle açıp kurun. Grafik paket yükleyicisi dağıtıma bağlıdır.
3. Uygulama menüsünden NuxPrint'i açın.
4. İlk kullanımda yerel erişim için sistem yöneticisi parolası istenebilir.
5. Web sitesinde oluşturduğunuz hesapla giriş yapın.

Terminalle kurulum alternatifi:

```bash
sudo apt install ./nuxprint-agent-desktop_1.9.5-3_x64.deb
```

Paket kendi .NET çalışma ortamını içerir; hedef bilgisayara ayrıca .NET SDK kurmak gerekmez.
Bağımlılıkların kurulması için internet gerekebilir.

## Kaynaktan derleme

Geliştirme bilgisayarında .NET 8 SDK ve Python 3 gerekir.

```bash
bash scripts/build.sh
bash scripts/publish.sh linux-x64
```

Diğer yayın hedefleri `linux-arm64` ve `linux-arm` şeklindedir.
Yayın betiği yalnız masaüstü kurulum paketi üretir.

## Proje düzeni

```text
src/PrintAgent.Common/   Veri modelleri ve iletişim sözleşmeleri
src/PrintAgent.Core/     Kimlik doğrulama ve API bağlantısı
src/PrintAgent.Linux/    CUPS, IPP ve yazdırma
src/PrintAgent.Service/  Arka plan servisi
src/PrintAgent.UI/       Linux masaüstü arayüzü
src/PrintAgent.CLI/      Yerel yönetim yardımcısı
tests/                  Linux ajanının testleri
packaging/              Linux kurulum dosyaları
scripts/                Derleme, yayın ve kurulum betikleri
docs/                   API sözleşmesi ve teknik notlar
artifacts/deb/          Hazır kurulum paketleri
```

`bin`, `obj` ve `artifacts/publish` geçici derleme çıktılarıdır. Kaynak kod değildir;
testler ve paketleme tamamlandıktan sonra temizlenebilir, gerektiğinde yeniden üretilir.

API sözleşmesi: [docs/api-contract.md](docs/api-contract.md).
