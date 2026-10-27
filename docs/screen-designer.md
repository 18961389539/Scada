# Screen Designer

Open **Screens**, create or edit a screen, then save the draft and publish it when ready.
Draft saves do not change the published runtime screen. Publishing uses the current
server revision; stale saves remain rejected instead of overwriting newer edits.

## Interaction Components

The screen editor uses Moveable for node/group dragging and the single-node resize
handle, and Selecto for marquee hit testing. Coordinates still belong to the screen
document, including grid snapping, group boundary limits and one-step undo/redo.
Existing screen JSON, draft recovery and the published runtime remain unchanged.

Sequence diagrams use Vue Flow for nodes, links and viewport interaction. Connections
still update the existing step fields (`next`, `then`, `else`, branches, joins and
failure targets), rather than saving a separate graph format. Runtime diagrams remain
read-only. On narrow screens the step properties appear below the diagram.

## Equipment Components

The palette includes **Polyline pipe**, **Equipment panel** and **Numeric input**.
Pumps, valves, motors, fans, tanks, library symbols and panels also support a
configurable state table.

- A free polyline has 2-64 waypoints, stored as percentages of its bounding box.
  Edit their coordinates, add/remove points, or drag the visible waypoint handles
  on an unrotated pipe. Dragging an endpoint within 18 screen pixels of an
  equipment port snaps it to that port. The properties panel can also select the
  source/target equipment and its left, right, top or bottom port.
- Connected endpoints follow equipment movement and rotation. With both ends
  connected, the pipe uses an automatic orthogonal route through the midpoint;
  this is not obstacle-avoiding routing. Disconnect both ends to edit waypoints.
  Connected pipes cannot rotate independently. Copying a selected equipment group
  remaps its internal connections; deleting equipment detaches its connected pipes
  at their current positions instead of leaving invalid references.
- State tables map exact numeric values to labels and colors, with up to 16
  distinct states. Common defaults are stopped, running, moving and fault.
  Missing, bad-quality, stale or unmapped values display an unknown state.
  A valid alarm input overrides a normal mapped state. Existing threshold-based
  behavior remains unchanged when no state table is configured.
- Equipment panels show the feedback value and state, with up to eight named
  operations. Each operation has its own writable target tag and fixed value.
  Saving the selection as a library template converts these targets to binding
  placeholders, like ordinary command buttons. Internal pipe references are
  preserved; connections to equipment outside the selection are detached.
- Numeric inputs accept a setpoint within the configured minimum/maximum and
  precision, then open the shared control confirmation dialog. Their command
  action must be bound to a writable tag. The server independently checks the
  input range, resolves panel operations from the published configuration and
  rejects unknown operations or modified panel values.

Both new control types use the existing permission, area, interlock, approval,
readback and receipt flow. Controls are disabled in draft previews, mobile
read-only mode and while a confirmation/submission is pending. A failed tag
catalog disables them rather than allowing an unverified write.

Optional JSON fields keep schema version 1 and require no database migration.
Normal draft saving and publication are still required.

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

## Switching Between Runtime and Configuration

Users with global Configure permission can select **Edit current screen** in a
published screen's runtime toolbar. The shortcut edits the screen currently shown,
including after navigation within a layout region. Draft previews omit this shortcut.

The editor provides **Return to runtime** when opened from a running screen, or
**Open runtime** when opened directly. Returning to a layout restores its monitor,
region and current screen. Unsaved edits still require confirmation before leaving;
opening runtime does not save or publish them. Unpublished screens must be published
before their runtime can be opened.
