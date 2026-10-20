#!/usr/bin/env bash
# Builds DotCharter's minimal static ffmpeg.exe inside an MSYS2 MINGW64 shell.
# Driven by Build-MinimalFfmpeg.ps1, which downloads and hash-checks every
# source tarball first and passes the configure flags it derived from
# components.json. Do not run this by hand without those variables.
#
#   DL_DIR      folder holding the verified tarballs
#   WORK_DIR    scratch folder (wiped and rebuilt)
#   JOBS        make -j value
#   FFMPEG_VERSION, OGG_VERSION, VORBIS_VERSION, OPUS_VERSION, LAME_VERSION, ZLIB_VERSION
#   FFMPEG_COMPONENT_ARGS  the --enable-<kind>=<name> flags, space-separated
set -euo pipefail

: "${DL_DIR:?}" "${WORK_DIR:?}" "${JOBS:?}" "${FFMPEG_COMPONENT_ARGS:?}" "${FFMPEG_EXTRA_VERSION:?}"
: "${FFMPEG_VERSION:?}" "${OGG_VERSION:?}" "${VORBIS_VERSION:?}" "${OPUS_VERSION:?}" "${LAME_VERSION:?}" "${ZLIB_VERSION:?}"

DL_DIR=$(cygpath -u "$DL_DIR")
WORK_DIR=$(cygpath -u "$WORK_DIR")
PREFIX="$WORK_DIR/prefix"
SRC="$WORK_DIR/src"
rm -rf "$WORK_DIR"
mkdir -p "$PREFIX/include" "$PREFIX/lib/pkgconfig" "$SRC"

export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
export CFLAGS="-O2 -I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib"

unpack() { tar -xf "$DL_DIR/$1" -C "$SRC"; }

echo "== zlib $ZLIB_VERSION"
unpack "zlib-$ZLIB_VERSION.tar.gz"
( cd "$SRC/zlib-$ZLIB_VERSION"
  make -f win32/Makefile.gcc -j"$JOBS" libz.a
  cp libz.a "$PREFIX/lib/" && cp zlib.h zconf.h "$PREFIX/include/" )

echo "== libogg $OGG_VERSION"
unpack "libogg-$OGG_VERSION.tar.xz"
( cd "$SRC/libogg-$OGG_VERSION"
  ./configure --prefix="$PREFIX" --disable-shared --enable-static
  make -j"$JOBS" && make install )

echo "== libvorbis $VORBIS_VERSION"
unpack "libvorbis-$VORBIS_VERSION.tar.xz"
( cd "$SRC/libvorbis-$VORBIS_VERSION"
  ./configure --prefix="$PREFIX" --disable-shared --enable-static --disable-docs --disable-examples --disable-oggtest
  make -j"$JOBS" && make install )

echo "== opus $OPUS_VERSION"
unpack "opus-$OPUS_VERSION.tar.gz"
( cd "$SRC/opus-$OPUS_VERSION"
  # No stack protector: mingw would need libssp for __stack_chk_fail.
  ./configure --prefix="$PREFIX" --disable-shared --enable-static --disable-doc \
      --disable-extra-programs --disable-stack-protector
  make -j"$JOBS" && make install )

echo "== lame $LAME_VERSION"
unpack "lame-$LAME_VERSION.tar.gz"
( cd "$SRC/lame-$LAME_VERSION"
  ./configure --prefix="$PREFIX" --disable-shared --enable-static --disable-frontend \
      --disable-decoder --enable-nasm
  make -j"$JOBS" && make install )

echo "== ffmpeg $FFMPEG_VERSION"
unpack "ffmpeg-$FFMPEG_VERSION.tar.xz"
cd "$SRC/ffmpeg-$FFMPEG_VERSION"
# LGPL: no --enable-gpl / --enable-nonfree. --disable-autodetect keeps every
# system library (schannel, mediafoundation, d3d, ...) out, so the binary
# carries only what components.json names. w32threads is autodetected, so it
# is switched back on explicitly.
# The configure line is compiled into the exe (-buildconf) and shipped in
# ffmpeg-BUILDINFO.txt, so it carries no machine path: the default prefix
# (nothing is installed) and include/lib paths RELATIVE to this in-tree build.
# shellcheck disable=SC2086
./configure \
    --extra-version="$FFMPEG_EXTRA_VERSION" \
    --pkg-config-flags=--static \
    --extra-cflags="-I../../prefix/include" \
    --extra-ldflags="-L../../prefix/lib -static" \
    --disable-autodetect --enable-w32threads \
    --disable-everything --disable-network --disable-doc --disable-debug \
    --disable-ffplay --disable-ffprobe --disable-avdevice \
    --enable-small \
    --enable-zlib --enable-libvorbis --enable-libopus --enable-libmp3lame \
    $FFMPEG_COMPONENT_ARGS
make -j"$JOBS"

mkdir -p "$WORK_DIR/out/bin"
cp ffmpeg.exe "$WORK_DIR/out/bin/ffmpeg.exe"   # the stripped one; ffmpeg_g.exe keeps symbols
cp "COPYING.LGPLv2.1" "$WORK_DIR/out/ffmpeg-LICENSE.txt"
{
  echo "ffmpeg $FFMPEG_VERSION ($FFMPEG_EXTRA_VERSION), LGPL v2.1+ static win64"
  echo "Sources: ffmpeg-$FFMPEG_VERSION libogg-$OGG_VERSION libvorbis-$VORBIS_VERSION opus-$OPUS_VERSION lame-$LAME_VERSION zlib-$ZLIB_VERSION"
  echo "Toolchain: $(gcc --version | head -n1); $(nasm -v)"
  echo "Configure:"
  ./ffmpeg -hide_banner -buildconf
} > "$WORK_DIR/out/ffmpeg-BUILDINFO.txt"
echo "== done: $WORK_DIR/out/bin/ffmpeg.exe"
