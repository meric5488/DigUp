import AppKit
import AVFoundation
import CoreLocation
import Foundation
import ImageIO
import Photos
import UniformTypeIdentifiers

/// The Photos library, through PhotoKit, iCloud Photos included.
///
/// Each photo becomes a JPEG of `SourceOptions.photoSide` pixels on its longest side, named after when it was taken
/// and its original name, with the date it was taken in its EXIF. That's all the model needs to find "the dog on the
/// beach", and it works for photos whose originals are only in iCloud ("Optimize Mac Storage"): Photos downloads what
/// it takes to make the picture. Screenshots are named as such, so the engine reads their text too. Videos are off
/// by default (`SourceOptions.photoVideos`); when on, each is exported small (640×480).
final class PhotosProvider: SourceProvider {
    let kind = SourceKind.photos

    func accessProblem() -> String? {
        switch PHPhotoLibrary.authorizationStatus(for: .readWrite) {
        case .authorized, .limited: return nil
        case .notDetermined: return "Turn Photos off and on again to let Fouineur read your library."
        default: return "Allow Fouineur to use your photos: System Settings → Privacy & Security → Photos."
        }
    }

    func sync(_ context: SyncContext) throws {
        let options = context.options
        let fetch = PHFetchOptions()
        fetch.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        fetch.includeHiddenAssets = false
        fetch.predicate = options.photoVideos
            ? NSPredicate(format: "mediaType == %d OR mediaType == %d", PHAssetMediaType.image.rawValue,
                          PHAssetMediaType.video.rawValue)
            : NSPredicate(format: "mediaType == %d", PHAssetMediaType.image.rawValue)
        let result = PHAsset.fetchAssets(with: fetch)
        var assets: [PHAsset] = []
        assets.reserveCapacity(result.count)
        result.enumerateObjects { asset, _, _ in assets.append(asset) }
        context.setTotal(assets.count)

        for asset in assets {
            if context.cancelled { return }
            let id = asset.localIdentifier
            let modified = asset.modificationDate ?? asset.creationDate ?? .distantPast
            let isVideo = asset.mediaType == .video
            let stamp = isVideo ? "\(Int(modified.timeIntervalSince1970))-v\(options.photoVideoMinutes)"
                : "\(Int(modified.timeIntervalSince1970))-\(options.photoSide)"
            guard context.needsWriting(id, stamp: stamp) else { continue }
            if isVideo, asset.duration > Double(options.photoVideoMinutes) * 60 {
                context.writer.put(nativeID: id, stamp: stamp, files: [])
                continue
            }
            autoreleasepool {
                context.writer.clear(id)
                // Nothing written (offline, a photo iCloud couldn't send): no stamp either, so the next sync tries again.
                guard let written = isVideo ? exportVideo(asset, writer: context.writer)
                        : writePhoto(asset, side: options.photoSide, writer: context.writer) else { return }
                let (file, url) = written
                context.writer.put(nativeID: id, stamp: stamp, files: [file], reserved: [file.name: url])
            }
        }
        context.removeUnseen()
    }

    // MARK: Naming

    private struct Naming {
        let date: Date
        let original: String?
        let folders: [String]
        let stem: String
    }

    private func naming(_ asset: PHAsset) -> Naming {
        let date = asset.creationDate ?? asset.modificationDate ?? Date()
        let original = PHAssetResource.assetResources(for: asset).first?.originalFilename
        let originalStem = original.map { ($0 as NSString).deletingPathExtension } ?? ""
        let screenshot = asset.mediaSubtypes.contains(.photoScreenshot)
        let stem = (screenshot ? "Screenshot " : "") + SourceNames.minute(date)
            + (originalStem.isEmpty ? "" : " " + originalStem)
        return Naming(date: date, original: original, folders: [SourceNames.year(date), SourceNames.month(date)],
                      stem: stem)
    }

    private func item(_ asset: PHAsset, naming: Naming) -> SourceItem {
        var subtitle = "Photos · \(SourceNames.readable(naming.date))"
        if asset.isFavorite { subtitle += " · ♥︎" }
        return SourceItem(kind: .photos, nativeID: asset.localIdentifier,
                          title: naming.original ?? naming.stem,
                          subtitle: subtitle, openURL: "photos-redirect://",
                          openScript: """
                          tell application "Photos"
                              activate
                              spotlight media item id "\(asset.localIdentifier)"
                          end tell
                          """,
                          date: naming.date)
    }

    // MARK: Photos

    private func writePhoto(_ asset: PHAsset, side: Int, writer: SourceWriter) -> (SourceFile, URL)? {
        let naming = naming(asset)
        let request = PHImageRequestOptions()
        request.isSynchronous = true
        request.deliveryMode = .highQualityFormat
        request.resizeMode = .fast
        request.isNetworkAccessAllowed = true
        request.version = .current
        var picture: CGImage?
        let size = CGSize(width: side, height: side)
        _ = PHImageManager.default().requestImage(for: asset, targetSize: size, contentMode: .aspectFit,
                                                  options: request) { image, _ in
            picture = image?.cgImage(forProposedRect: nil, context: nil, hints: nil)
        }
        guard let picture else {
            SourceLog.write("photos: no picture for \(naming.original ?? asset.localIdentifier)")
            return nil
        }
        let name = SourceNames.file(naming.stem, extension: "jpg")
        let target = writer.reservePath(folders: naming.folders, name: name)
        guard Self.writeJPEG(picture, to: target.url, taken: naming.date, location: asset.location) else {
            SourceLog.write("photos: can't write \(target.relative)")
            return nil
        }
        let file = SourceFile(folders: naming.folders, name: name, content: .written, item: item(asset, naming: naming))
        return (file, target.url)
    }

    private static let exifFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy:MM:dd HH:mm:ss"
        return formatter
    }()

    /// A JPEG with the date it was taken (and where), as a camera writes it: the engine reads both.
    static func writeJPEG(_ image: CGImage, to url: URL, taken: Date, location: CLLocation?) -> Bool {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil)
        else { return false }
        var properties: [CFString: Any] = [
            kCGImageDestinationLossyCompressionQuality: 0.85,
            kCGImagePropertyExifDictionary: [
                kCGImagePropertyExifDateTimeOriginal: exifFormatter.string(from: taken),
                kCGImagePropertyExifDateTimeDigitized: exifFormatter.string(from: taken),
            ] as [CFString: Any],
        ]
        if let location {
            let coordinate = location.coordinate
            properties[kCGImagePropertyGPSDictionary] = [
                kCGImagePropertyGPSLatitude: abs(coordinate.latitude),
                kCGImagePropertyGPSLatitudeRef: coordinate.latitude >= 0 ? "N" : "S",
                kCGImagePropertyGPSLongitude: abs(coordinate.longitude),
                kCGImagePropertyGPSLongitudeRef: coordinate.longitude >= 0 ? "E" : "W",
            ] as [CFString: Any]
        }
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        return CGImageDestinationFinalize(destination)
    }

    // MARK: Videos

    private final class Box<T> {
        var value: T?
    }

    private func exportVideo(_ asset: PHAsset, writer: SourceWriter) -> (SourceFile, URL)? {
        let naming = naming(asset)
        let request = PHVideoRequestOptions()
        request.isNetworkAccessAllowed = true
        request.deliveryMode = .mediumQualityFormat
        request.version = .current
        let box = Box<AVAssetExportSession>()
        let got = DispatchSemaphore(value: 0)
        _ = PHImageManager.default().requestExportSession(forVideo: asset, options: request,
                                                          exportPreset: AVAssetExportPreset640x480) { session, _ in
            box.value = session
            got.signal()
        }
        guard got.wait(timeout: .now() + 600) == .success, let session = box.value else {
            SourceLog.write("photos: no video for \(naming.original ?? asset.localIdentifier)")
            return nil
        }
        let name = SourceNames.file(naming.stem, extension: "mp4")
        let target = writer.reservePath(folders: naming.folders, name: name)
        session.outputURL = target.url
        session.outputFileType = .mp4
        session.shouldOptimizeForNetworkUse = false
        let exported = DispatchSemaphore(value: 0)
        session.exportAsynchronously { exported.signal() }
        guard exported.wait(timeout: .now() + 1800) == .success, session.status == .completed else {
            session.cancelExport()
            try? FileManager.default.removeItem(at: target.url)
            SourceLog.write("photos: video export failed for \(naming.original ?? asset.localIdentifier): "
                            + (session.error?.localizedDescription ?? "timed out"))
            return nil
        }
        let file = SourceFile(folders: naming.folders, name: name, content: .written, item: item(asset, naming: naming))
        return (file, target.url)
    }
}
