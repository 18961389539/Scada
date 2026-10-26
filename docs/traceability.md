# Production Traceability

Open `/traceability` from the production traceability navigation entry.

## Configure a Capture Job

1. Configure the PLC and its physical data points first.
2. Select a writable, unscaled numeric trigger point. It must support values 1, 2 and 3; a Boolean point cannot be used.
3. Select exactly one SN source: PLC registers or web scan.
4. Add named data fields, such as current and voltage, bound to points on the same PLC.
5. Enable the job. New jobs are disabled by default.

Immediate acquisition currently supports directly connected Modbus TCP/RTU,
Siemens S7, Mitsubishi MC and Omron FINS devices. Remote gateway acquisition is
not enabled for this workflow. Each trigger point can belong to only one job.

## PLC Handshake

| Trigger Value | Meaning |
| --- | --- |
| 1 | PLC requests capture. |
| 2 | SN and all data fields were read successfully and committed to the database. |
| 3 | Capture failed, including an absent/invalid SN, invalid data quality or a failed database commit. |

The PLC must keep the trigger at 1 and hold the product data unchanged until
it receives 2 or 3. A capture reads the PLC again, rather than copying the
last polling-cache values. The server writes the result back to the same
trigger point and verifies the feedback.

After receiving 2 or 3, the PLC may load the next product and write 1 again.
Do not send a short trigger pulse or replace the product data before the reply.

Committed records are unique by capture cycle, not by SN: the same SN can
have multiple station or process records. When a success reply fails, the
record remains saved and the original reply is retried without another
capture. After a restart, an interrupted, uncommitted capture fails with 3
rather than silently collecting a different product. If the trigger is changed
externally before acknowledgement, the pending result is held for operator
verification; restoring the original request value permits retrying that result.
No software can guarantee delivery of 2 or 3 while PLC communication is down.

## SN Sources

- **PLC numeric:** select one point containing a non-negative integer SN.
- **PLC ASCII:** select unscaled UInt16 register points in encoding order and
  choose low-byte-first or high-byte-first. NUL terminates the string; otherwise
  all selected bytes are decoded. ASCII letters, digits and printable symbols
  are supported. Numeric SNs do not retain leading zeros; use ASCII for those.
- **Web scan:** select a job and scan into its SN input using a keyboard/HID
  scanner with an Enter suffix, or enter and bind the SN manually. This does
  not require or activate a camera. Bind before the PLC requests capture.

A web SN is consumed once, including when acquisition fails. A waiting SN
cannot be overwritten; clear it explicitly before replacing it. Its configured
expiry is between 5 and 3600 seconds. Pending web scans are held in memory and
must be scanned again after a service restart or job configuration change.

## Query and Permissions

Scan or enter an SN in the records tab for an exact match. Optional job and
time filters and pagination are available. Expand a record to see its named
data fields and units. A saved record whose PLC reply has not been confirmed
is labelled separately from a confirmed capture.

Records are filtered by captured area. Task configuration requires configuration
and control permission on the trigger, plus view permission on all captured
points. Web SN binding requires control permission. Existing read-only
workstation and mobile control restrictions also apply to scan binding.

Configuration changes are rejected while a capture is awaiting a reply.
Database migrations run automatically during normal server startup.