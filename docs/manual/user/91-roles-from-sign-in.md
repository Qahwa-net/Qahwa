# Roles from your company sign-in (for server administrators)

If people sign in through your company's identity provider, you can give them roles from their groups there. In the provider's section of the server configuration add the claim that lists groups and a map from group to roles:

```
"Qahwa": { "Auth": { "Sso": { "Providers": { "company": {
  "GroupsClaim": "groups",
  "RoleMap": { "sales-team": [ "Sales User" ], "finance": [ "Accounts User", "Accounts Manager" ] }
} } } } }
```

- At each sign-in people get the roles of their groups and lose the mapped roles of groups they have left. Roles that are not in the map are never changed.
- **Administrator** and **System Manager** can never be given this way; a map that names them makes the provider refuse sign-in until it is fixed. Give those roles in Qahwa itself.
- Only the people who control the server can change the map.
