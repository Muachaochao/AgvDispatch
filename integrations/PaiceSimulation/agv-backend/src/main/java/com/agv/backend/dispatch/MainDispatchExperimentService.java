package com.agv.backend.dispatch;

import com.agv.backend.common.BusinessException;
import com.agv.backend.dto.TaskSetPayload;
import com.agv.backend.entity.SysUser;
import com.agv.backend.entity.TaskSet;
import com.agv.backend.security.CurrentUser;
import com.agv.backend.service.AuditService;
import com.agv.backend.service.TaskSetService;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.beans.factory.annotation.Qualifier;
import org.springframework.stereotype.Service;

import java.time.LocalDateTime;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.Executor;

@Service
public class MainDispatchExperimentService {

    private static final Logger log = LoggerFactory.getLogger(MainDispatchExperimentService.class);
    private static final List<String> ACTIVE = List.of(
            "starting", "running", "pending", "stopping", "aborting");
    private static final List<String> TERMINAL = List.of("FINISHED", "FAILED", "ABORTED");

    private final MainDispatchGateway gateway;
    private final MainDispatchExperimentRepository repository;
    private final TaskSetService taskSetService;
    private final CurrentUser currentUser;
    private final AuditService auditService;
    private final ObjectMapper objectMapper;
    private final Executor executor;
    private final int maxStatusAttempts;
    private final long pollMillis;

    @Autowired
    public MainDispatchExperimentService(
            MainDispatchGateway gateway,
            MainDispatchExperimentRepository repository,
            TaskSetService taskSetService,
            CurrentUser currentUser,
            AuditService auditService,
            ObjectMapper objectMapper,
            @Qualifier("mainDispatchTaskExecutor") Executor executor,
            MainDispatchProperties properties) {
        this(gateway, repository, taskSetService, currentUser, auditService, objectMapper,
                executor, Math.max(1, properties.getStartTimeoutSeconds() * 2), 500);
    }

    public MainDispatchExperimentService(
            MainDispatchGateway gateway,
            MainDispatchExperimentRepository repository,
            TaskSetService taskSetService,
            CurrentUser currentUser,
            AuditService auditService,
            ObjectMapper objectMapper,
            @Qualifier("mainDispatchTaskExecutor") Executor executor,
            int maxStatusAttempts,
            long pollMillis) {
        this.gateway = gateway;
        this.repository = repository;
        this.taskSetService = taskSetService;
        this.currentUser = currentUser;
        this.auditService = auditService;
        this.objectMapper = objectMapper;
        this.executor = executor;
        this.maxStatusAttempts = Math.max(1, maxStatusAttempts);
        this.pollMillis = Math.max(1, pollMillis);
    }

    public MainDispatchExperimentReport create(MainDispatchExperimentRequest request) {
        String name = requireText(request.name(), "实验名称");
        String schemeId = requireText(request.schemeId(), "主工程方案");
        String scenarioName = request.scenarioName() == null || request.scenarioName().isBlank()
                ? "dispatch-backlog"
                : request.scenarioName().trim();
        if (!"dispatch-backlog".equals(scenarioName)) {
            throw new BusinessException("当前只支持 dispatch-backlog 主工程调度实验");
        }
        int timeoutSeconds = request.timeoutSeconds() == null ? 7200 : request.timeoutSeconds();
        if (timeoutSeconds < 30 || timeoutSeconds > 604800) {
            throw new BusinessException("超时时间必须在 30 至 604800 秒之间");
        }
        TaskSet taskSet = taskSetService.require(request.taskSetId());
        if (!Objects.equals(taskSet.getRoadnetId(), request.roadnetId())) {
            throw new BusinessException("任务集「" + taskSet.getName() + "」绑定的路网与所选路网不一致");
        }
        TaskSetPayload payload = taskSetService.detail(request.taskSetId());
        MainDispatchTaskPlan plan = MainDispatchTaskPlan.from(payload);

        MainDispatchExperiment experiment = new MainDispatchExperiment();
        experiment.setName(name);
        experiment.setCreateUser(currentUser.id());
        experiment.setSchemeId(schemeId);
        experiment.setRoadnetId(request.roadnetId());
        experiment.setTaskSetId(request.taskSetId());
        experiment.setTaskCount(plan.calls().size());
        experiment.setScenarioName(scenarioName);
        experiment.setStatus("PREPARING");
        experiment.setTasksetSnapshot(taskSetService.rawJson(request.taskSetId()));
        experiment.setMessage("正在创建主工程隔离实例");
        experiment = repository.save(experiment);
        MainDispatchExperimentReport accepted = MainDispatchExperimentReport.from(experiment);
        Long experimentId = experiment.getId();
        int scenarioTimeout = timeoutSeconds;
        executor.execute(() -> launch(experimentId, scenarioTimeout));
        return accepted;
    }

    public List<MainDispatchExperimentReport> history() {
        return repository.findByCreateUserOrderByIdDesc(currentUser.id()).stream()
                .map(MainDispatchExperimentReport::from)
                .toList();
    }

    public MainDispatchExperimentReport refresh(Long id) {
        MainDispatchExperiment experiment = requireOwned(id);
        JsonNode live = gateway.get("/api/simulation/live");
        applyLiveState(experiment, live);
        save(experiment);
        return MainDispatchExperimentReport.from(experiment);
    }

    public MainDispatchExperimentReport stop(Long id) {
        MainDispatchExperiment experiment = requireOwned(id);
        boolean terminal = TERMINAL.contains(experiment.getStatus());
        try {
            gateway.post("/api/simulation/abort", Map.of());
        } catch (BusinessException error) {
            log.debug("主工程没有可中止场景：{}", error.getMessage());
        }
        gateway.post("/api/simulation/stop", Map.of());
        if (terminal) {
            experiment.setMessage("已停止隔离环境；保留原实验终态 " + experiment.getStatus());
        } else {
            experiment.setStatus("ABORTED");
            experiment.setMessage("用户已停止主工程调度实验");
        }
        save(experiment);
        auditService.success("MAIN_DISPATCH_STOP", "MAIN_DISPATCH_EXPERIMENT", id);
        return MainDispatchExperimentReport.from(experiment);
    }

    private void launch(Long id, int timeoutSeconds) {
        MainDispatchExperiment experiment = repository.findById(id).orElse(null);
        if (experiment == null) {
            return;
        }
        try {
            JsonNode live = gateway.get("/api/simulation/live");
            if (live.path("running").asBoolean(false) || isScenarioActive(live)) {
                gateway.post("/api/simulation/abort", Map.of());
                gateway.post("/api/simulation/stop", Map.of());
            }

            gateway.post("/api/simulation/scheme-select", Map.of("id", experiment.getSchemeId()));
            gateway.post("/api/simulation/start", Map.of());
            waitUntilReady();

            TaskSetPayload payload = objectMapper.readValue(
                    experiment.getTasksetSnapshot(), TaskSetPayload.class);
            MainDispatchTaskPlan plan = MainDispatchTaskPlan.from(payload);
            Map<String, Object> run = Map.of(
                    "name", "dispatch-backlog",
                    "rounds", 1,
                    "intervalSeconds", 0,
                    "timeoutSeconds", timeoutSeconds,
                    "calls", plan.calls());
            gateway.post("/api/simulation/run", run);

            JsonNode status = gateway.get("/api/simulation");
            applyFullState(experiment, status);
            if ("PREPARING".equals(experiment.getStatus())) {
                experiment.setStatus("SUBMITTED");
            }
            experiment.setMessage("主工程已受理 " + plan.calls().size() + " 条工位呼叫");
            save(experiment);
            auditService.success("MAIN_DISPATCH_START", "MAIN_DISPATCH_EXPERIMENT", id);
        } catch (Exception error) {
            experiment.setStatus("FAILED");
            experiment.setMessage(cut(error.getMessage() == null ? error.getClass().getSimpleName() : error.getMessage()));
            save(experiment);
            auditService.failure("MAIN_DISPATCH_START", "MAIN_DISPATCH_EXPERIMENT", id, experiment.getMessage());
        }
    }

    private void waitUntilReady() throws InterruptedException {
        for (int attempt = 0; attempt < maxStatusAttempts; attempt++) {
            JsonNode status = gateway.get("/api/simulation");
            if (status.path("running").asBoolean(false)
                    && status.path("fresh").asBoolean(false)
                    && !isScenarioActive(status)) {
                return;
            }
            Thread.sleep(pollMillis);
        }
        throw new BusinessException(5031, "等待主工程隔离实例启动超时");
    }

    private void applyFullState(MainDispatchExperiment experiment, JsonNode status) {
        applyLiveState(experiment, status);
        String runtime = status.path("rootName").asText("");
        if (!runtime.isBlank()) {
            experiment.setRuntimeInstance(runtime);
        }
        String hash = status.path("configuration").path("mapIdentity")
                .path("definitionHash").asText("");
        if (!hash.isBlank()) {
            experiment.setMapDefinitionHash(hash);
        }
    }

    private void applyLiveState(MainDispatchExperiment experiment, JsonNode live) {
        String scenarioId = live.path("scenario").path("id").asText("");
        if (!scenarioId.isBlank()) {
            experiment.setScenarioId(scenarioId);
        }
        String reportId = live.path("report").path("id").asText("");
        if (!reportId.isBlank()) {
            experiment.setReportId(reportId);
        }
        String runtime = live.path("rootName").asText("");
        if (!runtime.isBlank()) {
            experiment.setRuntimeInstance(runtime);
        }
        String status = live.path("scenario").path("status").asText("");
        if ("passed".equalsIgnoreCase(status) || "finished".equalsIgnoreCase(status)
                || "completed".equalsIgnoreCase(status)) {
            experiment.setStatus("FINISHED");
        } else if ("failed".equalsIgnoreCase(status) || "error".equalsIgnoreCase(status)) {
            experiment.setStatus("FAILED");
        } else if ("aborted".equalsIgnoreCase(status) || "stopped".equalsIgnoreCase(status)) {
            experiment.setStatus("ABORTED");
        } else if (ACTIVE.contains(status.toLowerCase())) {
            experiment.setStatus("RUNNING");
        }
        String message = live.path("scenario").path("message").asText("");
        if (!message.isBlank()) {
            experiment.setMessage(cut(message));
        }
    }

    private boolean isScenarioActive(JsonNode value) {
        return ACTIVE.contains(value.path("scenario").path("status").asText("").toLowerCase());
    }

    private MainDispatchExperiment requireOwned(Long id) {
        MainDispatchExperiment experiment = repository.findById(id)
                .orElseThrow(() -> new BusinessException(4040, "主工程调度实验不存在：" + id));
        SysUser user = currentUser.get();
        boolean owner = user != null && Objects.equals(user.getId(), experiment.getCreateUser());
        boolean admin = user != null && SysUser.ROLE_ADMIN.equals(user.getRole());
        if (!owner && !admin) {
            throw new BusinessException(4030, "只能查看或停止自己创建的主工程调度实验");
        }
        return experiment;
    }

    private void save(MainDispatchExperiment experiment) {
        experiment.setUpdateTime(LocalDateTime.now());
        repository.save(experiment);
    }

    private String requireText(String value, String label) {
        String normalized = value == null ? "" : value.trim();
        if (normalized.isEmpty()) {
            throw new BusinessException(label + "不能为空");
        }
        return normalized;
    }

    private String cut(String value) {
        return value.length() <= 1000 ? value : value.substring(0, 1000);
    }
}
