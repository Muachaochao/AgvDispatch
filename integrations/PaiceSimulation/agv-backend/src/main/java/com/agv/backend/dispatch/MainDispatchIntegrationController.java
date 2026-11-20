package com.agv.backend.dispatch;

import com.agv.backend.common.ApiResponse;
import com.agv.backend.common.BusinessException;
import com.fasterxml.jackson.databind.JsonNode;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.Map;

@RestController
@RequestMapping("/api/dispatch/main")
public class MainDispatchIntegrationController {

    private final MainDispatchGateway gateway;

    public MainDispatchIntegrationController(MainDispatchGateway gateway) {
        this.gateway = gateway;
    }

    @GetMapping("/simulation")
    public ApiResponse<JsonNode> simulation() {
        return ApiResponse.ok(gateway.get("/api/simulation"));
    }

    @GetMapping("/simulation/live")
    public ApiResponse<JsonNode> live() {
        return ApiResponse.ok(gateway.get("/api/simulation/live"));
    }

    @GetMapping("/simulation/resources")
    public ApiResponse<JsonNode> resources() {
        return ApiResponse.ok(gateway.get("/api/simulation/resources"));
    }

    @GetMapping("/simulation/tasks")
    public ApiResponse<JsonNode> tasks(
            @RequestParam(defaultValue = "All") String filter,
            @RequestParam(required = false) String search,
            @RequestParam(defaultValue = "1") int page,
            @RequestParam(defaultValue = "200") int pageSize) {
        if (!ListValues.TASK_FILTERS.contains(filter)) {
            throw new BusinessException("任务筛选无效");
        }
        if (page < 1 || pageSize < 1 || pageSize > 200) {
            throw new BusinessException("任务分页参数无效");
        }
        String query = "?filter=" + encode(filter);
        if (search != null && !search.isBlank()) {
            query += "&search=" + encode(search.trim());
        }
        query += "&page=" + page + "&pageSize=" + pageSize;
        return ApiResponse.ok(gateway.get("/api/simulation/tasks" + query));
    }

    @GetMapping("/simulation/tasks/{id}")
    public ApiResponse<JsonNode> task(@PathVariable String id) {
        return ApiResponse.ok(gateway.get("/api/simulation/tasks/" + safeId(id)));
    }

    @GetMapping("/simulation/report")
    public ApiResponse<JsonNode> report(@RequestParam(required = false) String id) {
        String suffix = id == null || id.isBlank() ? "" : "?id=" + safeHex(id);
        return ApiResponse.ok(gateway.get("/api/simulation/report" + suffix));
    }

    @GetMapping("/simulation/replay")
    public ApiResponse<JsonNode> replay(@RequestParam String id) {
        return ApiResponse.ok(gateway.get("/api/simulation/replay?id=" + safeHex(id)));
    }

    @GetMapping("/snapshot")
    public ApiResponse<JsonNode> snapshot() {
        return ApiResponse.ok(gateway.get("/api/snapshot"));
    }

    @GetMapping("/maps")
    public ApiResponse<JsonNode> maps() {
        return ApiResponse.ok(gateway.get("/api/maps"));
    }

    @GetMapping("/maps/{id}/workspace")
    public ApiResponse<JsonNode> workspace(@PathVariable String id) {
        return ApiResponse.ok(gateway.get("/api/maps/" + safeId(id) + "/workspace"));
    }

    @PostMapping("/simulation/{action}")
    public ApiResponse<JsonNode> simulationAction(
            @PathVariable String action,
            @RequestBody(required = false) JsonNode body) {
        if (!ListValues.ACTIONS.contains(action)) {
            throw new BusinessException(4040, "不支持的主工程仿真操作：" + action);
        }
        return ApiResponse.ok(gateway.post(
                "/api/simulation/" + action,
                body == null ? Map.of() : body));
    }

    private String safeId(String value) {
        if (value == null || value.isBlank() || value.length() > 128
                || !value.matches("[A-Za-z0-9_-]+")) {
            throw new BusinessException("主工程资源编号无效");
        }
        return value;
    }

    private String safeHex(String value) {
        if (!value.matches("[a-fA-F0-9]{32}")) {
            throw new BusinessException("主工程报告编号无效");
        }
        return value;
    }

    private String encode(String value) {
        return URLEncoder.encode(value, StandardCharsets.UTF_8);
    }

    private static final class ListValues {
        private static final java.util.Set<String> TASK_FILTERS =
                java.util.Set.of("All", "Active", "Completed", "Failed");
        private static final java.util.Set<String> ACTIONS =
                java.util.Set.of("start", "stop", "scheme-start", "scheme-select", "run", "abort",
                        "scheme-save", "point-table-validate");
    }
}
