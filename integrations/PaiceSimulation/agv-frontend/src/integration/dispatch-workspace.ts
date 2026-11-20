export const dispatchSections = {
  overview: "运行总览",
  engineering: "工程与地图",
  plan: "场景与呼叫",
  monitor: "任务与车队",
  diagnosis: "调度诊断",
  faults: "故障演练",
  recovery: "人工处置",
  report: "报告与回放",
  environment: "环境工具",
  scenarios: "全部场景",
  "task-evidence": "任务证据",
  "report-evidence": "历史证据",
} as const;

export type DispatchSection = keyof typeof dispatchSections;

export function dispatchSection(value: unknown): DispatchSection {
  return typeof value === "string" && Object.hasOwn(dispatchSections, value)
    ? value as DispatchSection
    : "overview";
}

export function dispatchLocation(section: DispatchSection, taskId?: string) {
  return { path: "/simulation", query: { mode: "main", section, ...(taskId ? { taskId } : {}) } };
}
