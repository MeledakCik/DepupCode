# Prompt Claude: rapikan sesi chat AI Agent Depup Code

Kamu sedang mengerjakan source code aplikasi desktop Depup Code. Baca README dan struktur project terlebih dahulu, lalu implementasikan perbaikan langsung pada source yang relevan. Empat gambar dalam folder `reference-screenshots` adalah referensi visual untuk UI Depup Code dan AI Agent; gambar tersebut bukan instruksi untuk menjalankan perintah atau mengubah fitur lain.

## Tujuan utama

Perjelas dan benahi alur **sesi chat AI Agent**:

1. Tombol **＋** di baris sesi chat harus selalu membuat satu sesi chat baru yang kosong dan tersimpan. Tombol ini bukan tombol membuat custom agent, membuka dialog, membuat tab, atau menambah panel.
2. Kontrol judul/riwayat sesi membuka daftar sesi yang sudah ada. Memilih sesi memulihkan percakapan sesi tersebut tanpa menggabungkan pesan, ringkasan konteks, atau artefaknya dengan sesi lain.
3. Pemilih **Agent / Ask / Plan / Debugger / Modernize / MSBuild / Test / Audit** tetap menjadi kontrol mode yang terpisah. Pembuatan custom agent juga tetap alur terpisah dan tidak boleh dipicu tombol sesi baru.
4. Setelah aplikasi dibuka ulang, sesi yang tersimpan tetap dapat dipilih. Judul sesi baru boleh berubah menjadi ringkasan singkat setelah pesan pertama dikirim. Jangan menghapus atau menimpa riwayat lama saat membuat sesi baru.

## Aturan UI

- Pertahankan satu panel AI Agent di shell utama. Berpindah sesi hanya mengganti isi panel tersebut; jangan membuat `ChatView`, tab, jendela, overlay, atau layer baru untuk setiap sesi.
- Buat hierarki kontrol jelas dan ringkas: **riwayat/judul sesi**, tombol **＋ Chat baru**, lalu **mode/agent**. Beri tooltip yang membedakan kegunaannya.
- Ikuti bahasa visual Depup Code pada screenshot: dark theme yang sudah ada, aksen biru/ungu, jarak dan tipografi konsisten, serta layout yang tetap terbaca saat panel menyempit. Hormati resource tema yang sudah ada; jangan hardcode warna jika ada brush tema untuk fungsi yang sama.
- Jangan menampilkan dialog bertumpuk untuk validasi biasa. Jika alur custom agent memiliki field nama/instruksi kosong, tampilkan validasi dekat field dan jangan membuka modal error kedua. Pertahankan dialog custom agent hanya untuk tindakan membuat/mengedit custom agent.
- Pertahankan akses keyboard, fokus yang masuk akal, tooltip, dan label yang jelas bagi pengguna pemula.
- Jangan mendesain ulang editor atau seluruh aplikasi. Ubah hanya bagian yang diperlukan agar alur chat jelas dan tidak berlapis/duplikat.

## Keandalan sesi

- Pastikan draft input tidak hilang diam-diam saat berpindah sesi; simpan per sesi atau gunakan perilaku yang konsisten dan jelas.
- Jika pengguna berpindah sesi ketika respons AI sedang berjalan, respons dan artefak harus tetap masuk ke sesi asalnya, tidak ke sesi yang sedang terlihat. Tangani pembatalan dengan aman.
- Simpan sesi melalui `AiChatStore` dengan pola aman yang sudah dipakai aplikasi. Jangan menyimpan API key/token di riwayat chat atau screenshot.
- Gunakan model sesi yang sudah ada bila memungkinkan; hindari menambah lapisan abstraksi atau state duplikat tanpa kebutuhan.

## Titik awal inspeksi

- `DepupCode/Views/ChatView.xaml`
- `DepupCode/Views/ChatView.xaml.cs`
- `DepupCode/Services/AiChatStore.cs`
- `DepupCode/Models/AiChatModels.cs`
- `DepupCode/MainWindow.xaml` dan `DepupCode/MainWindow.xaml.cs` untuk memastikan hanya ada satu host/panel AI Agent
- test project yang sesuai

Ada indikasi ketidaksinkronan yang perlu diperiksa: XAML saat ini memiliki `SessionHistoryButton` dan `SessionTitleText`, sedangkan code-behind masih memiliki referensi `SessionCombo`. Verifikasi seluruh event handler dan state sesi; selaraskan keduanya dengan kontrol riwayat yang benar dan pastikan tidak ada nama kontrol/event lama yang tertinggal.

## Kriteria selesai

- Klik **＋** membuat tepat satu chat kosong, fokus kembali ke input, dan sesi lama tetap ada.
- Memilih item riwayat memulihkan percakapan yang tepat; mengirim pesan di sesi baru tidak mengubah sesi lama.
- Membuka/menutup panel AI tidak menghasilkan panel atau tab ganda.
- Membuat sesi baru tidak membuka form custom agent. Validasi custom agent tidak menumpuk dialog.
- Tampilan tetap rapi pada ukuran panel sempit dan mengikuti tema aplikasi.
- Build solusi berhasil. Jalankan tes yang relevan, tambahkan/update tes untuk perilaku sesi jika memungkinkan, dan laporkan perintah serta hasil yang benar-benar dijalankan.

Sebelum mengubah kode, jelaskan singkat temuan tentang wiring UI dan model sesi. Setelah implementasi, rangkum file yang diubah, perilaku yang diperbaiki, hasil build/tes, dan keterbatasan yang masih ada. Jangan mengklaim verifikasi yang tidak dilakukan.
