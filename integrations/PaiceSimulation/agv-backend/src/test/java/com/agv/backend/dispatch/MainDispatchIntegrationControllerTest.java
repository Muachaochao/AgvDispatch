package com.agv.backend.dispatch;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
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
