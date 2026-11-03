<p align="center">
  <img src="DepupCode/Assets/depup.png" alt="Logo Depup Code" width="112" />
</p>

<h1 align="center">Depup Code</h1>

<p align="center">IDE desktop Windows untuk PHP/Laravel, AI-assisted coding, Git, dan workflow deployment.</p>

<p align="center">
	<a href="https://github.com/MeledakCik/DepupCode/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/MeledakCik/DepupCode?style=flat-square&label=stable" /></a>
  <img alt="Windows x64" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4?style=flat-square" />
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square" />
	<a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-3DA639?style=flat-square" /></a>
</p>

<p align="center">
	<a href="#unduh-dan-instal"><strong>Unduh untuk Windows</strong></a> ·
  <a href="#fitur-utama">Fitur</a> ·
  <a href="#akun-depup-cloud">Akun</a> ·
  <a href="#dokumentasi-dan-dukungan">Dokumentasi</a> ·
  <a href="#kontributor">Kontributor</a>
</p>

Depup Code adalah IDE desktop Windows berbasis WPF dan .NET 8 untuk membangun dan mengelola proyek dari satu aplikasi: editor, terminal, AI, Git, ekstensi, server PHP lokal, dan integrasi deployment.

## Fitur utama

- **Workspace dan editor:** file explorer, tab, pencarian/ganti, formatter, minimap, navigasi, dan pengaturan per-workspace.
- **Terminal dan preview:** terminal PowerShell terintegrasi, panel output/diagnostics, preview HTML/WebView2, dan browser untuk server lokal.
- **AI chat dan agent:** konteks workspace, mode Ask/Plan/Agent, operasi file melalui tool dengan persetujuan pengguna, dan provider yang dapat dikonfigurasi.
- **Source control:** status repository, branch, diff, commit, push, clone, serta operasi repository/GitHub yang membutuhkan autentikasi.
- **PHP/Laravel lokal:** runtime Apache dan PHP yang dikelola aplikasi, deteksi Laravel/PHP biasa, log, preview, dan fallback server yang diberi label.
- **Integrasi cloud:** deployment/provider yang tersedia untuk Depup Cloud, Vercel, Cloudflare Pages, dan Railway; juga pengelolaan domain, variabel environment, dan Link Gaib tunnel sesuai konfigurasi akun.
- **Extensions:** pencarian/pemasangan dari Open VSX, termasuk dukungan theme dan snippet; tidak semua ekstensi VS Code atau tipe aktivasi didukung.
- **Debug dan language server:** UI breakpoint/stepping/stack/variables serta diagnostics. Adapter dan language server eksternal harus dikonfigurasi terpisah.
- **Mode offline:** memakai data lokal/cache; sinkronisasi dan operasi cloud memerlukan koneksi internet.

## Unduh dan instal

<p align="center">
  <a href="https://github.com/MeledakCik/DepupCode/releases/latest"><strong>Unduh Depup Code · Setup Windows x64</strong></a>
  <br />
  <sub><a href="https://github.com/MeledakCik/DepupCode/releases">Semua rilis</a> · satu file setup · Windows 10/11 x64</sub>
</p>

Paket yang disarankan adalah satu file `DepupCode-Setup-v<versi>-win-x64.exe`. Setup memasang aplikasi untuk akun Windows saat ini tanpa meminta hak Administrator, membuat shortcut Start Menu, menawarkan shortcut Desktop, mendaftarkan `depup://` untuk login cloud, dan menambahkan menu **Open with Depup Code** pada folder. Wizard menampilkan ringkasan privasi sebelum instalasi; persetujuan kebijakan tetap dilakukan di dalam aplikasi pada pembukaan pertama.

1. Unduh `DepupCode-Setup-v<versi>-win-x64.exe` dari halaman Releases, lalu jalankan. Jika tersedia, verifikasi file `.sha256` di sampingnya.
2. Ikuti wizard (English/Indonesia). Setup tidak memerlukan Administrator dan tidak mengubah folder project.
3. Jalankan Depup Code. Pembukaan pertama menampilkan **kebijakan privasi**; setelah dibaca dan disetujui, wizard **Selamat datang** meminta folder workspace, tema, dan ukuran font editor. Folder boleh dilewati dan dibuka kapan saja dari Welcome screen. Tidak ada project yang dipindahkan otomatis.
4. Microsoft Edge WebView2 Evergreen Runtime harus tersedia; Windows 10/11 yang diperbarui biasanya sudah memilikinya dan setup akan menampilkan peringatan jika tidak ditemukan. .NET Desktop Runtime tidak perlu dipasang terpisah karena paket aplikasi self-contained.

ZIP portable tetap tersedia sebagai alternatif untuk penggunaan tanpa instalasi. Ekstrak seluruh ZIP ke folder lokal dan jangan menjalankan `DepupCode.exe` langsung dari dalam arsip. Setup satu-file jauh lebih nyaman untuk pengguna biasa karena tidak perlu mengelola ratusan file runtime.

Pada setiap paket baru, isi lengkap kebijakan privasi desktop ditampilkan di aplikasi. Pengakuan bahwa kebijakan telah dibaca tersimpan per akun Windows dan dapat dihapus kembali dari Settings. Pengakuan ini bukan persetujuan otomatis untuk mengirimkan data melalui AI atau layanan cloud; fitur tersebut hanya mengirim data ketika Anda menjalankan aksi terkait.

Paket rilis tidak menyertakan pengaturan, token, ataupun profil akun pengembang. Pengaturan aplikasi disimpan terpisah untuk setiap akun Windows; jika Anda pernah masuk di aplikasi pada PC yang sama, sesi tersebut tetap tersimpan saat aplikasi diperbarui.

### Akun Depup Cloud

Pilih **Settings → Depup Cloud → Masuk dengan Google** untuk memulai login melalui browser. Untuk keluar dari aplikasi ini, pilih **Keluar dari akun** di bagian yang sama. Keluar menghapus sesi Depup Cloud lokal, tetapi tidak menghapus data cloud atau akun Google yang sedang masuk di browser. Proyek lokal dan pengaturan lain tetap tersimpan.

## AI chat dan agent

Mode **Ask** digunakan untuk tanya-jawab, **Plan** untuk analisis/perencanaan, dan **Agent** untuk bantuan berbasis tool pada workspace. Tindakan yang dapat mengubah file atau menjalankan tool memerlukan persetujuan sesuai alurnya. Provider bawaan mencakup beberapa pilihan **gratis atau lokal** (OpenRouter `openrouter/free`, Groq, GitHub Models, Gemini, Ollama, LM Studio) serta OpenAI dan Mistral; provider OpenAI-compatible lain dapat **ditambahkan manual** (nama, URL API, model) dan API key disimpan terpisah per provider. Tombol **Muat daftar model** dan **Tes koneksi** ada di pengaturan AI. Lihat [docs/AI-PROVIDERS.md](docs/AI-PROVIDERS.md). Layanan online memerlukan koneksi dan autentikasi/provider yang sesuai; kuota gratis ditentukan provider. Hasil AI tetap perlu ditinjau sebelum digunakan.

## Persyaratan pengembangan

- Windows 10/11 x64.
- .NET 8 SDK dan PowerShell untuk build dari source.
- Microsoft Edge WebView2 Evergreen Runtime untuk menjalankan UI aplikasi.
- Koneksi internet untuk restore paket yang belum ada di cache NuGet dan untuk fitur/provider online.

Repository ini saat ini hanya mendistribusikan aplikasi Windows x64. Task VS Code tersedia untuk membuka aplikasi dari workspace, tetapi build/test tetap dapat dijalankan melalui perintah .NET di bawah.

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
- Paket rilis: `artifacts\release\DepupCode-v2.5.3-win-x64.zip`
- ZIP berisi seluruh output publish, `README.md`, dan kebijakan privasi desktop `PRIVACY.md`.

Untuk membuat setup satu-file, instal Inno Setup 6 (`winget install JRSoftware.InnoSetup`) lalu jalankan:

```powershell
.\build-installer.ps1
```

Script menghasilkan `artifacts\release\DepupCode-Setup-v<versi>-win-x64.exe` beserta file `.sha256` dari hasil publish Release. Setup menyediakan wizard berbahasa Inggris/Indonesia, halaman ringkasan privasi, pendaftaran `depup://`, menu **Open with Depup Code** untuk folder, shortcut Start Menu/Desktop, uninstall, dan pemasangan default ke `Program Files\Depup Code` dengan hak Administrator. Untuk rilis publik, tanda tangani payload dan installer sekaligus:

```powershell
.\build-installer.ps1 -CertificatePath .\cert\depup-codesign.pfx -CertificatePassword "<password>"
```

Script akan mencari `signtool.exe` dari Windows SDK dan menandatangani `DepupCode.exe` sebelum packaging, lalu menandatangani setup dengan timestamp.

**Microsoft Store:** paket Store dibuat terpisah sebagai MSIX. Setelah publish Release, jalankan:

```powershell
.\build-msix.ps1 -IdentityName "<Package/Identity Name dari Partner Center>" -Publisher "<Publisher dari Partner Center>"
```

Script menyusun layout MSIX, membuat logo Store dari aset aplikasi, memvalidasi manifest, dan menghasilkan `artifacts\release\DepupCode-v<versi>-win-x64.msix`. Tambahkan `-CertificatePath` hanya untuk pengujian instalasi lokal; unggahan Store ditandatangani oleh Partner Center. Nilai identitas tidak boleh ditebak: paket harus memakai nilai yang dipesan di Partner Center. Lihat [checklist Store](Packaging/Store/STORE_SUBMISSION.md) untuk audit kebijakan, WACK, dan batasan yang masih harus diselesaikan sebelum sertifikasi dapat diharapkan.

Nama versi diambil dari properti `<Version>` di `DepupCode\DepupCode.csproj`. Ubah versi di file tersebut sebelum membuat rilis berikutnya. Folder `artifacts` hanya berisi hasil lokal dan tidak perlu di-commit. Jalankan tes di atas sebelum mengunggah setup ke GitHub Releases. Untuk target lain, script sengaja berhenti karena konfigurasi runtime/native dependency belum diverifikasi.

Jalankan `build-installer.ps1` setelah `build.ps1` berhasil. Jika menyertakan setup dalam rilis GitHub, pastikan aset ditandatangani, diuji pada mesin Windows bersih, dan link unduh README diperbarui ke URL aset yang benar.

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
- `build-installer.ps1` + `build-installer.iss` — pembuatan setup satu file (Inno Setup).
- `build-msix.ps1` + `Packaging/Store/` — paket MSIX dan checklist Microsoft Store.
- `CHANGELOG.md` — catatan perubahan per versi; `LICENSE` — lisensi MIT.

## Dokumentasi dan dukungan

- Kebijakan privasi aplikasi desktop: [PRIVACY.md](PRIVACY.md).
- Kebijakan privasi demo web: [Web/privacy.html](Web/privacy.html).
- Rilis dan catatan perubahan: [GitHub Releases](https://github.com/MeledakCik/DepupCode/releases) dan [CHANGELOG.md](CHANGELOG.md).
- Untuk melaporkan bug atau mengusulkan perubahan, buat issue di [repository GitHub](https://github.com/MeledakCik/DepupCode/issues).

## Lisensi

Kode sumber Depup Code dirilis di bawah [MIT License](LICENSE). Anda bebas
menggunakan, mengubah, dan mendistribusikan ulang kode ini dengan tetap
mencantumkan pemberitahuan hak cipta dan lisensi. Perangkat lunak diberikan
tanpa jaminan apa pun.

Aset pihak ketiga yang dibundel atau diunduh aplikasi (misalnya runtime PHP,
Apache, Prettier, Ruff, dan WebView2) tetap mengikuti lisensi masing-masing.


## Kontributor

- Depup
- OpenAI Codex
- Google Gemini
- OpenCode
- GitHub Copilot
