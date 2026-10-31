# ADR-131: A JSON Schema for DocType files

**Status:** accepted (developer experience)

## Context
DocType files are written by hand (and by the Designer in developer mode). A typo such as `"inListview"` is silently ignored, and nobody knows which properties exist without reading the C# model.

## Decision
- `docs/schema/doctype.schema.json` is a JSON Schema (draft 2020-12) of a DocType file: every property of the DocType, its fields (with all 40 field types as a list), permissions, validation rules and status colours, each with the help text from the XML comment on the property, and `additionalProperties: false` so editors underline a misspelt property.
- **It is made from the model, never written by hand.** `qahwa schema` prints it; it is built by reflection over the same records the loader reads, with the descriptions taken from their XML comments (the Core project now writes its XML documentation file for this). A test fails when the committed file differs from what the model produces and says how to regenerate it, so it cannot go stale.
- **Everything we ship obeys it.** A test checks every built-in DocType (`CoreDocTypes/*.json`) and the sample files against the schema with a small validator (types, choices, required and unknown properties), so a typo in a shipped file is caught in CI, and so is a model property added without a schema update.
- To use it in an editor, put `"$schema": "../../docs/schema/doctype.schema.json"` at the top of a DocType file (the loader ignores it), or map the pattern `**/doctypes/**/*.json` to the schema in the editor's settings.

## Consequences
- The schema checks shape, not meaning: field names, link targets and expressions are checked by the loader when the DocType is saved or migrated, with messages that name the field.
- Not yet: schemas for the other JSON the system reads (workflows, bundles), and a published URL (the `$id` is a name, not a place).
