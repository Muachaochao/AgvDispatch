package com.agv.backend.dispatch;

import com.fasterxml.jackson.databind.JsonNode;

public interface MainDispatchGateway {

    JsonNode get(String path);

    JsonNode post(String path, Object payload);
}
