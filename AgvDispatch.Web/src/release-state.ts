declare const __AGV_RELEASE_ID__: string;
export const frontendReleaseId=typeof __AGV_RELEASE_ID__==='string'?__AGV_RELEASE_ID__:'development';
export interface AdapterIdentity {informationalVersion:string;sha256:string}
export function releaseAgreement(frontend:string,adapter:AdapterIdentity|null,backend:string|undefined):{status:'Pass'|'Blocked'|'Unknown';reason:string}{
 if(!adapter||!backend)return{status:'Unknown',reason:'尚未取得接入层或主服务的构建批次，无法确认同批发布。'};
 const versions=[frontend,adapter.informationalVersion,backend];
 if(!versions.some(v=>v.startsWith('commissioning-')))return{status:'Unknown',reason:'当前为开发或独立构建，未绑定统一候选发布批次。'};
 if(!versions.every(v=>v===frontend))return{status:'Blocked',reason:'页面、WebHost与主服务批次不一致；请检查端口、管道及部署目录，不沿用旧页面。'};
 return{status:'Pass',reason:'页面、WebHost与主服务声明为同一候选批次；文件完整性另按发布清单核对，现场配置及验收不由此证明。'};
}
