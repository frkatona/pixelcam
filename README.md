# PixelCam

Use a Pixel's rear camera as a Windows webcam. Control lens, resolution, color, zoom and framing from the PC. Optional NVIDIA Maxine effects provide background blur/replacement and Eye Contact.

## Download and install

1. Open [Releases](https://github.com/frkatona/pixelcam/releases/latest) and download **PixelCam-0.7.0-Setup.exe** from **Assets**.
2. Run the installer. It installs for your Windows user and creates a **PixelCam** Start menu shortcut. Internet access is needed for the video and USB tools.
3. Open PixelCam. You do not need Android Studio, Visual Studio, a separate .NET installation, OBS Studio, or the CUDA toolkit.

The installer is currently **unsigned**. Windows may display an unknown-publisher warning. Verify that the file came from this repository's release; `SHA256SUMS.txt` is provided alongside it.

## Connect your phone

1. Enable **Developer options → USB debugging** on the Pixel.
2. Connect with a USB data cable, unlock the phone, and accept **Allow USB debugging** for this PC. Windows may need the [Google USB driver](https://developer.android.com/studio/run/win-usb).
3. Click **Connect phone**. PixelCam installs or updates its Android sender when needed. Allow camera access on the phone and keep the sender visible.
4. Select the lens and resolution in PixelCam. Changes apply while streaming.
5. Leave **Webcam output** enabled and select **PixelCam** as the camera in your video application.

To try the app without a phone, choose **gear → Open video** and open one of the clips in the installed `samples` folder.

## Optional NVIDIA effects

Open **Background**. PixelCam shows the detected GPU and offers **Set up Maxine** and **Set up Eye contact**. It selects, downloads, verifies and prepares the matching NVIDIA packages. You do not launch a separate NVIDIA program or installer.

- Supported package families: RTX 20-series/Turing, 30-series/Ampere, 40-series/Ada, and 50-series/Blackwell. NVIDIA driver **572.61 or newer** is required.
- Packages are large: roughly 0.7–0.8 GB for backgrounds and another 1.3 GB for Eye Contact. Allow several GB of disk space for downloads and installed files.
- Downloads and settings persist across app upgrades and restarts. Models load into GPU memory again when effects start; that does not require downloading again.
- Video, color, framing and virtual-webcam output work with AI effects off on PCs without a supported GPU.

## Controls and limitations

- **Color:** saved settings return on launch. Enable Auto per control, or adjust manually. Double-click a slider handle to reset its neutral value.
- **Framing:** zoom and drag the crop box. **Rotate → Auto** follows the phone's orientation.
- **Background:** blur or choose a replacement preset/image. **Eye contact** is a separate checkbox near the capture controls.
- **Gear:** open recorded video and change appearance.
- Windows **x64** only; Windows 11 is recommended. Phone capture currently requires USB debugging.
- Stop OBS's virtual-camera output before using PixelCam. They share the OBS virtual-camera bridge.
- GPU effects have been run on an RTX 3080. Other families passed package/extraction checks and simulated GPU selection; their actual inference has not been tested on those cards.

See the [full manual](docs/MANUAL.md) for setup, controls, architecture and beginner programming explanations, or [building](docs/BUILDING.md) to rebuild it. Uninstall through Windows **Installed apps**; settings and downloaded AI packages are retained.

## Source and third-party components

Original source: GPL-2.0-or-later with the [Maxine linking permission](MAXINE-LINKING-EXCEPTION.txt). NVIDIA runtimes/models are downloaded separately under NVIDIA's terms. See [third-party notices](THIRD-PARTY-NOTICES.md). The release includes corresponding source archives for the bundled OBS webcam modules and 7-Zip helper. Video and USB tools are downloaded directly from their upstream publishers during installation.
