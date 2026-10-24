export function draftSaveBlocker(busy: boolean, backend: boolean, associated: boolean, issues: string[]): string {
  if (busy) return '正在处理，请稍候。';
  if (!backend) return '后台未连接，请先恢复后台连接。';
  if (!associated) return '尚未关联后台草稿：读取目录，选择已有地图并点击“关联并保留编辑稿”；首次使用也可新建后台地图。';
  if (issues.length) return `请先修正地图：${issues.join('；')}`;
  return '';
}

// Association reads only the destination revision. The editor's definition must remain untouched.
export function checkDraftDestination(result: any, expectedMapId: string): void {
  if (!expectedMapId || result?.project?.mapId !== expectedMapId ||
      !result?.draft?.versionId || !result.draft.editToken)
    throw new Error('目标草稿或版本标识无效，请重新读取目录和目标草稿。');
}
