# netMonitor — Lightweight Windows 11 Network Monitor

Monitor jaringan super ringan buat Windows 11: indikator koneksi di taskbar + overlay kecil yang menampilkan kecepatan **download/upload** secara real-time, lengkap dengan **riwayat pemakaian jaringan hari ini**.

![Made with C#](https://img.shields.io/badge/built%20with-C%23%20(WinForms)-blueviolet) ![RAM](https://img.shields.io/badge/RAM-~8--15%20MB-brightgreen)

## ✨ Fitur

- 🔴🟢 **Indikator koneksi** di taskbar (ijo = terhubung internet, merah = tidak) — otomatis "di-pin" ke taskbar Windows 11, bukan disembunyikan di menu overflow
- ⬇⬆ **Speed download/upload** real-time (update tiap 1 detik) di overlay kecil semi-transparan
- 🖱️ **Klik overlay** → panel detail dengan IP + riwayat jaringan hari ini; **mouse keluar** → panel menutup otomatis
- 🌐 **Info IP**: IP lokal (WiFi/LAN) + IP publik (via ipify)
- 🕘 **Riwayat jaringan hari ini** — jaringan apa saja yang dipakai hari ini, lengkap dengan:
  - nama interface (Wi-Fi / Ethernet) + IP gateway
  - jam mulai – jam selesai
  - durasi pemakaian
  - total serta rincian ↓ download / ↑ upload per jaringan
  - jaringan yang sedang aktif ditandai titik hijau
- 📊 **Kuota harian per jaringan**: pindah jaringan (LAN → WiFi A → WiFi B) = hitungan mulai dari 0 lagi, reset otomatis tiap ganti hari, dan tersimpan walau PC restart
- 🎨 Tampilan rounded 6px, ukuran compact **170×24 px** — posisi overlay bisa digeser dan diingat
- 🔒 **Single-instance** (anti dobel jalan / overlay bertumpuk)
- 🪶 Ringan: RAM ~8–15 MB, CPU nyaris nol

## 🖼️ Tampilan

```
Kecil (default)                Diklik (panel detail)
╭────────────────────────╮     ╭──────────────────────────────────╮
│ ● ↓ 1.2 MB/s  ↑ 340 KB/s│    │ ● ↓ 1.2 MB/s  ↑ 340 KB/s         │
╰────────────────────────╯     │ ──────────────────────────────── │
                               │ Lokal 192.168.2.5 · Publik 103.… │
                               │ ──────────────────────────────── │
                               │ Riwayat hari ini        10.4 MB  │
                               │ ● Wi-Fi · 192.168.2.1    8.2 MB  │
                               │   09:12–sekarang · 2j 15m ·      │
                               │   ↓ 8.0 MB ↑ 0.2 MB              │
                               │ ○ Ethernet · 192.168.1.1 2.2 MB  │
                               │   07:45–08:30 · 45m ·            │
                               │   ↓ 2.1 MB ↑ 0.1 MB              │
                               ╰──────────────────────────────────╯
```

## 📦 Instalasi

1. Download `netMonitor.exe` dari repo ini
2. Taruh di folder mana saja (misal `C:\Users\<kamu>\netMonitor\`)
3. Jalankan — ikon hijau langsung nempel di taskbar dekat ikon WiFi/volume
4. Biar otomatis jalan tiap nyalain PC: klik kanan `netMonitor.exe` → **Send to → Desktop (create shortcut)**, lalu pindahkan shortcut ke folder Startup (`Win + R` → `shell:startup`)

Tidak perlu install .NET tambahan — Windows 11 sudah punya runtime-nya.

## 🔨 Build dari source

Pakai compiler bawaan Windows (tanpa install apa pun):

```bat
csc /target:winexe /out:netMonitor.exe netMonitor.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
```

atau buka `netMonitor.cs` di Visual Studio / Rider / dotnet-script.

## 🗑️ Mau hilangin auto-start?

**Win + R** → `shell:startup` → Enter → hapus shortcut `netMonitor`.

## ❓ Catatan

- Kuota & riwayat diidentifikasi dari **IP gateway**. Dua SSID berbeda yang gateway-nya sama persis (misal 2.4GHz/5GHz router yang sama) dihitung sebagai satu jaringan.
- Data tersimpan lokal di `usage.cfg` (kuota + riwayat) dan `overlay.cfg` (posisi overlay), tidak ikut ter-commit ke repo.
- Overlay selalu di atas (topmost) — klik kanan ikon tray → "Tampilkan / Sembunyikan overlay" kalau mau disembunyikan sementara.
- Test cepat: jalankan `netMonitor.exe --expand` untuk langsung membuka panel detail saat start.
