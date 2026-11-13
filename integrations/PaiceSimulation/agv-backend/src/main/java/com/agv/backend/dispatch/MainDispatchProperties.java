package com.agv.backend.dispatch;

import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.stereotype.Component;

@Component
@ConfigurationProperties(prefix = "agv.main-dispatch")
public class MainDispatchProperties {

    private boolean enabled = true;
    private String baseUrl = "http://127.0.0.1:5199";
    private int startTimeoutSeconds = 240;

    public boolean isEnabled() {
        return enabled;
    }

    public void setEnabled(boolean enabled) {
        this.enabled = enabled;
    }

    public String getBaseUrl() {
        return baseUrl;
    }

    public void setBaseUrl(String baseUrl) {
        this.baseUrl = baseUrl;
    }

    public int getStartTimeoutSeconds() {
        return startTimeoutSeconds;
    }

    public void setStartTimeoutSeconds(int startTimeoutSeconds) {
        this.startTimeoutSeconds = startTimeoutSeconds;
    }
}
