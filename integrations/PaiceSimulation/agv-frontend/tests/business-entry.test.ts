import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

test('main business simulation is the default entry and explicit native remains available', () => {
  const source = readFileSync(new URL('../src/views/SimulationHubView.vue', import.meta.url), 'utf8')
  assert.match(source, /route.query.mode \|\| 'main'/)
  assert.match(source, /name: 'native'/)
})
test('business setup supports scheme selection and single call without native tasksets', () => {
  const source = readFileSync(new URL('../src/components/BusinessScenarioSetup.vue', import.meta.url), 'utf8')
  assert.match(source, /scheme-start/)
  assert.match(source, /initialSite/)
  assert.match(source, /stationId/)
  assert.doesNotMatch(source, /createTaskSet/)
})
test('task polling waits for an operating isolated backend', () => {
 const source = readFileSync(new URL('../src/views/DispatchIntegrationView.vue', import.meta.url), 'utf8')
 assert.match(source, /const shouldReadDetails = next.running === true &&/)
})
