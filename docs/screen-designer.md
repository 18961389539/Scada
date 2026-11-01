# Screen Designer

Open **Screens**, create or edit a screen, then save the draft and publish it when ready.
Draft saves do not change the published runtime screen. Publishing uses the current
server revision; stale saves remain rejected instead of overwriting newer edits.

## Immutable Publications

Publishing expands library templates into ordinary screen nodes and embeds the
referenced user SVG assets as `symbolAssets`. Rendered content, bound tags, panel
operations and fixed command values all come from that same published document.
Changing or importing a library item does not change an existing publication or its
revision. Drafts and draft previews still use the current library; explicitly
republish to adopt its changes. Refresh the runtime after republishing. Requests
from an older published revision are rejected, and fixed-value controls cannot
override their configured values in a request.

Published screens no longer require a successful library fetch. Engineering exports,
imports and configuration rollback preserve their embedded assets and expanded
nodes, while remapping tag and navigation IDs normally. Imported SVG assets undergo
the same static-content validation as the library. Drafts and library templates
cannot supply their own publication assets.

After upgrading, old publications that still contain template references or user
symbols without embedded assets require a configuration user to **republish** before
display or control. The runtime reports this explicitly; it does not guess the
original library contents or silently freeze the current ones. Old screens without
these library dependencies continue to work. Review the current draft and library
before republishing. Back up the project before upgrading.

## Publication History and Screen Rollback

Users with global Configure permission can open **Publication history** in the screen
editor, load older revisions and preview their frozen documents without issuing control
commands or depending on the live library. Live values remain current; navigation and
popups show the targets' current publications, also read-only, not historical target
snapshots. History shows the published name, actor, time, note, origin and rollback source.
Saving a draft does not add a publication. Normal one-click publishing remains unchanged;
HTTP and MCP callers may supply an optional publication note.

To roll back, save any unsaved editor changes first, select a historical publication,
enter a nonempty explanation of up to 500 characters and explicitly acknowledge the
runtime change. Rollback appends a new publication revision; it does not overwrite the
current draft, its name or older history. Historical tags, writable command targets,
navigation and SVG assets are revalidated against current configuration, without
re-expanding templates from the current library. Invalid references and stale editor
revisions are rejected. Refresh running clients after rollback; commands from previous
publication revisions are rejected. Automatic project-snapshot failure is reported
separately and does not undo a publication that already committed.

History is stored in the local configuration database, including publications made via
HTTP, MCP and project application. Each screen keeps its newest 100 revisions plus the
current published revision; older entries are trimmed after publishing, rollback and
project application, so very old revisions cannot be previewed or restored. Project
packages transfer the current draft and publication, not every source-environment
screen revision; importing or restoring a project snapshot appends local history.
Deleting a screen also deletes its history. Use database backups when retaining the
full publication history is required.

Upgrades capture only the existing current publication. Migration records explicitly
identify their timestamp and actor as inherited from the screen's last modification,
not proven original publication metadata. No missing older versions are fabricated.
Legacy records with unresolved library dependencies remain visible as metadata but
cannot be previewed or directly restored; review the draft and explicitly republish.

## Interaction Components

The screen editor uses Moveable for node/group dragging and the single-node resize
handle, and Selecto for marquee hit testing. Coordinates still belong to the screen
document, including grid snapping, group boundary limits and one-step undo/redo.
These interaction libraries do not change screen coordinates or draft recovery.

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
  placeholders, like ordinary command buttons. The library editor roundtrips these
  command bindings as well as feedback, alarm and ordinary command bindings.
  Placeholder IDs remain stable when another placeholder is removed; an in-use
  placeholder cannot be removed, and an invalid restored binding blocks saving.
  Internal pipe references are preserved; connections to equipment outside the
  selection are detached.
- Numeric inputs accept a setpoint within the configured minimum/maximum and
  precision, then submit through the shared control flow. The control dialog opens
  only when the control point requires operator confirmation; other writes proceed
  immediately and report refusals, read-back and receipts as non-blocking
  notifications. Each command action must be bound to a writable tag. The server
  independently checks the input range, resolves panel operations from the
  published configuration and rejects unknown operations or modified panel values.

Both new control types use the existing permission, area, interlock, approval,
readback and receipt flow. Controls are disabled in draft previews, mobile
read-only mode and while a confirmation/submission is pending. A failed tag
catalog disables them rather than allowing an unverified write.

## Operator Controls

The palette also provides **Reset**, **Jog**, **Momentary pulse**, **Silence**,
**Multi-position selector** and **Rotary knob**. Reset, jog/pulse and silence are
operator commands; the selector and knob are position pickers. All six render in
the designer and the published runtime; drafts, previews and read-only clients keep
them disabled like other controls.

- Reset has two modes. **Alarms** acknowledges all visible annunciator panels and
  requires AckAlarm permission; the server filters panels by area visibility and
  audits the batch. **Tag** writes one fixed value through the shared control flow
  and therefore needs a writable tag, confirmation and approval exactly like a
  command button.
- Jog runs the full control checks on press and writes the release value on release.
  The release deliberately skips interlocks, limits and confirmation so that letting
  go always attempts to return the equipment to its safe state. The server keeps a
  lease per tag, rejects a concurrent press on the same tag, and releases after the
  configured maximum hold time (0.5-30 s) even if the client disappears. Window
  blur, tab hide, navigation and unmount also send releases.
- Momentary pulse writes on press and releases automatically after the configured
  duration (100-5000 ms). It uses the same lease and watchdog; a manual early
  release is ignored. Jog and pulse require a release value different from the
  action value.
- Silence batch-silences all visible enabled annunciator panels for 1-120 minutes
  (default 5) and requires AckAlarm permission. It does not acknowledge alarms and
  does not affect panels outside the operator's visible areas.
- Selectors and knobs must define 2-8 distinct state positions. Clicking or using
  the keyboard sends that position's exact value through the shared control flow;
  the displayed position is the configured state nearest to the current feedback
  value. Position values are validated server-side against the published screen.

Previous publication revisions remain rejected for these controls, and fixed
values cannot be overridden by a request. Optional JSON fields keep schema version 1
and require no database migration. Normal draft saving and publication are still
required.

## Trend Interaction

Line and area charts keep the crosshair inside the canvas. Hovering a chart with
bound tags draws a vertical cursor at the nearest history sample of each series,
marks that sample, and shows the timestamp plus per-series values in a small
tooltip. Values come from the same history window that feeds the chart, so a stale
or unavailable history never produces a tooltip. Touch and pen input do not open
the hover overlay; it stays a pointing-device affordance.

Clicking a line/area chart, or a table row with bound tags, opens the trend dialog.
It reuses the runtime history already loaded and renders it with ECharts: legend
toggles, inside/slider zoom, a reset-zoom button and a latest-value list. The
dialog shows an explicit unavailable state when history could not be verified
within its freshness window, and an empty state when the window has no samples.
The dialog is read-only and never changes the screen document.

## Recipe Control

**Recipe selection** renders a template picker bound to one recipe template. The
runtime lists recipes of that template that the operator may download; the list is
loaded once per screen, not per widget. Selecting a recipe and pressing **Download**
runs the existing recipe check first: parameter tags, current and target values and
refusals are shown, dual-approval templates are flagged, and the download is
submitted only after explicit confirmation. A pending or running download is
polled to a terminal status, then the screen values refresh. The control is
disabled in draft previews, monitor mode, mobile read-only mode, for templates the
operator cannot control (area or any parameter tag), and while another command is
pending.

The designer requires a recipe template on every recipe control and rejects a
screen action on it. A template that has since been deleted renders as unavailable
at runtime; publication validation does not block on it, matching the behaviour of
other deletable references.

## Data Tables

Table and history-table headers sort by name, unit, live value or min/max/avg.
Sorting applies to the full bound tag list before rows are trimmed to the visible
height, so off-screen tags still participate. Clicking a row selects it for reading
without sending any command; history-table rows also indicate a double-click drills
into that tag's trend. Hovering highlights the row. Sort and selection are runtime
state only and are never written to the document; an empty header click restores
the original tag order.

## Click Actions

Besides navigation, popup and command, a click action can start a control sequence
or download a recipe. Both require a selected target in the designer and are
validated for presence on save. At runtime the operator sees the existing
confirmation dialog, and the sequence start is rejected for operators without
Control permission in the sequence's area, or on mobile clients that block control.
A sequence that is no longer available renders as unavailable instead of failing
silently. Like other actions, both are disabled in draft previews, monitor mode and
mobile read-only mode. The server still resolves the sequence, recipe and all
parameter tags from its own configuration; nothing in the published action can
override fixed values.

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

## Connection Recovery

Screens share one reference-counted realtime connection. If SignalR exhausts its
automatic reconnect attempts, the shared connection continues retrying every five
seconds while authenticated consumers remain. Recovery restores the current tag
subscription union before requesting fresh snapshots. Initial startup is not treated
as a reconnect. Logout, an explicit stop or disposal of all consumers prevents
background reconnection, and callbacks from retired connections cannot overwrite
the active connection. Existing periodic HTTP snapshots remain the fallback. Manual
refresh also requests fresh alarms instead of reusing the short-lived shared cache;
a failed alarm request does not mark old data as freshly verified. This is not
offline operation or a replacement for device/network commissioning.

## Switching Between Runtime and Configuration

Users with global Configure permission can select **Edit current screen** in a
published screen's runtime toolbar. The shortcut edits the screen currently shown,
including after navigation within a layout region. Draft previews omit this shortcut.

The editor provides **Return to runtime** when opened from a running screen, or
**Open runtime** when opened directly. Returning to a layout restores its monitor,
region and current screen. Unsaved edits still require confirmation before leaving;
opening runtime does not save or publish them. Unpublished screens must be published
before their runtime can be opened.
