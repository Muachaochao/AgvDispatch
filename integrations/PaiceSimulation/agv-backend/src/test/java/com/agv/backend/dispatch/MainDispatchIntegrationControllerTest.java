package com.agv.backend.dispatch;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.agv.backend.common.BusinessException;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.mockito.Mockito.mock;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

class MainDispatchIntegrationControllerTest {

    private final ObjectMapper objectMapper = new ObjectMapper();

    @Test
    @DisplayName("主工程轻量状态通过固定代理路径读取")
    void readsLiveStateThroughGateway() throws Exception {
        MainDispatchGateway gateway = mock(MainDispatchGateway.class);
        JsonNode live = objectMapper.readTree("{\"running\":true,\"phase\":\"running\"}");
        when(gateway.get("/api/simulation/live")).thenReturn(live);
        MainDispatchIntegrationController controller = new MainDispatchIntegrationController(gateway);

        assertEquals(live, controller.live().data());
        verify(gateway).get("/api/simulation/live");
    }

    @Test
    @DisplayName("回放按报告编号读取同轮证据")
    void readsReplayByReportId() throws Exception {
        MainDispatchGateway gateway = mock(MainDispatchGateway.class);
        String id = "c98f4be8450a4ce2a4ce246fdcb09572";
        String path = "/api/simulation/replay?id=" + id;
        JsonNode replay = objectMapper.readTree("{\"timeline\":[{\"time\":1}]}");
        when(gateway.get(path)).thenReturn(replay);
        MainDispatchIntegrationController controller = new MainDispatchIntegrationController(gateway);

        assertEquals(replay, controller.replay(id).data());
        assertThrows(BusinessException.class, () -> controller.replay("../report"));
        verify(gateway).get(path);
    }

    @Test
    @DisplayName("主工程高级操作只转发允许的动作")
    void forwardsAllowedSimulationAction() throws Exception {
        MainDispatchGateway gateway = mock(MainDispatchGateway.class);
        JsonNode state = objectMapper.readTree("{\"running\":true}");
        JsonNode body = objectMapper.readTree("{\"id\":\"scheme-1\"}");
        when(gateway.post("/api/simulation/scheme-start", body)).thenReturn(state);
        MainDispatchIntegrationController controller = new MainDispatchIntegrationController(gateway);

        assertEquals(state, controller.simulationAction("scheme-start", body).data());
        verify(gateway).post("/api/simulation/scheme-start", body);
    }
}
