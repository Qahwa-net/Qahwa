# ADR-160: Web pages

**Status:** accepted

## Context
Public web forms (ADR-034) let strangers send information in. There was no way to publish information out: an about page, opening hours, news. Frappe's Web Page, Blog Post and website pages cover this.

## Decision
- **A Core DocType `Web Page`** (System Managers write it): an address (`route`: lower-case letters, digits and dashes, up to 61), a title, rich-text content, a summary, a date shown, **Published** and **Listed on the index**. It keeps an updates log like any document.
- **Public API, open to anyone:** `GET /api/public/pages` (the index) and `GET /api/public/pages/{route}`. Like public forms they never look at a cookie or API key, are rate limited per address, and are exempt from the network allow-list. Only **published** pages are given out: a draft, an unknown address and a malformed address are all the same "not found". An unlisted page is still readable by its address.
- **HTML is cleaned when it is read**, with the same cleaner as workspace text (no scripts, event handlers, styles or unsafe link schemes; links open in a new tab without referrer and `nofollow`), and the Desk cleans it again before showing it, so what is stored is never trusted to still be clean. Content is limited to the cleaner's maximum size.
- **Pages for visitors in the Desk:** `/pages` (the index, newest first, with each page's summary and date) and `/pages/<address>` (the page, with a breadcrumb), outside the sign-in. *Web Page* is under **Tools** in the sidebar.

## Consequences
- Tested: reading with cleaning (script, handler, `javascript:` link removed), drafts and odd addresses as "not found", the index (published and listed only, newest first, unlisted readable), address and size validation, the System Manager restriction, and the API without any credentials. Run for real on PostgreSQL: two pages created, read anonymously through the API and shown in the browser (the pages were deleted afterwards).
- Not yet: a blog (comments, authors, tags), images and uploads in pages (use links), a site header and menu, a page template or theme, a page at the site's root address, search engines' sitemap, custom domains per page. Pages are shown by the Desk, so a visitor needs scripts enabled.
