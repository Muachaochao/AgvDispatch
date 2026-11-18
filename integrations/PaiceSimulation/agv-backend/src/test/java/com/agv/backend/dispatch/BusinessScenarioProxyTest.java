package com.agv.backend.dispatch;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import static org.mockito.Mockito.*;
class BusinessScenarioProxyTest {
 @Test void forwardsEngineeringConfigurationActions() {
  var gateway=mock(MainDispatchGateway.class);
  var body=new ObjectMapper().createObjectNode();
  var controller=new MainDispatchIntegrationController(gateway);
  for (String action : java.util.List.of("point-table-validate", "scheme-save")) {
   controller.simulationAction(action, body);
   verify(gateway).post("/api/simulation/"+action,body);
  }
 }
 @Test void allowsSelectingSavedSchemeWithoutStartingCalls() throws Exception {
  var gateway=mock(MainDispatchGateway.class);
  var body=new ObjectMapper().readTree("{\"id\":\"source\"}");
  new MainDispatchIntegrationController(gateway).simulationAction("scheme-select", body);
  verify(gateway).post("/api/simulation/scheme-select",body);
 }
}
