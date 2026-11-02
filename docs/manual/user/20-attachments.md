# Attachments: files on a document

You can keep files with a document: a scanned contract, a photo, a receipt.

## Adding files

1. Open the document (it must be saved) and find **Attachments** in the panel on the right (below the form on a phone).
2. Click the **+**, or **choose files**, or simply **drag files onto the dotted box**. You can add several at once.
3. The files appear in the list. Pictures show a small preview.

You need permission to **change** the document to add or remove files. Anyone who can **open** the document can see and download its files.

## Opening and downloading

Click an image or PDF filename to open the in-app viewer. PDFs have page navigation and zoom controls; images fit inside the preview dialog. Close the viewer to return to the document. These actions do not start a download. Other formats, such as Word documents and spreadsheets, still offer a download. File access always follows the parent document's permissions.

## Removing a file

Click the bin next to it. You can remove files you added, and files on documents you may change. The document's Activity records who added or removed what.

## Limits

- Up to **25 MB** per file (an administrator can change this) and **50 files** per document.
- Empty files are refused.
- When a document is deleted, its files are deleted with it.

## For administrators

Files are stored on the server's disk (`Qahwa:Files:Path`, by default a `data/files` folder next to the server) or, if your administrator set it up, in an S3-compatible bucket or an Azure Blob container (see below). **Include that folder (or the bucket) in your backups** together with the database. The **File** list in the sidebar (Core) shows every attachment, its size and which document it belongs to.

## Keeping files in a bucket (S3)
Instead of the server's disk, files can live in an **S3-compatible bucket**: Amazon S3, MinIO, Cloudflare R2, Wasabi, Contabo Object Storage and the like. This helps when the server is replaced often, runs on several machines, or the disk is small. An administrator sets it in the server's configuration (not in the Desk):

```
Qahwa:Files:Provider = s3
Qahwa:Files:S3:ServiceUrl = https://eu2.contabostorage.com
Qahwa:Files:S3:Bucket     = my-qahwa-files
Qahwa:Files:S3:Region     = us-east-1
Qahwa:Files:S3:AccessKey  = ...
Qahwa:Files:S3:SecretKey  = ...
Qahwa:Files:S3:Prefix     = prod            (optional folder inside the bucket)
Qahwa:Files:S3:PathStyle  = true            (the default; false puts the bucket in the host name)
```

Put the keys in environment variables (`Qahwa__Files__S3__SecretKey`) or a secret store, not in a file that is kept in source control. The bucket must already exist, and the keys need permission to read, write and delete objects in it. A wrong setting stops the server at start-up with a message saying which one. The files are served to people through Qahwa as before (permission checked on every request); the bucket should be **private**, because anyone with a link into a public bucket would bypass those checks. Switching an existing site from disk to a bucket does not move the files already saved: copy the `data/files` folder into the bucket first (keep the same names, in sub-folders by the first two characters of each name), or keep the old folder until you have.

## Keeping files in Azure Blob Storage
On Azure, files can live in a **Blob Storage container** instead:

```
Qahwa:Files:Provider = azure
Qahwa:Files:Azure:Account   = mystorageaccount
Qahwa:Files:Azure:Key       = ...               (an access key of the account)
Qahwa:Files:Azure:Container = qahwa-files
Qahwa:Files:Azure:Prefix    = prod              (optional folder inside the container)
Qahwa:Files:Azure:ServiceUrl =                  (optional: only for Azurite or a sovereign cloud)
```
The same advice applies as for a bucket: keep the key in an environment variable (`Qahwa__Files__Azure__Key`), make the container **private**, create it first, and copy existing files across before switching. A wrong setting stops the server at start-up.

