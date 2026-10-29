export default {
  configurationUx: {
    query: 'Query', pendingQuery: 'Conditions changed. Previous results remain visible; click Query or press Enter to apply.',
    resultScope: 'Results: {0} · Page {1} · {2} records', all: 'All', loadHelp: 'Previous results are retained on failure. Reload; if the failure persists, check the service connection and account permissions.',
    basic: 'Basic connection', advanced: 'Advanced collection settings', advancedHint: 'Defaults usually suffice. Changes do not modify other protocol settings.',
    savedNext: 'Device saved: {0}. Saving does not verify connectivity. Continue with a test read or add tags.',
    addTags: 'Add / view tags', testConnection: 'Test read',
    guidanceTitle: 'Troubleshooting suggestions (not a confirmed diagnosis)',
    networkHelp: 'The message mentions connectivity or a timeout. Check host, port / serial port, reachability and device status before retrying. Do not mask failures by simply increasing timeouts.',
    certificateHelp: 'The message mentions certificates. Check the endpoint, security policy, validity and fingerprint. Verify the source before trusting a certificate; do not disable security checks.',
    permissionHelp: 'The message mentions authorization or authentication. Check the account, device credentials and area permissions. Ask an administrator to grant access before retrying.',
    addressHelp: 'The message mentions an address. Check the register area, address base, data type, DB / bit offset or OPC NodeId.',
    qualityHelp: 'A response does not imply valid data. Quality is not Good; check the tag address, type, quality and timestamps in diagnostics.',
    unknownHelp: 'The cause is not yet known. Keep the original error above and open collector diagnostics to inspect failures, configuration status and tag quality.',
    opcHelp: 'OPC UA: check the endpoint, NodeId, security mode and certificate trust. Do not switch to insecure mode to bypass a read failure.',
    modbusHelp: 'Modbus: check station number, register area and zero-based address. If connected values are incorrect, check data type and byte order.',
    protocolHelp: 'Check connection settings and addresses against the protocol manual. Diagnostics show whether configuration has taken effect.',
    verify: 'Verify again', setupDevice: 'Device to verify', setupProgress: '{0} / {1} checks verified',
    setupScope: 'Saved configuration, connectivity and tag quality are verified for the selected device. Published screens are checked across the site visible to this account. Status comes from actual data, not manual checkboxes.',
    checkedAt: 'Checked at {0}', done: 'Verified', pending: 'Pending', unknown: 'Unverified',
    deviceSaved: 'Device configuration saved', deviceSavedHint: 'Saving does not verify connectivity. Add tags and verify collection next.',
    tagsSaved: 'Tag configuration saved', tagsSavedHint: '{0} physical tags configured for the selected device.',
    connected: 'Device collection connected', connectedHint: 'Enable the device and configure at least one tag before checking connectivity. No test or reconnect commands are sent automatically.',
    values: 'Tag data valid', valuesHint: 'Good tags: {0} / configured tags: {1}. Every tag must have valid, fresh data.',
    screenPublished: 'Site has published screens', screenHint: 'Visible published screens: {0}. This does not verify binding to the selected device or runtime appearance.',
    expired: 'The diagnostic snapshot expired or configuration is pending. Verify again.',
    openStep: 'Open step', continueSetup: 'Continue setup: {0}',
    manualChecks: 'Manual checks remain necessary: screen bindings, units, ranges, runtime appearance, backup and restore. Automatic checks do not replace site acceptance.',
    tagsFailed: 'Failed to read tag configuration. Verify again.',
    screensFailed: 'Failed to read screen publication status. Verify again.',
    diagnosticsFailed: 'Collector diagnostics are unavailable. Check device enablement, configuration application and gateway connectivity, then verify again.',
    noViewPermission: 'This account cannot view screens. Ask an authorized user to verify.',
    testUnknown: 'The read did not pass and the service provided no error details.'
  },
  recipeListReview: {
    moreParameters: '{0} more parameters'
  },
  traceQueryReview: {
    conditionsChanged: 'Filters changed. The results below are from the previous query; run the query to update them.',
    resultContext: 'Showing: SN {0} · Job {1} · Outcome {2} · Time {3} · Page {4} / {5} records · Queried {6}',
    allSn: 'All SNs', allTime: 'Any time',
    notQueried: 'Run the query to load traceability records for the current filters.',
    invalidRange: 'Select a valid time range. The end must not precede the start.',
    failedKeepsPrevious: 'The query failed. The results below are from the last successful query.'
  },
  oeeHierarchy: {
    coreMetrics: 'Core production metrics', supportingMetrics: 'Supporting statistics', target: 'Target {0}',
    onTarget: 'On target', belowTarget: 'Below target', noData: 'No data',
    viewDay: 'View day', backToRange: 'Back to previous range'
  },
  alarmListReview: {
    sort: 'Alarm order', priorityFirst: 'Severity first', newestFirst: 'Newest first',
    source: 'Source', deviceId: 'Device #{0}', tagId: 'Tag #{0}',
    sourceCatalogFailed: 'Device or tag catalog could not be loaded. Some sources are shown by ID.',
  },
  alarmWorkbench: {
    title: 'Alarm workbench', states: 'Live alarm states', filters: 'Alarm filters', actions: 'Alarm actions',
    liveScope: 'Active & pending',
    table: 'Alarm records', scrollHint: 'Focus the table and use the arrow keys to scroll through records',
    silenceHint: 'Silence authorized annunciator panels. This does not acknowledge alarms or change browser alarm audio.',
    rangeChanged: 'The time range has changed. The previous results are still displayed; run the query to update them.',
    pageSize: 'Rows per page', pageSizeOption: '{0} / page',
  },
  common: {
    retry: 'Reload',
    notFound: 'The item does not exist or has been deleted',
    timeRange: 'Time range',
    compareFrom: 'Compare from version',
    compareTo: 'Compare to version',
    screenRun: 'Screen run',
    kiosk: 'Screen display',
  },
  alarmReview: {
    conditionsChanged: 'Filters have changed. The previous analysis is still displayed. Run the query again before exporting.',
    resultContext: 'Result range: {0} to {1}; Area: {2}; Top {3}'
  },
  oeeReview: {
    conditionsChanged: 'Filters have changed. The previous statistics are still displayed. Run the query again before exporting.',
    resultContext: 'Result range: {0} to {1}; Area: {2}; Line: {3}'
  },
  operationsReview: {
    modeUnknown: 'Mode unverified', modesFailed: 'Could not load device control modes. Refresh to retry.',
    requestChanged: 'This request expired or was already processed. Refresh the list to verify.'
  },
  layoutMonitor: {
    notFound: 'The requested layout does not exist or has not been published. Select another layout.', unassigned: 'This workstation has no published layout assigned. Contact an administrator.', stationFailed: 'Could not resolve this workstation',
    targetRegion: 'Navigation target region', summaryFailed: 'Could not load alarm totals', summaryStale: 'Alarm totals unverified; counts unknown', alarmsStale: 'Alarm list unverified',
    screensFailed: 'Could not load screen catalog', versionFailed: 'Could not verify layout version', updateAvailable: 'Layout V{0} is available', applyUpdate: 'Apply new version',
    applyConfirm: 'Applying this version resets region navigation and closes floating panels. Continue?', updatedAt: 'Alarm list updated: {0}; totals updated: {1}',
    stationsFailed: 'Could not load workstations', unsaved: 'Unsaved changes', discardChanges: 'Discard the changes that have not been saved?',
    replacePreset: 'Replace every monitor and region with the preset?', removeMonitor: 'Remove this monitor and all of its regions?',
    closePanel: 'Close floating panel', monitorMapping: 'Monitor mapping', currentWindow: 'Current window', regularWindow: 'Regular new window (not positioned)',
    monitorFallback: 'There are fewer physical screens or positioning permission is unavailable. Check the mapping; unmapped logical screens open regular windows.',
    monitorDuplicates: 'Multiple logical screens target the same physical screen. Their windows may overlap.',
    returnedUnack: 'Returned, awaiting acknowledgement', activeUnack: 'Active, unacknowledged', activeAcked: 'Active, acknowledged', returnedCount: 'Returned, unacknowledged {0}',
    alarmTitle: 'Active and pending alarms', alarmRegion: 'Region: {0}', alarmLimit: 'First {0} records (partial list)', allAlarms: 'View all alarms',
    alarmLevel: 'Severity', alarmState: 'State', alarmStartedAt: 'Started at', alarmMessage: 'Alarm message', alarmAction: 'Action',
    alarmTable: 'Alarm details; use arrow keys to scroll', ackAlarm: 'Acknowledge alarm: {0}', ackUnavailable: 'Unavailable', emptyAlarms: 'No active alarms or alarms awaiting acknowledgement'
  },
  screenMonitor: {
    unavailable: 'Data unavailable', alarmsUnavailable: 'Alarm data unavailable', historyUnavailable: 'History unavailable', oeeUnavailable: 'OEE data unavailable',
    alarmsFailed: 'Could not load alarms', historyFailed: 'Could not load history', oeeFailed: 'Could not load OEE', tagsFailed: 'Could not load the point catalog. Refresh to retry; controls are unavailable.',
    systemAlarms: 'System active alarms', alarmCount: 'System active alarms: {0}', partialAlarmCount: 'System active alarms: at least {0}',
    alarmsLimited: 'The system alarm list reached its query limit. Only part of the list is shown; check the alarms page.',
    publishedVersion: 'Running V{0}', draftVersion: 'Draft V{0}', draftName: 'Draft name', runVersion: 'Run published V{0}', more: 'More actions', moreFor: 'Screen actions: {0}'
  },
  realtimeMonitor: {
    retry: 'Reload', verified: 'Snapshot verified: {0}', quality: 'Data quality', good: 'Good', sampleTime: 'Latest sample',
    stale: 'Unverified', staleCount: '{0} points have stale or unverified samples', loading: 'Loading', noMatch: 'No points match the filters',
    bitOne: 'Set (1)', bitZero: 'Clear (0)', sort: 'Sort', configurationOrder: 'Configuration order', abnormalFirst: 'Attention first', clearFilters: 'Clear filters',
    activeAlarm: 'Linked active alarms', alarmCount: 'Active alarms: {0}', alarmUnavailable: 'Not authorized', alarmUnknown: 'Unverified', noAlarmObserved: 'No active alarms observed', tagFilter: 'Linked point #{0}',
    protectedAlarm: 'Includes suppressed/shelved', alarmsLimited: 'Linked alarm list is truncated. Check the alarms page.', alarmsFailed: 'Could not load linked alarms',
    writeReferenceWarning: 'The reference value has bad quality or is unverified. Check its sample time; server-side checks still apply.',
    invalidWrite: 'Enter a valid value within the configured range. Integer points do not accept decimals.',
    connection: { connected: 'Push connected', connecting: 'Push connecting', reconnecting: 'Push reconnecting', disconnected: 'Push disconnected' }
  },
  overviewStatus: {
    updated: 'Last complete update: {0}', unknown: 'Awaiting data', stale: 'Data is stale', loadedOnly: 'Loaded subset only',
    activeUnack: 'Active, unacknowledged', returned: 'Returned, awaiting acknowledgment', protection: 'Suppressed / Shelved', review: 'Review', flood: 'Alarm flood',
    alarmScope: 'Active unacknowledged excludes returned, shelved and suppressed alarms. Flood detection uses the recent arrival rate, not the current active count.',
    listed: 'Showing {0} / {1}, severity first', triggerValue: 'Trigger value', state: 'State', acked: 'Active, acknowledged',
    locate: 'Locate', trend: 'Trend', abnormalOnly: 'Abnormal only', noDeviceIssues: 'No communication issues detected',
    maintenance: 'Maintenance metrics', systemEvent: 'System event', serviceOk: 'Service available', serviceIssue: 'Service issue', serviceUnknown: 'Service status unknown'
  },
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
