# Permissions, sharing, profiles and user permissions

## The model in one minute

People have **roles**. Each DocType says what each role may do. On top of that you can hide some fields, limit someone to certain values, share single documents, and hand out roles or hide modules through profiles. All of it is edited in the Desk.

## Role Permissions (Tools → Role Permissions)

Pick a DocType to see who may do what. Each row is a role with ticks for **Read, Write, Create, Delete, Export, Report** (and **Submit, Cancel** on submittable DocTypes) and **Own only** (only documents they created). **Level** is for field protection (below). **Save** applies at once; **Reset to default** returns to the DocType's own rules. If no row exists, only Administrators can use the DocType. Framework DocTypes (User, Role) can only be changed by an Administrator.

## Protecting fields (permission levels)

Give a field a level from 1 to 9 (in the DocType Designer or Customize Form), then add a rule for the role at the same level with Read and/or Write. Everyone else does not see the field at all: not in forms, lists, filters or exports. Example: salary at level 1, HR Manager has a level-1 rule.

## Users, roles and profiles

Documents of **User**, **Role** and **Has Role** control who has which role. For many people at once, create a **Role Profile** (a named set of roles) and choose it on each user: their roles become exactly the profile's, and editing the profile updates everyone who has it. A **Module Profile** lists modules to hide: users with it do not see those DocTypes at all. Only System Managers, with the right to assign roles, can change these.

## User Permissions: limit someone to certain values

Create a **User Permission**: user *ali@example.com*, allow *Territory*, value *North*. Ali now only sees territories, and customers or orders linked to territories, that are North (documents with no territory stay visible). Tick **Apply to all DocTypes** or name one DocType. Ali also cannot pick another territory in a form.

## Sharing one document

On a form click **Share**, search for a person, and choose whether they may edit. See [The Desk](01-desk-basics.md#sharing-a-document). Sharing never gives the right to delete, submit or export, and user permissions still apply.

## Who can do what, quickly

| I want to... | Do this |
|---|---|
| Let a role use a DocType | Role Permissions → pick the DocType → add a row |
| Hide salary from most people | Field level 1, rule at level 1 for HR only |
| Give a new hire the standard roles | Role Profile on the user |
| Hide the Accounts module from a team | Module Profile with *Accounts* blocked |
| Keep a rep to their own region | User Permission on Territory |
| Show one customer to a colleague | Share on that customer |
