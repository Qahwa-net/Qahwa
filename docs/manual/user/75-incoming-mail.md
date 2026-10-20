# Receiving email in Qahwa

Qahwa can read a mailbox (for example `support@yourcompany.com`) and keep each new message as a **Communication**.

## Turn it on (System Manager)
1. Open **Email Account**.
2. Tick **Read incoming mail (IMAP)**.
3. Enter the **IMAP server** (for example `imap.gmail.com`), the **username** and the **password**. For Gmail and Microsoft 365 use an *app password*.
4. Leave the port at **993**. For a server that uses STARTTLS choose that option and use port **143**. The connection is always encrypted.
5. Keep the folder as **INBOX** or enter another folder.
6. **Save**, then press the **inbox** button to test the connection. It tells you how many messages the folder holds.

## What happens next
- About once a minute Qahwa looks for new mail. **Mail that was already in the folder is not imported**; only mail that arrives after you save.
- Each new message appears in **Communication** with the sender, subject, date and text.
- Qahwa only *reads*. It never deletes, moves or marks messages as read.
- Only the text is kept. Pictures, attachments and formatting are left out, and very large messages (over 512 KB) are skipped. Up to 50 messages are taken each time.
- Only System Managers can see Communications.

## If it does not work
Press the test button. If it fails, check the server name, the port and security choice, the folder name and the password. Details of failures are in **Scheduled Job Log** (step "incoming mail").
