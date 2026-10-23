# PixelCam icon

Original geometric camera artwork, included under PixelCam's project license. No third-party imagery, fonts, or generated bitmap artwork are used.

Run `New-PixelCamIcon.ps1` on Windows to recreate `PixelCam.ico`. The icon contains transparent 32-bit PNG images at 16, 20, 24, 32, 48, 64, 128, and 256 pixels. The script draws a camera glyph using paths and circles, then writes a multi-resolution ICO container.

Embed the icon as both the Windows application icon and an assembly resource so `MainForm.Icon` can use the same asset.
