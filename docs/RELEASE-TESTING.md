# Version 0.7.0 verification

Tested on Windows x64 with an NVIDIA RTX 3080 and driver 616.92, using only the public NVIDIA sample recordings included in `samples`. No phone was queried or used for these release tests.

- Self-contained Windows/.NET 10 and native bridge build succeeded.
- The installer downloaded the pinned FFmpeg and Google Platform Tools archives, checked their SHA-256 hashes, installed its files, and registered the virtual webcam.
- A separate-directory first-install test succeeded; uninstall removed the test application and retained existing preferences and both AI runtimes. Installation over the existing application succeeded.
- Saved preferences retained the same SHA-256 across these operations.
- All eight VFX/AR packages for Turing, Ampere, Ada and Blackwell passed pinned-hash verification, extraction with the bundled helper, and required DLL/model/license checks. Repeating setup performed no download or installation. Incorrect hashes were rejected.
- Simulated GPU reports selected all four intended families and rejected GTX/unknown hardware and an outdated driver. Multiple-GPU selection was checked. The actual RTX 3080 was detected as Ampere.
- A self-contained startup command ran with DOTNET_ROOT pointed at a nonexistent directory and multilevel runtime lookup disabled. This is useful evidence of bundled-runtime startup, but is not a clean Windows VM test.
- Recorded background tests passed 100 frames at 480p, 720p, 1080p, and resized 4K. Processing/color/framing tests and recorded real-time control/webcam tests passed.
- Recorded Eye Contact tests passed localized changes, exact off bypass, and combined blur/replacement without a reported eye error.
- The installed Background tab was visually inspected with recorded video and correctly showed the RTX 3080, Ampere package family and ready status.

Actual GPU inference has only been exercised on the RTX 3080. Other GPU families have package/selection validation, not inference or performance certification. No clean Windows VM or second physical PC has been tested. General webcam compatibility across every video application is not established. Phone behavior is inherited from the existing Android 0.2.0 sender; this release did not perform a new live phone test.
