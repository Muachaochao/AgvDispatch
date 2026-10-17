import type { Snapshot, TaskDetail } from './types';

export const stepNames = ['GoToPickup', 'PickupFullBox', 'DeliverFullBoxToDumper', 'WaitForDumperReady',
  'EnterDumpingPosition', 'DumpFullBox', 'ReturnEmptyBox', 'PlaceEmptyBox', 'CompletePlcHandshake'];
const positions = [[4, 4], [4, 4], [14, 4], [14, 4], [20, 9], [20, 9], [4, 4], [4, 4], [4, 4]];
export function demoSnapshot(step = 0): Snapshot {
  const now = new Date().toISOString();
  const nodes = [ ['HOME1', 1, 4, '停车位 01'], ['PICK1', 4, 4, '取料工位 A'], ['CROSS1', 10, 4, '共享通道'],
    ['WAIT', 14, 4, '倒料等待位'], ['DUMP', 20, 9, '倒料设备'], ['CROSS2', 10, 12, '汇合点'],
    ['PICK2', 4, 12, '取料工位 B'], ['HOME2', 1, 12, '停车位 02'] ] as const;
  const mapNodes = nodes.map(([id, x, y, name]) => ({ id, x, y, agvSiteId: `LM-${id}`, enabled: true, type: name }));
  const pairs = [['HOME1', 'PICK1'], ['PICK1', 'CROSS1'], ['CROSS1', 'WAIT'], ['WAIT', 'DUMP'], ['DUMP', 'CROSS2'],
    ['CROSS2', 'PICK2'], ['PICK2', 'HOME2'], ['PICK2', 'CROSS1'], ['CROSS2', 'CROSS1']];
  const [x, y] = positions[Math.min(step, 8)];
  const task = { taskId: 'DEMO-TASK-001', stationId: '取料工位 A', vehicleId: 'AGV-01', stage: step >= 9 ? 'Completed' : 'Executing', priority: 80, createdAt: now, updatedAt: now };
  return {
    receivedAt: now, connected: true, source: 'demo', error: null,
    map: { id: 'DEMO-MAP', version: 'demo-v1', name: '取满箱 · 倒料 · 返空箱 / 合成示例', unit: 'm', coordinateSystem: 'DemoCartesian',
      nodes: mapNodes, edges: pairs.map(([from, to], i) => ({ id: `E${String(i + 1).padStart(2, '0')}`, from, to, enabled: true, bidirectional: true, kind: 'Line',
        points: [mapNodes.find(n => n.id === from)!, mapNodes.find(n => n.id === to)!].map(n => ({ x: n.x, y: n.y })) })),
      resources: [{ id: 'SHARED-01', capacity: 1, boundary: [{ x: 8, y: 2 }, { x: 12, y: 2 }, { x: 12, y: 14 }, { x: 8, y: 14 }] },
        { id: 'DUMPER-01', capacity: 1, boundary: [{ x: 17, y: 7 }, { x: 23, y: 7 }, { x: 23, y: 11 }, { x: 17, y: 11 }] }], warnings: [] },
    alarms: [{ alarmId: 'DEMO-ALARM-001', sourceType: 'Task', sourceId: 'DEMO-TASK-002', code: 'DEMO_RESOURCE_WAIT', title: '等待共享通道',
      message: '合成示例：AGV-02 等待 SHARED-01，资源当前由 AGV-01 占用。这里只演示提示展示，不代表现场报警规则。', severity: 'Info', status: 'Open', firstOccurredAt: now, lastObservedAt: now, acknowledgedAt: null, recoveredAt: null }],
    positionedVehicleIds: ['AGV-01', 'AGV-02'],
    status: { generatedAt: now, agvs: [
      { vehicleId: 'AGV-01', displayName: '搬运车 01', connection: 'Online', quality: 'Good', batteryPercent: 83, currentStation: step === 4 || step === 5 ? 'LM-DUMP' : 'LM-PICK1',
        navigationState: step >= 9 ? '空闲' : '执行任务', x, y, angle: 0, localizationConfidence: 0.98, mapPositionState: 'Normal', mapPositionMessage: '合成坐标', lastSuccessfulPollAt: now, updatedAt: now, blocked: false, emergencyStopped: false },
      { vehicleId: 'AGV-02', displayName: '搬运车 02', connection: 'Online', quality: 'Good', batteryPercent: 68, currentStation: 'LM-PICK2',
        navigationState: '等待共享资源', x: 4, y: 12, angle: 0, localizationConfidence: 0.97, mapPositionState: 'Normal', mapPositionMessage: '合成坐标', lastSuccessfulPollAt: now, updatedAt: now, blocked: false, emergencyStopped: false }
    ], safety: { dispatchingEnabled: false, plcWritesAllowed: false, agvCommandsAllowed: false }, plc: { state: 'Online', goodSignals: 12, totalSignals: 12 } },
    tasks: { items: [task, { ...task, taskId: 'DEMO-TASK-002', stationId: '取料工位 B', vehicleId: 'AGV-02', stage: 'WaitingForVehicle', priority: 20 }], totalCount: 2 },
    devices: { equipment: [{ id: 'PLC-DEMO', name: '倒料设备 PLC / 示例', status: step === 3 ? '等待设备就绪' : '模拟条件', quality: 'Good', summary: '所有数值均为浏览器内合成示例', fields: [
      { name: 'DumperReady', value: String(step !== 3), quality: 'Good', address: '示例，无现场地址', updatedAt: now },
      { name: 'DumpingCompletedMayLeave', value: String(step > 5), quality: 'Good', address: '示例，无现场地址', updatedAt: now },
      { name: 'BoxPresent', value: String(step > 6), quality: 'Good', address: '示例，无现场地址', updatedAt: now }
    ] }] }, resources: [{ resourceId: 'SHARED-01', capacity: 1, occupiedCount: step >= 9 ? 0 : 1, leases: step >= 9 ? [] : [{ vehicleId: 'AGV-01', taskId: task.taskId }] },
      { resourceId: 'DUMPER-01', capacity: 1, occupiedCount: step === 4 || step === 5 ? 1 : 0, leases: [] }]
  };
}
export function demoDetail(step: number): TaskDetail {
  return { task: demoSnapshot(step).tasks!.items[0], waitingExplanation: step === 3 ? '模拟：等待 DumperReady=true' : step === 5 ? '模拟：等待倒料完成反馈' : '合成示例；不代表现场执行',
    steps: stepNames.map((stepType, i) => ({ sequence: i + 1, stepType, status: i < step ? 'Completed' : i === step ? 'Running' : 'Pending', isCurrent: i === step,
      waitingExplanation: '', vendorTaskId: [0, 2, 4, 6].includes(i) ? `DEMO-STEP-${i + 1}` : undefined })), plcConditions: [] };
}
