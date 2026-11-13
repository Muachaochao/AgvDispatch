package com.agv.backend.dispatch;

import com.agv.backend.common.BusinessException;
import com.agv.backend.dto.TaskDto;
import com.agv.backend.dto.TaskSetPayload;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

class MainDispatchTaskPlanTest {

    @Test
    @DisplayName("把 BUS-CNC 任务映射成主工程 backlog 呼叫并保持任务顺序")
    void mapsBusCncTasksToDispatchBacklogCalls() {
        MainDispatchTaskPlan plan = MainDispatchTaskPlan.from(new TaskSetPayload(List.of(
                new TaskDto(1, "BUS-CNC-001", "DUMPER-001"),
                new TaskDto(2, "CNC-003", "DUMPER-001"),
                new TaskDto(3, "BUS-CNC-020", "DUMPER-001"))));

        assertEquals(List.of(
                new MainDispatchTaskPlan.Call("CNC-001", 0.0, null),
                new MainDispatchTaskPlan.Call("CNC-003", 0.0, null),
                new MainDispatchTaskPlan.Call("CNC-020", 0.0, null)), plan.calls());
        assertEquals("DUMPER-001", plan.targetStation());
    }

    @Test
    @DisplayName("拒绝不能映射为主工程 CNC 工位的任务起点")
    void rejectsUnsupportedTaskSource() {
        BusinessException error = assertThrows(BusinessException.class,
                () -> MainDispatchTaskPlan.from(new TaskSetPayload(List.of(
                        new TaskDto(1, "W1", "DUMPER-001")))));

        assertEquals("任务 1：起点 W1 不能映射为主工程 CNC 工位", error.getMessage());
    }

    @Test
    @DisplayName("拒绝目标工位不一致的任务集")
    void rejectsMixedTaskTargets() {
        BusinessException error = assertThrows(BusinessException.class,
                () -> MainDispatchTaskPlan.from(new TaskSetPayload(List.of(
                        new TaskDto(1, "BUS-CNC-001", "DUMPER-001"),
                        new TaskDto(2, "BUS-CNC-002", "DUMPER-002")))));

        assertEquals("任务 2：终点 DUMPER-002 与当前主工程倒料目标 DUMPER-001 不一致",
                error.getMessage());
    }
}
