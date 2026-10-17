using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly ISqlSugarClient _database;
    private readonly TrafficResourceCatalog _trafficResources;

    public DatabaseInitializer(ISqlSugarClient database)
        : this(database, new TrafficResourceCatalog([]))
    {
    }

    public DatabaseInitializer(
        ISqlSugarClient database,
        TrafficResourceCatalog trafficResources)
    {
        _database = database;
        _trafficResources = trafficResources;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database.DbMaintenance.CreateDatabase();
        _database.CodeFirst.InitTables<
            DispatchTaskEntity,
            DispatchTaskStepEntity,
            AgvCommandOutboxEntity,
            TrafficResourceLeaseEntity,
            SystemSettingEntity>();
        _database.CodeFirst.InitTables<
            TrafficResourceReleaseAuditEntity,
            TrafficResourceDefinitionEntity,
            ManualTrafficReleaseRequestEntity,
            AlarmRecordEntity>();
        _database.CodeFirst.InitTables<MapProjectEntity, MapVersionEntity, MapOperationAuditEntity, AgvTrackPointEntity>();
        _database.CodeFirst.InitTables<MapSwitchEntity>();
        _database.CodeFirst.InitTables<MapTransferEntity,MapTransferEventEntity>();
        _database.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_map_transfer_request ON map_transfer_operation(RequestId)");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_map_transfer_identity BEFORE UPDATE ON map_transfer_operation WHEN NEW.OperationId IS NOT OLD.OperationId OR NEW.RequestId IS NOT OLD.RequestId OR NEW.IntentHash IS NOT OLD.IntentHash OR NEW.MapId IS NOT OLD.MapId OR NEW.VersionId IS NOT OLD.VersionId OR NEW.VehicleId IS NOT OLD.VehicleId OR NEW.TargetMapName IS NOT OLD.TargetMapName OR NEW.SourcePath IS NOT OLD.SourcePath OR NEW.SourceSha256 IS NOT OLD.SourceSha256 OR NEW.DefinitionHash IS NOT OLD.DefinitionHash OR NEW.ExpectedDeviceMd5 IS NOT OLD.ExpectedDeviceMd5 OR NEW.Firmware IS NOT OLD.Firmware OR NEW.Load IS NOT OLD.Load OR NEW.OwnerSession IS NOT OLD.OwnerSession OR NEW.Operator IS NOT OLD.Operator OR NEW.ConfirmationNote IS NOT OLD.ConfirmationNote OR NEW.AttemptCount<OLD.AttemptCount BEGIN SELECT RAISE(ABORT,'map transfer identity is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_map_transfer_event_update BEFORE UPDATE ON map_transfer_event BEGIN SELECT RAISE(ABORT,'map transfer event is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_map_transfer_active_identity BEFORE UPDATE ON map_transfer_operation WHEN NEW.ActiveMapId IS NOT OLD.ActiveMapId OR NEW.ActiveVersionId IS NOT OLD.ActiveVersionId OR NEW.ActiveDefinitionHash IS NOT OLD.ActiveDefinitionHash BEGIN SELECT RAISE(ABORT,'map transfer active identity is immutable'); END;");
        _database.CodeFirst.InitTables<TrafficRequestEntity, TrafficLifecycleEventEntity>();
        AgvDispatch.Infrastructure.Traffic.TransportRouteSnapshots.InstallImmutabilityGuard(_database);
        _database.CodeFirst.InitTables<TrafficSegmentReceiptEntity,TrafficSegmentReceiptEventEntity>();
        _database.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_segment_receipt_issued ON traffic_segment_receipt_event(ReceiptId,Kind)");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_segment_receipt_update BEFORE UPDATE ON traffic_segment_receipt BEGIN SELECT RAISE(ABORT,'segment receipt is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_segment_receipt_event_update BEFORE UPDATE ON traffic_segment_receipt_event BEGIN SELECT RAISE(ABORT,'segment receipt event is immutable'); END;");
        _database.CodeFirst.InitTables<TrafficSegmentCommitEntity,TrafficSegmentCommitLeaseEntity>();
        _database.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_segment_commit_receipt ON traffic_segment_commit(ReceiptId)");
        _database.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_segment_commit_lease_epoch ON traffic_segment_commit_lease(LeaseId,AcquiredAt)");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_segment_commit_update BEFORE UPDATE ON traffic_segment_commit BEGIN SELECT RAISE(ABORT,'segment commit is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_segment_commit_lease_update BEFORE UPDATE ON traffic_segment_commit_lease BEGIN SELECT RAISE(ABORT,'segment commit audit is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_traffic_request_waiting ON traffic_request(State, WaitingSinceUtc)");
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_traffic_request_updated ON traffic_request(UpdatedAt DESC)");
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_traffic_lifecycle_time ON traffic_lifecycle_event(OccurredAt DESC)");
        _database.CodeFirst.InitTables<ExecutionRecoverySessionEntity, ExecutionRecoveryTicketEntity, ExecutionEvidenceEntity>();
        _database.CodeFirst.InitTables<TaskHandshakeEntity, TaskRuntimeEventEntity, TaskSignalLastEntity, TaskEquipmentClaimEntity>();
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_runtime_event_task_sequence ON task_runtime_event(TaskId,Sequence)");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_runtime_event_delete BEFORE DELETE ON task_runtime_event BEGIN SELECT RAISE(ABORT,'runtime event is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_runtime_event_update BEFORE UPDATE ON task_runtime_event BEGIN SELECT RAISE(ABORT,'runtime event is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE TRIGGER IF NOT EXISTS guard_handshake_identity BEFORE UPDATE ON task_handshake_checkpoint WHEN NEW.TaskId IS NOT OLD.TaskId OR NEW.StepId IS NOT OLD.StepId OR NEW.CycleId IS NOT OLD.CycleId OR NEW.ContractVersion IS NOT OLD.ContractVersion OR NEW.BaselineJson IS NOT OLD.BaselineJson OR NEW.StartedAtUtc IS NOT OLD.StartedAtUtc OR NEW.Revision != OLD.Revision+1 BEGIN SELECT RAISE(ABORT,'handshake identity/version is immutable'); END;");
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_execution_evidence_task_time ON execution_evidence(TaskId, OccurredAt DESC)");
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_execution_evidence_session_task_time ON execution_evidence(SessionId, TaskId, OccurredAt DESC)");
        _database.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_traffic_release_task_time ON traffic_resource_release_audit(TaskId, ReleasedAt DESC)");
        _database.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_recovery_session_task ON execution_recovery_ticket(SessionId, TaskId)");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_map_switch_open ON map_switch((1)) WHERE Phase < 90;");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_dispatch_task_station_active " +
            "ON dispatch_task(StationId) WHERE Stage NOT IN (90, 110);");
        _database.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_dispatch_task_create_request ON dispatch_task(CreateRequestId) WHERE CreateRequestId IS NOT NULL;");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_dispatch_task_vendor_task " +
            "ON dispatch_task(VendorTaskId) WHERE VendorTaskId IS NOT NULL;");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_dispatch_task_stage_priority_created " +
            "ON dispatch_task(Stage, Priority DESC, CreatedAt);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_dispatch_task_step_task_sequence " +
            "ON dispatch_task_step(TaskId, Sequence);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_dispatch_task_step_task_status " +
            "ON dispatch_task_step(TaskId, Status, Sequence);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_agv_command_outbox_idempotency " +
            "ON agv_command_outbox(IdempotencyKey);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_agv_command_outbox_due " +
            "ON agv_command_outbox(Status, NextAttemptAt, CreatedAt);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_agv_command_outbox_task_step " +
            "ON agv_command_outbox(TaskId, StepId);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_traffic_resource_lease_resource_owner " +
            "ON traffic_resource_lease(ResourceId, OwnerStepId);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_traffic_resource_lease_resource " +
            "ON traffic_resource_lease(ResourceId, ExpiresAt);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_traffic_resource_lease_task " +
            "ON traffic_resource_lease(TaskId, OwnerStepId);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_traffic_resource_release_audit_time " +
            "ON traffic_resource_release_audit(ReleasedAt DESC);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_traffic_resource_release_audit_resource " +
            "ON traffic_resource_release_audit(ResourceId, ReleasedAt DESC);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_manual_release_status_time " +
            "ON manual_traffic_release_request(Status, RequestedAt DESC);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_manual_release_resource_status " +
            "ON manual_traffic_release_request(ResourceId, Status);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_manual_release_pending_lease " +
            "ON manual_traffic_release_request(LeaseId) WHERE Status = 0;");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_alarm_active_rule " +
            "ON alarm_record(RuleKey) WHERE Status IN (0, 10);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_alarm_status_severity_time " +
            "ON alarm_record(Status, Severity DESC, LastObservedAt DESC);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_map_project_name " +
            "ON map_project(Name COLLATE NOCASE);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_map_project_active " +
            "ON map_project(IsActive) WHERE IsActive = 1;");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_map_version_number " +
            "ON map_version(MapId, VersionNumber);");
        _database.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_map_version_draft " +
            "ON map_version(MapId) WHERE Status = 0;");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_map_operation_audit_map_time " +
            "ON map_operation_audit(MapId, OccurredAt DESC);");
        _database.Ado.ExecuteCommand(
            "CREATE INDEX IF NOT EXISTS ix_agv_track_vehicle_map_time " +
            "ON agv_track_point(VehicleId, MapId, SampledAt DESC);");
        _database.Deleteable<TrafficResourceDefinitionEntity>().ExecuteCommand();
        if (_trafficResources.All.Count > 0)
        {
            var now = DateTime.Now;
            _database.Insertable(_trafficResources.All.Select(resource =>
                new TrafficResourceDefinitionEntity
                {
                    ResourceId = resource.Id,
                    ResourceType = (int)resource.Type,
                    Capacity = resource.Capacity,
                    UpdatedAt = now
                }).ToArray()).ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _database.Ado.CheckConnection();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
