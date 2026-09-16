#!/usr/bin/env python3
# TOOLING — not a gate (tests/gates-tooling.txt).
#
# Preflight for the Avalonia -6661 launch failure (#1497, tracked upstream as
# AvaloniaUI/Avalonia#18895): can CVDisplayLinkCreateWithActiveCGDisplays
# succeed right now?
#
# `Avalonia.Native was not able to start the RenderTimer. Native error code is:
# -6661` is thrown when that call fails in PlatformRenderTimer.mm. The failure
# comes from CoreVideo finding no usable active display — a lid closed with no
# external display, a push-notification wake, or a WindowServer demote/promote
# race where CGMainDisplayID() == 0 while displays are active. It is display
# STATE that sticks until logout, not a per-login launch quota: an earlier
# project note claimed the latter and was wrong.
#
# So a failed launch is cheap and loud, and relaunching is fine — but a -6661
# means stop and wait for a logout rather than retry in a loop. This script
# lets an unattended harness make that call before it burns a launch.
#
# Exit 0 when a launch should work, 1 when it would fail with -6661.
import ctypes

cg = ctypes.CDLL('/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics')
cv = ctypes.CDLL('/System/Library/Frameworks/CoreVideo.framework/CoreVideo')
cg.CGMainDisplayID.restype = ctypes.c_uint32

ids = (ctypes.c_uint32 * 16)()
count = ctypes.c_uint32()
list_err = cg.CGGetActiveDisplayList(16, ids, ctypes.byref(count))

link = ctypes.c_void_p()
rc = cv.CVDisplayLinkCreateWithActiveCGDisplays(ctypes.byref(link))
if link.value:
    cv.CVDisplayLinkRelease(link)

main_display = cg.CGMainDisplayID()
print(f"CGMainDisplayID={main_display} activeDisplays={count.value} "
      f"(err {list_err}) CVDisplayLinkCreateWithActiveCGDisplays={rc}")

ok = rc == 0 and main_display != 0 and count.value > 0
if not ok:
    print("DISPLAY WEDGE: a GUI launch would fail with -6661. "
          "Try System Settings > Displays (reconfigure), or log out and back in. "
          "Do not retry in a loop.")
raise SystemExit(0 if ok else 1)
