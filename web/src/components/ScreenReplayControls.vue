<template>
  <el-button v-if="replayAt == null" :size="size" data-test="screen-replay-open" @click="openReplay">{{ $t('screenReplay.open') }}</el-button>
  <template v-else>
    <el-tag type="warning" size="small" data-test="screen-replay-active">{{ $t('screenReplay.active', [formatTime(replayAt ?? 0)]) }}</el-tag>
    <el-button size="small" data-test="screen-replay-play" @click="toggleReplayPlay">{{ replayPlaying ? $t('screenReplay.pause') : $t('screenReplay.play') }}</el-button>
    <el-button size="small" @click="stepReplay(-60)">-1m</el-button>
    <el-button size="small" @click="stepReplay(60)">+1m</el-button>
    <el-select v-model="replaySpeed" size="small" style="width: 90px" :aria-label="$t('screenReplay.speed')">
      <el-option v-for="speed in [0.5, 1, 2, 5]" :key="speed" :label="`${speed}x`" :value="speed" />
    </el-select>
    <el-button size="small" type="danger" plain data-test="screen-replay-exit" @click="exitReplay">{{ $t('screenReplay.exit') }}</el-button>
  </template>
  <el-dialog v-model="replayDialog" :title="$t('screenReplay.open')" width="460px" append-to-body data-test="screen-replay-dialog">
    <el-alert :title="$t('screenReplay.hint')" type="info" :closable="false" show-icon style="margin-bottom: 12px" />
    <el-form label-width="96px">
      <el-form-item :label="$t('screenReplay.time')">
        <el-date-picker v-model="replayDate" type="datetime" :disabled-date="(date: Date) => date.getTime() > Date.now()" style="width: 100%" data-test="screen-replay-time" />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="replayDialog = false">{{ $t('screenReplay.cancel') }}</el-button>
      <el-button type="primary" data-test="screen-replay-start" @click="startReplay">{{ $t('screenReplay.start') }}</el-button>
    </template>
  </el-dialog>
</template>
<script setup lang="ts">
import { watch } from 'vue'
import { useScreenReplay, readReplayAt } from '../screens/useScreenReplay'
import { formatTime } from '../utils'

const props = withDefaults(defineProps<{ modelValue?: number | null; size?: 'small' | 'default' }>(), { modelValue: null, size: 'default' })
const emit = defineEmits<{ 'update:modelValue': [value: number | null]; commit: [value: number | null] }>()
const {
  replayAt, replayDialog, replayDate, replayPlaying, replaySpeed,
  openReplay, startReplay, toggleReplayPlay, stepReplay, exitReplay, setReplayAt
} = useScreenReplay({
  initialAt: props.modelValue,
  onChange: (at, commit) => {
    emit('update:modelValue', at)
    if (commit) emit('commit', at)
  }
})
watch(() => props.modelValue, value => {
  const next = value == null ? null : readReplayAt(value)
  if (next !== replayAt.value) setReplayAt(next, true)
})
</script>
