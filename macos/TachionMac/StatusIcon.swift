import AppKit

enum StatusIcon {
    static func make(running: Bool, connected: Bool) -> NSImage {
        let size = NSSize(width: 18, height: 18)
        let image = NSImage(size: size)
        image.lockFocus()
        NSColor.clear.setFill(); NSRect(origin: .zero, size: size).fill()
        let baseColor: NSColor
        if connected { baseColor = NSColor(calibratedRed: 0.25, green: 0.62, blue: 0.35, alpha: 1) }
        else if running { baseColor = NSColor(calibratedRed: 0.72, green: 0.48, blue: 0.22, alpha: 1) }
        else { baseColor = NSColor(calibratedRed: 0.70, green: 0.28, blue: 0.32, alpha: 1) }
        let outer = NSBezierPath(ovalIn: NSRect(x: 2.0, y: 2.5, width: 14.0, height: 13.0))
        baseColor.setFill(); outer.fill()
        let highlight = NSBezierPath(ovalIn: NSRect(x: 5.0, y: 9.0, width: 6.0, height: 4.0))
        baseColor.blended(withFraction: 0.45, of: .white)?.setFill(); highlight.fill()
        let inner = NSBezierPath(ovalIn: NSRect(x: 8.0, y: 6.0, width: 5.6, height: 6.8))
        NSColor(calibratedWhite: 0.10, alpha: 0.55).setFill(); inner.fill()
        image.unlockFocus()
        image.isTemplate = false
        return image
    }
}
