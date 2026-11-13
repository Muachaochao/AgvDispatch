package com.agv.backend.dispatch;

import com.agv.backend.entity.BaseEntity;
import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.Lob;
import jakarta.persistence.Table;

import java.time.LocalDateTime;

@Entity
@Table(name = "main_dispatch_experiment")
public class MainDispatchExperiment extends BaseEntity {

    @Column(nullable = false, length = 128)
    private String name;

    @Column(name = "create_user", nullable = false)
    private Long createUser;

    @Column(name = "scheme_id", nullable = false, length = 64)
    private String schemeId;

    @Column(name = "roadnet_id", nullable = false)
    private Long roadnetId;

    @Column(name = "task_set_id", nullable = false)
    private Long taskSetId;

    @Column(name = "task_count", nullable = false)
    private Integer taskCount;

    @Column(name = "scenario_name", nullable = false, length = 32)
    private String scenarioName;

    @Column(nullable = false, length = 16)
    private String status;

    @Column(name = "runtime_instance", length = 128)
    private String runtimeInstance;

    @Column(name = "scenario_id", length = 64)
    private String scenarioId;

    @Column(name = "map_definition_hash", length = 64)
    private String mapDefinitionHash;

    @Column(name = "report_id", length = 64)
    private String reportId;

    @Column(length = 1000)
    private String message;

    @Lob
    @Column(name = "taskset_snapshot")
    private String tasksetSnapshot;

    @Column(name = "create_time", nullable = false)
    private LocalDateTime createTime = LocalDateTime.now();

    @Column(name = "update_time", nullable = false)
    private LocalDateTime updateTime = LocalDateTime.now();

    public String getName() {
        return name;
    }

    public void setName(String name) {
        this.name = name;
    }

    public Long getCreateUser() {
        return createUser;
    }

    public void setCreateUser(Long createUser) {
        this.createUser = createUser;
    }

    public String getSchemeId() {
        return schemeId;
    }

    public void setSchemeId(String schemeId) {
        this.schemeId = schemeId;
    }

    public Long getRoadnetId() {
        return roadnetId;
    }

    public void setRoadnetId(Long roadnetId) {
        this.roadnetId = roadnetId;
    }

    public Long getTaskSetId() {
        return taskSetId;
    }

    public void setTaskSetId(Long taskSetId) {
        this.taskSetId = taskSetId;
    }

    public Integer getTaskCount() {
        return taskCount;
    }

    public void setTaskCount(Integer taskCount) {
        this.taskCount = taskCount;
    }

    public String getScenarioName() {
        return scenarioName;
    }

    public void setScenarioName(String scenarioName) {
        this.scenarioName = scenarioName;
    }

    public String getStatus() {
        return status;
    }

    public void setStatus(String status) {
        this.status = status;
    }

    public String getRuntimeInstance() {
        return runtimeInstance;
    }

    public void setRuntimeInstance(String runtimeInstance) {
        this.runtimeInstance = runtimeInstance;
    }

    public String getScenarioId() {
        return scenarioId;
    }

    public void setScenarioId(String scenarioId) {
        this.scenarioId = scenarioId;
    }

    public String getMapDefinitionHash() {
        return mapDefinitionHash;
    }

    public void setMapDefinitionHash(String mapDefinitionHash) {
        this.mapDefinitionHash = mapDefinitionHash;
    }

    public String getReportId() {
        return reportId;
    }

    public void setReportId(String reportId) {
        this.reportId = reportId;
    }

    public String getMessage() {
        return message;
    }

    public void setMessage(String message) {
        this.message = message;
    }

    public String getTasksetSnapshot() {
        return tasksetSnapshot;
    }

    public void setTasksetSnapshot(String tasksetSnapshot) {
        this.tasksetSnapshot = tasksetSnapshot;
    }

    public LocalDateTime getCreateTime() {
        return createTime;
    }

    public void setCreateTime(LocalDateTime createTime) {
        this.createTime = createTime;
    }

    public LocalDateTime getUpdateTime() {
        return updateTime;
    }

    public void setUpdateTime(LocalDateTime updateTime) {
        this.updateTime = updateTime;
    }
}
