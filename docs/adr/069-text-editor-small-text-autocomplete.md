# ADR-069: Text Editor, Small Text and Autocomplete fields

Status: accepted

## Context
The field types offered a plain textarea for every kind of text. People also need formatted text (bold, lists, links) and a short text that suggests values without forcing a choice.

## Decision
- **Small Text** is plain text up to 5,000 characters (a two-line box in the form).
- **Autocomplete** is a Data field (up to 140 characters) with suggestions taken from its options (one per line, at most 200). Any value is accepted; the suggestions are only offered.
- **Text Editor** holds formatted text as HTML. **The server never stores what it is sent:** `HtmlSanitizer.Clean` reads the tags and text and writes a new document from scratch using only a short list of tags (paragraphs, line breaks, bold, italic, underline, lists, headings up to h4, quotes, code, tables, links). Attributes are dropped except `href` on a link, which must be an `http`, `https`, `mailto` or `tel` address (character references are decoded and spaces ignored before checking, so `&#106;avascript:` and `java<tab>script:` fail); links open in a new tab with `rel="noopener noreferrer nofollow"`. Scripts, styles, frames, objects, SVG and MathML are removed with everything in them; other unknown tags lose the tag and keep the text; comments and processing instructions go; every piece of text is escaped; nesting is limited to 40 levels; input is limited to 1,000,000 characters. Cleaning twice changes nothing. The conversion runs wherever a value is converted for storage, so API saves, imports, backup restores and scripts are all covered.
- **Everywhere text is shown without formatting** (lists, print, the default print layout, email text) a Text Editor value is turned into plain text (`HtmlSanitizer.ToPlainText`, `htmlToText` in the Desk).
- **Desk:** a small toolbar (bold, italic, underline, lists, link, clear) on an editable area; pasted content is inserted as plain text; links are checked before they are made; the stored value is shown through a second client-side clean (`sanitizeHtml`, DOM based, same list) in case a value reached the database another way.

## Consequences
- No pictures, colours, fonts or classes can be put in a Text Editor field (no `img`, no `style`); that is the price of not having to trust the input.
- Text Editor fields are not offered on public web forms.
- Searching and filtering a Text Editor field sees the HTML, not just the words.
