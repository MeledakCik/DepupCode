# Changelog

Semua perubahan penting pada Depup Code dicatat di file ini.

Format mengikuti [Keep a Changelog](https://keepachangelog.com/id/1.1.0/),
dan penomoran versi mengikuti [Semantic Versioning](https://semver.org/lang/id/).
Versi rilis diambil dari properti `<Version>` pada `DepupCode/DepupCode.csproj`.

## [Unreleased]

Belum ada perubahan yang direncanakan untuk rilis berikutnya.

## [2.5.1] - 2026-11-03

Rilis ini berfokus pada pengalaman pemasangan: satu file setup, onboarding
pertama kali, dan kesiapan paket Microsoft Store.

### Ditambahkan

- **Setup satu file** berbasis Inno Setup 6 (`DepupCode-Setup-v2.5.1-win-x64.exe`).
  Instalasi per-user tanpa hak Administrator, wizard English/Indonesia, halaman
  ringkasan privasi sebelum instalasi, shortcut Start Menu, opsi shortcut Desktop,
  entri uninstall, dan file checksum `.sha256`.
- Pendaftaran protokol `depup://` untuk login Depup Cloud serta menu konteks
  **Open with Depup Code** pada folder, keduanya dipasang dan dibersihkan oleh installer.
- **Wizard pertama kali** setelah kebijakan privasi disetujui: pemilihan folder
  workspace, tema, dan ukuran font editor, lengkap dengan daftar workspace terakhir.
  Folder dapat dilewati dan dibuka kapan saja dari Welcome screen.
- Dukungan membuka folder langsung dari argumen command line dan menu konteks,
  tanpa melewati tampilan kebijakan privasi.
- Skrip `build-msix.ps1` dan manifest `Packaging/Store/AppxManifest.xml` untuk
  paket MSIX Microsoft Store, termasuk pembuatan logo Store dari aset aplikasi
  dan checklist `Packaging/Store/STORE_SUBMISSION.md`.
- Terjemahan installer bahasa Indonesia (`Packaging/Installer/Indonesian.isl`)
  dan teks pemberitahuan privasi installer (`Packaging/Installer/PRIVACY-NOTICE.txt`).

### Diubah

- Jendela kebijakan privasi menampilkan `PRIVACY.md` sebagai teks yang mudah dibaca
  beserta label versi kebijakan; persetujuan dicatat per akun Windows.
- Cache Microsoft Edge WebView2 untuk preview dan terminal dipindahkan ke
  `%LOCALAPPDATA%\DepupCode\WebView2` agar tetap dapat menulis pada instalasi
  per-user maupun MSIX.
- Data aplikasi (pengaturan, riwayat chat AI, ekstensi, runtime, dan sandbox)
  memakai satu direktori pengaturan. Build `DEBUG` memakai `DepupCode-Development`
  supaya tidak bercampur dengan instalasi rilis.
- `README.md` dan `PRIVACY.md` diperbarui untuk alur setup satu file, wizard
  workspace pertama kali, serta tautan unduhan.
- `.gitignore` kini mengabaikan `artifacts/` dan file sementara build WPF
  (`*_wpftmp.csproj`).

### Diperbaiki

- Dialog pemilihan folder tidak lagi muncul otomatis dari `MainWindow`; onboarding
  berjalan sekali di awal dan tidak berulang setiap aplikasi dibuka.
- Preview dan terminal WebView2 tidak lagi gagal ketika folder aplikasi bersifat
  read-only atau dipasang sebagai paket MSIX.
- Pemasangan ulang tidak menimpa pengaturan pengguna; uninstall hanya menghapus
  file aplikasi, registry, dan shortcut yang dibuat installer.

### Catatan rilis

- Setup dan payload belum ditandatangani secara digital. Untuk distribusi publik,
  build dengan `.\build-installer.ps1 -CertificatePath .\cert\depup-codesign.pfx`.
- Paket MSIX hanya dapat dikirim ke Store setelah memakai `IdentityName` dan
  `Publisher` dari Partner Center. Sertifikasi tetap mengikuti hasil pemeriksaan Microsoft.

## [2.5.0] - 2026-11-02

Rilis pertama Depup Code sebagai IDE desktop Windows x64.

### Ditambahkan

- Workspace, file explorer, editor bertab, pencarian/ganti, formatter, minimap,
  navigasi, dan pengaturan per-workspace.
- Terminal PowerShell terintegrasi, panel output/diagnostics, preview HTML/WebView2,
  dan browser untuk server lokal.
- AI chat/agent dengan konteks workspace, mode Ask/Plan/Agent, dan persetujuan
  sebelum aksi file; provider OpenAI-compatible, Gemini, dan GitHub Copilot.
- Git/GitHub: status, branch, diff, commit, push, clone, dan operasi repository.
- PHP/Laravel lokal dengan runtime resmi PHP 8.4.27 dan Apache 2.4.66 yang diunduh
  saat pemasangan pertama (sekitar 44.81 MB) dan diverifikasi dengan SHA-256.
- Integrasi deployment Depup Cloud, Vercel, Cloudflare Pages, dan Railway beserta
  pengelolaan domain, environment, dan Link Gaib tunnel sesuai konfigurasi akun.
- Marketplace Open VSX untuk menjelajah dan memasang ekstensi, termasuk theme dan snippet.
- UI debugging (breakpoint, stepping, stack, variables, console) dan konfigurasi
  language server.
- Mode offline dengan cache lokal untuk fitur yang tidak membutuhkan koneksi.
- Paket ZIP portable Windows x64 dan kebijakan privasi desktop serta demo web.

### Batasan

- PHP lokal hanya untuk development di `127.0.0.1` melalui HTTP; MySQL, phpMyAdmin,
  dan HTTPS lokal tidak disertakan.
- Hanya PHP 8.4.27 yang disediakan.
- Adapter debug dan language server eksternal harus disiapkan terpisah.
- Tidak semua ekstensi VS Code atau tipe aktivasi didukung.

[Unreleased]: https://github.com/MeledakCik/DepupCode/compare/v2.5.1...HEAD
[2.5.1]: https://github.com/MeledakCik/DepupCode/compare/v2.5.0...v2.5.1
[2.5.0]: https://github.com/MeledakCik/DepupCode/releases/tag/v2.5.0
