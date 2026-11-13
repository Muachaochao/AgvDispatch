package com.agv.backend.dispatch;

import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;

public record MainDispatchExperimentRequest(
        @NotBlank @Size(max = 128) String name,
        @NotBlank @Size(max = 64) String schemeId,
        @NotNull Long roadnetId,
        @NotNull Long taskSetId,
        String scenarioName,
        Integer timeoutSeconds) {
}
