export default {
  flow: {
    fit: 'Fit', autoLayout: 'Auto layout', deleteEdge: 'Delete link', then: 'yes', else: 'no', fail: 'fail',
    hintEdit: 'Drag nodes to move; drag from a dot under a node onto the target to link; select a link to delete it; wheel to zoom, drag empty space to pan. Dashed = falls through to next step.',
    hintRun: 'Live step state: yellow running, green done, red failed, faded skipped.',
    port: { next: 'Next', then: 'If true', else: 'If false', branch: 'Parallel branch', join: 'Join step', target: 'Jump target', fail: 'On failure go to' }
  }
}
