import test from 'node:test';
import assert from 'node:assert/strict';
import {releaseAgreement} from '../src/release-state.ts';
test('同一候选批次才可确认页面接入层与后台一致',()=>{
 const id='commissioning-20261017-test';const adapter={informationalVersion:id,sha256:'a'};
 assert.equal(releaseAgreement(id,adapter,id).status,'Pass');
 assert.equal(releaseAgreement(id,adapter,'commissioning-older').status,'Blocked');
 assert.equal(releaseAgreement('development',adapter,id).status,'Blocked');
 assert.equal(releaseAgreement(id,null,id).status,'Unknown');
 assert.equal(releaseAgreement('development',{...adapter,informationalVersion:'1.0.0'},'1.0.0').status,'Unknown');
});
