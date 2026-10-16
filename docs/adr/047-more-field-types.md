# ADR-047: Rating, duration, colour, barcode, place and signature fields

**Status:** accepted (L3, part 3)

## Context
Forms need more than text and numbers: a satisfaction score, how long a job took, a tag colour, a barcode, where something happened, a signature on delivery.

## Decision
Six new field types, each stored in an ordinary column, validated by the server whatever the client sends, and usable in the DocType Designer:

| Type | Stored as | Rule |
|---|---|---|
| Rating | integer | a whole number of stars from 0 to 5 |
| Duration | integer (seconds) | whole seconds, 0 up to ten years; people type `1h 30m`, `1:30` or seconds |
| Color | short text | `#rrggbb`, stored in lower case |
| Barcode | short text | up to 140 characters, no control characters (the text of a barcode or QR code) |
| Geolocation | text (JSON) | `{"lat":..,"lng":..}`, latitude within 90 and longitude within 180, nothing else in the object, normalised to six decimals |
| Signature | text (data address) | a **PNG** picture as `data:image/png;base64,...` of at most 150,000 characters; the picture's own first bytes are checked, not what it claims to be (so an SVG or HTML page labelled as PNG is refused) |

- **Empty is no value** for all of them. Rating and duration count as numbers in reports and dashboards (averages, totals, grouping a rating); the Desk filters them like numbers.
- **Safe in every place they appear.** The Desk shows a signature as an `<img>` (a picture can never run script), the colour only as a picker and text, and the map link is built from the two numbers only. Printouts show stars, `1h 30m` and `24.7, 46.7`, and never put the signature picture on paper (`(signed)`). Web forms accept rating and colour from visitors but not signature or place (large or odd values from strangers). OpenAPI describes each type.
- **Desk controls:** stars (a radio group, click the chosen star again to clear), a duration box that understands what people type, a colour picker with its hex text, latitude and longitude with "Use my location" and "View on map" (OpenStreetMap, opened with `noopener noreferrer`), and a signature box for mouse, pen or finger (several strokes, "Clear", "Sign again").

## Consequences
- Not yet: Attach and Image fields in forms, Icon, an editor for rich text, a map picker, signatures drawn by a stylus with pressure.
