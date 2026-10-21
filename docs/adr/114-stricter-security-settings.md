# ADR-114: Stricter sign-in and password rules in System Settings

Status: accepted. Narrows the "session and password rules stay in configuration" limit of ADR-107.

## Decision
System Settings has three optional numbers: the shortest password (8 to 64), the longest sign-in in days (1 to 90) and how often passwords must be changed (1 to 3650); 0 means "as configured". The server configuration (`Qahwa:Auth:MinPasswordLength`, `AbsoluteSessionDays`, `PasswordMaxAgeDays`) stays the **floor**: the Desk can only make a rule **stricter** (a longer minimum, a shorter sign-in, a shorter password life, or adding an expiry where the configuration has none), never looser. A value that is missing or out of range is ignored, and saving one is refused. `SecurityPolicyService` computes the rule in force.

## Why this is safe
The reason these rules were left out of the Desk was that a wrong value could lock every administrator out with no way back except the database. With stricter-only values that cannot happen through a typo: a longer minimum affects only passwords set from then on, a shorter sign-in only new sign-ins (people sign in again), and a shorter password life asks people to choose a new password through the normal reset. Nothing becomes easier to guess or easier to keep alive than the configuration allows.

## Not moved into the Desk
Lockout rules (attempts and minutes), the hash cost, the network allow-list and the public address stay in configuration.
