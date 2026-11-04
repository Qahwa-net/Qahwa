# Energy points

Energy points recognise work done in the system: for example **5 points** for raising a ticket, **10** for closing one. Open **My work → Energy Points** to see your total, this week and this month, the **leaderboard** (names only) and your latest points; each latest entry links to the document it came from.

## Setting the rules (System Managers)
Open **Tools → Energy Point Rule** and add a rule:
- **When this DocType changes** and the **Event**: *New*, *Update*, *ValueChange* (name the field to watch), *Submit* or *Cancel*.
- **Only when**: an optional condition such as `status == 'Closed'`.
- **Points**: from -100 to 100, not zero (negative points take points away).
- **Who gets the points**: `owner` (who made the document), `modified_by` (who last changed it), or a Link-to-User field of the document, such as a *Fixed by* field.
- **Once for each document** (on by default) and **Most times a day** for one person (10 by default, 0 for no limit): these stop points being collected by editing the same document again and again.

Points go only to enabled users. A person's points are never edited by hand: they are the sum of the log (**Energy Point Log**, readable by System Managers).
