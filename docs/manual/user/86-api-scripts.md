# API scripts (for System Managers)

Open **Server Script**, choose **When = API** and give it a **method name** such as `sum_two`. Write the script: the values sent to it are in `doc`, and what you assign to `result` is sent back.

```
result = { total: doc.a + doc.b };
```

Call it with `POST /api/script-method/sum_two` and a body like `{"a": 2, "b": 3}`. It answers `{"data": {"total": 5}}`. Anyone signed in may call it unless you choose a role in **Who may call it**. Whatever the script reads or changes is checked against the person who called it, so it can never do more than they could do themselves.
