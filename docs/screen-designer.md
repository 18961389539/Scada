# Screen Designer

Open **Screens**, create or edit a screen, then save the draft and publish it when ready.
Draft saves do not change the published runtime screen. Publishing uses the current
server revision; stale saves remain rejected instead of overwriting newer edits.

## Selection and Movement

- Click a node or its layer entry to select it. Shift-click adds or removes a node.
- Drag an empty canvas region to select intersecting nodes. Shift-drag adds to the
  selection; reverse dragging and rotated bounds are supported. Escape cancels an
  active selection box and restores the previous selection.
- Arrow keys move the selected group by one pixel; Shift uses a larger step.
  Movement and paste preserve spacing when any member reaches a canvas boundary.
- Ctrl/Cmd+A selects all nodes while the editor has focus. Text inputs, dialogs,
  global search and IME composition retain their own keyboard behavior.
- Undo/redo treats each arrangement, layer move or local-draft recovery as one edit.

## Arrangement and Layers

The **Arrange** menu aligns left, horizontal center, right, top, vertical center
or bottom. The first selected node is the reference. When an alignment would place
a member outside the canvas, the reference is adjusted to keep the group inside.

Horizontal and vertical distribution require at least three nodes. They distribute
equal edge-to-edge gaps, accounting for differing node dimensions. The two outer
edges stay fixed. Insufficient space is reported rather than introducing a negative gap.

Match-width, match-height and match-size use the first selected node's dimensions,
subject to the canvas bounds. Bring-forward/send-backward move one layer at a time;
bring-to-front/send-to-back remain available. Selected nodes retain their relative order.

Component search includes basic shapes, bilingual symbols, user symbols and templates.
Layer search includes text, translations, identifiers, types and bound tags. Fit and
locate-selection change only the viewport, not the document or undo history.

On narrow screens, the component/layer and property icon buttons open the side panels
over the canvas. Selecting a component closes its panel. The desktop three-column
layout is unchanged.

## Local Draft Recovery

Unsaved edits, including input that has not lost focus, are backed up in browser
localStorage after a short debounce and on page exit. Records are isolated by account,
screen and editor instance, so separate tabs do not overwrite each other's backups.
Embedded object-template editors do not create standalone screen backups.

Reopening a screen offers the newest structurally readable unsaved backup. Recovery
is manual and undoable, and does not save or publish anything. If the server baseline
or current editor has changed, recovery requires confirmation. Review recovered content
before saving. Incomplete numeric inputs can be recovered and corrected in the editor.

A successful save removes only the current instance's backup and the specific recovered
record if unchanged. Other tabs' records are retained. Discarding a recovery removes only
that displayed record; confirming navigation away from unsaved edits discards the current
instance's backup. A normal reload retains it for recovery.

Browser backups are size-bounded, local, unencrypted convenience copies, not server
version history or disaster-recovery backups. Clearing browser data removes them;
another browser or computer cannot recover them. Account separation does not encrypt
the underlying browser files. Use a trusted browser profile on shared workstations.
Storage denial, full storage or an oversized record produces a visible warning without
blocking manual server saving. Explicit save, validation and publish remain required.