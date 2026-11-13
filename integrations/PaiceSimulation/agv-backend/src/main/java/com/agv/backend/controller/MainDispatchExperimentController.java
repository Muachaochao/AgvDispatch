package com.agv.backend.controller;

import com.agv.backend.common.ApiResponse;
import com.agv.backend.dispatch.MainDispatchExperimentReport;
import com.agv.backend.dispatch.MainDispatchExperimentRequest;
import com.agv.backend.dispatch.MainDispatchExperimentService;
import jakarta.validation.Valid;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;

@RestController
@RequestMapping("/api/dispatch/experiments")
public class MainDispatchExperimentController {

    private final MainDispatchExperimentService service;

    public MainDispatchExperimentController(MainDispatchExperimentService service) {
        this.service = service;
    }

    @PostMapping
    public ApiResponse<MainDispatchExperimentReport> create(
            @Valid @RequestBody MainDispatchExperimentRequest request) {
        return ApiResponse.ok(service.create(request));
    }

    @GetMapping
    public ApiResponse<List<MainDispatchExperimentReport>> history() {
        return ApiResponse.ok(service.history());
    }

    @GetMapping("/{id}")
    public ApiResponse<MainDispatchExperimentReport> detail(@PathVariable Long id) {
        return ApiResponse.ok(service.refresh(id));
    }

    @PostMapping("/{id}/stop")
    public ApiResponse<MainDispatchExperimentReport> stop(@PathVariable Long id) {
        return ApiResponse.ok(service.stop(id));
    }
}
