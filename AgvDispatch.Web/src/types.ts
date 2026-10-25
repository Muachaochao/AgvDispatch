export interface Point { x: number; y: number }
export interface MapNode extends Point { id: string; agvSiteId?: string; enabled: boolean; type: string }
export interface MapEdge { id: string; from: string; to: string; enabled: boolean; bidirectional: boolean; kind: string; points: Point[] }
export interface MapResource { id: string; capacity: number; boundary?: Point[] }
export interface TaskRoute {taskId:string;stepId:string;commandId:string;vehicleId:string;mapId:string;mapVersionId:string;mapDefinitionHash:string;nodeIds:string[];edgeIds:string[];vendorSites:string[];action:string}
export interface WebMap { areaAnnotations?:import('./workbench').AreaAnnotation[]; definitionHash?:string; id: string; version: string; name: string; unit: string; coordinateSystem: string; nodes: MapNode[]; edges: MapEdge[]; resources: MapResource[]; warnings: string[] }
export interface Vehicle { vehicleId: string; displayName: string; connection: string; quality: string; batteryPercent: number | null; currentStation: string | null;
  navigationState: string | null; x: number | null; y: number | null; angle: number | null; localizationConfidence: number | null;
  mapPositionState: string; mapPositionMessage: string; lastSuccessfulPollAt: string | null; updatedAt: string; blocked: boolean | null; emergencyStopped: boolean | null; charging?: boolean | null }
export interface TaskRow { taskId: string; stationId: string; vehicleId: string | null; stage: string; priority: number; createdAt: string; updatedAt: string; errorMessage?: string }
export interface DeviceField { name: string; value: string; quality: string; address: string; updatedAt: string }
export interface Device { id: string; name: string; status: string; quality: string; summary: string; fields: DeviceField[] }
export interface Snapshot { receivedAt: string; connected: boolean; source: 'demo' | 'backend'; error: string | null; map: WebMap | null;
  traffic?: TrafficObservation | null;
  alarms?: Alarm[];
  positionedVehicleIds: string[]; status: { service?:{version:string;startedAt:string};execution?:import('./commissioning').BackendExecution; generatedAt: string; agvs: Vehicle[]; safety: { dispatchingEnabled: boolean; plcWritesAllowed: boolean; agvCommandsAllowed: boolean }; plc: { state: string; goodSignals: number; totalSignals: number } } | null;
  tasks: { items: TaskRow[]; totalCount: number } | null; devices: { equipment: Device[] } | null;
  resources: { resourceId: string; capacity: number; occupiedCount: number; leases: { vehicleId: string; taskId: string }[] }[] | null }
export interface TaskDetail { cancellation?:{requestId:string;reason:string;requestedAt?:string;sendAttempts:number;sentAt?:string;confirmedAt?:string}; handshakeMode?:string; handshakeContractVersion?:string; handshakes?:HandshakeCheckpoint[]; handover?:HandoverDiagnosis; task: TaskRow; waitingExplanation: string; steps: { stepId?:string; observedPhase?:string; completionRequirement?:string; evidenceBoundary?:string; commandStateExplanation?:string; progressConditions?:ProgressCondition[]; sequence: number; stepType: string; status: string; isCurrent: boolean; waitingExplanation: string; vendorTaskId?: string; sourceSite?:string; destinationSite?:string; elapsedText?:string; timeoutText?:string; errorMessage?:string }[];
  diagnosticsObservedAt?:string; executionTimelineTotal?:number; executionTimeline?:ExecutionEvent[];
  commands?: {route?:TaskRoute|null;commandId:string;stepId:string;status:string;attemptCount:number;lastError?:string;vendorCommandId?:string}[];
  resources?: {resourceId:string;vehicleId:string;expiresAt:string;heartbeatExpired:boolean}[];
  plcConditions: { stationId: string; variableName: string; currentValue: boolean | null; expectedValue: boolean; quality: string; satisfied: boolean }[] }
export interface CreationOptions { available: boolean; explanation: string; mapId: string; mapVersionId: string; parameterRevision: string;
  waitingSite?: string; dumpingSite?: string;
  stations: { stationId: string; pickupSite: string; available: boolean; explanation: string; pickupReachableVehicleIds?: string[] }[]; vehicles: { vehicleId: string; label: string; available: boolean; explanation: string }[] }
export interface RoutePreview { verified: boolean; message: string; mapIssue: string; legs: { sequence: number; name: string; logicalPath: string; vendorPath: string; message: string }[];
  segments: { edgeId: string; status: string; message: string }[] }
export interface Alarm { alarmId: string; sourceType: string; sourceId: string; code: string; title: string; message: string; severity: string; status: string; firstOccurredAt: string; lastObservedAt: string; acknowledgedAt: string | null; recoveredAt: string | null }
export interface MapSummary { mapId: string; name: string; description: string; coordinateSystem: string; unit: string; isActive: boolean; activeVersionId: string | null; publishedVersionId: string | null; updatedAt: string }

export interface ProgressCondition { ruleId:string; name:string; stationId:string; variableName:string; address:string; expectedValue:boolean; currentValue:boolean|null; quality:string; satisfied:boolean; result:string; reason:string; observedAt:string|null; signalVersion:number|null; role:string }
export interface ExecutionEvent { id:string; occurredAt:string; stepId:string; kind:string; title:string; summary:string; source:string }

export interface HandshakeCheckpoint {taskId:string;stepId:string;cycleId:string;contractVersion:string;phase:string;revision:number;reason:string;pendingSignal:string|null;pendingValue:boolean|null;nextPhase:string|null;startedAt:string;updatedAt:string;baseline:{stationId:string;variableName:string;value:boolean|null;quality:string;observedAt:string|null;version:number|null;address:string}[]}
export interface HandoverDiagnosis {state:string;reason:string;returnCommandCompleted:boolean;exitPhysicallyVerified:boolean;heldResources:string[];recordedReleaseReasons:string[]}
export interface RuntimeEvent {evidence?:string;sequence:number;taskId:string;stepId:string;kind:string;phase:string;stationId:string;signal:string;previousValue:boolean|null;value:boolean|null;quality:string;observedAt:string|null;recordedAt:string;reason:string}
export interface RuntimePage {items:RuntimeEvent[];totalCount:number;page:number;pageSize:number;throughSequence:number}

export interface TrafficOwner {resourceId:string;mapId?:string|null;mapVersionId?:string|null}
export interface TrafficBlocker extends TrafficOwner {taskId:string;vehicleId:string;stepId:string;heartbeatExpired:boolean}
export interface TrafficRequest {taskId:string;stepId:string;vehicleId:string;mapId?:string|null;mapVersionId?:string|null;state:string;reason:string;resourceIds:string[];heldResources:TrafficOwner[];blockers:TrafficBlocker[];blockingObservationCurrent:boolean;waitingSeconds?:number|null;waitingStartKnown?:boolean;waitingClockValid?:boolean;priority?:number|null;waitingCategory?:'Route'|'Equipment';waitingSince?:string|null;waitingAlertLevel?:string}
export interface TrafficObservation {capturedAt:string;requests:TrafficRequest[];requestsTruncated:boolean}

export interface WaitingQuery {blockingResourceId:string|null;minimumPriority:number|null;sort:'LongestWait'|'PriorityThenWait';pageNumber:number;pageSize:number}
export interface WaitingPage {page:{capturedAt:string;totalWaiting:number;totalMatched:number;pageNumber:number;pageSize:number;items:TrafficRequest[];blockingResources:string[]};mapId:string|null;mapVersion:string|null;mapDefinitionHash:string|null}

export interface ReleaseRequirement {id:string;state:string;text:string;evidence:string}
export interface BlockingNode {taskId:string;stepId:string;vehicleId:string;mapId:string|null;mapVersionId:string|null;state:string;reason:string;taskStage:string|null;stepStatus:string|null;commandStatus:string|null;missingEvidence:boolean;releaseRequirements:ReleaseRequirement[]}
export interface BlockingLink {fromTaskId:string;fromStepId:string;toTaskId:string;toStepId:string;vehicleId:string;kind:string;resourceId:string|null;heartbeatExpired:boolean;mapId:string|null;mapVersionId:string|null}
export interface TrafficEvent {eventId:string;taskId:string;stepId:string;vehicleId:string;kind:string;reason:string;resourceIds:string[];occurredAt:string}
export interface BlockingDetail {capturedAt:string;taskId:string;stepId:string;available:boolean;message:string;nodes:BlockingNode[];links:BlockingLink[];events:TrafficEvent[];truncated:boolean;eventsTruncated:boolean;cycleObserved:boolean}
export interface BlockingPage {detail:BlockingDetail;mapId:string|null;mapVersion:string|null;mapDefinitionHash:string|null}
