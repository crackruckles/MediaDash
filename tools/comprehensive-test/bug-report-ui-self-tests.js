'use strict';
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const assert = require('node:assert/strict');
const {test} = require('node:test');
const base = path.resolve(__dirname, '../../Jellyfin.Plugin.MediaDash/Configuration/scripts');
const read = p => fs.readFileSync(path.join(base,p),'utf8');
const settings = read('tabs/settings-tab.js'), wizard = read('wizard.js'), page = read('page-controller.js');
function fn(source, name, next) { const start=source.indexOf('function '+name+'('); const end=source.indexOf('function '+next+'(',start); assert(start>=0&&end>start); return source.slice(start,end); }
test('BUG-29 malformed encoder has explicit null index',()=>{const c={window:{}}; vm.runInNewContext(read('settings-mapper.js'),c); assert.equal(c.window.MediaDash.encoderValueToConfig('').gpuIndex,null);});
test('BUG-61 hydrated bitrate survives sub-1080 slider rounding and resolution changes',()=>{const els={'#mdMaxRes':{value:'720'},'#mdMaxBitrate':{},'#mdMaxBitrateVal':{}}; const c={deps:{view:{querySelector:s=>els[s]||null},fmt:(_,v)=>v,STR:{}},RES_PIXEL_FACTOR:{720:.4444,480:.1975},currentBitrateFactor:1}; vm.createContext(c); vm.runInContext(fn(settings,'bitrateFactor','setBitrateSliderForResolution')+fn(settings,'setBitrateSliderForResolution','bitrateBaseFromSlider')+fn(settings,'bitrateBaseFromSlider','modeOptions').split('var SLIDERS')[0],c);c.setBitrateSliderForResolution(8);assert.equal(c.bitrateBaseFromSlider(),8);els['#mdMaxRes'].value='480';c.setBitrateSliderForResolution(c.bitrateBaseFromSlider());assert.equal(c.bitrateBaseFromSlider(),8);});
test('BUG-59 filtering drops invisible selections',()=>{assert.match(read('tabs/files-tab.js'),/fileSelection\.forEach[\s\S]*?fileSelection\.delete/);});
test('BUG-26 reopen refreshes config and resets wizard step',()=>{assert.match(wizard,/function reopen\(/);assert.match(page,/wizardTab\.reopen\(/);});
test('BUG-27 programmatic mode and chip edits mark dirty',()=>{assert.match(fn(settings,'setTaskMode','syncModeSegments'),/markSettingsDirty\(/);assert.match(fn(settings,'notifyLangChange','initTaskCardsAfterLoad'),/markSettingsDirty\(/);});
test('BUG-28 dirty library lists retain checks',()=>{assert.match(fn(settings,'loadSettings','loadStaleExcludedLibraries'),/dirtyBeforeApply[\s\S]*?mdLib/);});
test('BUG-30 save errors are handled and stale success cleared',()=>{assert.match(fn(settings,'saveSettings','saveSettingsAsync'),/catch/);assert.match(fn(settings,'saveSettingsAsync','validateAndSaveConfig'),/mdSaveMsg.*textContent = ''/);});
test('BUG-31 wizard requires explicit risky-change consent',()=>{assert.match(fn(wizard,'saveThen','gateOnBinDiskInfo'),/confirmAuto/);assert.match(fn(wizard,'saveThen','gateOnBinDiskInfo'),/confirmPermanent/);});
test('BUG-58 format fixer is reorderable',()=>{assert.match(settings.slice(settings.indexOf('var DEFAULT_FIXER_ORDER'),settings.indexOf('var FIXER_HARD_CONSTRAINTS')),/'FormatCompatibility'/);});
test('BUG-60 force save uses current form through normal consent path',()=>{assert.match(fn(settings,'showBinDiskWarning','bindSettings'),/saveSettingsAsync\(true\)/);});
test('BUG-62 revisit restores transient controls',()=>{assert.match(page,/showToken = lifetime.start\(\);[\s\S]*?mdBinEmptyBar/);});
test('BUG-63 saves and back/skip are guarded while saving',()=>{assert.match(fn(wizard,'saveThen','gateOnBinDiskInfo'),/if \(saving\) return/);assert.match(fn(wizard,'saveThen','gateOnBinDiskInfo'),/saving = true/);assert.match(wizard,/finally\(function \(\) \{ saving = false/);});
test('BUG-64 schedule handler registered once',()=>{assert.equal((page.match(/querySelector\('#mdResetSchedule'\)\.addEventListener/g)||[]).length,1);});
test('BUG-65 bin card uses shared expand helper',()=>{assert.match(fn(settings,'showBinDiskWarning','bindSettings'),/setSetCardCollapsed\(card, false\)/);});
test('BUG-66 bulk calls chunk and report errors',()=>{assert.match(fn(page,'bulkIssueAction','errorMessage'),/slice\(offset, offset \+ 500/);assert.match(read('tabs/issues-tab.js'),/bulkIssueAction\(opt.ids, 'Dismiss'\)[\s\S]*?catch/);});
test('BUG-67 mutations use normalized ajax',()=>{assert.doesNotMatch(settings,/ApiClient\.ajax\(/);assert.doesNotMatch(read('tabs/recycle-tab.js'),/ApiClient\.ajax\(/);});
test('BUG-68 redownload filenames support both separators',()=>{assert.match(read('tabs/overview-tab.js'),/w.Path.split\(\/\[\\\\\/\]\/\)/);});
test('BUG-06 retention cannot save zero or negative',()=>{assert.match(settings,/RecycleBinRetentionDays = Math.max\(1,/);});
function wizardSaveEnvironment(confirm = () => true, update = () => Promise.resolve({})) {
    const msg={textContent:'',classList:{remove(){},add(){}}};
    const c={saving:false,total:18,deps:{view:{querySelector:()=>msg,querySelectorAll:()=>[]},api:{configuration:()=>Promise.resolve({TranscodeFixMode:'ManualApprove',TranscodeDisposal:'RecycleBin'})},PLUGIN_ID:'test',STR:{confirmAuto:'auto',confirmPermanent:'permanent'}},window:{confirm},ApiClient:{updatePluginConfiguration:update},Dashboard:{processPluginConfigurationUpdateResult(){}},wizApplyStepToConfig(_, config){config.TranscodeFixMode='Automatic';config.TranscodeDisposal='Permanent';},gateOnBinDiskInfo:()=>Promise.resolve(true)};
    vm.createContext(c);vm.runInContext(fn(wizard,'saveThen','gateOnBinDiskInfo'),c);return c;
}
const settle=()=>new Promise(resolve=>setImmediate(resolve));
test('BUG-63 actual rapid Continue calls persist and advance exactly once',async()=>{let writes=0,steps=0,finish;const c=wizardSaveEnvironment(()=>true,()=>{writes++;return new Promise(resolve=>finish=resolve);});c.saveThen(4,()=>steps++);c.saveThen(4,()=>steps++);await settle();assert.equal(writes,1);assert.equal(steps,0);finish({});await settle();assert.equal(steps,1);assert.equal(c.saving,false);});
for(const refuse of ['auto','permanent'])test('BUG-31 declining '+refuse+' causes no persistence and no advance',async()=>{const prompts=[];let writes=0;const c=wizardSaveEnvironment(prompt=>{prompts.push(prompt);return prompt!==refuse;},()=>{writes++;return Promise.resolve({});});c.saveThen(4,()=>assert.fail('advanced after refusal'));await settle();assert.equal(writes,0);assert.equal(c.saving,false);assert(prompts.includes(refuse));});
test('BUG-63 a failed configuration fetch releases save lock and shows error',async()=>{const c=wizardSaveEnvironment();c.deps.api.configuration=()=>Promise.reject(new Error('offline'));c.saveThen(4,()=>assert.fail());await settle();assert.equal(c.saving,false);assert.match(c.deps.view.querySelector().textContent,/Could not save/);});
test('BUG-29 undetected GPU option preserves stored index',()=>{const select={innerHTML:'',value:''};const c={deps:{view:{querySelector:()=>select},lastStatus:{System:{Gpus:[]}},esc:String,encoderConfigToValue:(_,n)=>String(n)}};vm.createContext(c);vm.runInContext(fn(settings,'populateEncoderOptions','readFormIntoConfig'),c);c.populateEncoderOptions(true,3);assert.match(select.innerHTML,/value="3"/);assert.equal(select.value,'3');});
test('BUG-66 actual bulk helper sends bounded sequential batches and sums responses',async()=>{const calls=[];const c={api:{ajax:options=>{const b=JSON.parse(options.data);calls.push(b);return Promise.resolve(b.Ids.length);}},ApiClient:{getUrl:s=>s}};vm.createContext(c);vm.runInContext(fn(page,'bulkIssueAction','errorMessage'),c);const ids=Array.from({length:1001},(_,i)=>i);assert.equal(await c.bulkIssueAction(ids,'Dismiss'),1001);assert.deepEqual(calls.map(c=>c.Ids.length),[500,500,1]);assert.equal(calls[2].Ids[0],1000);});
test('BUG-27 wizard chip changes do not dirty Settings while settings chips do',()=>{let dirty=0;const c={markSettingsDirty(){dirty++;},updateSummary(){}};vm.createContext(c);vm.runInContext(fn(settings,'notifyLangChange','initTaskCardsAfterLoad'),c);c.notifyLangChange({closest:()=>null});assert.equal(dirty,0);c.notifyLangChange({closest:()=>({})});assert.equal(dirty,1);});
test('BUG-30 form submit handles a rejected persistence promise',async()=>{let handled;const c={saveSettingsAsync:()=>Promise.reject(new Error('offline')),showSaveError:e=>handled=e.message};vm.createContext(c);vm.runInContext(fn(settings,'saveSettings','showSaveError'),c);assert.equal(c.saveSettings({preventDefault(){}}),false);await settle();assert.equal(handled,'offline');});
test('BUG-68 displayed Windows filename is basename only',()=>{const line=read('tabs/overview-tab.js').match(/w.Path.split\([^\n]*?\.pop\(\)/)[0];const c={w:{Path:'C:\\owned\\movie.mkv'}};assert.equal(vm.runInNewContext(line,c),'movie.mkv');});
test('BUG-07 cleanup preview messages never claim deletion or reclamation', () => {
    for (const source of [page, wizard]) {
        const lines = source.split('\n').filter(line => /textContent = .*WasDryRun/.test(line));
        assert(lines.length > 0);
        for (const line of lines) {
            const context = {data:{WasDryRun:true},r:{WasDryRun:true},count:3,resultEl:{},msg:{}};
            vm.runInNewContext(line.trim(), context);
            const message = context.resultEl.textContent || context.msg.textContent;
            assert.match(message, /Dry-run preview/);
            assert.match(message, /No files were deleted/);
            assert.doesNotMatch(message, /Reclaimed/);
        }
    }
});
