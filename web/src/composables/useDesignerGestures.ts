import { computed, ref, watch, type Ref } from 'vue'
import Moveable from 'moveable'
import Selecto from 'selecto'
import { clampNode, portPoint, translateNodes } from '../screens/model'
import type { ScreenDocument, ScreenNode, ScreenPort } from '../screens/model'

interface GestureOptions {
  document: Ref<ScreenDocument>
  selectedIds: Ref<string[]>
  busy: Ref<boolean>
  zoom: Ref<number>
  snap: Ref<boolean>
  stage: Ref<HTMLDivElement | undefined>
  canvasViewport: { x: number; y: number; width: number; height: number }
  groupMembers: (nodeId: string) => string[]
  select: (nodeId: string, additive?: boolean) => void
  commit: () => void
  isDisposed: () => boolean
}

/** 画布手势：Moveable 拖动/缩放、Selecto 框选、管道锚点拖动与对齐辅助线。 */
export function useDesignerGestures(options: GestureOptions) {
  const { document, selectedIds, busy, zoom, snap, stage, canvasViewport, groupMembers, select, commit } = options
  const dragState = { value: null as { x: number; y: number; nodes: ScreenNode[]; resize: boolean; moved: boolean } | null }
  let moveable: Moveable | null = null, selecto: Selecto | null = null
  let waypointGesture: AbortController | undefined
  const guides = ref<{ vertical: number[]; horizontal: number[] } | null>(null)
  const marquee = ref<{ startX: number; startY: number; x: number; y: number; baseIds: string[]; originalIds: string[] } | null>(null)
  const selectionBox = computed(() => {
    const box = marquee.value
    if (!box || Math.max(Math.abs(box.x - box.startX), Math.abs(box.y - box.startY)) * zoom.value < 3) return null
    return { x: Math.min(box.x, box.startX), y: Math.min(box.y, box.startY), width: Math.abs(box.x - box.startX), height: Math.abs(box.y - box.startY) }
  })
  const pipePorts: ScreenPort['port'][] = ['left', 'right', 'top', 'bottom']

  function canvasPoint(event: { clientX: number; clientY: number }): { x: number; y: number } | null {
    const bounds = stage.value?.querySelector('.screen-canvas')?.getBoundingClientRect()
    if (!bounds) return null
    return { x: Math.max(0, Math.min(document.value.width, (event.clientX - bounds.left) / zoom.value)),
      y: Math.max(0, Math.min(document.value.height, (event.clientY - bounds.top) / zoom.value)) }
  }

  function beginWaypoint(rendered: ScreenNode, index: number, event: PointerEvent): void {
    if (busy.value || event.button !== 0 || rendered.rotation !== 0) return
    const node = document.value.nodes.find(item => item.id === rendered.id); if (!node?.points) return
    commit(); waypointGesture?.abort(); const controller = new AbortController(); waypointGesture = controller
    const move = (pointer: PointerEvent) => {
      const point = canvasPoint(pointer); if (!point || pointer.pointerId !== event.pointerId) return
      node.points![index] = { x: Math.max(0, Math.min(100, (point.x - node.x) / node.width * 100)), y: Math.max(0, Math.min(100, (point.y - node.y) / node.height * 100)) }
    }
    window.addEventListener('pointermove', move, { signal: controller.signal })
    window.addEventListener('pointerup', pointer => {
      if (pointer.pointerId !== event.pointerId) return
      move(pointer)
      if (index === 0 || index === node.points!.length - 1) {
        const point = canvasPoint(pointer)
        let nearest: ScreenPort | null = null, distance = 18 / zoom.value
        if (point) for (const target of document.value.nodes.filter(item => item.id !== node.id && !['polyline', 'template'].includes(item.type))) for (const port of pipePorts) {
          const connection = { nodeId: target.id, port }, anchor = portPoint(connection, document.value.nodes)!
          const next = Math.hypot(anchor.x - point.x, anchor.y - point.y)
          if (next < distance) { distance = next; nearest = connection }
        }
        if (nearest) node[index === 0 ? 'source' : 'target'] = nearest
      }
      controller.abort(); commit()
    }, { signal: controller.signal })
    window.addEventListener('pointercancel', () => { controller.abort(); commit() }, { signal: controller.signal, once: true })
  }

  function beginDrag(node: ScreenNode, event: MouseEvent | TouchEvent): void {
    if ('button' in event && event.button !== 0 || busy.value) return
    const target = document.value.nodes.find(item => item.id === node.id)
    if (target?.locked) return
    event.preventDefault()
    stage.value?.focus({ preventScroll: true })
    if (event.shiftKey) { select(node.id, true); return }
    commit(); if (!selectedIds.value.includes(node.id)) selectedIds.value = [node.id]
    syncInteractionTargets(); moveable?.dragStart(event)
  }

  function beginSelection(event: PointerEvent): void {
    if (busy.value || event.button !== 0) return
    stage.value?.focus({ preventScroll: true })
  }

  function startTransform(event: { clientX: number; clientY: number }, resize = false): void {
    if (busy.value) return
    commit()
    dragState.value = { x: event.clientX, y: event.clientY, nodes: JSON.parse(JSON.stringify(document.value.nodes.filter(node => selectedIds.value.includes(node.id)))), resize, moved: false }
  }

  function snapAlignment(originals: ScreenNode[], dx: number, dy: number): { dx: number; dy: number } {
    const threshold = Math.max(2, 6 / zoom.value)
    const selected = new Set(originals.map(node => node.id))
    const others = document.value.nodes.filter(node => !selected.has(node.id) && !node.hidden)
    const xs: number[] = [0, document.value.width / 2, document.value.width]
    const ys: number[] = [0, document.value.height / 2, document.value.height]
    for (const node of others) { xs.push(node.x, node.x + node.width / 2, node.x + node.width); ys.push(node.y, node.y + node.height / 2, node.y + node.height) }
    let snapDx = dx, snapDy = dy, bestX = threshold + 1, bestY = threshold + 1
    for (const node of originals) {
      for (const value of [node.x + dx, node.x + dx + node.width / 2, node.x + dx + node.width]) for (const target of xs) {
        const delta = target - value
        if (Math.abs(delta) <= threshold && Math.abs(delta) < Math.abs(bestX)) { bestX = delta; snapDx = dx + delta }
      }
      for (const value of [node.y + dy, node.y + dy + node.height / 2, node.y + dy + node.height]) for (const target of ys) {
        const delta = target - value
        if (Math.abs(delta) <= threshold && Math.abs(delta) < Math.abs(bestY)) { bestY = delta; snapDy = dy + delta }
      }
    }
    return { dx: snapDx, dy: snapDy }
  }

  function updateGuides(moved: ScreenNode[]): void {
    const threshold = Math.max(1, 2 / zoom.value)
    const selected = new Set(dragState.value?.nodes.map(node => node.id) ?? [])
    const others = document.value.nodes.filter(node => !selected.has(node.id) && !node.hidden)
    const vertical = new Set<number>(), horizontal = new Set<number>()
    for (const node of moved) {
      const xs = [node.x, node.x + node.width / 2, node.x + node.width]
      const ys = [node.y, node.y + node.height / 2, node.y + node.height]
      for (const other of others) {
        for (const x of xs) for (const ox of [other.x, other.x + other.width / 2, other.x + other.width]) if (Math.abs(x - ox) <= threshold) vertical.add(Number(x.toFixed(1)))
        for (const y of ys) for (const oy of [other.y, other.y + other.height / 2, other.y + other.height]) if (Math.abs(y - oy) <= threshold) horizontal.add(Number(y.toFixed(1)))
      }
    }
    guides.value = vertical.size || horizontal.size ? { vertical: [...vertical], horizontal: [...horizontal] } : null
  }

  function transformNodes(event: { clientX: number; clientY: number }): void {
    const drag = dragState.value
    if (!drag || busy.value) return
    const grid = snap.value ? 10 : 1
    let dx = Math.round((event.clientX - drag.x) / zoom.value / grid) * grid, dy = Math.round((event.clientY - drag.y) / zoom.value / grid) * grid
    if (snap.value && !drag.resize) ({ dx, dy } = snapAlignment(drag.nodes, dx, dy))
    const moved = drag.resize
      ? drag.nodes.map(original => clampNode({ ...original, width: original.width + dx, height: original.height + dy }, document.value))
      : translateNodes(drag.nodes, document.value, dx, dy)
    for (const node of moved) {
      const index = document.value.nodes.findIndex(original => original.id === node.id)
      document.value.nodes[index] = node
    }
    if (snap.value && !drag.resize) updateGuides(moved); else guides.value = null
    if (moved.some((node, index) => { const original = drag.nodes[index]; return node.x !== original.x || node.y !== original.y || node.width !== original.width || node.height !== original.height })) drag.moved = true
  }

  function finishTransform(): void { guides.value = null; if (dragState.value?.moved) commit(); dragState.value = null; syncInteractionTargets() }

  // 设计器画布在滚动容器内，只渲染视口附近图元；选中图元始终保留，避免拖拽/缩放目标丢失。
  function updateCanvasViewport(): void {
    const viewport = stage.value
    const canvas = viewport?.querySelector<SVGElement>('.screen-canvas')
    if (!viewport || !canvas) return
    const z = zoom.value || 1
    const view = viewport.getBoundingClientRect(), box = canvas.getBoundingClientRect()
    canvasViewport.x = Math.max(0, (view.left - box.left) / z)
    canvasViewport.y = Math.max(0, (view.top - box.top) / z)
    canvasViewport.width = view.width / z
    canvasViewport.height = view.height / z
  }

  function syncInteractionTargets(): void {
    updateCanvasViewport()
    if (!moveable || !stage.value || options.isDisposed()) return
    if (!dragState.value) {
      const targets = selectedIds.value.flatMap(id => {
        const node = document.value.nodes.find(item => item.id === id)
        if (!node || node.locked || node.hidden) return []
        const element = stage.value!.querySelector<SVGElement>(`.screen-canvas > g[data-node-id="${CSS.escape(id)}"]`)
        return element ? [element] : []
      })
      moveable.setState({ target: busy.value ? [] : targets.length === 1 ? targets[0] : targets, resizable: !busy.value && targets.length === 1 })
    }
    moveable.updateRect()
  }

  function initializeInteractions(): void {
    const viewport = stage.value
    if (!viewport) return
    moveable = new Moveable(viewport, { container: viewport, draggable: true, resizable: true, origin: false,
      renderDirections: ['se'], throttleDrag: 0, throttleResize: 0, preventDefault: true })
    moveable.on('dragStart', event => { if (busy.value) event.stop(); else startTransform(event) })
      .on('drag', transformNodes).on('dragEnd', finishTransform)
      .on('dragGroupStart', event => { if (busy.value) event.stop(); else startTransform(event) })
      .on('dragGroup', transformNodes).on('dragGroupEnd', finishTransform)
      .on('resizeStart', event => { if (busy.value) event.stop(); else startTransform(event, true) })
      .on('resize', transformNodes).on('resizeEnd', finishTransform)
    selecto = new Selecto({ container: viewport, dragContainer: viewport, className: 'designer-selecto',
      selectableTargets: ['.screen-canvas > g[data-node-id]:not(.inner)'], selectByClick: false, selectFromInside: false,
      hitRate: 0, checkInput: true, boundContainer: viewport })
    selecto.on('dragStart', event => {
      const target = event.inputEvent.target as Element | null
      if (busy.value || !target?.closest('.screen-canvas') || target.closest('[data-node-id]')) { event.stop(); return }
      const point = canvasPoint(event.inputEvent); if (!point) { event.stop(); return }
      commit(); dragState.value = null; viewport.focus({ preventScroll: true })
      marquee.value = { startX: point.x, startY: point.y, ...point, baseIds: event.inputEvent.shiftKey ? [...selectedIds.value] : [], originalIds: [...selectedIds.value] }
      selecto?.setSelectedTargets([])
      if (!event.inputEvent.shiftKey) selectedIds.value = []
    }).on('drag', event => {
      const point = canvasPoint(event.inputEvent)
      if (marquee.value && point) { marquee.value.x = point.x; marquee.value.y = point.y }
    }).on('select', event => {
      if (!marquee.value || busy.value) return
      selectedIds.value = [...new Set([...marquee.value.baseIds, ...event.selected.flatMap(element => {
        const id = element.getAttribute('data-node-id')
        const node = id ? document.value.nodes.find(item => item.id === id) : undefined
        return id && node && !node.locked && !node.hidden ? groupMembers(id) : []
      })])]
    }).on('dragEnd', () => { marquee.value = null })
    viewport.addEventListener('scroll', syncInteractionTargets)
    window.addEventListener('resize', syncInteractionTargets)
    syncInteractionTargets()
  }

  watch([selectedIds, zoom, busy], () => syncInteractionTargets(), { flush: 'post' })
  // Moveable 只依赖几何与层级顺序；文字、颜色、测点等属性变化不再触发整份文档深度监听。
  watch(() => document.value.nodes.map(node => `${node.id}:${node.x}:${node.y}:${node.width}:${node.height}:${node.rotation}`), () => syncInteractionTargets(), { flush: 'post' })

  function disposeInteractions(): void {
    waypointGesture?.abort()
    stage.value?.removeEventListener('scroll', syncInteractionTargets)
    window.removeEventListener('resize', syncInteractionTargets)
    moveable?.destroy(); selecto?.destroy()
    moveable = null; selecto = null
  }

  return {
    guides, marquee, selectionBox,
    canvasPoint, beginWaypoint, beginDrag, beginSelection,
    startTransform, snapAlignment, updateGuides, transformNodes, finishTransform,
    syncInteractionTargets, initializeInteractions, disposeInteractions
  }
}
