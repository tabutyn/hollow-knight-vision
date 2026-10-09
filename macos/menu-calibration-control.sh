#!/bin/zsh
set -euo pipefail

case "${1:-}" in
  status) hkv_command="menu-calibration-status" ;;
  capture)
    if (( $# == 1 )); then
      hkv_command="menu-calibration-capture"
    elif (( $# == 3 )); then
      hkv_command="menu-calibration-capture:$2:$3"
    elif (( $# == 4 )); then
      hkv_command="menu-calibration-capture:$2:$3:$4"
    else
      print -u2 "usage: $0 capture [language-identifier] [context-identifier selected-identifier]"
      exit 64
    fi
    ;;
  *) print -u2 "usage: $0 status|capture [language-identifier] [context-identifier selected-identifier]"; exit 64 ;;
esac

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

HKV_AUTOMATION_COMMAND="$hkv_command" /usr/bin/swift -e "$hkv_native_pasteboard"'
let command = ProcessInfo.processInfo.environment["HKV_AUTOMATION_COMMAND"] ?? ""
let deadline = ProcessInfo.processInfo.systemUptime + 5
var reply = pasteboard.string(forType: .string) ?? command
while reply == command && ProcessInfo.processInfo.systemUptime < deadline {
    Thread.sleep(forTimeInterval: 0.05)
    reply = pasteboard.string(forType: .string) ?? command
}
guard reply != command else { exit(69) }
print(reply)
'
