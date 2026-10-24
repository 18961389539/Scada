export type CommandOutcome = 'Confirmed' | 'Failed' | 'NotSent' | 'Unknown'
export function createCommandId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  const bytes = crypto.getRandomValues(new Uint8Array(16)); bytes[6] = (bytes[6]! & 15) | 64; bytes[8] = (bytes[8]! & 63) | 128
  const hex = [...bytes].map(x => x.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
export interface CommandSubmissionResult { success: boolean; message: string; readBack: number | null; commandId?: string | null; outcome?: CommandOutcome | null }
export interface GatewayCommandStatus {
  id: string; gatewayId: number; deviceId: number; tagId: number; value: number; state: string; createdTs: number; expiresTs: number; completedTs: number | null
  result: { outcome: CommandOutcome; message: string; readBack: number | null } | null
}
