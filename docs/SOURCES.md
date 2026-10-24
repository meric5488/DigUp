# Sources: Mail, Notes, Calendar, Reminders and Photos

This fork of DigUp searches your Apple apps' content together with your folders, by what's in it, with the same
model (EmbeddingGemma 2), entirely on your Mac. Turn sources on in **Settings → Sources**.

| Source | What's searched | How DigUp reads it | Opens in |
| --- | --- | --- | --- |
| Mail | every account Mail has (iCloud, Gmail, Exchange…): subject, people, text, and attachments (PDFs, documents, pictures, recordings) | Mail's own store in `~/Library/Mail` (needs **Full Disk Access**) | Mail, at the message |
| Notes | every note except locked ones and Recently Deleted, with its pictures, scans and PDFs | Notes' database in its group container (needs **Full Disk Access**) | Notes, at the note |
| Calendar | events from 5 years back to 1 year ahead (adjustable); holidays and birthdays off by default | EventKit (asks for access to Calendars) | Calendar, at the event |
| Reminders | open reminders, and completed ones of the last year | EventKit (asks for access to Reminders) | Reminders |
| Photos | every photo of the library, iCloud Photos included; videos optional | PhotoKit (asks for access to Photos) | Photos, at the photo |

## How it works

Each item becomes an ordinary file in `~/Library/Application Support/DigUp/Sources.noindex`, which the engine indexes
like a chosen folder (but never lists with your folders):

- a mail, note, event or reminder becomes a short text file named after it (date, subject, sender), with its people,
  dates and text; a mail's or a note's attachments become files of their own beside it;
- a photo becomes a JPEG of 1280 px (adjustable) with the date it was taken in its EXIF; photos only in iCloud are
  downloaded at that size, not in full; screenshots are named as such, so their text is read too;
- a video (when turned on) is exported small (640×480).

`~/Library/Application Support/DigUp/sources.sqlite` remembers what each file stands for, so a result shows the mail's
subject ("Mail · INBOX · Jean Dupont") and **↩** opens it in its app rather than the copy. It also keeps each item's
version, so a sync only reads what changed.

Syncs run when the app starts, when you turn a source on, whenever Mail's or Notes' store changes or Photos and
EventKit say something changed, and every 20 minutes. Pausing indexing pauses them too. Turning a source off deletes
its copies, and their entries leave the index.

## Permissions

- **Mail and Notes**: System Settings → Privacy & Security → **Full Disk Access** → add DigUp. Turning either on opens
  that pane; come back to DigUp and it starts.
- **Calendar, Reminders, Photos**: macOS asks the first time you turn them on.
- **Opening a note or a photo** asks Notes or Photos to show it (AppleScript): macOS asks once whether DigUp may
  control them.

An ad-hoc build (no signing identity, see `build.sh`) is a new app to macOS after each rebuild: it asks again. Put a
signing identity in `.sign-id` to keep the permissions across builds.

## Updates

Upstream DigUp updates itself with Sparkle. This fork has no update feed (an upstream update would replace it with a
DigUp without sources): to update, pull and rebuild.

## Code

- `Sources/DigUpSources/`: the module (Swift 5 language mode).
  - `SourceSyncer`: scheduling, change watching, states.
  - `SourceCatalog`: what each file stands for, and each item's version.
  - `SourceWriter`, `SourceNames`: files and their names.
  - `Providers/`: `MailProvider` (with `MailMessage` and `MIME`, an .emlx/MIME reader), `NotesProvider` (gzip and
    protocol buffers), `CalendarProvider` and `RemindersProvider` (EventKit), `PhotosProvider` (PhotoKit).
  - `SourceAccess`, `SourceOpener`: permissions, opening results in their apps.
- App: `Engine` indexes `SourcesLayout.root` with the chosen folders; `ResultRow` takes a source item's title and
  subtitle; `Engine.open` hands source items to `SourceOpener`; `SourcesSettings` is the Settings tab.
- Tests: `swift test --filter DigUpSourcesTests` (the mail reader, the note reader, file names).
