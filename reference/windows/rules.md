# Windows Print Agent — Teknik Özet

NuxPrint ismindeki uygulama api tarafından gelen yazdırma isteklerini ilgili yazıcıya gönderir.

## Mimari

- **Tek proje çözümü:** PrintAgent.Common (modeller/sabitler) + PrintAgent.UI (WPF uygulaması)
- **Singleton AgentService:** Tüm iş mantığı tek sınıfta (~850 satır) — auth, socket, WMI, print, heartbeat
- **MVVM:** CommunityToolkit.Mvvm ile MainViewModel, INotifyPropertyChanged ile PrinterInfo
- **Socket.IO:** `/agent` namespace, JWT auth, `print:job` dinleyici, `agent:register` + `printer:status-update` emitter
- **Yazdırma:** SumatraPDF CLI, orientation algılama, timeout koruması

## Gelecek Geliştirmelerde Dikkat Edilecekler

1. **AgentService büyüklüğü:** 850+ satır tek sınıf. Yeni özellik eklerken servis bölünmesi düşünülebilir (AuthService, PrinterService, SocketService, PrintService). Şimdilik çalışıyor ama büyüdükçe bakımı zorlaşır.

2. **PrinterInfo INotifyPropertyChanged:** UI binding'leri düzgün çalışması için property değişimlerinde bildirim gerekli. Yeni model eklerken UI'da gösterilecekse aynı pattern'i uygula.

3. **ObservableCollection güncellemesi:** Clear+Add yapmak flicker yaratır. Diff-based güncelleme pattern'ini kullan (SyncPrinters/SyncJobs örneklerindeki gibi).

4. **StateChanged debounce:** 100ms debounce var. Yeni OnStateChanged çağrısı eklerken bu debounce'un yeterli olduğundan emin ol.

5. **Socket.IO path:** Agent socket client'ında `path: '/api/socket.io'` kullanılmalı. Yeni socket client eklerken bunu unutma.

6. **Sürüm çıkma süreci:** Constants.cs + setup.iss'te versiyon güncelle → `dotnet publish` → `iscc setup.iss` → sunucuya upload → admin panelden sürüm bilgisi güncelle.

7. **Serilog entegre değil:** NuGet'te mevcut ama yapılandırılmamış. Dosyaya log yazmak için entegre edilebilir.

8. **DisposeSocket non-blocking:** Socket dispose Task.Run ile arka planda yapılıyor. Yeni dispose/cleanup kodu eklerken UI thread'i bloklamamaya dikkat et.
