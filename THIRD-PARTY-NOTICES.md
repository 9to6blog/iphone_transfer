# Third-party components

This application includes the Microsoft .NET 8 runtime (Microsoft, MIT),
iMobileDevice-net 1.3.17 (Quamotion, LGPL-2.1), and the native libraries supplied
by that package. The package is not modified; our application code uses its public API.

- .NET runtime and WPF: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- Managed binding and native build source: https://github.com/libimobiledevice-win32/imobiledevice-net/tree/d043dda518cc4df830c37f8c85d3f19384978541
- libimobiledevice / libusbmuxd / libplist: https://libimobiledevice.org
- OpenSSL 1.1: https://github.com/openssl/openssl/tree/OpenSSL_1_1_1-stable
- libxml2: https://gitlab.gnome.org/GNOME/libxml2
- curl: https://github.com/curl/curl
- libusb: https://github.com/libusb/libusb
- zlib: https://zlib.net
- libzip: https://libzip.org
- bzip2: https://sourceware.org/bzip2
- xz / liblzma: https://tukaani.org/xz/
- PCRE: https://www.pcre.org/
- libiconv: https://www.gnu.org/software/libiconv/
- pthreads-win32: https://sourceware.org/pthreads-win32/

The original binding license is in licenses/iMobileDevice-net-LICENSE.txt.
The licenses directory also includes the .NET and WPF license and .NET third-party notices.
Component licenses and source are available at the upstream links above.
Apple Devices, iTunes and Apple USB drivers are obtained separately from Apple
or Microsoft Store and are not included in this distribution.

## Media previews (2.1)

- Magick.NET-Q8-x64 / Magick.NET.Core 14.17.1, Apache-2.0:
  https://github.com/dlemstra/Magick.NET/tree/298053457c16f413a9df6edaca668b12289bea2d
  Includes ImageMagick, libheif and libde265. Full component notices and licenses
  are in `licenses/Magick.NET-NOTICE.txt` and `licenses/Magick.NET-LICENSE.txt`.
- FFmpeg 9.0.2 essentials Windows build by Gyan, GPL v3, invoked as a separate executable:
  https://www.gyan.dev/ffmpeg/builds/ ; source revision
  https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
  Original build details and license: `licenses/ffmpeg-BUILD.txt`, `licenses/ffmpeg-LICENSE.txt`.
  Pinned binary/archive hashes: `vendor/ffmpeg/README.md` in the source repository.
- Embedded HEIC verification fixture from libheif examples, MIT:
  https://github.com/strukturag/libheif/tree/5c7b41f3cc097447dd3c700cc9ec7d94fbb59eec/examples
  See `licenses/libheif-example-LICENSE.txt`.
