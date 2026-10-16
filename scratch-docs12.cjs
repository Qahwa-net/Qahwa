const fs = require('fs')
process.chdir('D:/Me/Qahwa')
function ed(f, a, b) {
  const s = fs.readFileSync(f, 'utf8')
  if (!s.includes(a)) throw new Error(f + ' missing ' + a)
  fs.writeFileSync(f, s.replace(a, b))
}
fs.writeFileSync('docs/adr/043-patches.md', `# ADR-043: Patches (one-time data migrations)

**Status:** accepted (L12, part 6)

## Context
Schema changes are additive and automatic, but a new version sometimes needs data changed too: fill a new field from an old one, rename a value, tidy rows. That must happen exactly once per site, in order, even when several servers start together, and a failure must not be silent.

## Decision
- **A patch is code with a name.** \`IPatch { Name; RunAsync(services, ct) }\`, registered with \`services.AddQahwaPatch<T>()\`. Names are lowercase \`[a-z0-9._-]\`, unique and never changed (a date prefix such as \`2026_03_01_fill_group\` gives the order). A bad or duplicate name stops the server at start-up.
- **The Patch Log is the record**, as a Core DocType (name = patch name; Running, Done or Failed; start, end, error text), readable and deletable by System Managers, not editable by anyone. A patch is **claimed by inserting its row**: the name is unique, so when several servers start together exactly one runs it and the others skip it (tested with five concurrent runners on PostgreSQL).
- **Order and failure.** Pending patches run in name order at start-up, after DocTypes, customizations, rules and workflows are loaded. A failing patch is recorded as Failed with its error, **stops the run** (later patches may rely on it) and stops the server's start-up: carrying on with half-migrated data is worse than not starting. It is tried again on the next start (delete the row to force a re-run of a Done patch).
- **Crashes.** A patch claimed but never finished (the server died) is taken over after 30 minutes; a patch that is still within that time is left alone.
- **Write patches to be safe to repeat**, since an interrupted run is repeated from the start.

## Consequences
- Not yet: running a single patch from the command line, patches that belong to an installable app (with app dependencies and ordering across apps), a Desk button to retry.
`)
fs.writeFileSync('docs/manual/user/33-patch-log.md', `# Patch Log

When a new version needs to change existing data (for example fill a new field), the change is made by a **patch** that runs automatically once when the system starts. **Patch Log** (System Manager) shows every patch: its name, whether it is **Done**, **Running** or **Failed**, when it started and finished, and the problem if it failed.

- A **failed** patch stops the system from starting until it is fixed; the error is shown here and in the server log. After the cause is fixed it runs again on the next start.
- You do not run or edit patches yourself. Deleting a row makes that patch run again at the next start; only do that on the advice of whoever maintains the system.
`)
ed('docs/manual/user/README.md', '32. [System Settings and network restrictions](32-system-settings.md)', '32. [System Settings and network restrictions](32-system-settings.md)\n33. [Patch Log](33-patch-log.md)')
fs.writeFileSync('docs/manual/developer/06-features-reference.md', fs.readFileSync('docs/manual/developer/06-features-reference.md', 'utf8').trimEnd() + `

## Patches ([ADR-043](../../adr/043-patches.md))

\`Patches.cs\`: \`IPatch\`, \`PatchRunner\` (\`Validate\`, \`RunPendingAsync\`), \`PatchFailedException\`; Core DocType \`patch_log.json\`. Register with \`services.AddQahwaPatch<MyPatch>()\` (\`PatchRegistration\` in \`HostingExtensions.cs\`). \`Program.cs\` runs pending patches after all loaders. Example:

\`\`\`csharp
public sealed class FillCustomerGroup : IPatch
{
    public string Name => "2026_03_01_fill_customer_group";
    public async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var documents = services.GetRequiredService<DocumentService>();
        // read rows, change them, save: safe to run twice
    }
}
\`\`\`

Tests: \`PatchTests\`, \`PostgresPatchTests\`.
`)
ed('README.md', '- **Webhooks**:', '- **Patches**: one-time versioned data migrations, recorded in a Patch Log, claimed atomically across servers, a failure stops start-up ([ADR-043](docs/adr/043-patches.md))\n- **Webhooks**:')
ed('docs/PLAN.md', '| Patches / data migrations (patch log) | | ⬜ | L12 |', '| Patches / data migrations (patch log) | done (ADR-043); not per-app ordering or a CLI to run one | ✅ (v1) | L12 |')
fs.copyFileSync('docs/PLAN.md', 'D:/Me/Qahwa-Framework-Plan.md')
console.log('ok')
