# ADR-099: Module Definition

Status: accepted

## Decision
A **Module Definition** document gives a module (the text in a DocType's *Module* field) a display label, a position, a description and a "hide from the home page" flag. Everyone signed in may read them (they hold no private data); only System Managers change them. A module with no definition still works and is listed after the defined ones, by name. The home page groups DocType tiles by module in that order (`lib/modules.ts` `groupByModule`). Hiding only hides tiles; access is still decided by roles and Module Profiles (ADR-081), which block modules by name.
