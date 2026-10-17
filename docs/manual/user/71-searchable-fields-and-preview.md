# Searchable fields, linked records and attachment preview

Select fields on documents and child rows use a searchable dropdown. Country-code choices also support search. Type part of a label, use the arrow keys to move through matches, and press Enter to select. Clear removes a selected value. Existing selected labels stay visible when the control receives focus.

Link and Dynamic Link fields search the destination DocType. If you may create that DocType, the dropdown offers **Create new**. The dialog starts with your search text in its title/name field where appropriate. Fill the fields and choose **Create and select**: the record is saved through the normal API and selected in the original field. Unsaved parent fields and child rows remain in place.

Required fields, permission levels, server validations, defaults, child rows and server-side automation apply. The dialog also offers **Open full form** in a separate tab for advanced workflows. Nested creation dialogs are disabled while creating a linked record; existing links remain searchable. Client scripts from the full document editor do not run in the creation dialog.

PDF and raster-image attachments open in an in-app viewer from both the attachment panel and attachment fields. PDF previews use a locally bundled PDF.js worker and canvas, with page navigation and zoom; they do not execute PDF JavaScript or use a remote preview service. Protected file endpoints and their existing permissions remain unchanged. Other formats continue to download. Password-protected or damaged PDFs show an error instead of forcing a download.

The document action bar stays flush with the top of the scrollable content area beneath the application header.
