# Prompt Claude: finishing UI halaman Extensions Depup Code

Kamu mengerjakan aplikasi desktop WPF (.NET 8) **Depup Code** di `C:\Project-App\DepupCode`. Tugasmu adalah **menyelesaikan dan merapikan UI halaman Extensions** agar terasa seperti bagian native dari IDE ini, bukan halaman tambahan. Screenshot target dilampirkan di percakapan; gunakan sebagai acuan komposisi, hierarki, dan kepadatan informasi, bukan sebagai instruksi untuk mengubah fitur lain.

Sebelum mengubah kode, baca README, `DepupCode/Views/ExtensionsView.xaml`, `ExtensionsView.xaml.cs`, `Services/ExtensionMarketplaceService.cs`, `Services/VsCodeExtensionHostService.cs`, dan tema di `DepupCode/App.xaml` + `DepupCode/MainWindow.xaml`. Jelaskan singkat temuanmu, lalu implementasikan perbaikan langsung.

## Tujuan

Selesaikan halaman Extensions dengan satu layout dua kolom yang rapi pada lebar normal maupun sempit:

1. **Header**: judul `Extensions`, subjudul singkat, dan badge status `Open VSX · registry terbuka`. Badge memakai titik hijau dari resource `BrandLime`, bukan warna hardcode baru.
2. **Baris pencarian**: input pencarian yang fleksibel, dropdown kategori, dan tombol **Cari** primer. Tidak boleh ada kontrol yang terpotong di tepi kanan pada lebar jendela 1500–1950 px (skala 150%) atau saat panel menyempit.
3. **Tab**: `Jelajahi` dan `Terpasang (n)` dengan indikator aktif berupa garis aksen; status hasil (`n hasil · Open VSX` atau `n terpasang`) rata kanan.
4. **Daftar extension (kiri)**: kartu dengan ikon, judul, penerbit, versi di kanan atas, deskripsi 1–2 baris, serta badge `✓ Terpasang` bila sudah terpasang. Kartu terpilih memakai border aksen + latar `ThemeActiveNavBrush`; hover memakai `ThemeHoverBrush`.
5. **Detail (kanan)**: ikon besar, judul + badge terdaftar Open VSX, baris `Penerbit · identifier · vX.Y.Z`, deskripsi, jumlah unduhan, kategori, tombol aksi, callout keamanan/kompatibilitas, bagian **IKHTISAR FITUR**, dan **INFORMASI PAKET** (identifier, versi, penerbit, kategori, sumber). Semua isi detail harus berada di `ScrollViewer` dan tidak terpotong saat tinggi jendela pendek (terminal bawah sering menyisakan ruang sedikit).
6. **Empty state**: overlay penuh (tanpa transparansi yang memperlihatkan konten di belakangnya) untuk kondisi memuat, gagal koneksi, hasil kosong, dan tab Terpasang kosong; beri judul + satu kalimat arahan.

## Yang sudah ada — audit, jangan duplikasi

Implementasi saat ini sudah memiliki:

- `CategoryCombo` dengan opsi `Semua extension`, `Tema, snippets & Node.js`, `Tema`, `Snippets`, `Runtime Node.js`; filter diterapkan lewat `ApplyCategoryFilter()`.
- `SearchBox` + placeholder, tombol hapus kata kunci, pencarian via Enter/tombol **Cari**, dan pembatalan pencarian lama melalui `_searchCancellation`.
- Tombol aksi `Pasang`, `Aktifkan`, `Nonaktifkan`, `Pakai Tema`, serta menu overflow `⋯` berisi `Buka di Open VSX`, `Salin ID extension`, dan `Hapus extension`.
- `RenderFeatures()` menghasilkan poin **IKHTISAR FITUR** dari kategori/status extension (tema, snippets, entry point Node.js, terpasang) — jangan menulis klaim fitur palsu.
- `UpdateMetadata()` mengisi **INFORMASI PAKET**.

Periksa ulang hasilnya. Jika ada yang belum sesuai target, perbaiki; jangan membuat ulang kontrol yang sudah bekerja atau menambah layer/tab/jendela baru.

## Batasan data (penting)

- `ExtensionMarketplaceItem` hanya menyediakan: namespace, name, display name, description, version, publisher, download count, icon URL, download URL, categories, `IsInstalled`, `HasActivationEntryPoint`. **Jangan mengarang rating bintang, jumlah review, ukuran paket, tanggal rilis, atau badge “verified publisher”** bila datanya tidak ada. Screenshot target menampilkan bintang hanya sebagai ilustrasi desain.
- Badge centang pada judul hanya boleh berarti **“Terdaftar di Open VSX”** (tooltip), karena semua item berasal dari registry tersebut. Jangan menulis “verified” jika tidak ada data verifikasi.
- Jangan menambah panggilan jaringan baru di luar `ExtensionMarketplaceService`. Jangan menyimpan token/API key di UI atau log.
- Gunakan brush/font resource yang sudah ada (`ThemePrimaryBrush`, `ThemeSecondaryBrush`, `ThemeMutedBrush`, `ThemeDimBrush`, `ThemeSurfaceBrush`, `ThemeActiveSurfaceBrush`, `ThemeActiveNavBrush`, `ThemeBorderBrush`, `ThemeBorderLightBrush`, `ThemeSeparatorBrush`, `ThemeAccentBrush`, `ThemeAccentSoftBrush`, `BrandLime`, `Mono`). Jangan hardcode warna baru kecuali benar-benar tidak ada padanannya.

## Perilaku yang wajib dipertahankan

- Alur pasang: unduh lewat `ExtensionMarketplaceService.InstallAsync`, perbarui daftar tanpa menghapus hasil pencarian, dan segarkan hitungan tab Terpasang.
- Alur aktivasi tetap meminta konfirmasi (`_confirmActivation`) dan berjalan di AppContainer. `Nonaktifkan` menghentikan runtime; `Hapus` menonaktifkan lalu menghapus paket.
- `ThemeSelected` tetap memicu penerapan tema ke seluruh aplikasi; `InstalledExtensionsChanged` tetap memberi tahu shell.
- Filter kategori, pencarian baru, tab Terpasang, dan pemilihan item tidak boleh saling menimpa state secara salah. Saat hasil kosong, detail harus di-reset ke keadaan “Pilih extension”.
- Jangan mengubah halaman lain, editor, terminal, atau panel AI. Fokus hanya pada Extensions.

## Polish yang diharapkan

- Jarak konsisten: padding header 24 px horizontal, kartu 8–10 px, radius 8–10 px, tinggi kontrol baris pencarian 38 px, tombol aksi 34 px.
- Tipografi konsisten: judul halaman 22 bold, judul detail 20 bold, judul kartu 11.5 semibold, metadata 9–10 px. Hindari teks terpotong tanpa ellipsis.
- Dropdown kategori dan menu overflow harus memakai tema gelap yang sama, menutup saat klik di luar, dan tidak menutupi kontrol penting saat dibuka.
- Scroll detail kembali ke atas ketika pengguna memilih extension lain.
- Aksesibilitas: beri `ToolTip` dan `AutomationProperties.Name` pada tombol ikon, pertahankan fokus keyboard yang masuk akal, dan pastikan tombol utama bisa diakses dengan Tab.
- Perhatikan skala Windows 150%: semua kolom memakai `*`/`Auto` dengan `MinWidth` yang aman, bukan lebar piksel tetap yang menyebabkan clipping.

## Kriteria selesai

- Pada lebar jendela 1500×950 dan 1920×1040 (skala 150%), tidak ada kontrol yang terpotong; tombol **Cari**, dropdown kategori, tombol aksi detail, dan menu `⋯` tetap terlihat.
- Mencari `theme`, memilih kategori `Tema`, membuka tab `Terpasang`, dan mengosongkan pencarian menghasilkan state yang benar tanpa error.
- Kartu terpilih, detail, empty state, dan menu overflow tampil konsisten di tema `VS Code Dark` maupun tema lain yang tersedia.
- Build bersih: `dotnet build DepupCode\DepupCode.csproj -c Debug --nologo` → 0 warning, 0 error.
- Jalankan aplikasi dan verifikasi visual benar-benar dilakukan. Jangan mengklaim sudah diuji jika belum.

## Build dan release

Setelah UI selesai dan build Debug bersih:

```powershell
# 1. Pastikan tidak ada instance yang mengunci file build.
Get-Process -Name DepupCode -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. Build debug untuk verifikasi cepat.
dotnet build .\DepupCode\DepupCode.csproj -c Debug --nologo -v:minimal

# 3. Publish self-contained win-x64 (script resmi repo).
.\build.ps1
```

Hasil publish yang harus dilaporkan:

- `DepupCode\bin\Release\net8.0-windows\win-x64\publish\DepupCode.exe`
- Script `build.ps1` memverifikasi host CoreCLR, Windows Desktop runtime, aset terminal, dan runtime Copilot. Jika script gagal, laporkan error aslinya; jangan memaksa publish parsial.

Untuk uji manual, jalankan exe hasil publish, buka activity bar **Extensions**, lalu periksa skenario di bagian “Kriteria selesai”. Jika membuat screenshot verifikasi, simpan di folder sementara, bukan di repo.

## Laporan akhir

Ringkas: file yang diubah, perilaku yang diperbaiki, hasil build/publish beserta path exe, hasil verifikasi visual (ukuran jendela dan skenario yang dicoba), serta keterbatasan yang tersisa (mis. rating tidak tersedia dari Open VSX, panel terminal memakan tinggi). Jangan menyertakan klaim yang tidak benar-benar dijalankan.

---

Catatan: prompt lama untuk pekerjaan **sesi chat AI Agent** disimpan di `CLAUDE_HANDOFF_PROMPT_AI_CHAT.md` agar tidak hilang.
