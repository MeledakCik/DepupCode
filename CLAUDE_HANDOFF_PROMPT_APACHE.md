# Prompt Claude: server Apache + PHP lokal tanpa XAMPP

Kamu mengerjakan **Depup Code**, aplikasi desktop WPF (.NET 8) di `C:\Project-App\DepupCode`. Tugasmu: menambahkan **runtime web server lokal milik Depup Code sendiri** untuk menjalankan project **PHP dan Laravel di Windows tanpa XAMPP, WAMP, Laragon, atau instalasi Apache/PHP global**.

Baca README dan telusuri arsitektur yang ada sebelum mengubah kode. Jelaskan rencanamu secara singkat, lalu implementasikan.

## Hasil yang diinginkan

Pengguna membuka folder project PHP/Laravel di Depup Code, menekan **Start Server**, dan aplikasi:

1. Mendeteksi apakah project itu Laravel atau PHP biasa.
2. Menjalankan Apache + PHP yang dikelola Depup Code pada `127.0.0.1` dengan port bebas.
3. Membuka halaman di panel Preview / browser pada `http://127.0.0.1:<port>`.
4. Menampilkan status server dan log di aplikasi.
5. Menghentikan server beserta seluruh proses anak saat diminta atau saat aplikasi ditutup.

Tidak boleh ada syarat: XAMPP terpasang, Apache terdaftar sebagai Windows service, perubahan `PATH` global, hak Administrator, atau firewall rule. Semua runtime dan konfigurasi dikelola di folder milik aplikasi.

## Keputusan arsitektur yang harus diambil (dan dijelaskan)

Apache tetap dipakai karena PHP membutuhkan SAPI; jangan menggantinya dengan server HTTP .NET buatan sendiri. Pilih salah satu pendekatan PHP SAPI dan dokumentasikan alasannya:

- **Opsi A — mod_php**: Apache Lounge Win64 + PHP Thread Safe, `LoadModule php_module "php8apache2_4.dll"`. Paling sederhana, tetapi varian PHP harus Thread Safe dan versi harus cocok dengan Apache/VC runtime.
- **Opsi B — FastCGI**: Apache Lounge + `php-cgi.exe -b 127.0.0.1:<port>` + `mod_proxy_fcgi`. Lebih terisolasi dan tidak bergantung pada modul Apache PHP, tetapi mengelola dua proses.

Rekomendasi: mulai dengan **Opsi A** untuk MVP yang sederhana, siapkan fallback `php -S 127.0.0.1:<port> -t <docroot>` hanya jika runtime Apache tidak tersedia dan beri label jelas “PHP built-in server (fallback)”. Jangan diam-diam beralih tanpa memberi tahu pengguna.

## Komponen yang perlu dibuat

### 1. `LocalPhpRuntimeService`

- Runtime disimpan di `%LOCALAPPDATA%\DepupCode\runtimes\apache\<versi>` dan `...\php\<versi>` (bukan di folder instalasi aplikasi, bukan di Program Files).
- Manifest runtime (JSON) berisi versi, URL unduhan resmi, SHA-256, ukuran, lisensi, dan daftar file wajib. Pin versi; jangan memakai “latest” tanpa checksum.
- Unduh **atas persetujuan pengguna** dengan progres, verifikasi SHA-256, ekstraksi aman (tolak zip-slip, symlink, path absolut), lalu tandai terpasang. Jika checksum gagal, hapus hasil unduhan dan tampilkan error.
- Dukung pemakaian offline setelah runtime pernah diunduh. Jangan menghapus cache otomatis.
- Izinkan override path PHP/Apache yang sudah ada di Settings (opsional), tetapi jangan pernah mewajibkan instalasi sistem.
- Sertakan file lisensi Apache 2.0 dan PHP di folder runtime; jangan mendistribusikan ulang binary tanpa notice yang sesuai.

### 2. `PhpServerService`

Tanggung jawab:

- **Deteksi project**:
  - Laravel: ada `artisan` dan `composer.json` yang memuat `laravel/framework` (atau `bootstrap/app.php` + `public/index.php`).
  - PHP biasa: ada `composer.json`, `index.php`, atau file `*.php` di root/subfolder yang dipilih.
- **Document root**:
  - Laravel: `<workspace>/public` (wajib, jangan root project).
  - PHP biasa: root workspace atau subfolder yang dipilih pengguna; simpan pilihan per workspace.
- **Konfigurasi Apache** dibuat ulang setiap start di `%LOCALAPPDATA%\DepupCode\servers\<hash-workspace>\httpd.conf`:
  - `Listen 127.0.0.1:<port>` (jangan `0.0.0.0` secara default).
  - `DocumentRoot`, `<Directory>` dengan `AllowOverride All` dan `Require all granted`.
  - `DirectoryIndex index.php index.html`.
  - `RewriteEngine On` + aturan front controller Laravel:
    ```apache
    RewriteCond %{REQUEST_FILENAME} !-f
    RewriteCond %{REQUEST_FILENAME} !-d
    RewriteRule ^ index.php [L]
    ```
  - Blokir akses ke `.env`, `.git`, `composer.json`, `composer.lock`, dan file di luar document root. Jangan mengaktifkan directory listing.
  - `ErrorLog` dan `CustomLog` ke file di folder state server; tail log ke UI.
  - Path Windows di-escape dengan benar; gunakan forward slash di konfigurasi Apache.
- **php.ini** minimal: `extension_dir`, ekstensi yang dibutuhkan Laravel (`openssl`, `pdo_mysql`, `pdo_sqlite`, `mbstring`, `fileinfo`, `curl`, `tokenizer`, `xml`, `ctype`, `json`, `bcmath`), `date.timezone`, `memory_limit`, `upload_max_filesize`, `post_max_size`, `max_execution_time`, `display_errors=Off`, `log_errors=On`, `error_log`.
- **Port**: cari port kosong di rentang yang bisa dikonfigurasi (default 8000–8099) pada loopback; jika bentrok, naikkan otomatis. Simpan port terakhir per workspace; jangan memakai port yang sedang dipakai proses lain.
- **Lifecycle**:
  - Cegah dua server untuk workspace yang sama.
  - Jalankan `httpd.exe` tanpa jendela console, redirect stdout/stderr, dan gunakan Job Object `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` agar proses anak ikut mati.
  - Health check HTTP ke `/` setelah start; jika gagal dalam ±10 detik, hentikan proses dan tampilkan 20 baris terakhir log.
  - Stop: hentikan proses tree dengan rapi, tunggu, lalu paksa bila perlu. Bersihkan state saat aplikasi exit/crash.
  - Jangan memakai `httpd -k install` atau service Windows.
- **Keamanan**:
  - Bind loopback saja; jangan membuka firewall.
  - Jangan pernah menjalankan `httpd.conf` yang disediakan workspace; konfigurasi selalu dihasilkan aplikasi.
  - Jangan menaruh token/secret di file konfigurasi atau log. Redaksi pola secret yang sudah ada bila relevan.
  - Apache berjalan dengan hak user saat ini; jangan meminta elevasi. Jika proses harus disandbox, jelaskan kompromi AppContainer terhadap listening loopback dan proses anak; minimal gunakan Job Object + ACL yang wajar.

### 3. Integrasi UI

Pilih integrasi paling kecil dan konsisten dengan shell Depup Code; jangan menambah halaman/layer baru yang berlebihan. Minimal:

- **Command Palette**: `PHP: Start Local Server`, `PHP: Stop Local Server`, `PHP: Restart Local Server`, `PHP: Open in Preview`, `PHP: Show Server Log`.
- **Status bar**: indikator `PHP: Off` / `PHP: 127.0.0.1:8000`; klik membuka menu Start/Stop/Open Preview/Log. Ikuti pola `TunnelStatusText` dan status bar yang ada.
- **Preview**: gunakan `HtmlPreviewWebView` dan alur `NavigateHtmlPreviewAsync` yang sudah ada; hormati setelan `AutoOpenHtmlPreviewOnServerStart`.
- **Settings**: bagian **Local Server** berisi path runtime (opsional), rentang port, auto-open preview, tombol unduh/hapus runtime, versi terpasang, dan catatan lisensi. Simpan lewat `SettingsService`/`AppSettings`; jangan menyimpan path yang tidak valid.
- **Log**: kirim log server ke tab OUTPUT lewat `AppendOutput("[PHP] ...")`; sediakan aksi “Show Server Log” yang membuka output dan menampilkan tail.
- **Laravel**: tampilkan petunjuk jika `vendor/` belum ada atau `.env` belum ada. Jangan otomatis menjalankan `composer install`, `artisan migrate`, `key:generate`, atau `storage:link` tanpa persetujuan eksplisit; sediakan tombol/command terpisah bila ingin memudahkan. Jangan mengubah isi `.env` pengguna secara diam-diam.

## Perilaku & batasan

- Jangan mengganti fitur Deploy, Link Gaib, terminal, preview, atau AI.
- Jangan menambah dependency pada XAMPP/Laragon/WAMP atau installer pihak ketiga.
- Jangan mengunduh binary tanpa persetujuan dan tanpa checksum.
- Jangan menjalankan perintah shell yang tidak perlu; hindari `cmd /c` bila `ProcessStartInfo` langsung bisa.
- Jangan menulis ke folder workspace untuk konfigurasi server; gunakan `%LOCALAPPDATA%`. File `.depup/` hanya jika benar-benar perlu dan sudah menjadi pola aplikasi.
- Jangan mengklaim PHP/Laravel berjalan jika health check belum benar-benar lolos.
- Jika runtime belum tersedia atau unduhan gagal, tampilkan langkah pemulihan yang jelas; jangan crash.

## Test yang diharapkan

Tambahkan unit test untuk logika yang tidak butuh desktop:

- Deteksi Laravel vs PHP biasa.
- Pemilihan document root (`public` untuk Laravel).
- Render `httpd.conf`/`php.ini`: escaping path dengan spasi/backslash, blokir file sensitif, rewrite rule.
- Pemilihan port bebas dan penanganan port bentrok.
- Validasi manifest + SHA-256 + penolakan zip-slip.
- Pencegahan start ganda dan cleanup state.
- Redaksi secret pada log.

Untuk verifikasi manual di desktop:

1. Project PHP biasa dengan `index.php` → start → halaman tampil di preview → stop.
2. Project Laravel tanpa XAMPP → start → route `/` dan satu route lain (mis. `/login`) merespons, aset CSS/JS termuat, `storage/logs` bisa ditulis → stop.
3. Port 8000 sedang dipakai → server memilih port lain dan tetap jalan.
4. Tutup aplikasi saat server hidup → tidak ada `httpd.exe`/`php-cgi.exe` tersisa.
5. Tidak ada prompt Administrator/firewall; hanya loopback yang listen.
6. Jalankan tanpa XAMPP/Laragon terpasang untuk memastikan tidak ada dependensi tersembunyi.

## Build dan release

Setelah implementasi dan test lulus:

```powershell
Get-Process -Name DepupCode -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\DepupCode\DepupCode.csproj -c Debug --nologo -v:minimal
dotnet test .\tests\DepupCode.Tests\DepupCode.Tests.csproj -c Debug --nologo
.\build.ps1
```

- Jika runtime Apache/PHP ikut dibundel, perbarui `build.ps1` agar memverifikasi file runtime wajib dan lisensi; laporkan ukuran paket.
- Jika runtime diunduh saat pertama dipakai (disarankan), pastikan manifest ikut terpublish dan `build.ps1` memverifikasi manifest tersebut.
- Hasil release: `DepupCode\bin\Release\net8.0-windows\win-x64\publish\DepupCode.exe`.

## Kriteria selesai

- PHP/Laravel berjalan tanpa XAMPP dan tanpa instalasi global.
- Start/stop/restart stabil, port otomatis, log terlihat, preview terbuka.
- Laravel memakai document root `public` dan routing front controller bekerja.
- Tidak ada proses yatim setelah stop/exit.
- Hanya listen di `127.0.0.1`; tidak ada elevasi/firewall prompt.
- Settings dan status bar mencerminkan kondisi server dengan benar.
- Build dan test lulus; hasil verifikasi manual dilaporkan apa adanya.

## Laporan akhir

Sebutkan: keputusan SAPI (mod_php/FastCGI), file yang diubah/ditambah, manifest runtime dan sumber resminya, perilaku start/stop, hasil test/build/publish, path exe, serta batasan yang tersisa (mis. hanya satu versi PHP, belum ada MySQL/phpMyAdmin, belum HTTPS lokal).
