import Foundation

/// iCloud Drive. Its files are indexed where they are (the engine adds `SourcesLayout.iCloudFolders` to its folders), so
/// a sync writes nothing: it counts them, and with `SourceOptions.icloudDownload` asks iCloud to download those that
/// are only in iCloud ("Optimize Mac Storage"), which the engine can't read until they're here.
final class ICloudDriveProvider: SourceProvider {
    let kind = SourceKind.icloud
    private(set) var itemCount: Int?

    func accessProblem() -> String? {
        let fm = FileManager.default
        guard fm.fileExists(atPath: SourcesLayout.iCloudDrive.path) else {
            return "iCloud Drive is off on this Mac: System Settings → Apple Account → iCloud → iCloud Drive."
        }
        guard (try? fm.contentsOfDirectory(atPath: SourcesLayout.iCloudDrive.path)) != nil else {
            return "Give DigUp Full Disk Access: System Settings → Privacy & Security → Full Disk Access."
        }
        return nil
    }

    func sync(_ context: SyncContext) throws {
        let fm = FileManager.default
        var files = 0, inCloud = 0, downloading = 0
        for folder in SourcesLayout.iCloudFolders() {
            guard let walker = fm.enumerator(at: folder, includingPropertiesForKeys: nil,
                                             options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { continue }
            while let url = walker.nextObject() as? URL {
                if context.cancelled { return }
                var info = stat()
                guard lstat(url.path, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { continue }
                files += 1
                guard info.st_flags & 0x4000_0000 != 0 else { continue }   // SF_DATALESS: only in iCloud
                inCloud += 1
                if context.options.icloudDownload, (try? fm.startDownloadingUbiquitousItem(at: url)) != nil {
                    downloading += 1
                }
            }
        }
        itemCount = files
        SourceLog.write("icloud: \(files) files, \(inCloud) only in iCloud"
                        + (downloading > 0 ? ", \(downloading) asked to download" : ""))
    }
}
