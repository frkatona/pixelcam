# Recorded test footage

Both unmodified MP4 files come from NVIDIA's public [Maxine VFX SDK samples/input directory](https://github.com/NVIDIA-Maxine/Maxine-VFX-SDK/tree/f12bd18929e9065cff4f24cb93ac5f4202dc1c4a/samples/input). Revision: f12bd18929e9065cff4f24cb93ac5f4202dc1c4a. Downloaded October 21, 2026 from the official repository archive. NVIDIA states that samples/ except external/ is governed by MIT; that notice is included in NVIDIA-LICENSE.txt.

| File | SHA-256 |
| --- | --- |
| input_0_100_frames.mp4 | c50228656559502838e136d5e3b8525f1a0c54d259dafe38e461f50e8abbdf5b |
| input_100_200_frames.mp4 | 9808ea67384238beef4d3e81665f38f31fc77679422d442a2b210a8cebe7333d |

Each contains 100 frames, 1920 × 1080, 30 fps, MPEG-4 Part 2 video, about 3.33 seconds. They are adjacent excerpts of the same seated speaker, not two independent subjects. Test coverage includes a moving face, hair, shoulders, clothing, and a detailed room. They do not establish quality for every person, light level, hands, multiple people, or full-body movement. 4K testing resizes the 1080p footage and checks buffer/performance compatibility; it is not a native 4K detail test.
