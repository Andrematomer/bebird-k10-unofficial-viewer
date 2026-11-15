# Bebird K10 Viewer (unofficial)

A small Windows app for the **Bebird K10 USB otoscope** (the wired ear camera). It shows the live picture in a round
scope view, keeps it upright using the wand's built-in tilt sensor, and lets you take photos and record video.

> **Unofficial.** Not made by, affiliated with, endorsed by or supported by Bebird. "Bebird" is their name; it is used
> here only to say which device this works with. It is built for the K10 only and has not been tried on other models.

## Why this exists

The K10 plugs in over USB and shows up as an ordinary webcam, but it also sends tilt-sensor data. Bebird's PC program
used that data to keep the picture upright. Without it the picture turns with your hand and is practically unusable.

That program is gone. The download button on Bebird's website still points at a server that no longer exists (DNS
error). I emailed their support and asked for the installer, even just as an email attachment. First answer: computers
are not supported. Second answer: the PC side is discontinued and there is no tool anymore. My last question, "can I at
least get it by email?", got a one-word reply: **No.** As far as I can tell they no longer sell the wired model at all.

So a product sold with a PC app lost the software it needs, and the company would not hand over a file it no longer
distributes. If you sell hardware, the software it needs shouldn't be able to disappear, and if it does, owners should be
free to replace it. This is that replacement, written from scratch by observing how the device talks to Windows. No Bebird
code or files were used.

## Features

- Live round scope view, as large as the window allows, with **auto-level** from the gyro (the picture stays upright as you rotate the wand)
- Normal / mirrored view (for the other ear), round mask on/off
- **Photos** (PNG) and **video recording** (MP4, H.264, what you see is what you get)
- Thumbnail of your latest photo/video; click it to show the file in Explorer
- Choose the save folder right in the window
- Freeze, fullscreen, buttons that scale with your screen (readable on 4K)
- Warns you when the picture runs slowly because of a USB 2 port, hub or extension cable

## Using it

1. Plug the K10 into a **USB 3 port, directly** (no hub, no extension cable). Plugging in before or after starting the app both work.
2. Start `BebirdScope.exe` (single file, nothing to install). The picture appears by itself.
3. When you are done, close the app and unplug the K10.

Every button shows its shortcut key next to it.

| Button | Key | What it does |
|---|---|---|
| Auto-level | S | Keeps the picture upright using the gyro |
| Ear icons | M | Normal view / mirrored view |
| Scope | C | Round scope mask on / off |
| Record | R | Start / stop recording an MP4 |
| Snapshot | Space | Save a photo |
| Freeze | P | Pause the picture |
| Fullscreen | F11 | Also double-click the picture; Esc leaves fullscreen |
| (no button) Reverse rotation | I | If the picture ever rotates the wrong way |
| (no button) Diagnostics | F12 | Raw gyro values and counters |

Photos and videos go to `Pictures\K10 Captures` unless you pick another folder in the box at the bottom right.

## USB 3 matters

The K10 calls itself "USB2.0 Camera" but it needs a USB 3 port to run smoothly. On USB 2 it technically works but only
at about 8 to 20 frames per second instead of 30. If the picture stays below about 22 fps for a few seconds, the app says
so and suggests a direct USB 3 port. You can dismiss it, and tick the box to be left alone for a month.

## Known limitations (a quirk of the K10 firmware)

- **If you unplug the K10 while the app is open, the app tells you and closes itself.** Plug it back in and start the
  app again. (Restarting the camera inside the same app run hangs inside Windows, so a fresh start is the reliable way.)
- **After the app has read the tilt sensor, the K10 will not start video again until it is unplugged and plugged back
  in.** In normal use (plug in, use, close, unplug) you never notice. If you close the app and reopen it without
  unplugging, it shows "Unplug and replug your K10" and starts by itself when you do.

## Files and logs

- Settings: `%LOCALAPPDATA%\BebirdScope\settings.json`
- Log (useful for bug reports): `%LOCALAPPDATA%\BebirdScope\log.txt`

## Building

`powershell -ExecutionPolicy Bypass -File build-release.ps1` builds `dist\BebirdScope.exe`. It uses a private .NET 8 SDK in
`.tools\` (downloaded automatically if missing, about 290 MB, only needed for building). Source is in `BebirdScope\`
(WPF, .NET 8, DirectShow via the DirectShowLib package, Windows' built-in H.264 encoder for video).

Developer switches: `BebirdScope.exe --demo` shows a test pattern without a camera (`--demo-slow` for the slow-picture
warning, `--demo-autorecord` for a recording test); `--selftest ...` runs headless camera experiments (`SelfTest.cs`).

## License

[MIT](LICENSE): use it for whatever you like, just keep the copyright notice and don't claim you wrote it.
