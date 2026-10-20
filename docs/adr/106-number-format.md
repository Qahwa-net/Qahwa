# ADR-106: Number format

Status: accepted

## Decision
System Settings has a **Number format** (`1,234.56`, `1.234,56` or `1 234,56`), part of the display settings every signed-in person receives (`DisplaySettings.NumberFormat`; an unknown stored value falls back to `1,234.56`). The Desk writes number, decimal, currency and percent values in lists and forms' read-only views with it (`formatNumber` in `lib/display.ts`); values are still stored and sent through the API as plain numbers, and typing in a number box is unchanged. It is a site setting, not per person.
