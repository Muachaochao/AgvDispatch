package com.agv.backend;

import com.agv.backend.dto.RoadNetPayload;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import java.util.List;
import static org.junit.jupiter.api.Assertions.assertEquals;

class RoadNetPayloadMetadataTest {
    private final ObjectMapper mapper = new ObjectMapper()
            .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);

    @Test
    void preservesSourceIdentityThroughStoredPayload() throws Exception {
        String source = "{\"nodes\":[],\"edges\":[],\"integration\":{\"identity\":{\"definitionHash\":\"source-hash\"}}}";
        RoadNetPayload payload = mapper.readValue(source, RoadNetPayload.class);
        String hash = mapper.readTree(mapper.writeValueAsString(payload))
                .path("integration").path("identity").path("definitionHash").asText();
        assertEquals("source-hash", hash);
    }

    @Test
    void retainsLegacyConstructorsAndJson() throws Exception {
        assertEquals(50, new RoadNetPayload(List.of(), List.of()).width());
        assertEquals(60, new RoadNetPayload(List.of(), List.of(), 60, 40).width());
        assertEquals(34, mapper.readValue("{\"nodes\":[],\"edges\":[]}", RoadNetPayload.class).height());
    }
}
