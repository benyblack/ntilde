#!/bin/bash
# Profile command for the #838 check: `/bin/bash run-images.sh <log> <image>`.
# Asks #838's Kitty probe and records the reply, then draws <image> with notcurses' ncplayer
# (Kitty graphics, when the terminal answers the probe) and iTerm2's imgcat (OSC 1337: the
# File= form with -l, then its default multipart form), and holds the screen so it can be captured. Writes <log>.env and <log>.probe; <log>.done last.
out="$1"; image="$2"
imgcat=/Applications/iTerm.app/Contents/Resources/utilities/imgcat
{
  echo "TERM=$TERM TERM_PROGRAM=$TERM_PROGRAM NTILDE_MUX_SESSION=${NTILDE_MUX_SESSION-none}"
  echo "ncplayer: $(ncplayer -V 2>&1 | head -1)"
  echo "imgcat: iTerm2 $(defaults read /Applications/iTerm.app/Contents/Info.plist CFBundleShortVersionString 2>/dev/null)"
} > "$out.env"

# #838's probe, exactly as terminfo.dev sends it; the reply ends with ESC backslash.
saved=$(stty -g); stty raw -echo
printf '\033_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\033\\'
IFS= read -r -s -t 3 -d '\' reply
stty "$saved"
printf 'probe reply: %q\n' "$reply" > "$out.probe"

clear
echo "Kitty graphics (ncplayer -b pixel -k -s none):"
ncplayer -b pixel -k -s none "$image" 2>"$out.ncplayer.err"
echo "ncplayer exit $?" >> "$out.env"
echo
echo "iTerm2 inline image, File= (imgcat -l):"
"$imgcat" -l -W 8 "$image"
echo "iTerm2 multipart image, MultipartFile= (imgcat, its default):"
"$imgcat" -W 8 "$image"
echo
echo "done"
date > "$out.done"
sleep 3600
