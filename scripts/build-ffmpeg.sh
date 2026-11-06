#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
src="$root/.tools/sources/ffmpeg-9.0.2.tar.xz"
echo '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e  '"$src" | sha256sum -c -
# Build on the Linux filesystem, avoiding slow compiler IO on /mnt/c.
build="$(mktemp -d /tmp/sarucast-ffmpeg.XXXXXXXX)"
trap 'rm -rf -- "$build"' EXIT
tar -xJf "$src" -C "$build"
cd "$build/ffmpeg-9.0.2"
./configure --target-os=mingw32 --arch=x86_64 --enable-cross-compile \
 --cross-prefix=x86_64-w64-mingw32- --disable-x86asm --disable-autodetect \
 --disable-everything --disable-doc --disable-debug --disable-network \
 --enable-ffmpeg --enable-ffprobe --enable-small --enable-w32threads \
 --enable-protocol=file,pipe --enable-indev=lavfi,gdigrab \
 --enable-filter=ddagrab,hwdownload,format,scale,aresample,aformat,anull,anullsrc,testsrc2,sine,null,signalstats,metadata \
 --enable-encoder=h264_mf,aac,pcm_s16le,wrapped_avframe --enable-decoder=pcm_s16le,rawvideo,h264,aac,wrapped_avframe \
 --enable-parser=h264,aac --enable-demuxer=pcm_s16le,rawvideo,mpegts,mov \
 --enable-muxer=hls,mpegts,mp4,adts,null --enable-bsf=h264_mp4toannexb,aac_adtstoasc \
 --enable-mediafoundation --enable-d3d11va --extra-ldflags=-static
make -j "${SARUCAST_JOBS:-8}"
mkdir -p "$root/.tools/ffmpeg" "$root/artifacts/ffmpeg-build"
cp ffmpeg.exe ffprobe.exe "$root/.tools/ffmpeg/"
cp config.h config_components.h ffbuild/config.log ffbuild/config.mak "$root/artifacts/ffmpeg-build/"
cp COPYING.LGPLv2.1 COPYING.LGPLv3 LICENSE.md "$root/.tools/ffmpeg/"
x86_64-w64-mingw32-objdump -p ffmpeg.exe | sed -n '/DLL Name/p' > "$root/artifacts/ffmpeg-build/imports.txt"
./ffbuild/version.sh . > "$root/artifacts/ffmpeg-build/version.txt"
