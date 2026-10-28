# Production Traceability

Open `/traceability` from the production traceability navigation entry.

## Configure a Capture Job

1. Configure the PLC and its physical data points first.
2. Select a writable, unscaled numeric trigger point. It must support values 1, 2 and 3; a Boolean point cannot be used.
3. Select exactly one SN source: PLC registers, web scan, TCP scanner or RS232 scanner.
4. Add named data fields, such as current and voltage, bound to points on the same PLC.
5. Optionally select one inspection-result field and distinct pass/fail values.
6. Enable the job. New jobs are disabled by default.

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

## Inspection Results Are Not PLC Acknowledgements

The records table and its outcome filter distinguish:

| Stored outcome | Meaning |
| --- | --- |
| `Passed` | The selected field matches its configured pass value. |
| `Failed` | The selected field matches its configured fail value. |
| `Unknown` | A mapped field was captured successfully but matches neither value. |
| `NotEvaluated` | No outcome mapping was configured, or the record predates outcome snapshots. |

Configure at most one mapped field per job. Values must be finite and distinct.
The comparison uses the captured numeric value, including tag scaling and offset,
not an independently re-read raw PLC register. For example:

```json
{
  "name": "Inspection",
  "tagId": 123,
  "outcomeMapping": { "passValue": 1, "failValue": 2 }
}
```

Both the record outcome and the mapped field's outcome are saved as snapshots
when the capture is committed. A failed product still receives PLC reply **2**
when its data was acquired and persisted correctly. Bad data quality or a failed
commit remains a capture failure with reply **3**, not a product inspection result.

Existing records are migrated to `NotEvaluated` without inspecting or rewriting
their saved values. Adding or changing a job's mapping affects only subsequent
captures; it never reclassifies history. The API accepts
`GET /api/traceability/records?outcome=Failed` (or any of the four outcome names).
Area and outcome filters are applied before counting and pagination.

## SN Sources

- **PLC numeric:** select one point containing a non-negative integer SN.
- **PLC ASCII:** select unscaled UInt16 register points in encoding order and
  choose low-byte-first or high-byte-first. NUL terminates the string; otherwise
  all selected bytes are decoded. ASCII letters, digits and printable symbols
  are supported. Numeric SNs do not retain leading zeros; use ASCII for those.
- **Web scan:** select a job and scan into its SN input using a keyboard/HID
  scanner with an Enter suffix, or enter and bind the SN manually. This does
  not require or activate a camera. Bind before the PLC requests capture.
- **TCP scanner:** select Connect when the scanner listens for connections;
  configure its host and port. Select Listen when the scanner connects to
  SCADA; configure a local server IP address and port, then point the scanner
  at that address. The default listen address is loopback; use the server's
  LAN IP for a remote scanner. Allow inbound traffic through the firewall.
- **RS232 scanner:** connect the scanner to the backend server, directly or
  through a USB-to-RS232 adapter, and configure its COM port, baud rate,
  data bits, parity and stop bits. On Linux, use the device path such as
  `/dev/ttyUSB0` and grant the service account serial-port access. This is
  a backend serial connection, not access to the browser workstation's port.

TCP and RS232 scanners send plain-text SNs, not Modbus messages. Configure
the matching CRLF, LF or CR suffix and UTF-8 or ASCII encoding; defaults are
CRLF and UTF-8. Leading zeros are retained. Split and combined transport
packets are handled, empty lines are ignored, and incomplete frames are
discarded when the connection closes. SNs must contain 1 to 200 printable
characters; overlong unterminated frames close the connection. Custom binary
frames and STX/ETX framing are not supported.

Enable the job to start its connection. The server retries disconnected
scanners every two seconds and releases connections on disable, configuration
change, standby or shutdown. Only one scanner connection is active per job.
Enabled jobs cannot share a scanner endpoint or serial port; a serial port
already assigned to an enabled Modbus PLC is also rejected.

In Scan Binding, select the TCP/RS232 job to view connection state, received
SN, expiry and errors, or clear a waiting SN. These jobs do not accept manual
web binding. Scanning alone does not save a production record: send the SN
before the PLC sets the capture trigger to 1. No acknowledgement is sent to
the scanner itself; the PLC handshake remains unchanged. TCP is unencrypted
and does not authenticate scanner devices, so use a trusted industrial
network and restrict the listener with a firewall. Do not expose it to the
public Internet.

An SN received from web, TCP or RS232 is consumed once, including when acquisition fails. A waiting SN
cannot be overwritten; clear it explicitly before replacing it. Its configured
expiry is between 5 and 3600 seconds. Pending scans are held in memory and
must be scanned again after a service restart or job configuration change.
Additional scans received while an SN is waiting are rejected and shown as
an error; they are not queued or substituted for the current product.

## Query and Permissions

Scan or enter an SN in the records tab for an exact match. Optional job and
time/outcome filters and pagination are available. Expand a record to see its named
data fields and units. A saved record whose PLC reply has not been confirmed
is labelled separately from a confirmed capture.

Records are filtered by captured area. Task configuration requires configuration
and control permission on the trigger, plus view permission on all captured
points. Web SN binding requires control permission. Existing read-only
workstation and mobile control restrictions also apply to scan binding.

Configuration changes are rejected while a capture is awaiting a reply.
Database migrations run automatically during normal server startup.
