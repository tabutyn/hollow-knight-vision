#!/bin/zsh
set -euo pipefail

if (( $# != 2 )); then
  print -u2 "usage: $0 path-file.json iteration"
  exit 64
fi

if [[ "$1" != "${1:t}" || "$1" != path-*.json ]]; then
  print -u2 "path must be a saved input-path filename"
  exit 64
fi

if (( $2 < 1 || $2 > 100 )); then
  print -u2 "iteration must be between 1 and 100"
  exit 64
fi

hkv_command="replay-path:$1:$2"
HKV_AUTOMATION_COMMAND="$hkv_command" /usr/bin/osascript -l JavaScript -e '
ObjC.import("Foundation")
ObjC.import("AppKit")
const environment = $.NSProcessInfo.processInfo.environment
const command = ObjC.unwrap(environment.objectForKey("HKV_AUTOMATION_COMMAND"))
const pasteboard = $.NSPasteboard.pasteboardWithName(
  "com.ballroller.hollow-knight-vision.automation-pasteboard"
)
pasteboard.clearContents
pasteboard.setStringForType(command, $.NSPasteboardTypeString)
null
'

/usr/bin/notifyutil -p com.ballroller.hollow-knight-vision.automation-command
sleep 0.2

hkv_reply=$(/usr/bin/osascript -l JavaScript -e '
ObjC.import("Foundation")
ObjC.import("AppKit")
const pasteboard = $.NSPasteboard.pasteboardWithName(
  "com.ballroller.hollow-knight-vision.automation-pasteboard"
)
const reply = pasteboard.stringForType($.NSPasteboardTypeString)
reply ? ObjC.unwrap(reply) : "missing"
')

if [[ "$hkv_reply" != "accepted" ]]; then
  print -u2 "Vision rejected path playback: $hkv_reply"
  exit 69
fi

print "accepted $hkv_command"
