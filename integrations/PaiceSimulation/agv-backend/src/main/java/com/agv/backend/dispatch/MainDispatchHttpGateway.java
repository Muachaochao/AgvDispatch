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
import java.util.Set;

@Component
public class MainDispatchHttpGateway implements MainDispatchGateway {
    private static final Set<String> INSTANCE_PREPARATION_PATHS = Set.of(
            "/api/simulation/new-instance", "/api/simulation/configure",
            "/api/simulation/scheme-select", "/api/simulation/scheme-start");

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

    @Override
    public byte[] archive(String reportId) {
        if (!properties.isEnabled()) throw new BusinessException(5031, "主工程调度实验未启用");
        if (!reportId.matches("[a-fA-F0-9]{32}")) throw new BusinessException("主工程报告编号无效");
        int maximumBytes = 64 * 1024 * 1024;
        try {
            var request = HttpRequest.newBuilder(uri("/api/simulation/archive?id=" + reportId))
                    .timeout(Duration.ofSeconds(95)).header("Accept", "application/zip").GET().build();
            var response = client.send(request, HttpResponse.BodyHandlers.ofInputStream());
            try (var stream = response.body()) {
                if (response.statusCode() != 200) throw new BusinessException(5031, "5199 证据包读取失败");
                byte[] bytes = stream.readNBytes(maximumBytes + 1);
                if (bytes.length > maximumBytes) throw new BusinessException(5031, "证据包超过大小限制");
                return bytes;
            }
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new BusinessException(5031, "证据包读取已中断");
        } catch (IOException error) {
            throw new BusinessException(5031, "无法读取 5199 证据包");
        }
    }

    private JsonNode send(String method, String path, String body) {
        if (!properties.isEnabled()) {
            throw new BusinessException(5031, "主工程调度实验未启用");
        }
        try {
            long timeoutSeconds = "POST".equals(method) && INSTANCE_PREPARATION_PATHS.contains(path) ? 130 : 48;
            HttpRequest.Builder request = HttpRequest.newBuilder(uri(path))
                    .timeout(Duration.ofSeconds(timeoutSeconds))
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
                String message = parsed.path("detail").asText(parsed.path("error").asText(
                        parsed.path("message").asText("5199 请求失败 HTTP " + response.statusCode())));
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
