const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
let chromium;try{({chromium}=require('playwright'));}catch{({chromium}=require('../testdeps/node_modules/playwright'));}
const engine=fs.readFileSync(path.join(__dirname,'engine.js'),'utf8');
const fixture=fs.readFileSync(path.join(__dirname,'demo.html'),'utf8');
const defaults={message:'桌面消息\n你好 🔥',includeActive:true,includeRekindling:true,includeDormant:false,includeExpiring:false,intervalSeconds:10,maxRecipients:100};
let browser;const tests=[];const test=(name,fn)=>tests.push([name,fn]);
async function make(options={}){
 const ctx=await browser.newContext({viewport:{width:1300,height:850}});
 await ctx.route('**/*',r=>r.fulfill(r.request().url().includes('/chat/')?{contentType:'text/html',body:fixture}:{contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" fill="red"/></svg>'}));
 const page=await ctx.newPage();await page.goto('https://www.douyin.com/chat/');
 await page.evaluate(options=>{
  window.events=[];window.disk={config:{},records:{}};window.phase='';
  const R=Date,start=R.now(),base=new R().setHours(12,0,0,0),factor=50;
  window.Date=class extends R{constructor(...a){super(...(a.length?a:[base+(R.now()-start)*factor]));}static now(){return base+(R.now()-start)*factor;}};
  const timeout=setTimeout.bind(window);window.setTimeout=(fn,ms,...args)=>timeout(fn,Math.max(1,ms/factor),...args);
  window.chrome.webview={postMessage(envelope){
   window.events.push(JSON.parse(JSON.stringify(envelope)));
   if(envelope.type!=='persist')return;
   const records=Object.values(envelope.data.records||{});const outcome=records.length?records[records.length-1].outcome:'config';
   if(options.drop===outcome)return;
   timeout(()=>{
    if(options.reject===outcome){window.__SparkDesktop.ack(envelope.id,false,'模拟磁盘保存失败');return;}
    window.disk=JSON.parse(JSON.stringify(envelope.data));window.__SparkDesktop.ack(envelope.id,true,'');
   },options.delay||1);
  }};
 },options);
 const boot={session:'isolated-test',state:{config:defaults,records:{}}};
 await page.addScriptTag({content:engine.replace('__SPARK_BOOT__',JSON.stringify(boot))});
 return{page,ctx};
}
async function run(page,mode='send',config=defaults,exclude=[],selected=[]){
 await page.evaluate(({mode,config,exclude,selected})=>{window.events=[];window.__SparkDesktop.start(mode,config,exclude,selected);},{mode,config,exclude,selected});
 await page.waitForFunction(()=>events.some(e=>e.type==='busy'&&e.data.value===false),null,{timeout:10000});
 return page.evaluate(()=>({sent,events,disk}));
}
test('desktop engine exposes only desktop commands and no userscript UI',async()=>{
 const{page,ctx}=await make();assert.deepEqual(await page.evaluate(()=>Object.keys(__SparkDesktop)),['start','loadEmoji','stop','ack']);assert.equal(await page.locator('#dy-spark-desktop-panel').count(),0);await ctx.close();
});
test('scan forwards matched friends to native panel without sending',async()=>{
 const{page,ctx}=await make();const r=await run(page,'scan');assert.equal(r.sent.length,0);assert.deepEqual(r.events.find(e=>e.type==='preview').data.map(x=>x.name),['阿青','阿白']);await ctx.close();
});
test('desktop custom messages are delivered and recorded',async()=>{
 const{page,ctx}=await make();const r=await run(page);assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.text===defaults.message));assert.equal(Object.keys(r.disk.records).length,2);await ctx.close();
});
test('prepared-record write failure prevents sending',async()=>{
 const{page,ctx}=await make({reject:'prepared'});const r=await run(page);assert.equal(r.sent.length,0);assert.ok(r.events.some(e=>e.type==='status'&&e.data.text.includes('保存失败')));await ctx.close();
});
test('missing durable acknowledgement times out without a send',async()=>{
 const{page,ctx}=await make({drop:'prepared'});const r=await run(page);assert.equal(r.sent.length,0);assert.ok(r.events.some(e=>e.type==='status'&&e.data.text.includes('没有确认保存')));await ctx.close();
});
test('post-click record failure stops before a second recipient',async()=>{
 const{page,ctx}=await make({reject:'clicked'});const r=await run(page);assert.equal(r.sent.length,1);assert.ok(r.events.some(e=>e.type==='status'&&e.data.text.includes('保存失败')));await ctx.close();
});
test('delayed disk acknowledgements preserve send ordering',async()=>{
 const{page,ctx}=await make({delay:30});const r=await run(page);assert.equal(r.sent.length,2);const outcomes=r.events.filter(e=>e.type==='persist').map(e=>Object.values(e.data.records||{}).map(v=>v.outcome));assert.ok(outcomes.some(o=>o.includes('prepared')));await ctx.close();
});
test('native exclusions survive scanning',async()=>{
 const{page,ctx}=await make();const scan=await run(page,'scan');const first=scan.events.find(e=>e.type==='preview').data[0].identity;const r=await run(page,'send',defaults,[first]);assert.deepEqual(r.sent.map(x=>x.name),['阿白']);await ctx.close();
});
test('native stop cancels the remaining batch',async()=>{
 const{page,ctx}=await make();await page.evaluate(config=>__SparkDesktop.start('send',config,[]),{...defaults,intervalSeconds:300});await page.waitForFunction(()=>sent.length===1);await page.evaluate(()=>__SparkDesktop.stop());await page.waitForFunction(()=>events.some(e=>e.type==='busy'&&!e.data.value));assert.equal(await page.evaluate(()=>sent.length),1);await ctx.close();
});
test('duplicate native command cannot run a second batch concurrently',async()=>{
 const{page,ctx}=await make({delay:20});const r=await page.evaluate(config=>[__SparkDesktop.start('send',config,[]),__SparkDesktop.start('send',config,[])],defaults);assert.deepEqual(r,[true,false]);await page.waitForFunction(()=>events.some(e=>e.type==='busy'&&!e.data.value));assert.equal(await page.evaluate(()=>sent.length),2);await ctx.close();
});
const api=require('./engine.js');
const gray='https://p3.douyinpic.com/aweme-client-static-resource/chat_days_disable_3140%403x.png~tplv-obj.image';
test('gray icon with days is dormant rather than ongoing recovery',()=>assert.equal(api.classifyEvidence({labels:['15'],images:[gray]}).state,'dormant'));
test('unlit and recoverable labels enter dormant group',()=>{for(const t of ['尚未点亮','未点燃','可重燃','点击重燃','火花已熄灭'])assert.equal(api.classifyEvidence({labels:[t]}).state,'dormant',t);});
test('countdown expiry takes priority over gray icons',()=>{for(const t of ['2天后消失','火花将在3天后消失','即将熄灭','剩余2小时','还有3天将消失'])assert.equal(api.classifyEvidence({labels:[t],images:[gray],labelStyle:true}).state,'expiring',t);});
test('normal fire day number is not expiry countdown',()=>assert.notEqual(api.classifyEvidence({labels:['15'],images:[gray]}).state,'expiring'));
test('already disappeared remains excluded by automatic filters',()=>assert.equal(api.classifyEvidence({labels:['火花已消失'],images:[gray]}).state,'inactive'));
test('old config defaults include new status categories',()=>{const c=api.validateConfig({message:'hi',includeActive:true,includeRekindling:true,intervalSeconds:15,maxRecipients:100});assert.equal(c.includeDormant,true);assert.equal(c.includeExpiring,true);});
test('manual config works without any fire filters',()=>assert.equal(api.validateConfig({...defaults,includeActive:false,includeRekindling:false,manualSelection:true}).manualSelection,true));
test('automatic scan includes active recovery gray countdown and unlit',async()=>{
 const{page,ctx}=await make();const r=await run(page,'scan',{...defaults,includeDormant:true,includeExpiring:true});const people=r.events.find(e=>e.type==='preview').data;
 assert.deepEqual(people.map(x=>x.state),['active','rekindling','dormant','expiring','dormant']);assert.equal(r.sent.length,0);await ctx.close();
});
test('automatic send includes dormant and expiring friends',async()=>{
 const{page,ctx}=await make();const r=await run(page,'send',{...defaults,includeDormant:true,includeExpiring:true});assert.deepEqual(r.sent.map(x=>x.name),['阿青','阿白','灰色火花','即将消失的好友','尚未点燃的好友']);await ctx.close();
});
test('manual scan includes non-fire chats but excludes groups',async()=>{
 const{page,ctx}=await make();const r=await run(page,'scan',{...defaults,manualSelection:true});const people=r.events.find(e=>e.type==='preview').data;assert.equal(people.length,6);assert.ok(people.some(x=>x.name==='普通好友'&&x.state==='none'));assert.ok(!people.some(x=>x.name==='群聊'));await ctx.close();
});
test('manual mode without selection sends nobody',async()=>{
 const{page,ctx}=await make();const r=await run(page,'send',{...defaults,manualSelection:true});assert.equal(r.sent.length,0);assert.ok(r.events.some(e=>e.type==='status'&&e.data.text.includes('勾选至少一位')));await ctx.close();
});
test('manual allowlist sends only the chosen non-fire friend',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const scan=await run(page,'scan',cfg);const id=scan.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友').identity;
 const r=await run(page,'send',cfg,[],[id]);assert.deepEqual(r.sent.map(x=>x.name),['普通好友']);const second=await run(page,'send',cfg,[],[id]);assert.equal(second.sent.length,1);await ctx.close();
});
test('unknown manual recipient cannot fall back to all recipients',async()=>{
 const{page,ctx}=await make();const r=await run(page,'send',{...defaults,manualSelection:true},[],['not-in-list']);assert.equal(r.sent.length,0);await ctx.close();
});
test('manual send still rejects chat header mismatch',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const scan=await run(page,'scan',cfg);const id=scan.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友').identity;await page.evaluate(()=>options.wrongHeader=true);
 const r=await run(page,'send',cfg,[],[id]);assert.equal(r.sent.length,0);assert.ok(r.events.some(e=>e.type==='status'&&e.data.text.includes('身份无法核对')));await ctx.close();
});
test('manual send preserves drafts',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const scan=await run(page,'scan',cfg);const id=scan.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友').identity;await page.evaluate(()=>people.find(x=>x.name==='普通好友').draft='原有草稿');const r=await run(page,'send',cfg,[],[id]);assert.equal(r.sent.length,0);await ctx.close();
});
const now=new Date(2026,10,10,12,0).getTime();
test('official recent labels resolve within thirty days',()=>{for(const text of ['刚刚','10分钟前','2小时前','昨天 20:00','前天','周一','11/05','2026/11/01'])assert.equal(api.contactRecency([text],now).recentContact,'recent',text);});
test('old dated contact is stale',()=>assert.equal(api.contactRecency(['2025/01/01'],now).recentContact,'stale'));
test('thirty-day date-only boundary is unknown rather than guessed',()=>assert.equal(api.contactRecency(['2026/10/11'],now).recentContact,'unknown'));
test('exact timestamp at cutoff is recent',()=>assert.equal(api.contactRecency([String(now-30*86400000)],now).recentContact,'recent'));
test('exact timestamp beyond cutoff is stale',()=>assert.equal(api.contactRecency([String(now-30*86400000-1)],now).recentContact,'stale'));
test('unknown invalid future time stays unselected',()=>{for(const value of ['未知','13/40','2026/02/30','2027/01/01','99:99'])assert.equal(api.contactRecency([value],now).recentContact,'unknown',value);});
test('yearless future date does not infer last year',()=>assert.equal(api.contactRecency(['12/31'],now).recentContact,'unknown'));
test('ISO and seconds timestamps are parsed consistently',()=>{assert.equal(api.contactRecency([new Date(now-86400000).toISOString()],now).recentContact,'recent');assert.equal(api.contactRecency([String((now-86400000)/1000)],now).recentContact,'recent');});
test('explicit non-mutual signal overrides streak inference',()=>assert.equal(api.relationshipEvidence(['已关注'],true).mutualFollow,'no'));
test('single-sided following is not mutual',()=>{for(const s of ['已关注','关注','回关','未互关','已请求'])assert.equal(api.relationshipEvidence([s]).mutualFollow,'no');});
test('mutual label and official streak are recognized',()=>{assert.equal(api.relationshipEvidence(['互相关注']).mutualFollow,'yes');assert.equal(api.relationshipEvidence([],true).mutualFollow,'yes');assert.equal(api.relationshipEvidence([]).mutualFollow,'unknown');});
test('default selection uses AND for recent and mutual',()=>{
 assert.equal(api.defaultSelection({recentContact:'recent',mutualFollow:'yes'}).defaultSelected,true);
 for(const recentContact of ['recent','stale','unknown'])for(const mutualFollow of ['yes','no','unknown'])if(recentContact!=='recent'||mutualFollow!=='yes')assert.equal(api.defaultSelection({recentContact,mutualFollow}).defaultSelected,false);
});
test('manual preview supplies eligibility and unchecked reasons',async()=>{
 const{page,ctx}=await make();const r=await run(page,'scan',{...defaults,manualSelection:true});const p=r.events.find(e=>e.type==='preview').data;
 assert.deepEqual(p.filter(x=>x.defaultSelected).map(x=>x.name),['阿青','灰色火花','即将消失的好友']);
 assert.match(p.find(x=>x.name==='阿白').defaultReason,/超过30天/);assert.match(p.find(x=>x.name==='普通好友').defaultReason,/未互相关注/);assert.match(p.find(x=>x.name==='尚未点燃的好友').defaultReason,/待确认/);await ctx.close();
});
test('official conversation timestamp and not message preview drives defaults',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>{const rows=document.querySelectorAll('[data-e2e="conversation-item"]');rows[0].querySelector('small').textContent='2020/01/01 未互关';});const r=await run(page,'scan',{...defaults,manualSelection:true});assert.equal(r.events.find(e=>e.type==='preview').data[0].defaultSelected,true);await ctx.close();
});
test('manual scan resolves scoped header mutual status without clicking follow',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>{const row=document.querySelectorAll('[data-e2e="conversation-item"]')[2];row.removeAttribute('data-mutual-follow');people[2].headerFollow='互相关注';});const r=await run(page,'scan',{...defaults,manualSelection:true});const p=r.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友');assert.equal(p.defaultSelected,true);assert.equal(r.sent.length,0);await ctx.close();
});
test('scan returns conversation list to top',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>{document.querySelector('#scroller').style.height='160px';document.querySelector('#scroller').scrollTop=250;});assert.ok(await page.evaluate(()=>document.querySelector('#scroller').scrollTop)>0);await run(page,'scan',{...defaults,manualSelection:true});assert.equal(await page.evaluate(()=>document.querySelector('#scroller').scrollTop),0);await ctx.close();
});
test('manual override permits old non-mutual friend without sending others',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const preview=await run(page,'scan',cfg);const person=preview.events.find(e=>e.type==='preview').data.find(x=>x.name==='阿白');assert.equal(person.defaultSelected,false);const r=await run(page,'send',cfg,[],[person.identity]);assert.deepEqual(r.sent.map(x=>x.name),['阿白']);await ctx.close();
});

async function addProfileCardFixture(page,label,title='普通好友'){
 await page.evaluate(({label,title})=>{
  const row=document.querySelectorAll('[data-e2e="conversation-item"]')[2];row.removeAttribute('data-mutual-follow');window.followClicks=0;window.profileOpens=0;
  const user=document.createElement('div');user.className='RightPanelHeaderuser';const trigger=document.createElement('button');trigger.setAttribute('data-apm-action','个人页卡片');trigger.textContent='资料';user.appendChild(trigger);document.querySelector('.RightPanelHeadertitleContainer').appendChild(user);
  trigger.onclick=()=>{profileOpens++;const card=document.createElement('div');card.className='UserProfileCardcontentbox';card.style.cssText='position:fixed;top:50px;left:500px;z-index:50;background:#222;padding:20px';const name=document.createElement('div');name.className='UserProfileCardcontenttitle';name.textContent=title;const follow=document.createElement('button');follow.className='UserProfileCardcontentleftButton';follow.textContent=label;follow.onclick=()=>followClicks++;card.append(name,follow);document.body.appendChild(card);};
  row.addEventListener('mousedown',()=>document.querySelectorAll('.UserProfileCardcontentbox').forEach(x=>x.remove()));
 },{label,title});
}
test('profile card confirms mutual status without any follow action',async()=>{
 const{page,ctx}=await make();await addProfileCardFixture(page,'互相关注');const r=await run(page,'scan',{...defaults,manualSelection:true});const p=r.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友');assert.equal(p.defaultSelected,true);assert.equal(await page.evaluate(()=>profileOpens),1);assert.equal(await page.evaluate(()=>followClicks),0);assert.equal(r.sent.length,0);assert.equal(await page.locator('.UserProfileCardcontentbox').count(),0);await ctx.close();
});
test('profile card non-mutual relationship remains unchecked',async()=>{
 const{page,ctx}=await make();await addProfileCardFixture(page,'回关');const r=await run(page,'scan',{...defaults,manualSelection:true});const p=r.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友');assert.equal(p.defaultSelected,false);assert.equal(p.mutualFollow,'no');assert.equal(await page.evaluate(()=>followClicks),0);await ctx.close();
});
test('mismatched profile card cannot confirm another friend',async()=>{
 const{page,ctx}=await make();await addProfileCardFixture(page,'互相关注','错误好友');const r=await run(page,'scan',{...defaults,manualSelection:true});const p=r.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友');assert.equal(p.defaultSelected,false);assert.equal(p.mutualFollow,'unknown');await ctx.close();
});
test('missing relationship evidence remains unchecked',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>document.querySelectorAll('[data-e2e="conversation-item"]')[2].removeAttribute('data-mutual-follow'));const r=await run(page,'scan',{...defaults,manualSelection:true});const p=r.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友');assert.equal(p.defaultSelected,false);assert.equal(p.mutualFollow,'unknown');await ctx.close();
});


async function catalog(page,kind){
 await page.evaluate(()=>document.querySelector('[data-e2e="conversation-item"]').dispatchEvent(new MouseEvent('mousedown',{bubbles:true})));
 await page.evaluate(kind=>{window.events=[];window.__SparkDesktop.loadEmoji(kind);},kind);
 await page.waitForFunction(()=>events.some(e=>e.type==='busy'&&!e.data.value));
 return page.evaluate(()=>events.find(e=>e.type==='emojiCatalog').data);
}
test('built-in emoji library reads without inserting or sending',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'builtin');assert.equal(c.items.length,3);assert.deepEqual(c.items.map(x=>x.token),['[微笑]','[捂脸]','[比心]']);assert.deepEqual(c.items.map(x=>x.name),c.items.map(x=>x.token));assert.equal(await page.evaluate(()=>emojiClicks),0);assert.equal(await page.evaluate(()=>sent.length),0);assert.equal(await page.locator('.componentsemojiemojiPanel').count(),0);await ctx.close();
});
test('favorite emoji library reads without clicking any sticker',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');assert.equal(c.items.length,3);assert.equal(await page.evaluate(()=>emojiClicks),0);assert.equal(await page.evaluate(()=>sent.length),0);await ctx.close();
});
test('text plus builtin emoji uses official insertion then sends',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'builtin');const composed=defaults.message+c.items[1].token;const r=await run(page,'send',{...defaults,message:composed,contentKind:'builtin'});assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.kind==='builtin'&&x.text===composed&&x.emoji.endsWith('small-1.png')));await ctx.close();
});
test('builtin emoji can send without any text',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'builtin');const r=await run(page,'send',{...defaults,message:c.items[0].token,contentKind:'builtin'});assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.kind==='builtin'&&x.text===c.items[0].token));await ctx.close();
});
test('favorite sticker sends native sticker rather than URL or extra text',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');const r=await run(page,'send',{...defaults,contentKind:'custom',emoji:c.items[2]});assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.kind==='custom'&&x.text===''&&x.emoji.endsWith('custom-2.png')));assert.equal(Object.keys(r.disk.records).length,2);await ctx.close();
});
test('sticker missing from current account stops without clicking',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');const r=await run(page,'send',{...defaults,contentKind:'custom',emoji:{...c.items[0],key:'/nonexistent.png'}});assert.equal(r.sent.length,0);assert.equal(await page.evaluate(()=>emojiClicks),0);await ctx.close();
});
test('sticker write failure prevents immediate send click',async()=>{
 const{page,ctx}=await make({reject:'prepared'});const c=await catalog(page,'custom');const r=await run(page,'send',{...defaults,contentKind:'custom',emoji:c.items[0]});assert.equal(r.sent.length,0);assert.equal(await page.evaluate(()=>emojiClicks),0);await ctx.close();
});
test('existing image draft is preserved instead of sending a sticker',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');await page.evaluate(()=>document.querySelectorAll('[data-e2e="conversation-item"]').forEach(row=>row.addEventListener('mousedown',()=>{const img=document.createElement('img');img.src='https://p3.douyinpic.com/existing-draft.png';document.querySelector('.messageEditorinputArea').appendChild(img);})));const r=await run(page,'send',{...defaults,contentKind:'custom',emoji:c.items[0]});assert.equal(r.sent.length,0);await ctx.close();
});
test('sticker uncertain click stops later recipients and records attempt',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');await page.evaluate(()=>window.keepEmojiPanel=true);const r=await run(page,'send',{...defaults,contentKind:'custom',emoji:c.items[0]});assert.equal(r.sent.length,1);assert.equal(Object.values(r.disk.records)[0].outcome,'uncertain');await ctx.close();
});
test('invalid content type and mismatched emoji metadata are rejected',()=>{
 assert.throws(()=>api.validateConfig({...defaults,contentKind:'arbitrary'}));assert.throws(()=>api.validateConfig({...defaults,message:'',contentKind:'builtin'}));assert.throws(()=>api.validateConfig({...defaults,contentKind:'custom',emoji:{kind:'custom',key:'x',src:'javascript:alert(1)'}}));
});


test('scan works before choosing content or entering a message',async()=>{
 const{page,ctx}=await make();const r=await run(page,'scan',{...defaults,message:'',contentKind:'custom',emoji:null});assert.equal(r.sent.length,0);assert.equal(r.events.find(e=>e.type==='preview').data.length,2);await ctx.close();
});
test('sticker matching includes origin and query, not only common path',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');const samePath=c.items[0].key.replace('p3.douyinpic.com','example.com');const r=await run(page,'send',{...defaults,contentKind:'custom',emoji:{...c.items[0],key:samePath}});assert.equal(r.sent.length,0);await ctx.close();
});
test('favorite sticker daily history prevents a second send',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');const cfg={...defaults,contentKind:'custom',emoji:c.items[0]};await run(page,'send',cfg);const r=await run(page,'send',cfg);assert.equal(r.sent.length,2);await ctx.close();
});
test('loading emoji requires a verified one-to-one conversation',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>{window.events=[];window.__SparkDesktop.loadEmoji('custom');});await page.waitForFunction(()=>events.some(e=>e.type==='busy'&&!e.data.value));const r=await page.evaluate(()=>({sent,events,emojiClicks}));assert.equal(r.sent.length,0);assert.equal(r.emojiClicks,0);assert.match(r.events.find(e=>e.type==='emojiCatalog').data.error,/单聊/);await ctx.close();
});



test('official builtin names come from read-only preview not invented index labels',async()=>{
 const{page,ctx}=await make();assert.equal(await page.locator('.emojiSmallEmojismallEmojiItemIcon[alt]').count(),0);const c=await catalog(page,'builtin');assert.deepEqual(c.items.map(x=>x.token),['[微笑]','[捂脸]','[比心]']);assert.equal(await page.evaluate(()=>emojiClicks),0);assert.equal(await page.evaluate(()=>document.querySelector('.messageEditorinputArea').textContent),'');await ctx.close();
});
test('missing official names are skipped rather than converted into false tokens',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>window.hideBuiltinNames=true);const c=await catalog(page,'builtin');assert.equal(c.items.length,0);assert.equal(await page.evaluate(()=>emojiClicks),0);assert.equal(await page.evaluate(()=>sent.length),0);await ctx.close();
});
test('multiple repeated builtin tokens preserve exact mixed message order',async()=>{
 const{page,ctx}=await make();const message='你好[捂脸][捂脸]早安[微笑]\n[比心]再见';const r=await run(page,'send',{...defaults,message,contentKind:'builtin'});assert.equal(r.sent.length,2);for(const item of r.sent){assert.equal(item.text,message);assert.deepEqual(item.emojis,['[捂脸]','[捂脸]','[微笑]','[比心]']);}assert.equal(await page.evaluate(()=>emojiClicks),0);await ctx.close();
});
test('text mode also sends manually typed bracket emoji codes',async()=>{
 const{page,ctx}=await make();const message='[微笑]你好[捂脸]';const r=await run(page,'send',{...defaults,message,contentKind:'text'});assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.text===message));await ctx.close();
});
test('literal bracket text is preserved when editor does not render it',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>window.inlineEmojiParse=false);const message='你好[捂脸][自定义备注]';const r=await run(page,'send',{...defaults,message,contentKind:'builtin'});assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.text===message));await ctx.close();
});
test('stale single emoji metadata cannot append a hidden extra emoji',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'builtin');const message='[捂脸][捂脸]';const r=await run(page,'send',{...defaults,message,contentKind:'builtin',emoji:c.items[0]});assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.text===message&&x.emojis.length===2));await ctx.close();
});
test('token normalizer accepts exact names and rejects unsafe or guessed syntax',()=>{
 for(const x of ['[捂脸]','[微笑]','[OK]'])assert.equal(api.builtinToken(x),x);
 for(const x of ['捂脸','[]','[ ]','[[捂脸]]','[a\nb]','自带表情 1'])assert.equal(api.builtinToken(x),'');
 assert.equal(api.validateConfig({...defaults,message:'[捂脸][捂脸]',contentKind:'builtin'}).message,'[捂脸][捂脸]');
 assert.throws(()=>api.validateConfig({...defaults,message:'字'.repeat(301),contentKind:'builtin'}));
});
test('reordered inline emoji content is detected before sending',async()=>{
 const{page,ctx}=await make();await page.evaluate(()=>document.querySelector('.messageEditorinputArea').addEventListener('input',()=>{const el=document.querySelector('.messageEditorinputArea');const imgs=el.querySelectorAll('img');if(imgs.length>1){const first=imgs[0].parentElement,last=imgs[imgs.length-1].parentElement;el.insertBefore(last,first);}}));const r=await run(page,'send',{...defaults,message:'[微笑][捂脸]',contentKind:'builtin'});assert.equal(r.sent.length,0);assert.ok(r.events.some(x=>x.type==='status'&&x.data.text.includes('核对失败')));await ctx.close();
});

test('daily deduplication defaults on for new and legacy settings',()=>{assert.equal(api.DEFAULTS.dailyDedup,true);assert.equal(api.validateConfig(defaults).dailyDedup,true);});
test('explicitly disabled daily deduplication survives validation',()=>{assert.equal(api.validateConfig({...defaults,dailyDedup:false}).dailyDedup,false);assert.equal(api.validateConfig({...defaults,dailyDedup:true}).dailyDedup,true);});
test('disabled daily deduplication permits repeat automatic text batches',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,dailyDedup:false};await run(page,'send',cfg);const r=await run(page,'send',cfg);assert.equal(r.sent.length,4);assert.equal(r.disk.config.dailyDedup,false);assert.equal(Object.keys(r.disk.records).length,2);await ctx.close();
});
test('reenabled daily deduplication honors records collected while disabled',async()=>{
 const{page,ctx}=await make();await run(page,'send',{...defaults,dailyDedup:false});const r=await run(page,'send',{...defaults,dailyDedup:true});assert.equal(r.sent.length,2);await ctx.close();
});
test('disabled daily deduplication still restricts manual batches to selected friends',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true,dailyDedup:false};const scan=await run(page,'scan',cfg);const id=scan.events.find(x=>x.type==='preview').data[0].identity;await run(page,'send',cfg,[],[id]);const r=await run(page,'send',cfg,[],[id]);assert.equal(r.sent.length,2);assert.ok(r.sent.every(x=>x.name==='阿青'));await ctx.close();
});
test('disabled daily deduplication permits repeated builtin token messages',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,dailyDedup:false,contentKind:'builtin',message:'[捂脸][捂脸]'};await run(page,'send',cfg);const r=await run(page,'send',cfg);assert.equal(r.sent.length,4);assert.ok(r.sent.every(x=>x.text===cfg.message));await ctx.close();
});
test('disabled daily deduplication still requires durable prepared acknowledgement',async()=>{
 const{page,ctx}=await make({reject:'prepared'});const r=await run(page,'send',{...defaults,dailyDedup:false});assert.equal(r.sent.length,0);assert.ok(r.events.some(x=>x.type==='status'&&x.data.text.includes('保存失败')));await ctx.close();
});

test('disabled daily deduplication permits repeated favorite stickers',async()=>{
 const{page,ctx}=await make();const c=await catalog(page,'custom');const cfg={...defaults,dailyDedup:false,contentKind:'custom',emoji:c.items[0]};await run(page,'send',cfg);const r=await run(page,'send',cfg);assert.equal(r.sent.length,4);assert.ok(r.sent.every(x=>x.kind==='custom'));assert.equal(Object.keys(r.disk.records).length,2);await ctx.close();
});

test('completed manual batch reports final summary in native log',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const scan=await run(page,'scan',cfg);const id=scan.events.find(e=>e.type==='preview').data[0].identity;const r=await run(page,'send',cfg,[],[id]);assert.equal(r.sent.length,1);const summaries=r.events.filter(e=>e.type==='log'&&e.data.text.includes('发送任务完成'));assert.equal(summaries.length,1);assert.ok(summaries[0].data.text.includes('1 位'));await ctx.close();
});
test('daily skips are logged without claiming every checked friend was sent',async()=>{
 const{page,ctx}=await make();await run(page);const r=await run(page);assert.equal(r.sent.length,2);assert.ok(r.events.some(e=>e.type==='log'&&e.data.text.includes('本次未发送')&&e.data.text.includes('2 位')));assert.ok(!r.events.some(e=>e.type==='log'&&e.data.text.includes('发送任务完成')));await ctx.close();
});
test('preserved drafts produce partial summary rather than all-sent message',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const scan=await run(page,'scan',cfg);const id=scan.events.find(e=>e.type==='preview').data.find(x=>x.name==='普通好友').identity;await page.evaluate(()=>people.find(x=>x.name==='普通好友').draft='原有草稿');const r=await run(page,'send',cfg,[],[id]);assert.equal(r.sent.length,0);assert.ok(r.events.some(e=>e.type==='log'&&e.data.text.includes('其他跳过 1 位')));assert.ok(!r.events.some(e=>e.type==='log'&&e.data.text.includes('发送任务完成')));await ctx.close();
});
test('stopping mid-batch never reports full completion',async()=>{
 const{page,ctx}=await make();await page.evaluate(config=>__SparkDesktop.start('send',config,[]),{...defaults,intervalSeconds:300});await page.waitForFunction(()=>sent.length===1);await page.evaluate(()=>__SparkDesktop.stop());await page.waitForFunction(()=>events.some(e=>e.type==='busy'&&!e.data.value));assert.ok(!await page.evaluate(()=>events.some(e=>e.type==='log'&&e.data.text.includes('发送任务完成'))));await ctx.close();
});

test('disappeared checked friend is reported instead of claiming all were sent',async()=>{
 const{page,ctx}=await make();const cfg={...defaults,manualSelection:true};const scan=await run(page,'scan',cfg);const id=scan.events.find(e=>e.type==='preview').data[0].identity;const r=await run(page,'send',cfg,[],[id,'missing-selected-id']);assert.equal(r.sent.length,1);assert.ok(r.events.some(e=>e.type==='log'&&e.data.text.includes('当前列表未找到 1 位')));assert.ok(!r.events.some(e=>e.type==='log'&&e.data.text.includes('发送任务完成')));await ctx.close();
});

(async()=>{browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});let passed=0;try{for(const[name,fn]of tests){await fn();passed++;console.log('PASS '+name);}}finally{await browser.close();}fs.writeFileSync(path.join(__dirname,'engine-test-results.json'),JSON.stringify({version:'1.0.0',passed,total:tests.length,scope:'Isolated emoji reading/insertion/sticker sending, contact filters, desktop bridge and recipient selection tests; no real account messaging.'},null,2));console.log('All '+passed+' desktop bridge tests passed.');})().catch(e=>{console.error(e);process.exitCode=1;});
