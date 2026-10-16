# Patch Log

When a new version needs to change existing data (for example fill a new field), the change is made by a **patch** that runs automatically once when the system starts. **Patch Log** (System Manager) shows every patch: its name, whether it is **Done**, **Running** or **Failed**, when it started and finished, and the problem if it failed.

- A **failed** patch stops the system from starting until it is fixed; the error is shown here and in the server log. After the cause is fixed it runs again on the next start.
- You do not run or edit patches yourself. Deleting a row makes that patch run again at the next start; only do that on the advice of whoever maintains the system.
