# Pinned video preview decoder

`ffmpeg-9.0.2-win-x64.zip` contains only `ffmpeg.exe` from the installed
Gyan 9.0.2 essentials build. The archive is retained as a source dependency;
MSBuild verifies both hashes and extracts it before building. No PATH lookup
or runtime download is used by the application.

- Build provider: https://www.gyan.dev/ffmpeg/builds/
- FFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
- License: GPL v3; see `../../licenses/ffmpeg-LICENSE.txt`.
- Archive SHA-256: `9114190312DF510AC9C79CA8F16A1FA0F18B7E41691EE500BC062546C8F9E768`
- Executable SHA-256: `3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC`

The app invokes this separate executable to obtain a video frame and does
not link FFmpeg libraries. Its original build README is included alongside
the license in distributions.
