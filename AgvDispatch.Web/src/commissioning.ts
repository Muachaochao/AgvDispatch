import type { CreationOptions, Snapshot } from './types';
import { isFresh } from './state.ts';
export interface BackendExecution {buildId:string;informationalVersion:string;mode:string;handshakeEnabled:boolean;configuredContract:string;supportedContract:string;taskCreationEnabled:boolean;stepExecutionEnabled:boolean;outboxEnabled:boolean}
export interface CommissioningCheck {id:string;group:string;status:'Pass'|'Blocked'|'Unknown'|'Notice';title:string;reason:string;target:string;action:string;observedAt:string|null}
export interface CommissioningReport {generatedAt:string;runtime:BackendExecution;serviceVersion:string;serviceStartedAt:string;mapId:string|null;mapVersion:string|null;parameterRevision:string;stationId:string;vehicleId:string;options:CreationOptions;checks:CommissioningCheck[];signals:{stationId:string;variableName:string;name:string;address:string;writer:string;value:boolean|null;quality:string;observedAt:string|null;status:string;reason:string}[]}
export function commissioningCurrent(report:CommissioningReport|null,snapshot:Snapshot|null,now:number):boolean {
  if(!report||!snapshot||!isFresh(snapshot,now))return false;
  const age=now-Date.parse(report.generatedAt);
  const service=snapshot.status?.service,execution=snapshot.status?.execution;
  return Number.isFinite(age)&&age>=-1000&&age<=15000&&!!service&&!!execution&&
    report.runtime.buildId===execution.buildId&&report.serviceStartedAt===service.startedAt&&
    report.mapId===(snapshot.map?.id??null)&&report.mapVersion===(snapshot.map?.version??null)&&
    !report.checks.some(c=>c.id==='map-changed');
}
export const checkLabels:Record<string,string>={Pass:'观察通过',Blocked:'阻塞',Unknown:'待核对',Notice:'说明'};
