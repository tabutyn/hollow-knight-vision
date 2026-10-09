#!/bin/zsh
set -euo pipefail

if [[ "${1:-}" == replay ]]; then
  if (( $# != 3 )); then
    print -u2 "usage: $0 replay path-file.json iteration"
    exit 64
  fi
  hkv_command="replay-path:$2:$3"
else
  if (( $# < 1 || $# > 2 )); then
    print -u2 "usage: $0 left|right|down|up|a|z|x|inventory|pause [seconds]"
    print -u2 "       $0 replay path-file.json iteration"
    exit 64
  fi
  hkv_command="$1:${2:-1.5}"
fi
# macOS 26's JavaScript-for-Automation bridge returns nil for named
# NSPasteboards. Use the same native AppKit API as Vision so the test channel
# remains isolated from the user's general clipboard.
hkv_native_pasteboard='import AppKit
import Foundation

let pasteboard = NSPasteboard(name: .init(
    "com.ballroller.hollow-knight-vision.automation-pasteboard"
))'

HKV_AUTOMATION_COMMAND="$hkv_command" /usr/bin/swift -e "$hkv_native_pasteboard"'
let command = ProcessInfo.processInfo.environment["HKV_AUTOMATION_COMMAND"] ?? ""
pasteboard.clearContents()
guard pasteboard.setString(command, forType: .string) else { exit(70) }
'

/usr/bin/notifyutil -p com.ballroller.hollow-knight-vision.automation-command
sleep 0.2

hkv_reply=$(/usr/bin/swift -e "$hkv_native_pasteboard"'
let deadline = ProcessInfo.processInfo.systemUptime + 5
var reply = pasteboard.string(forType: .string) ?? "missing"
while reply != "accepted" && reply != "rejected"
    && ProcessInfo.processInfo.systemUptime < deadline {
    Thread.sleep(forTimeInterval: 0.05)
    reply = pasteboard.string(forType: .string) ?? "missing"
}
print(reply)
')

if [[ "$hkv_reply" != "accepted" ]]; then
  if [[ "$hkv_reply" == rejected ]]; then
    print -u2 "Vision rejected automation command"
  else
    print -u2 "Vision did not acknowledge automation command within 5s: $hkv_reply"
  fi
  exit 69
fi

print "accepted $hkv_command"
