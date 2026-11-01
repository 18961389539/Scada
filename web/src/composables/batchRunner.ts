import { tr } from '../i18n'
import { errorMessage } from '../api/http'
/** 顺序执行批量操作：带进度回调，收集部分失败，不静默吞错。 */
export type BatchProgress = { done: number; total: number; current: string }

export type BatchFailure = { label: string; error: string }

export type BatchReport = {
  ok: number
  failed: BatchFailure[]
  total: number
  /** 被调用方主动中止，剩余条目没有执行 */
  canceled: boolean
}

export async function runBatch<T>(
  items: T[],
  labelOf: (item: T) => string,
  action: (item: T) => Promise<void>,
  onProgress?: (p: BatchProgress) => void,
  /** 每项开始前询问一次；返回 true 则停下，已处理的结果照常返回 */
  shouldStop?: () => boolean
): Promise<BatchReport> {
  const failed: BatchFailure[] = []
  let ok = 0
  let canceled = false
  const total = items.length
  for (let i = 0; i < items.length; i++) {
    if (shouldStop?.()) { canceled = true; break }
    const item = items[i]
    const label = labelOf(item)
    onProgress?.({ done: i, total, current: label })
    try {
      await action(item)
      ok++
    } catch (error) {
      failed.push({ label, error: errorMessage(error) })
    }
  }
  onProgress?.({ done: total, total, current: '' })
  return { ok, failed, total, canceled }
}

/** 把批量结果整理成可读摘要（成功数 + 失败明细）。 */
export function summarizeBatch(report: BatchReport, unitLabel: string): string {
  if (report.failed.length === 0) return `${report.ok}/${report.total} ${unitLabel}`
  const lines = report.failed.slice(0, 8).map((f) => `${f.label}：${f.error}`)
  const more = report.failed.length > 8 ? tr('batchRunner.k26edde', [report.failed.length - 8]) : ''
  return tr('batchRunner.kabac79', [report.ok, report.total, report.failed.length, lines.join('\n'), more])
}
