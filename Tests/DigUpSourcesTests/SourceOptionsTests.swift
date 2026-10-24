import Foundation
import Testing
@testable import DigUpSources

@Suite struct SourceOptionsTests {
    @Test func syncEverythingLiftsTheLimits() {
        var options = SourceOptions()
        options.mailYears = 2
        options.mailAttachments = false
        options.calendarPastYears = 1
        options.completedReminders = false
        options.photoSide = 1024
        #expect(options.effective == options)

        options.syncEverything = true
        let effective = options.effective
        #expect(effective.mailYears == 0)
        #expect(effective.mailAttachments)
        #expect(effective.calendarPastYears == 30 && effective.calendarFutureYears == 5)
        #expect(effective.completedReminders && effective.allCompletedReminders)
        #expect(effective.photoVideos && effective.photoSide == 2048)
        // What isn't a limit stays as chosen.
        #expect(!effective.mailJunkAndTrash && !effective.icloudDownload)
    }

    @Test func iCloudDriveIsIndexedWhereItIs() {
        #expect(!SourceKind.icloud.isCopied)
        #expect(SourceKind.allCases.filter(\.isCopied).count == SourceKind.allCases.count - 1)
        #expect(SourcesLayout.iCloudPaths().first?.hasSuffix("/Library/Mobile Documents") == true)
    }
}
