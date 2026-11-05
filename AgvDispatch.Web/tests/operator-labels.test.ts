import test from 'node:test';
import assert from 'node:assert/strict';
import {
  actionStateLabel,
  alarmMessageText,
  alarmTitleText,
  contractLabel,
  engineeringFieldValueLabel,
  equipmentDirectionLabel,
  handshakePhaseLabel,
  failurePolicyLabel,
  industrialText,
  observedPhaseLabel,
  qualityLabel,
  runtimeKindLabel,
  signalLabel,
  timeoutActionLabel,
  vehicleActionLabel
} from '../src/operator-labels.ts';

test('operator labels show Chinese meaning for PLC signals and states', () => {
  assert.equal(signalLabel('DumperReady'), '倒料机就绪许可');
  assert.equal(signalLabel('unknown-signal'), '未识别信号');
  assert.equal(signalLabel('NEW_VENDOR_SIGNAL'), '未识别信号');
  assert.equal(handshakePhaseLabel('ReadyToComplete'), '等待本步骤收尾');
  assert.equal(actionStateLabel('Completed'), '已完成并核对到点');
  assert.equal(qualityLabel('Stale'), '已过期');
  assert.equal(failurePolicyLabel('HoldUntilCondition'), '保持并等待条件');
  assert.equal(timeoutActionLabel('NotifyAndHold'), '通知并暂停等待核对');
  assert.equal(contractLabel('plc-28-stage-v2'), 'PLC 28 阶段统一执行契约（第 2 版）');
  assert.equal(observedPhaseLabel('持久化阶段 ExitHandoverComplete / r7：已交接'), '车辆已出区，设备和区域已交接');
  assert.equal(vehicleActionLabel('JackUnload'), '下降放箱');
  assert.equal(runtimeKindLabel('HandshakeTransition'), '握手阶段变化');
  assert.equal(engineeringFieldValueLabel('capability', 'Jack'), '顶升式车辆');
  assert.equal(equipmentDirectionLabel('Down'), '下降');
  assert.equal(industrialText('CNC-001/DumperReady=False（Good）'), 'CNC-001/倒料机就绪许可=0（正常）');
  assert.equal(industrialText('DumperExitWaiting · Completed；Pass / Wait'), '等待倒料区出区核对 · 已完成；满足 / 等待');
  assert.match(industrialText('工程版本摘要不一致'), /先刷新工程草稿和版本/);
});

test('alarm text translates known vendor and PLC messages', () => {
  assert.equal(alarmTitleText('AGV-01连接状态异常：Interrupted'), 'AGV-01连接状态异常：通信中断');
  assert.match(alarmMessageText('Caution: robot is blocked'), /车辆当前受阻/);
  assert.equal(alarmMessageText('The operation was canceled.'), '厂家接口提示：本次操作已取消。');
  assert.equal(alarmMessageText('PLC存在37个Stale质量点位'), 'PLC 有 37 个数据质量过期点位');
  assert.equal(alarmTitleText('PLC存在37个Stale质量点位'), 'PLC 有 37 个数据质量过期点位');
  assert.equal(alarmMessageText("The requested operation requires an element of type 'Number', but the target element has type 'Null'."),
    '厂家接口返回了空数值，无法完成本次操作。请核对车辆采样、原任务和连接状态。');
  assert.match(alarmMessageText('["robot is slipping. slip.vw.t = {1}, slip.vw.v = {2}",1,0]'), /车辆出现打滑/);
  assert.match(alarmTitleText('PLC连接状态异常：Connecting'), /正在连接/);
  assert.match(alarmMessageText('Socket Exception -> 目标计算机拒绝连接'), /网络连接异常：目标计算机拒绝连接/);
  assert.match(alarmMessageText('CNC-001.FullBoxRequested；CNC-001.BoxPresent'), /CNC-001.满箱呼叫；CNC-001.箱体检测/);
});

 test('structured simulation report evidence cannot crash or imply a verdict', () => {
  assert.equal(industrialText({ accepted: false, error: 'device rejected' }), '结构化核对记录已保存，详情见下载报告。');
  assert.equal(industrialText(['TASK-1', 'TASK-2']), '共 2 项核对记录，详情见下载报告。');
  assert.equal(industrialText(0), '0');
  assert.equal(industrialText(false), '0');
});
