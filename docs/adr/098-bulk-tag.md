# ADR-098: Bulk tagging

Status: accepted

## Decision
The bulk endpoint (`POST /api/resource/{doctype}/bulk`) accepts `action: "tag"` with the tag in `value`. `BulkService.TagAsync` runs the ordinary single-document `LabelService.AddTagAsync` for each chosen document (up to 100), so read access, the tag limits and the rules for one document apply to each, and each document is reported on its own like the other bulk actions. An empty tag or a signed-out caller is refused once.

Exporting uses the list's filters (CSV or Excel buttons) rather than the ticked rows, and printing stays per document; they were not changed.
