/** The Developer Hub (ADR-162): every tool a developer or administrator uses, grouped by the job, with a line saying what it is for. */
export interface HubTool { title: string; to: string; about: string; keywords?: string }
export interface HubGroup { title: string; tools: HubTool[] }

export const HUB: HubGroup[] = [
  { title: 'Model your data', tools: [
    { title: 'DocType Designer', to: '/designer', about: 'Create a DocType: fields, naming, rules and who can use it. Shows what saving does to the database.', keywords: 'table schema fields model' },
    { title: 'Customize Form', to: '/customize', about: 'Change fields and properties of an existing DocType without touching its definition.', keywords: 'custom field property' },
    { title: 'Naming Series', to: '/naming', about: 'See and reset the counters behind generated names.', keywords: 'autoname counter' },
    { title: 'Export & Import', to: '/bundle', about: 'Move DocType definitions and settings between sites as one file.', keywords: 'bundle migrate deploy' },
  ] },
  { title: 'Control access', tools: [
    { title: 'Role Permissions', to: '/permissions', about: 'Who may read, write, create, delete, submit and export each DocType.', keywords: 'roles rights security' },
    { title: 'Assignment Rules', to: '/assignment-rules', about: 'Hand new documents to people automatically.', keywords: 'assign owner round robin' },
  ] },
  { title: 'Automate', tools: [
    { title: 'Workflow Builder', to: '/workflows', about: 'States, transitions and approvals for a DocType.', keywords: 'approval state' },
    { title: 'Notification Rules', to: '/notification-rules', about: 'Send an email or alert when a document changes.', keywords: 'email alert' },
    { title: 'Auto Repeat', to: '/auto-repeat', about: 'Create copies of a document on a schedule.', keywords: 'recurring' },
    { title: 'Scheduled Jobs', to: '/scheduler', about: 'Background jobs, when they last ran and a Run now button.', keywords: 'cron background' },
  ] },
  { title: 'Connect other systems', tools: [
    { title: 'Webhooks', to: '/webhooks', about: 'Call another system when a document is saved, with a signed request.', keywords: 'integration http' },
    { title: 'OAuth Clients', to: '/oauth-clients', about: 'Let another application sign in on behalf of your users.', keywords: 'sso token' },
    { title: 'API reference (OpenAPI)', to: '/api/openapi.json', about: 'Every call this site offers, as JSON for Postman or a code generator. Keys are made in My account.', keywords: 'rest swagger api key' },
  ] },
  { title: 'Look and feel', tools: [
    { title: 'Print Formats', to: '/print-formats', about: 'Design the printed page of a document.', keywords: 'pdf template' },
    { title: 'Workspaces', to: '/workspaces', about: 'Arrange the sidebar and workspace pages people see.', keywords: 'sidebar menu' },
    { title: 'Brand', to: '/brand', about: 'Name, logo and colours.', keywords: 'logo theme' },
  ] },
  { title: 'Run and look after', tools: [
    { title: 'Apps', to: '/apps', about: 'Installed apps and their versions.', keywords: 'install' },
    { title: 'Sites', to: '/sites', about: 'Add or remove sites served by this server.', keywords: 'multi tenant domain' },
    { title: 'Slow requests', to: '/slow-requests', about: 'The slowest calls and how many database statements each used.', keywords: 'performance' },
    { title: 'Backup & Restore', to: '/backup', about: 'Download a backup of the data or restore one.', keywords: 'dump' },
  ] },
]

/** The tools that match what was typed (title, description or keywords); everything for an empty query. */
export function filterHub(groups: HubGroup[], query: string): HubGroup[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return groups
  return groups
    .map((g) => ({ ...g, tools: g.tools.filter((t) => { const hay = `${t.title} ${t.about} ${t.keywords ?? ''} ${g.title}`.toLowerCase(); return words.every((w) => hay.includes(w)) }) }))
    .filter((g) => g.tools.length > 0)
}
