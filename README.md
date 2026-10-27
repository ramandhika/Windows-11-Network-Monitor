# netMonitor — Lightweight Windows 11 Network Monitor

Monitor jaringan super ringan buat Windows 11: indikator koneksi di taskbar + overlay kecil yang menampilkan kecepatan **download/upload** secara real-time.

![Made with C#](https://img.shields.io/badge/built%20with-C%23%20(WinForms)-blueviolet) ![Size](https://img.shields.io/badge/size-~8%20MB%20RAM-brightgreen)

## ✨ Fitur

- 🔴🟢 **Indikator koneksi** di taskbar (ijo = terhubung internet, merah = tidak) — otomatis "di-pin" ke taskbar Windows 11, bukan dihidden di menu overflow
- ⬇⬆ **Speed download/upload** real-time (update tiap 1 detik) dalam overlay kecil semi-transparan di atas area tray
- 📊 **Kuota pemakaian per jaringan + per hari**:
  - Tiap jaringan (LAN / WiFi A / hotspot HP...) punya hitungan sendiri — pindah jaringan = mulai dari 0
  - Reset otomatis tiap ganti hari, dan tersimpan ke file (aman walau PC restart)
- 🌐 **Info IP** (klik overlay): IP lokal WiFi/LAN + IP publik (via ipify)
- 🖱️ Overlay bisa **digeser** bebas, posisinya diingat
- 🔒 **Single-instance** (anti dobel jalan/overlay bertumpuk)

## 🖼️ Tampilan

```
Kecil (default)              Diklik (buka detail)
╭────────────────────────╮   ╭────────────────────────╮
│ ● ↓ 1.2 MB/s  ↑ 340 KB/s│  │ ● ↓ 1.2 MB/s  ↑ 340 KB/s│
╰────────────────────────╯   │ ────────────────────────│
                             │ Lokal:  192.168.2.5     │
                             │ Publik: 103.20.45.12    │
                             │ Kuota: 3.21 MB          │
                             ╰────────────────────────╯
```

- Ujung kanan atas taskbar → pilih **netMonitor → On**

## 🗑️ Mau ilangin auto-start?

Hapus `netMonitor.lnk` di folder Startup: **Win + R** → `shell:startup` → Enter → hapus shortcut-nya.

## ❓ Catatan

- Kuota diidentifikasi dari **IP gateway**. Dua SSID berbeda yang gateway-nya sama persis (misal 2.4GHz/5GHz router yang sama) dihitung sebagai satu jaringan.
- Overlay bakal keliatan selalu di atas (topmost) — tinggal klik kanan ikon tray → "Tampilkan / Sembunyikan overlay" kalau mau semua disembunyiin.
