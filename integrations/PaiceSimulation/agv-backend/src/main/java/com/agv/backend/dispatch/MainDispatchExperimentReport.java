package com.agv.backend.dispatch;

import java.time.LocalDateTime;

public record MainDispatchExperimentReport(
        Long id,
        String name,
        String schemeId,
        Long roadnetId,
        Long taskSetId,
        Integer taskCount,
        String scenarioName,
        String status,
        String runtimeInstance,
        String scenarioId,
        String mapDefinitionHash,
        String reportId,
        String message,
        Long createUser,
        LocalDateTime createTime,
        LocalDateTime updateTime) {

    static MainDispatchExperimentReport from(MainDispatchExperiment experiment) {
        return new MainDispatchExperimentReport(
                experiment.getId(),
                experiment.getName(),
                experiment.getSchemeId(),
                experiment.getRoadnetId(),
                experiment.getTaskSetId(),
                experiment.getTaskCount(),
                experiment.getScenarioName(),
                experiment.getStatus(),
                experiment.getRuntimeInstance(),
                experiment.getScenarioId(),
                experiment.getMapDefinitionHash(),
                experiment.getReportId(),
                experiment.getMessage(),
                experiment.getCreateUser(),
                experiment.getCreateTime(),
                experiment.getUpdateTime());
    }
}
