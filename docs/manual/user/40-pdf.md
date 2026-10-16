# PDF files of printouts

In the **Print** dialog, **Download PDF** gives you the printout as a PDF file: the same format and letter head you see in the preview. If the button is not there, this server has no browser set up to make PDF files; use **Open to print** and choose **Save as PDF** in the print window.

- You can make PDF files of documents you are allowed to read, up to 10 a minute.
- Pictures in a **letter head** must be embedded in it (a picture pasted or uploaded, not a web address), because the server never loads anything from the internet when it makes a PDF.

## For the person who runs the system
A PDF is made by a copy of Chrome, Chromium or Edge on the server. The usual places are searched; to choose one:

```json
"Qahwa": { "Pdf": { "BrowserPath": "/usr/bin/chromium", "TimeoutSeconds": 30, "Concurrency": 2 } }
```

On Linux, install Chromium (`apt install chromium`). Inside a container that runs as root you may need `"NoSandbox": true`; prefer running as a normal user.
