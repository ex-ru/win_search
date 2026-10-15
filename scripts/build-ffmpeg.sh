#!/usr/bin/env bash
# Build the pinned local-file FFmpeg tools; no external codec libraries or GPL components.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
output="${1:-$repo/state/ffmpeg-build}"
mkdir -p "$output/source" "$output/build" "$output/tools/licenses/ffmpeg" "$output/sources"
output="$(cd "$output" && pwd)"
readarray -t source_info < <(python3 - "$repo/native-dependencies.json" <<'PY'
import json,sys
s=json.load(open(sys.argv[1]))['ffmpegSource']
print(s['url']);print(s['sha256']);print(s['version'])
PY
)
archive="$output/sources/ffmpeg-${source_info[2]}.tar.xz"
curl --fail --location --retry 3 --connect-timeout 30 --max-time 240 "${source_info[0]}" --output "$archive"
printf '%s  %s\n' "${source_info[1]}" "$archive" | sha256sum --check
tar -xJf "$archive" -C "$output/source" --strip-components=1
cd "$output/build"
configuration=(
  --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32-
  --enable-cross-compile --disable-autodetect --disable-doc --disable-debug
  --disable-ffplay --disable-avdevice --disable-network --disable-gpl
  --disable-version3 --disable-nonfree --enable-small --enable-static --disable-shared
  --extra-ldflags=-static
)
printf '%s\n' "${configuration[@]}" > "$output/tools/licenses/ffmpeg/configure-arguments.txt"
"$output/source/configure" "${configuration[@]}"
make -j "${WAI_BUILD_JOBS:-4}" ffmpeg.exe ffprobe.exe
cp ffmpeg.exe ffprobe.exe "$output/tools/"
cp "$output/source/COPYING.LGPLv2.1" "$output/source/LICENSE.md" "$output/tools/licenses/ffmpeg/"
cp "$repo/scripts/build-ffmpeg.sh" "$output/sources/"
for package in mingw-w64-common gcc-mingw-w64-base; do
  cp "/usr/share/doc/$package/copyright" "$output/tools/licenses/ffmpeg/$package-copyright.txt"
done
dpkg-query -W gcc-mingw-w64-x86-64 gcc-mingw-w64-base mingw-w64-common nasm make > "$output/tools/licenses/ffmpeg/toolchain-packages.txt"
x86_64-w64-mingw32-gcc --version > "$output/tools/licenses/ffmpeg/compiler-version.txt"
cp config.log "$output/tools/licenses/ffmpeg/"
python3 - "$repo" "$output" <<'PY'
import hashlib,json,pathlib,subprocess,sys,zipfile
repo,out=map(pathlib.Path,sys.argv[1:])
lock=json.loads((repo/'native-dependencies.json').read_text())
files={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (out/'tools').glob('*.exe')}
dependencies={}
for p in (out/'tools').glob('*.exe'):
    text=subprocess.check_output(['x86_64-w64-mingw32-objdump','-p',str(p)],text=True)
    dlls=[line.split('DLL Name:',1)[1].strip() for line in text.splitlines() if 'DLL Name:' in line]
    # Compiler runtime DLLs must never be silently absent from the distribution.
    assert not any(x.lower().startswith(('libgcc','libstdc++','libwinpthread')) for x in dlls),dlls
    dependencies[p.name]=dlls
manifest={'source':lock['ffmpegSource'],'configuration':(out/'tools/licenses/ffmpeg/configure-arguments.txt').read_text().splitlines(),'files':files,'importedDlls':dependencies,'externalCodecLibraries':[],'license':'LGPL-2.1-or-later'}
(out/'tools/licenses/ffmpeg/build.json').write_text(json.dumps(manifest,indent=2)+'\n')
for directory,name in [('tools','ffmpeg-tools-win-x64.zip'),('sources','ffmpeg-corresponding-source.zip')]:
    with zipfile.ZipFile(out/name,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in sorted((out/directory).rglob('*')):
            if p.is_file():z.write(p,p.relative_to(out/directory))
for name in ['ffmpeg-tools-win-x64.zip','ffmpeg-corresponding-source.zip']:
    p=out/name
    print(json.dumps({'name':name,'size':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()}))
PY
