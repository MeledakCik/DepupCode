# Mengatur dan menguji AI Agent

Buka panel **AI Agent → ⚙ Pengaturan AI Agent**. Pilih provider dari daftar; URL API dan model
saran terisi otomatis. Isi API key (kecuali provider lokal), lalu tekan **Tes koneksi** dan **Simpan**.
API key disimpan terpisah untuk setiap provider dan dienkripsi dengan Windows DPAPI.

## Pilihan gratis atau lokal

Kuota, batas, dan daftar model gratis ditentukan provider dan bisa berubah. Tekan **Muat daftar model**
untuk melihat model yang benar-benar tersedia, lalu pilih atau ketik manual.

| Provider | Biaya | API key | Catatan |
| --- | --- | --- | --- |
| OpenRouter | model `:free` / `openrouter/free` | ya (akun gratis) | Ada batas harian; dukungan tool bervariasi; prompt dapat dicatat provider. |
| Groq | tier gratis ber-rate-limit | ya | Cek konsol untuk model aktif. |
| GitHub Models | batas gratis | token GitHub (PAT) | Izin Models diperlukan. |
| Gemini | tier gratis | ya (Google AI Studio) | |
| Ollama | gratis, lokal | tidak | `http://localhost:11434/v1`; unduh model dulu. |
| LM Studio | gratis, lokal | tidak | `http://localhost:1234/v1`; aktifkan Local Server. |

Untuk model lokal, jalankan Ollama lalu `ollama pull qwen2.5-coder:7b` (atau model lain yang mendukung tools).
Kecepatan dan kualitas bergantung pada RAM/VRAM.

## Menambah provider sendiri

Pilih **＋ Tambah provider manual…**, isi nama, URL API (base URL, tanpa `/chat/completions`), dan model,
lalu simpan. Provider tampil di daftar dengan label *(manual)* dan dapat diubah atau dihapus. Syaratnya:
provider mendukung format OpenAI Chat Completions (`POST {url}/chat/completions`).

## Tool-calling

Mode **Agent** memakai tool (baca/tulis file, terminal) sehingga butuh model yang mendukung tool-calling.
Jika model tidak mendukungnya, mode **Ask/Plan** otomatis berjalan sebagai chat biasa tanpa tools,
sedangkan **Agent** menampilkan pesan agar Anda memilih model lain.

## Smoke test

1. **Tes koneksi** di dialog pengaturan mengirim `Balas hanya dengan: DEPUP AI OK` ke provider.
2. Di panel chat, mode **Ask**, kirim pesan yang sama dan pastikan balasan muncul.
3. Mode **Agent** di workspace aman: minta agent hanya membaca/menyebut file tertentu. Jangan minta perubahan untuk smoke test.
4. Tes otomatis (Windows, .NET 8 SDK): `dotnet test .\tests\DepupCode.Tests\DepupCode.Tests.csproj`.
   Tes ini tidak membuktikan API eksternal dapat diakses atau key valid.

Jangan menaruh API key di prompt, issue, log, atau file yang akan di-commit, dan periksa kebijakan privasi
provider sebelum mengirim source code.
