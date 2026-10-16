# ADR-056: Light and dark themes, and the organisation's brand

**Status:** accepted (L7)

## Context
The Desk followed the operating system's light or dark setting and nothing else: a person could not choose, and an organisation could not put its own name, logo and colours on the product. The brand also has to appear on the sign-in page, before anyone is signed in.

## Decision
- **Light, dark or automatic, per person.** The Desk has a theme button in the header and an Appearance setting on the account page. The choice is kept in the browser (`localStorage`, wrapped so a blocked store only means "no memory"); with no choice the organisation's default applies (Auto, Light or Dark); Auto follows the device and reacts when it changes. The stylesheet's dark palette is written once per mechanism: under the system media query only when no explicit theme is set, and under `[data-theme='dark']`. A tiny external script (`theme-init.js`, allowed by the page's `script-src 'self'` policy) applies a saved light or dark choice before the first paint so the other theme does not flash.
- **Brand Settings**, a single Core DocType for System Managers: name (up to 60 characters), logo, brand colour, optional dark-theme colour, default theme, sign-in message (up to 300). `GET /api/brand` is public (the sign-in page needs it) and returns only these values.
- **Everything is plain data, checked on save and again on read.** Name and message are plain text without `<` or `>` and control characters; colours are `#rrggbb`; the logo is an embedded picture (`data:image/png|jpeg|gif|webp|svg+xml;base64`, at most 100 KB) whose **bytes must match the type it claims**, and an SVG must be well-formed text starting with `<svg` and carry no script, event handler, `href`/`xlink:href`, `<a>`, `<image>`, `<use>`, `<foreignObject>`, DOCTYPE/entity or external `url()`. A stored value that fails the read check is replaced by the default, so a bad row cannot break the sign-in page. The Desk checks again before using any value (`parseBrand`, `safeLogo`), the logo only ever goes into an `<img>` (where scripts do not run), and the page's content-security policy is unchanged.
- **Colours stay readable.** The brand colour sets `--brand`; the text on it is chosen by contrast (WCAG luminance) as near-white or near-black; a soft tint is derived for highlights. In the dark theme the dark colour is used, or the brand colour is lightened until it has at least 4.5:1 contrast with the dark surface, so a dark brand colour never vanishes.
- **A Brand page** (Tools, System Manager) edits all of it with a logo file picker (type and size checked before sending, validated again by the server), colour pickers, and a live preview. The last brand seen is cached in the browser so the page does not flash the default name.
- The name is also the page title.

## Consequences
- The logo travels inside the brand response (up to ~135 KB of base64) on each page load, uncompressed by the cache rules of API responses; fine for one small image, a reason to keep the 100 KB limit.
- No custom fonts, favicon or layout changes; no per-site brand beyond what multi-site gives (each site has its own Brand Settings).
- Print formats and letter heads have their own styling (ADR-025) and are not changed by the brand.
