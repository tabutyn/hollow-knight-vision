#!/bin/zsh
set -euo pipefail

case "${1:-}" in
  status) hkv_command="bot-demo-status" ;;
  stop) hkv_command="bot-demo-stop" ;;
  mark-return) hkv_command="bot-mark-return" ;;
  return) hkv_command="bot-return" ;;
  restore-start) (( $# == 2 )) || exit 64; hkv_command="bot-restore-start:$2" ;;
  *) print -u2 "usage: $0 status|stop|mark-return|return|restore-start path-file.json"; exit 64 ;;
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
