/**
 * 判断当前是否有弹层打开。
 *
 * Element Plus 会把 `.el-overlay` 常驻在 DOM 里，关闭时只是 `display: none`。
 * 因此不能按「元素存在」判断——那样永远为真，任何依赖它的快捷键都会被自己屏蔽。
 * 这里按「是否真的占据布局」判断：`display:none` 的元素 `getClientRects()` 为空。
 */
export function modalOpen(): boolean {
  for (const overlay of document.querySelectorAll<HTMLElement>('.el-overlay')) {
    if (overlay.getClientRects().length > 0) return true
  }
  return false
}
