import { ElMessageBox } from 'element-plus'
import { tr } from '../i18n'
import type { ExcelImportPreview } from '../types'

export function importPreviewSummary(preview: ExcelImportPreview): string {
  return tr('importPreview.summary', [preview.created, preview.updated, preview.skipped, preview.conflicts])
}

export async function confirmExcelImportPreview(preview: ExcelImportPreview, title: string): Promise<boolean> {
  const errors = preview.errors.length
    ? `\n${tr('importPreview.errors', [preview.errors.length])}\n${preview.errors.join('\n')}`
    : ''
  const warnings = preview.warnings.length ? `\n${preview.warnings.join('\n')}` : ''
  const body = `${importPreviewSummary(preview)}\n${tr('importPreview.impact')}${warnings}${errors}`
  if (!preview.canApply) {
    await ElMessageBox.alert(body || tr('importPreview.noChanges'), title, {
      type: 'warning',
      confirmButtonText: tr('importPreview.cancel')
    })
    return false
  }
  try {
    await ElMessageBox.confirm(body, title, {
      type: 'warning',
      confirmButtonText: tr('importPreview.confirm'),
      cancelButtonText: tr('importPreview.cancel')
    })
    return true
  } catch {
    return false
  }
}
