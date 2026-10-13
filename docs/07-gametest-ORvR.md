# 07 — In-game verification on OR_vR (ORNYMG Rev. 174, 2026-10-13)

Machine: Win11, i5-7600K, RTX 2070 SUPER, .NET Framework 4.8. No headset.
Method: patched Release build installed over a copy-consistent full set
(see `docs/06-install-ORNYMG.md`); sessions launched via
`RunActivity.exe -start -explorer <path.pat> <consist.con> 12:00 1 0`.

## Result 1 — patched game reaches live 3D (VR off)

- Route CGL_V22N + `RENFE 446 Original.con`: loader completed (~75 s, ~48 MB),
  window `Open Rails`, live 3D, **zero `Error` lines** in `OpenRailsLog.txt`
  (only route-content warnings: a signal-script parse warning and a missing
  `wtower.sms`, both pre-existing content issues).
- Log shows the new key registered: `VREnabled = False`.
- `Menu.exe` also starts and runs on the patched DLLs.

## Result 2 — stereo path executes in the real game (VR on, no headset)

- With `VREnabled=1` (registry) and no runtime/shim: session alive in 3D,
  window `Open Rails`, desktop shows the **side-by-side stereo composite**
  produced by the new `RenderProcess.Draw` hook (two full eye passes +
  composite). Screenshot: `docs/img-vr-sbs-fallback.png`.
- The SBS frame shows the world (catenary, sky, stock) in both halves with
  the cab area dark (unlit 3D-cab interior at the spawn viewpoint — to be
  confirmed visually on a headset; the geometry path itself is proven by the
  per-half rendering).

## Result 3 — failures are content-identical with and without the mod

- `et3500.con` (missing engine/wagons in the content pack) and
  `AVLO_106_8C.con` on the test path fail identically on stock
  (`ORVR-Backup`) and patched binaries (`Player train original position not
  clear` / `Can't find player locomotive`). A/B exit codes match
  (`0xC0000005` with no args on both — pre-existing no-args behavior).
- Lesson applied to the install: a **consistent full set** of program
  binaries must be replaced together. A partial install (only the 4 directly
  patched assemblies) produced `TypeLoadException: TelepoleDataFile` because
  the branch tip is newer than the shipped Rev. 174 build; the full-set
  install eliminates it.

## Not done here

- On-headset validation, controller testing, headset screenshot comparison
  (see `docs/04-limitations.md` checklist). A mono-vs-SBS pixel comparison was
  started but stopped to avoid stealing the foreground while the machine owner
  was playing another game.
