import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  EditHistory, arrangeNodes, clampNode, cleanNode, createNode, cullNodes, emptyDocument, readableDocument, translateNodes,
  type ScreenDocument, type ScreenNode
} from './model'

const makeNode = (overrides: Partial<ScreenNode> = {}): ScreenNode => ({ ...createNode('rectangle'), ...overrides })
const makeDocument = (nodes: ScreenNode[]): ScreenDocument => ({ ...emptyDocument(), width: 1000, height: 500, nodes })

describe('cullNodes', () => {
  const near = makeNode({ id: 'near', x: 10, y: 10, width: 100, height: 50 })
  const far = makeNode({ id: 'far', x: 5000, y: 10, width: 100, height: 50 })

  it('keeps nodes inside the viewport and its margin', () => {
    expect(cullNodes([near, far], { x: 0, y: 0, width: 1000, height: 600 }, 0).map(node => node.id)).toEqual(['near'])
    expect(cullNodes([near, far], { x: 0, y: 0, width: 1000, height: 600 }, 5000).map(node => node.id)).toEqual(['near', 'far'])
  })

  it('always keeps selected nodes and returns all nodes without a usable viewport', () => {
    expect(cullNodes([near, far], { x: 0, y: 0, width: 1000, height: 600 }, 0, ['far']).map(node => node.id)).toEqual(['near', 'far'])
    expect(cullNodes([near, far], null, 0)).toHaveLength(2)
    expect(cullNodes([near], { x: 0, y: 0, width: 0, height: 0 }, 0)).toHaveLength(1)
  })
})

describe('readableDocument', () => {
  it('rejects values without a node array', () => {
    expect(readableDocument(null)).toBeNull()
    expect(readableDocument('document')).toBeNull()
    expect(readableDocument({ width: 800 })).toBeNull()
    expect(readableDocument({ nodes: 'nope' })).toBeNull()
  })

  it('keeps stored fields and fills missing defaults', () => {
    const document = readableDocument({ nodes: [], width: 800, background: '#000000' })

    expect(document).toMatchObject({ schemaVersion: 1, width: 800, height: 720, background: '#000000', nodes: [] })
  })
})

describe('cleanNode', () => {
  it('drops empty extensions and false flags without touching the source node', () => {
    const node = makeNode({
      templateKey: null, bindings: [], tags: [], texts: {}, locked: false, opacity: 0,
      action: { type: 'none', screenId: null, tagId: null, value: 1, bind: null, sequenceId: null }
    })

    const cleaned = cleanNode(node)

    expect(cleaned).not.toHaveProperty('templateKey')
    expect(cleaned).not.toHaveProperty('bindings')
    expect(cleaned).not.toHaveProperty('tags')
    expect(cleaned).not.toHaveProperty('texts')
    expect(cleaned).not.toHaveProperty('locked')
    expect(cleaned.opacity).toBe(0)
    expect(cleaned.action).not.toHaveProperty('bind')
    expect(cleaned.action).not.toHaveProperty('sequenceId')
    expect(node.action.bind).toBeNull()
  })

  it('keeps non-empty extensions and true flags', () => {
    const node = makeNode({
      templateKey: 'tpl-1', bindings: [{ key: 'level', tagId: 3 }], tags: [1], locked: true, wrap: true,
      action: { type: 'navigate', screenId: 2, tagId: null, value: 1, sequenceId: 4 }
    })

    const cleaned = cleanNode(node)

    expect(cleaned.templateKey).toBe('tpl-1')
    expect(cleaned.bindings).toEqual([{ key: 'level', tagId: 3 }])
    expect(cleaned.tags).toEqual([1])
    expect(cleaned.locked).toBe(true)
    expect(cleaned.wrap).toBe(true)
    expect(cleaned.action).toMatchObject({ type: 'navigate', screenId: 2, sequenceId: 4 })
  })
})

describe('node geometry', () => {
  it('clamps size and position into the document', () => {
    const document = makeDocument([])

    expect(clampNode(makeNode({ x: -10, y: -20, width: 2000, height: 10 }), document))
      .toMatchObject({ x: 0, y: 0, width: 1000, height: 16 })
    expect(clampNode(makeNode({ x: 900, y: 490, width: 200, height: 100 }), document))
      .toMatchObject({ x: 800, y: 400 })
  })

  it('moves a group until its outer edge reaches the document boundary', () => {
    const document = makeDocument([])
    const nodes = [makeNode({ id: 'a', x: 100, width: 100 }), makeNode({ id: 'b', x: 200, width: 100 })]

    expect(translateNodes(nodes, document, -500, 0).map(node => node.x)).toEqual([0, 100])
    expect(translateNodes(nodes, document, 5000, 0).map(node => node.x)).toEqual([800, 900])
    expect(translateNodes([], document, 10, 10)).toEqual([])
  })

  it('aligns nodes to the anchor and distributes spacing', () => {
    const document = makeDocument([])
    const nodes = [makeNode({ id: 'a', x: 100, y: 100, width: 100, height: 50 }), makeNode({ id: 'b', x: 300, y: 200, width: 200, height: 80 })]

    expect(arrangeNodes(nodes, document, 'left')!.map(node => node.x)).toEqual([100, 100])
    expect(arrangeNodes(nodes, document, 'right')!.map(node => node.x)).toEqual([100, 0])
    expect(arrangeNodes(nodes, document, 'center-x')!.map(node => node.x)).toEqual([100, 50])
    expect(arrangeNodes(nodes, document, 'width')!.map(node => node.width)).toEqual([100, 100])

    const spaced = [makeNode({ id: 'a', x: 0, width: 100 }), makeNode({ id: 'b', x: 100, width: 50 }), makeNode({ id: 'c', x: 400, width: 100 })]
    expect(arrangeNodes(spaced, document, 'space-x')!.map(node => node.x)).toEqual([0, 225, 400])
    const crowded = [makeNode({ id: 'a', x: 0, width: 400 }), makeNode({ id: 'b', x: 0, width: 400 }), makeNode({ id: 'c', x: 0, width: 400 })]
    expect(arrangeNodes(crowded, document, 'space-x')).toBeNull()
  })
})

describe('EditHistory', () => {
  afterEach(() => vi.useRealTimers())

  it('undoes and redoes snapshots without duplicating an unchanged snapshot', () => {
    const history = new EditHistory()
    history.push('one')
    history.push('one')
    expect(history.undo('one')).toBe('one')
    expect(history.canUndo).toBe(false)

    const steps = new EditHistory()
    steps.push('one')
    steps.push('two')
    expect(steps.undo('two')).toBe('two')
    expect(steps.undo('two')).toBe('one')
    expect(steps.undo('one')).toBeUndefined()
    expect(steps.redo('one')).toBe('two')
    expect(steps.canRedo).toBe(true)
  })

  it('coalesces rapid edits with the same key but keeps later edits separate', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
    const history = new EditHistory()
    history.push('one', 'color')
    history.push('two', 'color')

    expect(history.undo('two')).toBe('one')

    const slower = new EditHistory()
    slower.push('one', 'color')
    vi.advanceTimersByTime(600)
    slower.push('two', 'color')

    expect(slower.undo('after-two')).toBe('two')
    expect(slower.undo('after-one')).toBe('one')
  })

  it('drops the oldest snapshots when the byte budget is exceeded', () => {
    // 40 字节预算、每份快照 20 字节：最多保留两份，避免大画面历史无上限增长。
    const history = new EditHistory(100, 40)
    history.push('aaaaaaaaaa')
    history.push('bbbbbbbbbb')
    history.push('cccccccccc')

    expect(history.undo('cccccccccc')).toBe('cccccccccc')
    expect(history.undo('cccccccccc')).toBe('bbbbbbbbbb')
    expect(history.canUndo).toBe(false)
  })
})
