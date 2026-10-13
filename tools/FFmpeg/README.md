# Local FFmpeg Tools

This directory contains the local Windows x64 FFmpeg tools used by PodcastBatchCleaner.

- Build: Gyan FFmpeg 9.0.2 essentials (includes libmp3lame, ffprobe and ffplay).
- Source: https://www.gyan.dev/ffmpeg/builds/
- Download: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip
- Archive SHA-256: 60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba
- License: GPLv3; see the original LICENSE included with these tools.

The executables are local dependencies and are excluded from Git. The program searches this directory from its executable location and ancestor directories.

FFmpeg's project download page lists Gyan as a Windows binary provider: https://ffmpeg.org/download.html
