# ADR-085: App dependencies

Status: accepted

## Context
An app often builds on another (a shop on a billing app). Installing them in the wrong order, or without the app they need, produced confusing errors (fixtures for DocTypes that did not exist yet).

## Decision
- `IQahwaApp.Dependencies` lists the names of the apps this one needs (empty by default).
- At start-up the catalog orders the apps so that each comes **after** the apps it needs (ties keep the order they were added), and fixtures are made in that order. A dependency that is not installed, an app that needs itself, or a circle of apps stops the server with a message that names the apps.
- Uninstalling an app and installing one by CLI are separate, later steps; today every registered app is installed on every site at start-up.

## Consequences
- Version ranges are not checked; only presence and order.
