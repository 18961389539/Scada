// E2E 每个 worker 是独立进程：按 TEST_PARALLEL_INDEX 分配一段端口，与 fixtures 里
// 每用例独立的临时数据目录一起，保证并行运行时互不踩端口、互不共享库。
const parallelIndex = Number(process.env.TEST_PARALLEL_INDEX ?? 0) || 0

export const SERVER_PORT = Number(process.env.PW_SERVER_PORT ?? 18581 + parallelIndex * 10)
export const OPC_PORT = Number(process.env.PW_OPC_PORT ?? 18583 + parallelIndex * 10)
export const SERVER_URL = `http://127.0.0.1:${SERVER_PORT}`
export const OPC_ENDPOINT = `opc.tcp://localhost:${OPC_PORT}/ScadaSimulator`
