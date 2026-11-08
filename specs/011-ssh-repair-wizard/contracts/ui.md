# UI Contract: Repairs in the SSH Check Wizard

Mockups: [spec.md](../spec.md#layout-mockup-proposed--to-be-approved-before-planning). Extends the
wizard of feature 009 (non-modal, four pages); without edit mode nothing changes.

## Result page additions

| Control | Behaviour |
|---------|-----------|
| **Fix…** (one per repairable reason, right of the reason) | opens `RepairConfirmDialog` for that reason; absent when the reason has no repair; disabled with a tooltip when the plan is `Unavailable` (reason text from `RepairRefusal`) |
| **Add a key…** (on reasons `NoPasswordNoKey`, `PasswordAuthOffNoKey`, and always as a link under the account list on page 3) | opens `AddKeyDialog`; account pre-selected: the reason's account, else the only account that can log in, else none |
| Waiting repairs block | only on images, only when ≥ 1 waits: one line per repair in words + **Undo…** (menu of repairs); text "After these repairs the verdict will be: …" from the re-run check; verdict banner of the page shows the *current* result with "(waiting for Save)" for each reason resolved only by waiting repairs |
| Hint line | when repairs are unavailable: "To repair, switch on Edit on the Files tab." or the 010 reason (not clean, unsupported, write-protected, not removable) |
| Busy | while a repair is applied/re-checked: all repair buttons disabled, spinner text in the status line; the dialog stays closable (closing never cancels a card write already started) |

Page 3 (Accounts) gets a **Fix…** next to an account whose key file is unsafe, and the same
**Add a key…** link.

## RepairConfirmDialog

Title "Fix: {repair}". Body: one sentence on what is wrong; a list "This will change:" of
`ChangeLine`s (path, before → after; for new links the link and its target; for modes `755 → 700`,
owner `root → pi`); on a card the line "Written to the card at once; this cannot be undone."; on an
image "Waits for Save; you can undo it." When `Risk != None`: a red paragraph with the risk and a
check box "I understand" that enables **Apply**. When `Alternatives` exist (root login), a radio list
with the key-only choice selected. Buttons **Apply** (default only when no risk) / **Cancel**.
The dialog is modal to the wizard only.

## AddKeyDialog

Fields: account (combo of accounts with a home), key box (multi-line, wrap, 3 rows), **Load…**
(`.pub` file, ≤ 16 KiB, first non-empty line used; more than one key → refusal text), read-only
facts row (type, fingerprint, comment, options warning), target path line ("will be created, mode
600, owner pi" / "added at the end of the existing file"), message line for refusals/warnings.
**Add** is enabled only for a valid, non-duplicate key and a writable target. A private key in the
box: box cleared, message "This is a private key. Never share it; paste the *public* key (.pub)."
Pasting is the only way text arrives besides Load; the box has no history; closing the dialog drops
the string.

## Host behaviour (Files tab)

- `Edit` toggled off, session closed, or file system closed → wizard receives `Changed`, hides
  repair controls at once; open confirm/add dialogs are cancelled.
- After **Apply** on an image: pending tree refreshes (tree shows the new/changed entries per 010),
  status line count rises, wizard re-checks. On a card: change written with the 010 progress banner,
  then the tree is rebuilt and the wizard re-checks; a failure shows the 010 error text with offset.
- **Save** / **Discard** of the Files tab clear/commit the waiting repairs; the wizard re-checks.

## Report text

`Copy` / `Save as…` add a section "Repairs in this session": one line per repair in words (images:
"waiting for Save" / "saved"; cards: "written"), the fingerprint for keys; no key body.

## Strings (new, all six languages)

`Repair_*` titles per `RepairKind`, `Repair_Why_*` explanations, `Repair_Risk_*`, `Repair_Refused_*`
(per `RepairRefusal`), `Repair_Key_*` (labels, refusals per `Refusal`, warnings), `Repair_Hint_*`,
`Repair_Waiting`, `Repair_AfterVerdict`, `Repair_Report_*`, tooltips. `ResourceParityTests` already
covers presence in all languages; a new test covers that every enum value has a string.
