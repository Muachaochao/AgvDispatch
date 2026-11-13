package com.agv.backend.dispatch;

import com.agv.backend.common.BusinessException;
import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.springframework.stereotype.Component;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;

@Component
public class MainDispatchHttpGateway implements MainDispatchGateway {

    private final MainDispatchProperties properties;
    private final ObjectMapper objectMapper;
    private final HttpClient client;

    public MainDispatchHttpGateway(MainDispatchProperties properties, ObjectMapper objectMapper) {
        this.properties = properties;
        this.objectMapper = objectMapper;
        this.client = HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(5))
                .build();
    }

    @Override
    public JsonNode get(String path) {
        return send("GET", path, null);
    }

    @Override
    public JsonNode post(String path, Object payload) {
        return send("POST", path, payload == null ? "{}" : write(payload));
    }

    private JsonNode send(String method, String path, String body) {
        if (!properties.isEnabled()) {
            throw new BusinessException(5031, "主工程调度实验未启用");
        }
        try {
            HttpRequest.Builder request = HttpRequest.newBuilder(uri(path))
                    .timeout(Duration.ofSeconds(45))
                    .header("Accept", "application/json");
            if ("POST".equals(method)) {
                request.header("Content-Type", "application/json")
                        .POST(HttpRequest.BodyPublishers.ofString(body == null ? "{}" : body));
            } else {
                request.GET();
            }
            HttpResponse<String> response = client.send(request.build(),
                    HttpResponse.BodyHandlers.ofString());
            JsonNode parsed;
            try {
                parsed = objectMapper.readTree(response.body());
            } catch (JsonProcessingException error) {
                throw new BusinessException(5031, "5199 返回了无效 JSON");
            }
            if (response.statusCode() < 200 || response.statusCode() >= 300) {
                String message = parsed.path("error").asText(
                        parsed.path("message").asText("5199 请求失败 HTTP " + response.statusCode()));
                throw new BusinessException(5031, message);
            }
            return parsed;
        } catch (BusinessException error) {
            throw error;
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new BusinessException(5031, "连接 5199 时被中断");
        } catch (IOException | IllegalArgumentException error) {
            throw new BusinessException(5031, "无法连接 5199：" + error.getMessage());
        }
    }

    private URI uri(String path) {
        String base = properties.getBaseUrl() == null
                ? "http://127.0.0.1:5199"
                : properties.getBaseUrl().replaceAll("/+$", "");
        if (!base.startsWith("http://127.0.0.1:") && !base.startsWith("http://localhost:")) {
            throw new IllegalArgumentException("主工程地址必须是本机回环地址");
        }
        return URI.create(base + (path.startsWith("/") ? path : "/" + path));
    }

    private String write(Object payload) {
        try {
            return objectMapper.writeValueAsString(payload);
        } catch (JsonProcessingException error) {
            throw new BusinessException("主工程请求无法序列化");
        }
    }
}
