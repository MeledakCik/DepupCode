# Depup Code

Depup Code adalah IDE desktop Windows berbasis WPF dan .NET 8 untuk mengedit proyek, bekerja dengan PHP/Laravel, serta menggunakan fitur terminal, preview, Git, chat AI, ekstensi, dan integrasi deployment.

## Fitur utama

- Workspace, file explorer, editor, pencarian, terminal, dan preview.
- Server pengembangan Apache + PHP lokal yang dikelola aplikasi, tanpa perlu XAMPP.
- Dukungan alur Laravel dan PHP biasa, dengan konfigurasi server yang dibuat di luar folder proyek.
- Integrasi Git/GitHub, provider AI, marketplace ekstensi, dan layanan deployment yang tersedia di aplikasi.
- Task VS Code untuk membangun dan menjalankan aplikasi desktop.

## Menjalankan rilis Windows

1. Unduh `DepupCode-v<versi>-win-x64.zip` dari bagian **Releases** di GitHub.
2. Ekstrak seluruh ZIP ke folder yang dapat ditulis; jangan menjalankan aplikasi langsung dari dalam ZIP.
3. Jalankan `DepupCode.exe`.

Rilis dibuat sebagai aplikasi **self-contained** sehingga .NET Desktop Runtime tidak perlu dipasang terpisah. Aplikasi menggunakan Microsoft Edge WebView2; Windows yang belum memiliki WebView2 Evergreen Runtime perlu memasangnya terlebih dahulu. Jangan memindahkan atau menghapus file pendamping di folder rilis.

## Persyaratan pengembangan

- Windows 10/11 x64.
- .NET 8 SDK dan PowerShell untuk build dari source.
- Microsoft Edge WebView2 Evergreen Runtime untuk menjalankan UI aplikasi.
- Koneksi internet untuk restore paket yang belum ada di cache NuGet dan untuk fitur/provider online.

## Build dan tes

Jalankan dari root repository:

```powershell
dotnet restore .\DepupCode.sln
dotnet build .\DepupCode.sln -c Debug
dotnet test .\tests\DepupCode.Tests\DepupCode.Tests.csproj
```

Di VS Code, buka root repository dan jalankan **Terminal → Run Task → Depup Code: Run (PHP/Laravel)**. Task tersebut membangun dan membuka aplikasi. Setelah aplikasi terbuka, server PHP dimulai melalui **PHP: Start Local Server** atau tombol status PHP.

## Membuat paket rilis

Gunakan script berikut dari root repository:

```powershell
.\build.ps1
```

Script hanya menerima target publik `win-x64`. Script melakukan restore runtime pack, build Release, publish self-contained, memeriksa file aplikasi/runtime wajib, lalu membuat dan memvalidasi arsip rilis dari hasil publish (bukan dari source atau folder Debug).

- Hasil publish: `DepupCode\bin\Release\net8.0-windows\win-x64\publish\`
- Paket rilis: `artifacts\release\DepupCode-v2.5.0-win-x64.zip`
- ZIP berisi seluruh output publish, `README.md`, dan kebijakan privasi desktop `PRIVACY.md`.

Nama versi ZIP diambil dari properti `<Version>` di `DepupCode\DepupCode.csproj`. Ubah versi di file tersebut sebelum membuat rilis berikutnya. Folder `artifacts` hanya berisi hasil lokal dan tidak perlu di-commit. Jalankan tes di atas sebelum mengunggah ZIP ke GitHub Releases. Untuk target lain, script sengaja berhenti karena konfigurasi runtime/native dependency belum diverifikasi.

## Server PHP/Laravel lokal

Runtime yang didukung saat ini adalah **PHP 8.4.27** dan **Apache HTTP Server 2.4.66**. Pengguna mengunduh runtime dari **Settings → Local Server → Unduh Runtime**. Pemasangan pertama membutuhkan internet dan mengunduh sekitar **44.81 MB** total (33.82 MB PHP + 10.99 MB Apache; angka dapat berubah). File runtime diverifikasi menggunakan SHA-256 dan disimpan di `%LOCALAPPDATA%\DepupCode\runtimes` untuk pemakaian berikutnya.

Untuk menjalankan proyek:

1. Buka folder proyek PHP/Laravel di Depup Code.
2. Jika runtime belum terpasang, pilih **Unduh Runtime** dan tunggu sampai status siap.
3. Pilih **PHP: Start Local Server** dari Command Palette atau tombol status PHP.
4. Buka alamat preview/browser yang ditampilkan. Hentikan server dengan **PHP: Stop Local Server**.

Server hanya untuk development lokal dan bind ke `127.0.0.1` melalui HTTP. Aplikasi menyertakan DLL Microsoft Visual C++ 2015–2022 x64 bertanda tangan valid untuk runtime PHP; pertahankan seluruh file rilis. Runtime lokal tidak dipasang sebagai Windows service dan tidak mengubah PATH global.

### Batasan server lokal

- Hanya PHP 8.4.27 yang disediakan; versi PHP lain belum didukung.
- MySQL dan phpMyAdmin tidak disertakan. Sediakan database secara terpisah jika aplikasi membutuhkannya.
- HTTPS lokal belum disediakan.
- Server loopback bukan server publik dan tidak ditujukan untuk deployment production.
- Runtime PHP/Apache diunduh pada pemasangan pertama; penggunaan selanjutnya dapat dilakukan offline setelah unduhan berhasil.

## Privasi dan keamanan

- Kebijakan privasi aplikasi desktop: [PRIVACY.md](PRIVACY.md).
- Kebijakan privasi demo web: [Web/privacy.html](Web/privacy.html).
- Jangan memasukkan kredensial produksi ke demo web. Demo tersebut menyimpan data browser lokal tanpa enkripsi; kebijakan web menjelaskan koneksi eksternal dan batasan data.
- Periksa kebijakan penyedia pihak ketiga sebelum menghubungkan akun atau mengirim data melalui fitur online.

## Struktur repository

- `DepupCode/` — aplikasi WPF, layanan, aset, dan konfigurasi runtime.
- `tests/DepupCode.Tests/` — tes untuk layanan dan perilaku workspace.
- `Web/` — prototipe web dan kebijakan privasi web.
- `build.ps1` — build, publish, verifikasi, dan pengemasan rilis Windows x64.

## Kontributor

- Depup
- OpenAI Codex
- Google Gemini
- OpenCode
- GitHub Copilot
