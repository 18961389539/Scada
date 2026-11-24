import type { Tag, TagInput } from '../../types'

export function emptyTagInput(): TagInput {
  return {
    deviceId: 0,
    name: '',
    description: '',
    area: 'HoldingRegister',
    address: 0,
    dbNumber: 1, bitOffset: 0, nodeId: '',
    dataType: 'Float',
    textLength: 0,
    scale: 1,
    offset: 0,
    unit: '',
    writable: false,
    archiveMode: 'OnChange',
    deadband: 0,
    archivePeriodSec: 600,
    scanRateMs: null,
    minValue: null,
    maxValue: null
  }
}

export function tagToInput(tag: Tag, patch: Partial<TagInput> = {}): TagInput {
  return {
    revision: tag.calculationRevision ?? tag.revision,
    deviceId: tag.deviceId!,
    name: tag.name,
    description: tag.description ?? '',
    area: tag.area,
    address: tag.address,
    dbNumber: tag.dbNumber,
    bitOffset: tag.bitOffset,
    textLength: tag.textLength ?? 0,
    nodeId: tag.nodeId,
    dataType: tag.dataType,
    scale: tag.scale,
    offset: tag.offset,
    unit: tag.unit ?? '',
    writable: tag.writable,
    archiveMode: tag.archiveMode,
    deadband: tag.deadband,
    archivePeriodSec: tag.archivePeriodSec,
    scanRateMs: tag.scanRateMs ?? null,
    minValue: tag.minValue ?? null,
    maxValue: tag.maxValue ?? null,
    ...patch
  }
}
