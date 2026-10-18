export interface CancelIntent {requestId:string;taskId:string;expectedStage:string;reason:string}
export function cancelIntent(previous:CancelIntent|null,taskId:string,expectedStage:string,reason:string,newId:()=>string):CancelIntent{
 reason=reason.trim();if(reason.length<10)throw new Error('取消依据至少10字');
 if(previous){if(previous.taskId!==taskId||previous.expectedStage!==expectedStage||previous.reason!==reason)throw new Error('已有结果待核对的取消请求，请刷新原任务，不改参数重复申请');return previous;}
 return {requestId:newId(),taskId,expectedStage,reason};
}
