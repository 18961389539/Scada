export default {
  hmi: {
    polyline: 'Polyline pipe', panel: 'Equipment panel', input: 'Numeric input', apply: 'Send', value: 'Setpoint', invalidInput: 'Enter a valid value within the configured range',
    states: 'State mapping', stateValue: 'State value', stateLabel: 'State label', addState: 'Add state', stopped: 'Stopped', running: 'Running', moving: 'Moving', fault: 'Fault', unknown: 'Unknown state',
    points: 'Pipe waypoints', addPoint: 'Add waypoint', source: 'Source equipment', target: 'Target equipment', port: 'Connection port', left: 'Left', right: 'Right', top: 'Top', bottom: 'Bottom',
    commands: 'Panel operations', addCommand: 'Add operation', commandLabel: 'Operation label', commandTag: 'Command tag', commandValue: 'Command value', remove: 'Delete', start: 'Start', stop: 'Stop',
    enableStates: 'Add common states', feedback: 'Feedback', noCommands: 'No operations configured', stateColor: 'State color'
  },
  traceability: {
    title: 'Traceability', keywords: 'production SN QR barcode current voltage traceability',
    records: 'Records', scan: 'Scan Binding', config: 'Capture Jobs', refresh: 'Refresh', snQuery: 'Scan or enter SN',
    allJobs: 'All jobs', from: 'From', to: 'To', query: 'Search', empty: 'No records',
    job: 'Capture job', time: 'Captured at', ack: 'PLC acknowledgement', acknowledged: 'Replied 2', awaitingReply: 'Saved, awaiting reply',
    fieldName: 'Field name', value: 'Value', unit: 'Unit', scanSn: 'Product SN', bind: 'Bind SN',
    state: 'State', pendingSn: 'Pending SN', expires: 'Expires at', reply: 'Reply value', clearScan: 'Clear pending SN',
    noScanJobs: 'No enabled jobs accepting scans', add: 'New job', edit: 'Edit job', trigger: 'Trigger point', snSource: 'SN source',
    tcp: 'TCP scanner', serial: 'RS232 scanner', connection: 'Scanner connection', tcpMode: 'TCP mode', tcpClient: 'Connect', tcpServer: 'Listen',
    tcpHost: 'Scanner host', listenHost: 'Local listen address', port: 'Port', serialPort: 'Serial port', baudRate: 'Baud rate', dataBits: 'Data bits', parity: 'Parity', stopBits: 'Stop bits',
    parityNone: 'None', parityOdd: 'Odd', parityEven: 'Even', terminator: 'Frame terminator', encoding: 'Character encoding',
    scannerStates: { stopped: 'Stopped', connecting: 'Connecting', listening: 'Awaiting scanner connection', connected: 'Connected', disconnected: 'Disconnected, retrying', error: 'Invalid configuration' },
    plc: 'PLC registers', webScan: 'Web scan', enabled: 'Enabled', yes: 'Yes', no: 'No', device: 'PLC device',
    snFormat: 'SN format', numeric: 'Numeric', asciiLow: 'ASCII (low byte first)', asciiHigh: 'ASCII (high byte first)',
    snRegisters: 'SN registers (select in encoding order)', scanExpiry: 'Scan expiry (seconds)', fields: 'Capture fields', addField: 'Add field',
    tag: 'Data point', remove: 'Remove', cancel: 'Cancel', save: 'Save', required: 'Complete the job, trigger, SN source and data fields.',
    loadFailed: 'Could not load jobs or points', queryFailed: 'Could not query traceability records', scanFailed: 'Scan binding failed', saveFailed: 'Could not save the job',
    states: { idle: 'Awaiting trigger', ready: 'SN ready', capturing: 'Capturing', awaitingReply: 'Awaiting PLC reply', completed: 'Completed', failed: 'Failed', error: 'Error', offline: 'Device offline' }
  },
  flow: {
    fit: 'Fit', zoomIn: 'Zoom in', zoomOut: 'Zoom out', autoLayout: 'Auto layout', deleteEdge: 'Delete link', then: 'yes', else: 'no', fail: 'fail',
    hintEdit: 'Drag nodes to move; drag from a dot under a node onto the target to link; select a link to delete it; wheel to zoom, drag empty space to pan. Dashed = falls through to next step.',
    hintRun: 'Live step state: yellow running, green done, red failed, faded skipped.',
    port: { next: 'Next', then: 'If true', else: 'If false', branch: 'Parallel branch', join: 'Join step', target: 'Jump target', fail: 'On failure go to' }
  }
}
