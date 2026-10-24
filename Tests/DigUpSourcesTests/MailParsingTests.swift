import Foundation
import Testing
@testable import DigUpSources

@Suite struct MailParsingTests {
    /// An .emlx: the length line, the message, then Mail's property list.
    private func emlx(_ message: String, plist: String = "") -> Data {
        let bytes = Data(message.utf8)
        return Data("\(bytes.count)\n".utf8) + bytes + Data(plist.utf8)
    }

    @Test func plainMessage() throws {
        let message = """
            From: "Jean Dupont" <jean@example.com>\r
            To: eric@example.com\r
            Subject: =?UTF-8?Q?Devis_cuisine_=E2=80=94_r=C3=A9vision?=\r
            Date: Sun, 14 Jul 2024 10:32:00 +0200 (CEST)\r
            Message-ID: <abc.123@example.com>\r
            Content-Type: text/plain; charset=utf-8\r
            Content-Transfer-Encoding: quoted-printable\r
            \r
            Bonjour Eric,=0D=0Avoici le devis r=C3=A9vis=C3=A9 pour la cuisine.\r
            \r
            Le 12 juil. 2024 à 09:00, Eric <eric@example.com> a écrit :\r
            > ancien message\r
            """
        let mail = try #require(MailMessage(emlx: emlx(message)))
        #expect(mail.subject == "Devis cuisine — révision")
        #expect(mail.senderName == "Jean Dupont")
        #expect(mail.messageID == "abc.123@example.com")
        #expect(mail.text.contains("devis révisé pour la cuisine"))
        #expect(!mail.text.contains("ancien message"))
        #expect(!mail.text.contains("a écrit"))
        let date = try #require(mail.date)
        #expect(Int(date.timeIntervalSince1970) == 1_720_945_920)
    }

    @Test func multipartWithAttachment() throws {
        let pdf = Data("%PDF-1.4 fake".utf8).base64EncodedString()
        let message = """
            From: shop@example.com
            Subject: Your invoice
            Content-Type: multipart/mixed; boundary="outer"

            preamble
            --outer
            Content-Type: multipart/alternative; boundary=inner

            --inner
            Content-Type: text/plain; charset=iso-8859-1
            Content-Transfer-Encoding: quoted-printable

            Merci pour votre achat =E0 Dunkerque.
            --inner
            Content-Type: text/html; charset=utf-8

            <html><body><p>Merci</p></body></html>
            --inner--
            --outer
            Content-Type: application/pdf; name="facture.pdf"
            Content-Disposition: attachment; filename*=utf-8''facture%20n%C2%B01.pdf
            Content-Transfer-Encoding: base64

            \(pdf)
            --outer--
            """
        let mail = try #require(MailMessage(emlx: emlx(message)))
        #expect(mail.text.contains("Merci pour votre achat à Dunkerque."))
        #expect(!mail.text.contains("<p>"))
        #expect(mail.attachments.count == 1)
        #expect(mail.attachments.first?.filename == "facture n°1.pdf")
        #expect(mail.attachments.first.map { Data($0.bytes) } == Data("%PDF-1.4 fake".utf8))
    }

    @Test func htmlOnly() throws {
        let message = """
            Subject: News
            Content-Type: text/html; charset=utf-8

            <html><head><style>p{color:red}</style></head><body><p>Caf&eacute; &amp; cr&#232;me</p><br>Fin</body></html>
            """
        let mail = try #require(MailMessage(emlx: emlx(message)))
        #expect(mail.text.contains("Café & crème"))
        #expect(mail.text.contains("Fin"))
        #expect(!mail.text.contains("color"))
    }

    @Test func dateFromPlistWhenHeaderMissing() throws {
        let plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>date-received</key><integer>1700000000</integer></dict></plist>
            """
        let mail = try #require(MailMessage(emlx: emlx("Subject: x\n\nbody", plist: plist)))
        #expect(mail.date == Date(timeIntervalSince1970: 1_700_000_000))
    }

    @Test func encodedWordsAndParameters() {
        #expect(MIME.decodeWords("=?ISO-8859-1?B?Q2Fm6Q==?= =?UTF-8?Q?_au_lait?=") == "Café au lait")
        let (value, parameters) = MIME.parameters(#"attachment; filename*0*=utf-8''r%C3%A9; filename*1*=sum%C3%A9.pdf"#)
        #expect(value == "attachment")
        #expect(parameters["filename"] == "résumé.pdf")
        #expect(MIME.parameters(#"text/plain; charset="utf-8"; format=flowed"#).parameters["charset"] == "utf-8")
    }

    @Test func mailLink() {
        #expect(MailProvider.link("abc.123@example.com") == "message://%3Cabc.123@example.com%3E")
    }

    @Test func mailboxName() {
        #expect(MailProvider.mailbox(of: "ACCOUNT/Archive.mbox/2023.mbox/UUID/Data/1/Messages/12.emlx") == "Archive › 2023")
    }
}

@Suite struct NotesParsingTests {
    @Test func noteTextFromGzippedProtobuf() throws {
        // NoteStoreProto { 2: Document { 3: Note { 2: "Courses\nPain, café et 3 pommes ✓" } } }, gzipped.
        let data = try #require(Data(base64Encoded:
            "H4sIAAAAAAAC/+NgENIWYJBS52AUUnbOLy0qTi3mCkjMzNNRSE5MO7xSIbVEwVihID83N7VY4dGcyQBNkstYLwAAAA=="))
        #expect(NotesProvider.noteText(data) == "Courses\nPain, café et 3 pommes ✓")
    }
}

@Suite struct NamingTests {
    @Test func fileNames() {
        #expect(SourceNames.file("Re: Devis / plan", extension: "txt") == "Re - Devis - plan.txt")
        #expect(SourceNames.file(".cachée", extension: "txt") == "cachée.txt")
        #expect(SourceNames.file("", extension: "txt") == "Untitled.txt")
        #expect(SourceNames.folder("Projet.app") == "Projet\u{2024}app")
    }
}
