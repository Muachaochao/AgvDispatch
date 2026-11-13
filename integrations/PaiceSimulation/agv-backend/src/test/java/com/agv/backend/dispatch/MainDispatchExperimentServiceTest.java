package com.agv.backend.dispatch;

import com.agv.backend.dto.TaskDto;
import com.agv.backend.dto.TaskSetPayload;
import com.agv.backend.entity.SysUser;
import com.agv.backend.entity.TaskSet;
import com.agv.backend.security.CurrentUser;
import com.agv.backend.service.AuditService;
import com.agv.backend.service.TaskSetService;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.mockito.ArgumentCaptor;

import java.util.List;
import java.util.Optional;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

class MainDispatchExperimentServiceTest {

    private final ObjectMapper objectMapper = new ObjectMapper();

    @Test
    @DisplayName("创建主工程实验时依次选择方案、启动实例并提交任务集呼叫")
    void launchesMainDispatchExperimentFromTaskSet() throws Exception {
        MainDispatchGateway gateway = mock(MainDispatchGateway.class);
        MainDispatchExperimentRepository repository = mock(MainDispatchExperimentRepository.class);
        TaskSetService taskSetService = mock(TaskSetService.class);
        CurrentUser currentUser = mock(CurrentUser.class);
        AuditService auditService = mock(AuditService.class);

        TaskSet taskSet = new TaskSet();
        taskSet.setId(20L);
        taskSet.setRoadnetId(10L);
        taskSet.setName("五车20工位算法任务");
        when(taskSetService.require(20L)).thenReturn(taskSet);
        when(taskSetService.detail(20L)).thenReturn(new TaskSetPayload(List.of(
                new TaskDto(1, "BUS-CNC-001", "DUMPER-001"),
                new TaskDto(2, "BUS-CNC-002", "DUMPER-001"))));
        when(taskSetService.rawJson(20L)).thenReturn("""
                {"tasks":[
                  {"id":1,"from":"BUS-CNC-001","to":"DUMPER-001"},
                  {"id":2,"from":"BUS-CNC-002","to":"DUMPER-001"}
                ]}
                """);
        SysUser owner = new SysUser();
        owner.setId(7L);
        owner.setRole(SysUser.ROLE_DISPATCHER);
        when(currentUser.get()).thenReturn(owner);
        when(currentUser.id()).thenReturn(7L);
        AtomicReference<MainDispatchExperiment> persisted = new AtomicReference<>();
        when(repository.save(any(MainDispatchExperiment.class))).thenAnswer(invocation -> {
            MainDispatchExperiment experiment = invocation.getArgument(0);
            if (experiment.getId() == null) experiment.setId(99L);
            persisted.set(experiment);
            return experiment;
        });
        when(repository.findById(99L)).thenAnswer(invocation ->
                Optional.ofNullable(persisted.get()));
        when(gateway.get("/api/simulation/live")).thenReturn(
                json("""
                        {"running":true,"scenario":{"status":"failed","id":"old-scenario"}}
                        """),
                json("""
                        {"running":true,"rootName":"web-single-vehicle-test",
                         "scenario":{"status":"running","id":"scenario-1","message":"运行中"},
                         "report":{"id":"report-1"}}
                        """));
        when(gateway.get("/api/simulation")).thenReturn(
                json("""
                        {"running":true,"phase":"running","fresh":true,"rootName":"web-single-vehicle-test"}
                        """),
                json("""
                        {"running":true,"phase":"running","fresh":true,"rootName":"web-single-vehicle-test",
                         "configuration":{"mapIdentity":{"definitionHash":"ABC"}},
                         "scenario":{"id":"scenario-1","name":"dispatch-backlog","status":"running"}}
                        """));
        when(gateway.post(anyString(), any())).thenReturn(json("{}"));

        MainDispatchExperimentService service = new MainDispatchExperimentService(
                gateway, repository, taskSetService, currentUser, auditService, objectMapper,
                Runnable::run, 0, 1);

        MainDispatchExperimentReport created = service.create(new MainDispatchExperimentRequest(
                "五车20单主工程实验", "scheme-1", 10L, 20L, null, 7200));

        assertEquals("PREPARING", created.status());
        assertEquals("主工程已受理 2 条工位呼叫", persisted.get().getMessage());
        verify(gateway).post("/api/simulation/abort", java.util.Map.of());
        verify(gateway).post("/api/simulation/stop", java.util.Map.of());
        verify(gateway).post("/api/simulation/scheme-select", java.util.Map.of("id", "scheme-1"));
        verify(gateway).post("/api/simulation/start", java.util.Map.of());

        @SuppressWarnings("unchecked")
        ArgumentCaptor<Object> runCaptor = ArgumentCaptor.forClass(Object.class);
        verify(gateway).post(org.mockito.ArgumentMatchers.eq("/api/simulation/run"), runCaptor.capture());
        JsonNode run = objectMapper.valueToTree(runCaptor.getValue());
        assertEquals("dispatch-backlog", run.path("name").asText());
        assertEquals(2, run.path("calls").size());
        assertEquals("CNC-001", run.path("calls").get(0).path("stationId").asText());
        assertEquals("CNC-002", run.path("calls").get(1).path("stationId").asText());

        MainDispatchExperimentReport refreshed = service.refresh(99L);
        assertEquals("RUNNING", refreshed.status());
        assertEquals("web-single-vehicle-test", refreshed.runtimeInstance());
        assertEquals("scenario-1", refreshed.scenarioId());
        assertEquals(2, refreshed.taskCount());
    }

    @Test
    @DisplayName("其他调度用户不能查看或停止主工程实验")
    void rejectsAccessToAnotherUsersExperiment() {
        MainDispatchGateway gateway = mock(MainDispatchGateway.class);
        MainDispatchExperimentRepository repository = mock(MainDispatchExperimentRepository.class);
        TaskSetService taskSetService = mock(TaskSetService.class);
        CurrentUser currentUser = mock(CurrentUser.class);
        AuditService auditService = mock(AuditService.class);
        MainDispatchExperiment experiment = currentExperiment();
        when(repository.findById(99L)).thenReturn(Optional.of(experiment));
        SysUser other = new SysUser();
        other.setId(8L);
        other.setRole(SysUser.ROLE_DISPATCHER);
        when(currentUser.get()).thenReturn(other);
        when(currentUser.id()).thenReturn(8L);

        MainDispatchExperimentService service = new MainDispatchExperimentService(
                gateway, repository, taskSetService, currentUser, auditService, objectMapper,
                Runnable::run, 0, 1);

        assertThrows(com.agv.backend.common.BusinessException.class, () -> service.refresh(99L));
        assertThrows(com.agv.backend.common.BusinessException.class, () -> service.stop(99L));
        verify(gateway, never()).post("/api/simulation/stop", java.util.Map.of());
    }

    @Test
    @DisplayName("失败实验停止时仍关闭仍在运行的隔离环境")
    void stopsEnvironmentForTerminalExperiment() {
        MainDispatchGateway gateway = mock(MainDispatchGateway.class);
        MainDispatchExperimentRepository repository = mock(MainDispatchExperimentRepository.class);
        TaskSetService taskSetService = mock(TaskSetService.class);
        CurrentUser currentUser = mock(CurrentUser.class);
        AuditService auditService = mock(AuditService.class);
        SysUser owner = new SysUser();
        owner.setId(7L);
        owner.setRole(SysUser.ROLE_DISPATCHER);
        when(currentUser.get()).thenReturn(owner);
        MainDispatchExperiment experiment = currentExperiment();
        experiment.setStatus("FAILED");
        when(repository.findById(99L)).thenReturn(Optional.of(experiment));
        when(gateway.post(anyString(), any())).thenReturn(json("{}"));

        MainDispatchExperimentService service = new MainDispatchExperimentService(
                gateway, repository, taskSetService, currentUser, auditService, objectMapper,
                Runnable::run, 1, 1);

        MainDispatchExperimentReport stopped = service.stop(99L);

        assertEquals("FAILED", stopped.status());
        verify(gateway).post("/api/simulation/abort", java.util.Map.of());
        verify(gateway).post("/api/simulation/stop", java.util.Map.of());
    }

    private MainDispatchExperiment currentExperiment() {
        MainDispatchExperiment experiment = new MainDispatchExperiment();
        experiment.setId(99L);
        experiment.setName("五车20单主工程实验");
        experiment.setCreateUser(7L);
        experiment.setSchemeId("scheme-1");
        experiment.setRoadnetId(10L);
        experiment.setTaskSetId(20L);
        experiment.setTaskCount(2);
        experiment.setScenarioName("dispatch-backlog");
        experiment.setStatus("SUBMITTED");
        experiment.setTasksetSnapshot("{\"tasks\":[]}");
        return experiment;
    }

    private JsonNode json(String value) {
        try {
            return objectMapper.readTree(value);
        } catch (Exception error) {
            throw new IllegalStateException(error);
        }
    }
}
