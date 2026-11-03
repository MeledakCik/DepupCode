# Mengatur dan menguji AI Agent

Depup Code saat ini sudah menerima provider **OpenAI-compatible**. Jadi Groq maupun provider lain yang menyediakan endpoint Chat Completions kompatibel dapat diatur tanpa perubahan kode:

1. Buka panel **AI Agent → ⚙ Pengaturan AI Agent**.
2. Pilih **OpenAI / Compatible**.
3. Isi **URL API** dengan base URL provider (jangan tambahkan `/chat/completions` jika tidak diminta; aplikasi menambahkannya otomatis).
4. Isi **Model** dengan ID model yang tersedia di akun/provider.
5. Tempel API key pada kolom **API KEY**, lalu simpan.

## Groq

Gunakan base URL `https://api.groq.com/openai/v1`, API key dari akun Groq, dan model yang saat itu tercantum tersedia pada konsol Groq. Daftar model dan kuota dapat berubah; cek konsol/provider untuk model aktif dan batas penggunaan terbaru. Paket atau kuota gratis, jika tersedia untuk akun, tetap tunduk pada limit provider.

## Model lokal gratis dengan Ollama

Model lokal tidak memerlukan API key dan dapat berjalan tanpa mengirim prompt ke layanan model online. Instal Ollama, unduh model yang sesuai dengan perangkat, lalu jalankan Ollama. Pada pengaturan AI Depup Code pilih **OpenAI / Compatible**, isi:

- **URL API:** `http://localhost:11434/v1`
- **Model:** nama model yang terpasang, seperti yang ditampilkan oleh `ollama list`
- **API KEY:** kosong

Pastikan server Ollama aktif. Kualitas dan kecepatan sangat bergantung pada model, RAM/VRAM, dan perangkat. Model perlu diunduh terlebih dahulu dan ukuran unduhan dapat besar.

## Smoke test manual

Tidak ada kredensial provider yang dibundel, sehingga pengujian koneksi online harus dilakukan dengan API key milik pengguna. Setelah menyimpan konfigurasi:

1. Pilih mode **Ask** dan kirim `Balas hanya dengan: DEPUP AI OK`.
2. Pastikan balasan muncul. Jika gagal, baca pesan error di panel, lalu periksa URL, ID model, API key, koneksi jaringan, kuota, serta apakah provider mendukung endpoint dan format OpenAI Chat Completions.
3. Untuk mengecek tool agent, buka workspace yang aman, pilih **Agent**, lalu minta agent hanya membaca/menyebutkan file tertentu. Jangan meminta perubahan untuk smoke test. Tindakan yang dapat mengubah file atau menjalankan perintah tetap memerlukan persetujuan.
4. Tes otomatis repository memeriksa komponen layanan; tes tersebut tidak membuktikan bahwa API eksternal dapat diakses atau kredensial tertentu valid. Jalankan di Windows dengan .NET 8 SDK:

```powershell
dotnet test .\tests\DepupCode.Tests\DepupCode.Tests.csproj
```

Jangan menaruh API key di prompt, issue, log, atau file konfigurasi yang akan di-commit. Masukkan key melalui dialog pengaturan aplikasi; periksa pula kebijakan privasi provider sebelum mengirim source code.
