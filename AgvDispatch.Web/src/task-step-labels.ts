const names: Record<string, string> = {
  GoToPickup: '前往取货点', PickupFullBox: '取走满箱',
  DeliverFullBoxToDumper: '运送至倒料等待点', WaitForDumperReady: '等待倒料许可',
  EnterDumpingPosition: '进入倒料位', DumpFullBox: '执行倒料',
  ReturnEmptyBox: '返回取货点', PlaceEmptyBox: '放置空箱',
  CompletePlcHandshake: '完成PLC握手'
};
export function taskStepName(value: string): string {
  const known = names[value];
  if (known) return known;
  return /[\u3400-\u9fff]/.test(value) ? value : '业务步骤待核对';
}
