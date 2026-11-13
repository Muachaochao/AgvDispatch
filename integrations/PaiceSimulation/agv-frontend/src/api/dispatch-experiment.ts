import http from './http'

export interface MainDispatchExperiment {
  id: number
  name: string
  schemeId: string
  roadnetId: number
  taskSetId: number
  taskCount: number
  scenarioName: string
  status: 'PREPARING' | 'SUBMITTED' | 'RUNNING' | 'FINISHED' | 'FAILED' | 'ABORTED'
  runtimeInstance: string | null
  scenarioId: string | null
  mapDefinitionHash: string | null
  reportId: string | null
  message: string | null
  createUser: number
  createTime: string
  updateTime: string
}

export interface CreateMainDispatchExperiment {
  name: string
  schemeId: string
  roadnetId: number
  taskSetId: number
  scenarioName?: string
  timeoutSeconds?: number
}

export function createMainDispatchExperiment(payload: CreateMainDispatchExperiment) {
  return http.post<any, MainDispatchExperiment>('/dispatch/experiments', payload)
}

export function listMainDispatchExperiments() {
  return http.get<any, MainDispatchExperiment[]>('/dispatch/experiments')
}

export function getMainDispatchExperiment(id: number) {
  return http.get<any, MainDispatchExperiment>(`/dispatch/experiments/${id}`)
}

export function stopMainDispatchExperiment(id: number) {
  return http.post<any, MainDispatchExperiment>(`/dispatch/experiments/${id}/stop`)
}
