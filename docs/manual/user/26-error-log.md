# Error log

When the system hits an unexpected problem, the person sees a general message and a record is added to **Error Log** (System Manager only, in the sidebar).

Each record shows where it happened (a request or the background scheduler), the address (without anything after a `?`), who was signed in, the kind of error, the message and the technical trace. Send these to whoever maintains the system.

- The request's content, headers and passwords are never recorded.
- Very many errors in a short time are only partly recorded (30 a minute, 500 an hour) so the log cannot fill up.
- Delete old records from the list when you no longer need them.
