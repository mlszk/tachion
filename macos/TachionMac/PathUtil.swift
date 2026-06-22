import Foundation

enum PathUtil {
    static func relativeUnixPath(root: URL, file: URL) -> String? {
        let rootPath = root.standardizedFileURL.path
        let filePath = file.standardizedFileURL.path
        let prefix = rootPath.hasSuffix("/") ? rootPath : rootPath + "/"
        guard filePath.hasPrefix(prefix) else { return nil }
        return String(filePath.dropFirst(prefix.count)).replacingOccurrences(of: "\\", with: "/")
    }

    static func safeFileURL(root: URL, relativePath: String) throws -> URL {
        let cleaned = relativePath.replacingOccurrences(of: "\\", with: "/").trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let parts = cleaned.split(separator: "/").map(String.init)
        if parts.isEmpty || parts.contains("..") { throw TachionError.badPath }
        var url = root
        for part in parts { url.appendPathComponent(part) }
        let rootPath = root.standardizedFileURL.path
        let filePath = url.standardizedFileURL.path
        guard filePath == rootPath || filePath.hasPrefix(rootPath + "/") else { throw TachionError.badPath }
        return url
    }
}

enum TachionError: Error { case badPath, notConnected, badMessage }
