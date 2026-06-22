import CryptoKit
import Foundation

struct SyncConfig {
    let syncDir: String
    let syncUrl: String
    let syncName: String
    let token: String
}

final class SyncClient {
    private let config: SyncConfig
    private let log: (String) -> Void
    private let status: (Bool) -> Void

    private var mainTask: Task<Void, Never>?
    private var pollTask: Task<Void, Never>?
    private var ws: URLSessionWebSocketTask?
    private var knownManifest: [String: Int64] = [:]
    private var recentRemoteDeletes: [String: Date] = [:]

    private var rootURL: URL {
        URL(fileURLWithPath: config.syncDir, isDirectory: true)
    }

    init(config: SyncConfig, log: @escaping (String) -> Void, status: @escaping (Bool) -> Void) {
        self.config = config
        self.log = log
        self.status = status
    }

    func start() {
        try? FileManager.default.createDirectory(at: rootURL, withIntermediateDirectories: true)
        knownManifest = buildManifest()
        mainTask = Task { [weak self] in
            await self?.runLoop()
        }
        pollTask = Task { [weak self] in
            await self?.pollLoop()
        }
    }

    func stop() {
        mainTask?.cancel()
        pollTask?.cancel()
        ws?.cancel(with: .goingAway, reason: nil)
        ws = nil
        status(false)
        log("Stopped.")
    }

    private func runLoop() async {
        guard let url = URL(string: config.syncUrl) else {
            log("Bad sync URL.")
            return
        }

        while !Task.isCancelled {
            do {
                let session = URLSession(configuration: .default)
                let socket = session.webSocketTask(with: url)
                socket.maximumMessageSize = 64 * 1024 * 1024
                ws = socket
                socket.resume()

                try await sendHello(socket)
                status(true)
                log("Connected.")

                try await receiveLoop(socket)
            } catch {
                status(false)
                log("Connection error: \(error.localizedDescription)")
                ws?.cancel(with: .goingAway, reason: nil)
                ws = nil
                try? await Task.sleep(nanoseconds: 3_000_000_000)
            }
        }
    }

    private func pollLoop() async {
        while !Task.isCancelled {
            try? await Task.sleep(nanoseconds: 1_000_000_000)

            let current = buildManifest()

            // Local deletes.
            for rel in Array(knownManifest.keys) {
                if current[rel] == nil {
                    if isCoveredByRecentRemoteDelete(rel) {
                        knownManifest.removeValue(forKey: rel)
                        continue
                    }

                    let sent = await sendDelete(rel: rel)
                    if sent {
                        knownManifest.removeValue(forKey: rel)
                        log("Deleted \(rel)")
                    }
                }
            }

            // New or changed files. This also handles whole-folder copy/move,
            // because polling sees all files inside the new folder.
            for (rel, mtime) in current {
                let old = knownManifest[rel]
                if old == nil || mtime > old! {
                    let sent = await sendPut(rel: rel)
                    if sent {
                        knownManifest[rel] = mtime
                    }
                }
            }
        }
    }

    private func buildManifest() -> [String: Int64] {
        var result: [String: Int64] = [:]

        guard let enumerator = FileManager.default.enumerator(
            at: rootURL,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else {
            return result
        }

        for case let url as URL in enumerator {
            guard shouldIgnore(url) == false else { continue }

            guard (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else {
                continue
            }

            guard let rel = PathUtil.relativeUnixPath(root: rootURL, file: url) else {
                continue
            }

            result[rel] = mtimeNs(url: url)
        }

        return result
    }

    private func sendHello(_ socket: URLSessionWebSocketTask) async throws {
        try await sendJSON([
            "type": "hello",
            "token": config.token,
            "name": config.syncName,
            "manifest": knownManifest
        ], socket: socket)
    }

    private func receiveLoop(_ socket: URLSessionWebSocketTask) async throws {
        while !Task.isCancelled {
            let message = try await socket.receive()

            switch message {
            case .string(let text):
                await handleMessage(text)
            case .data(let data):
                if let text = String(data: data, encoding: .utf8) {
                    await handleMessage(text)
                } else {
                    log("Ignored binary WebSocket message.")
                }
            @unknown default:
                log("Ignored unknown WebSocket message.")
            }
        }
    }

    private func handleMessage(_ json: String) async {
        do {
            guard
                let data = json.data(using: .utf8),
                let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                let type = obj["type"] as? String
            else {
                log("Ignored malformed server message.")
                return
            }

            if type == "put" {
                try applyPut(obj)
            } else if type == "delete" {
                try applyDelete(obj)
            } else if type == "request" {
                let rawPaths = obj["paths"] as? [Any] ?? []
                for item in rawPaths {
                    if let rel = item as? String {
                        _ = await sendPut(rel: rel)
                    }
                }
            } else {
                log("Ignored server message type: \(type)")
            }
        } catch {
            log("Ignored bad server message: \(error.localizedDescription)")
        }
    }

    private func applyPut(_ msg: [String: Any]) throws {
        guard
            let rel = msg["path"] as? String,
            shouldIgnore(rel) == false,
            let encoded = msg["data"] as? String,
            let data = Data(base64Encoded: encoded)
        else {
            log("Ignored malformed put message.")
            return
        }

        let mtime = try parseMtime(msg)

        if let expectedHash = msg["hash"] as? String,
           sha256Hex(data) != expectedHash {
            log("Dropped \(rel): hash mismatch.")
            return
        }

        let dest = try PathUtil.safeFileURL(root: rootURL, relativePath: rel)

        if FileManager.default.fileExists(atPath: dest.path),
           mtimeNs(url: dest) >= mtime {
            return
        }

        try FileManager.default.createDirectory(
            at: dest.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )

        let tmp = dest.deletingLastPathComponent()
            .appendingPathComponent(dest.lastPathComponent + ".tmp.\(ProcessInfo.processInfo.processIdentifier)")

        try data.write(to: tmp, options: .atomic)

        if FileManager.default.fileExists(atPath: dest.path) {
            try FileManager.default.removeItem(at: dest)
        }

        try FileManager.default.moveItem(at: tmp, to: dest)

        try? FileManager.default.setAttributes([
            .modificationDate: Date(timeIntervalSince1970: Double(mtime) / 1_000_000_000.0)
        ], ofItemAtPath: dest.path)

        knownManifest[rel] = mtime
        log("Received \(rel)")
    }

    private func applyDelete(_ msg: [String: Any]) throws {
        guard
            let rel = msg["path"] as? String,
            shouldIgnore(rel) == false
        else {
            log("Ignored malformed delete message.")
            return
        }

        let full = try PathUtil.safeFileURL(root: rootURL, relativePath: rel)
        recentRemoteDeletes[rel] = Date().addingTimeInterval(5)

        do {
            if FileManager.default.fileExists(atPath: full.path) {
                try FileManager.default.removeItem(at: full)
                log("Deleted remote \(rel)")
            }

            for key in Array(knownManifest.keys) {
                if isSameOrChild(key, rel) {
                    knownManifest.removeValue(forKey: key)
                }
            }

            cleanupEmptyParents(startingAt: full.deletingLastPathComponent())
        } catch {
            log("Remote delete failed \(rel): \(error.localizedDescription)")
        }
    }

    private func sendDelete(rel: String) async -> Bool {
        guard shouldIgnore(rel) == false else { return false }
        guard let socket = ws else { return false }

        let deleteNs = Int64(Date().timeIntervalSince1970 * 1_000_000_000.0)

        do {
            try await sendJSON([
                "type": "delete",
                "path": rel,
                "delete_ns": deleteNs,
                "mtime_ns": deleteNs
            ], socket: socket)
            return true
        } catch {
            log("Delete send failed \(rel): \(error.localizedDescription)")
            return false
        }
    }

    private func sendPut(rel: String) async -> Bool {
        guard shouldIgnore(rel) == false else { return false }
        guard let socket = ws else { return false }

        do {
            let fileURL = try PathUtil.safeFileURL(root: rootURL, relativePath: rel)
            guard FileManager.default.fileExists(atPath: fileURL.path) else {
                return false
            }

            let data = try await readStableFile(fileURL)
            let mtime = mtimeNs(url: fileURL)

            try await sendJSON([
                "type": "put",
                "path": rel,
                "mtime_ns": mtime,
                "hash": sha256Hex(data),
                "data": data.base64EncodedString()
            ], socket: socket)

            log("Sent \(rel)")
            return true
        } catch {
            log("Send failed for \(rel): \(error.localizedDescription)")
            return false
        }
    }

    private func readStableFile(_ url: URL) async throws -> Data {
        var lastError: Error?

        for _ in 0..<8 {
            do {
                let info1 = try FileManager.default.attributesOfItem(atPath: url.path)
                let len1 = info1[.size] as? UInt64 ?? 0
                let mt1 = info1[.modificationDate] as? Date ?? .distantPast

                try await Task.sleep(nanoseconds: 150_000_000)

                let info2 = try FileManager.default.attributesOfItem(atPath: url.path)
                let len2 = info2[.size] as? UInt64 ?? 0
                let mt2 = info2[.modificationDate] as? Date ?? .distantPast

                guard len1 == len2, mt1 == mt2 else {
                    throw CocoaError(.fileReadUnknown)
                }

                let data = try Data(contentsOf: url)

                let info3 = try FileManager.default.attributesOfItem(atPath: url.path)
                let len3 = info3[.size] as? UInt64 ?? 0
                let mt3 = info3[.modificationDate] as? Date ?? .distantPast

                guard len2 == len3, mt2 == mt3 else {
                    throw CocoaError(.fileReadUnknown)
                }

                return data
            } catch {
                lastError = error
                try await Task.sleep(nanoseconds: 350_000_000)
            }
        }

        throw lastError ?? CocoaError(.fileReadUnknown)
    }

    private func sendJSON(_ obj: [String: Any], socket: URLSessionWebSocketTask) async throws {
        let data = try JSONSerialization.data(withJSONObject: obj)
        try await socket.send(.string(String(data: data, encoding: .utf8) ?? "{}"))
    }

    private func parseMtime(_ msg: [String: Any]) throws -> Int64 {
        if let n = msg["mtime_ns"] as? NSNumber {
            return n.int64Value
        }
        if let i = msg["mtime_ns"] as? Int64 {
            return i
        }
        if let i = msg["mtime_ns"] as? Int {
            return Int64(i)
        }
        throw TachionError.badMessage
    }

    private func mtimeNs(url: URL) -> Int64 {
        let attrs = try? FileManager.default.attributesOfItem(atPath: url.path)
        let date = attrs?[.modificationDate] as? Date ?? Date.distantPast
        return Int64(date.timeIntervalSince1970 * 1_000_000_000.0)
    }

    private func cleanupEmptyParents(startingAt startURL: URL) {
        let rootPath = rootURL.standardizedFileURL.path
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        var current = startURL

        while current.standardizedFileURL.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")) != rootPath {
            do {
                let items = try FileManager.default.contentsOfDirectory(atPath: current.path)
                if !items.isEmpty {
                    break
                }

                try FileManager.default.removeItem(at: current)
                current.deleteLastPathComponent()
            } catch {
                break
            }
        }
    }

    private func shouldIgnore(_ url: URL) -> Bool {
        shouldIgnore(url.lastPathComponent)
    }

    private func shouldIgnore(_ pathOrRel: String) -> Bool {
        let name = (pathOrRel as NSString).lastPathComponent
        if name.isEmpty { return true }
        if name.contains(".tmp.") { return true }
        if name.hasPrefix("~$") { return true }

        let ext = (name as NSString).pathExtension.lowercased()
        if ext == "dwl" || ext == "dwl2" || ext == "swp" {
            return true
        }

        return false
    }

    private func isCoveredByRecentRemoteDelete(_ rel: String) -> Bool {
        let now = Date()

        for (deletedRel, until) in recentRemoteDeletes {
            if until < now {
                recentRemoteDeletes.removeValue(forKey: deletedRel)
                continue
            }

            if isSameOrChild(rel, deletedRel) {
                return true
            }
        }

        return false
    }

    private func isSameOrChild(_ rel: String, _ parent: String) -> Bool {
        let rel = rel.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        let parent = parent.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return rel == parent || rel.hasPrefix(parent + "/")
    }

    private func sha256Hex(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
