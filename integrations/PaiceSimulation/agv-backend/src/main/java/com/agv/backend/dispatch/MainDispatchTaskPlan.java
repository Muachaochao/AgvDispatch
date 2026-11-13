package com.agv.backend.dispatch;

import com.agv.backend.common.BusinessException;
import com.agv.backend.dto.TaskDto;
import com.agv.backend.dto.TaskSetPayload;

import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public record MainDispatchTaskPlan(List<Call> calls, String targetStation) {

    private static final Pattern SOURCE = Pattern.compile("^(?:BUS-)?(CNC-(\\d{3}))$");
    private static final String DEFAULT_TARGET = "DUMPER-001";

    public static MainDispatchTaskPlan from(TaskSetPayload payload) {
        if (payload.tasks() == null || payload.tasks().isEmpty()) {
            throw new BusinessException("任务集为空，不能创建主工程调度实验");
        }
        String target = payload.tasks().get(0).to();
        List<Call> calls = new ArrayList<>();
        for (int index = 0; index < payload.tasks().size(); index++) {
            TaskDto task = payload.tasks().get(index);
            int sequence = index + 1;
            if (!DEFAULT_TARGET.equals(task.to())) {
                throw new BusinessException("任务 " + sequence + "：终点 " + task.to()
                        + " 与当前主工程倒料目标 " + DEFAULT_TARGET + " 不一致");
            }
            Matcher matcher = SOURCE.matcher(task.from() == null ? "" : task.from());
            if (!matcher.matches()) {
                throw new BusinessException("任务 " + sequence + "：起点 " + task.from()
                        + " 不能映射为主工程 CNC 工位");
            }
            int stationNumber;
            try {
                stationNumber = Integer.parseInt(matcher.group(2));
            } catch (NumberFormatException error) {
                throw new BusinessException("任务 " + sequence + "：工位编号无效");
            }
            if (stationNumber < 1 || stationNumber > 305) {
                throw new BusinessException("任务 " + sequence + "：工位编号必须为 CNC-001 至 CNC-305");
            }
            calls.add(new Call(matcher.group(1), 0.0, null));
        }
        return new MainDispatchTaskPlan(List.copyOf(calls), target);
    }

    public record Call(String stationId, Double atSeconds, String vehicleId) {
    }
}
