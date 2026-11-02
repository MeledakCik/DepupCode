# Depup Code

Depup Code is a desktop IDE for Windows (WPF / .NET 8) focused on PHP and Laravel workflows without XAMPP, plus AI chat, extensions, deploy, and preview tooling.

## Contributors

- Depup
- OpenAI Codex
- Google Gemini
- OpenCode
- GitHub Copilot

## Build

```powershell
dotnet build .\DepupCode\DepupCode.csproj -c Debug
.\\build.ps1
```

## Server PHP lokal

Depup Code menyediakan satu runtime lokal: PHP **8.4.27** dan Apache HTTP Server **2.4.66**. Unduhan runtime dilakukan atas tindakan pengguna dari Settings dan membutuhkan koneksi internet pada pemasangan pertama (sekitar **44.81 MB** total; ukuran unduhan dapat berubah). Setelah terpasang, runtime disimpan lokal untuk penggunaan berikutnya.

Server hanya tersedia untuk pengembangan lokal melalui loopback HTTP. MySQL, phpMyAdmin, dan HTTPS lokal belum disediakan; pasang/konfigurasikan database secara terpisah bila proyek membutuhkannya. PHP/Apache menggunakan build Windows VS17 dan memerlukan Microsoft Visual C++ 2015–2022 x64 runtime; biasanya runtime tersebut sudah tersedia di Windows.

Buka folder proyek PHP di Depup Code, lalu pilih **PHP: Start Local Server** atau tombol status PHP. Dari VS Code, jalankan task **Depup Code: Run (PHP/Laravel)** melalui **Terminal → Run Task** untuk membuka aplikasinya. Server lokal ini tidak dimaksudkan untuk deployment publik.

Kebijakan privasi aplikasi desktop: [PRIVACY.md](PRIVACY.md). Kebijakan demo web: [Web/privacy.html](Web/privacy.html).
