package com.agv.backend.dispatch;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

import java.util.concurrent.Executor;
import java.util.concurrent.Executors;

@Configuration
public class MainDispatchConfiguration {

    @Bean(name = "mainDispatchExecutor", destroyMethod = "shutdown")
    public java.util.concurrent.ExecutorService mainDispatchExecutor() {
        return Executors.newSingleThreadExecutor(runnable -> {
            Thread thread = new Thread(runnable, "main-dispatch-experiment");
            thread.setDaemon(true);
            return thread;
        });
    }

    @Bean(name = "mainDispatchTaskExecutor")
    public Executor mainDispatchTaskExecutor(java.util.concurrent.ExecutorService mainDispatchExecutor) {
        return mainDispatchExecutor;
    }
}
