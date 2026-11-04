<p align="center"><img src="logo.png" width="96" alt="RankMySetup"></p>

# RankMySetup Scanner

**TR** · [rankmysetup.com](https://rankmysetup.com) için açık kaynak donanım tarayıcı. Bilgisayarının donanımını okur, sonucu RankMySetup'a gönderir; puanın ve rankın sitede görünür.

**EN** · Open-source hardware scanner for [rankmysetup.com](https://rankmysetup.com). It reads your PC's hardware and sends it to RankMySetup, where your score and rank are shown.

## Ne okur? / What it reads

İşlemci, ekran kartı ve ekran belleği, RAM modülleri ve hızları, anakart ve BIOS sürümü, laptop modeli, diskler (tür, boyut, sağlık), ekran çözünürlüğü ve yenileme hızı, monitör modeli, takılı mouse / klavye / kulaklığın model adı (kablosuz alıcılarda yalnızca marka), Windows sürümü, güç planı, Oyun Modu ve GPU zamanlama ayarı, laptoplarda pil sağlığı ve yaklaşık 20 saniyelik bir işlemci ve bellek testi.

CPU, graphics card and video memory, RAM modules and speeds, motherboard and BIOS version, laptop model, drives (type, size, health), display resolution and refresh rate, monitor model, the model names of connected mice / keyboards / headsets (brand only for wireless receivers), Windows version, power plan, Game Mode and GPU scheduling settings, battery health on laptops, plus a roughly 20-second CPU and memory test.

Veriler yalnızca sen taramayı başlattığında ve yalnızca https://rankmysetup.com adresine gönderilir. / Data is sent only when you start a scan, and only to https://rankmysetup.com.

## Ne okumaz? / What it never reads

Dosyaların, şifrelerin, tarama geçmişin, bilgisayar adın, kullanıcı adın ve seri numaraların okunmaz ve gönderilmez. Program hiçbir ayarı değiştirmez, sürücü yüklemez ve yönetici izni istemez.

Your files, passwords, browsing history, computer name, user name and serial numbers are never read or sent. The program changes no settings, installs no drivers and never asks for administrator rights.

## Kurulum / Installation

Kurulum yok. `RankMySetupScanner.exe` dosyasını çalıştır. Program kendini `%LOCALAPPDATA%\RankMySetup` klasörüne kopyalar ve sitedeki **Tara** butonunun programı açabilmesi için `rankmysetup://` adresini kullanıcı hesabına kaydeder.

No installer. Run `RankMySetupScanner.exe`. It copies itself to `%LOCALAPPDATA%\RankMySetup` and registers the `rankmysetup://` link for your user account so the **Scan** button on the site can open it.

**Kaldırma / Uninstall:** `%LOCALAPPDATA%\RankMySetup` klasörünü ve `HKEY_CURRENT_USER\Software\Classes\rankmysetup` kaydını sil.

## Derleme / Build

Gereksinim: .NET SDK (Windows). / Requires the .NET SDK on Windows.

```
dotnet build RankMySetupScanner/RankMySetupScanner.csproj -c Release
```

Sonuç / Output: .NET Framework 4.8 hedefli tek bir `.exe`; Windows 10 ve 11'de ek bir şey kurmadan çalışır.

## Kod imzalama / Code signing

[CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md)

## Lisans / License

[GPL-3.0](LICENSE). Bu kodu kullanan ya da değiştiren herkes kendi sürümünü de aynı lisansla açık yayınlamak zorundadır. / Anyone who uses or modifies this code must publish their version under the same license.

## Sürümler / Changelog

- **1.2** · Daha uzun ve tutarlı performans testi (aynı bilgisayardaki yavaşlamaları tespit etmek için); güç planı, Oyun Modu, GPU zamanlama ve pil sağlığı okunur. · Longer, consistent performance test (to detect slowdowns on the same PC); reads power plan, Game Mode, GPU scheduling and battery health.
- **1.0.1** · Program açılınca siteye haber verir; site böylece programın kurulu olup olmadığını anlar. · Notifies the site when it opens so the site knows it’s installed.
- **1.0.0** · Yeni arayüz (sitedeki tasarımla aynı), Türkçe/İngilizce (Windows diline göre), mouse/klavye/kulaklık algılama, kablosuz alıcı markası, laptop model adı, rapor programın dilinde açılır. · New UI matching the website, Turkish/English based on the Windows language, mouse/keyboard/headset detection, wireless receiver brand, laptop model name, report opens in the app's language.
- **0.5.0** · 100 üzerinden skor. · Score out of 100.
- **0.4.0** · .NET Framework 4.8, kurulum yok, yönetici izni istemez. · .NET Framework 4.8, no install, no admin rights.
