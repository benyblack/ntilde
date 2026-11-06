#!/bin/bash
# Profile command: `/bin/bash run-multipart.sh <log> <image>`. Draws <image> with iTerm2's imgcat in
# its default multipart form, then with -l (File=), and holds the screen for capture.
out="$1"; image="$2"
imgcat=/Applications/iTerm.app/Contents/Resources/utilities/imgcat
echo "TERM=$TERM NTILDE_MUX_SESSION=${NTILDE_MUX_SESSION-none} iTerm2 $(defaults read /Applications/iTerm.app/Contents/Info.plist CFBundleShortVersionString 2>/dev/null)" > "$out.env"
"$imgcat" -W 8 "$image" > "$out.multipart.bytes"
clear
echo "multipart (imgcat, default):"
"$imgcat" -W 8 "$image"
echo "File= (imgcat -l):"
"$imgcat" -l -W 8 "$image"
echo "done"
date > "$out.done"
sleep 3600
