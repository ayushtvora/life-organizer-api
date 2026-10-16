# iPhone Shortcut

Build this in the **Shortcuts** app (tap **+** for a new shortcut). It has four actions.

1. **Ask for Input**
   - Prompt: `What do you want to add?`
   - Input Type: **Text**

2. **Get Contents of URL**
   - URL: `https://organizer.yourdomain.com/api/capture`
   - Tap **Show More**:
     - Method: **POST**
     - Headers: `X-API-Key` → *your API key*
     - Request Body: **JSON**, add a field: key `text`, type **Text**, value → the **Provided Input** variable

3. **Get Dictionary Value**
   - Get **Value** for key `message` in **Contents of URL**

4. **Show Result** (or **Show Notification**)
   - Text: the **Dictionary Value** variable

Name the shortcut (for example "Add"), then add it to the Home Screen, the Action Button, Back Tap
(**Settings → Accessibility → Touch → Back Tap**), or a Lock Screen widget.

## What you'll see

```
📅 Added to Google Calendar
Meeting
🕒 Wed, Oct 21 · 4:00 PM – 5:00 PM
```

```
✅ Added to Todoist
Do laundry
🗓 Due Tomorrow (Sat, Oct 17) · 5:00 AM
```

Errors, such as a bad API key or Google/Todoist being down, also come back as a `message`, so step 4 shows them too.

## Tips

- **Force the type:** start with `e`, `event`, `c`, `cal` or `calendar` for an event, or `t`, `task`, `todo` or
  `todoist` for a task. The colon is optional: `e pick up sam 3pm`, `t: review slides 4-5pm`.
- **Repeat:** `standup every weekday 9am`, `take out trash every monday`, `team sync every other tuesday 2-3pm`.
- **Multi-day:** `trip oct 20-22` creates an all-day event covering all three days.
- **Priority** (tasks): add `p1`–`p3`, for example `pay rent oct 31 p1`.
- **Dictation:** replace step 1 with **Dictate Text** to capture by voice.
- **Open the item:** add **Get Dictionary Value** for `link`, then **Open URLs**, if you want to jump to the created
  event or task.

## Response fields

`POST /api/capture` with `{"text": "..."}` and an `X-API-Key` header returns:

| Field | Example |
|---|---|
| `success` | `true` |
| `message` | ready-to-show confirmation text |
| `app` | `Google Calendar` / `Todoist` |
| `type` | `event` / `task` |
| `title` | `Meeting` |
| `start`, `end` | `2026-10-21T16:00` (or `2026-10-21` when `allDay`) |
| `allDay`, `recurrence`, `priority` | |
| `link` | URL of the created event/task |
| `parser` | `rules` or `llm` |
