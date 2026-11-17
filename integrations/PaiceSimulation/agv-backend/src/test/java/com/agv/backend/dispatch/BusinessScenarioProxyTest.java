package com.agv.backend.dispatch;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import static org.mockito.Mockito.*;
class BusinessScenarioProxyTest {
 @Test void allowsSelectingSavedSchemeWithoutStartingCalls() throws Exception {
  var gateway=mock(MainDispatchGateway.class);
  var body=new ObjectMapper().readTree("{\"id\":\"source\"}");
  new MainDispatchIntegrationController(gateway).simulationAction("scheme-select", body);
  verify(gateway).post("/api/simulation/scheme-select",body);
 }
}
