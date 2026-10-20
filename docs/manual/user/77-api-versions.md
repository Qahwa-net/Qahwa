# API versions

For programs that talk to Qahwa: use addresses that start with `/api/v1/` (for example `/api/v1/resource/Customer`). They behave exactly like `/api/…`, and Qahwa promises that anything that would break your program will only appear under a new number (`/api/v2/`), while `v1` keeps working. Every answer tells you the version in the `X-Qahwa-Api-Version` header.
