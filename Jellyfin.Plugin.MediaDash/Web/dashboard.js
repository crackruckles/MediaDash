// MediaDash  Jellyfin plugin controller
// Exported function is called by Jellyfin with (view, params) after DOM insertion.
export default function(view, params) {

var BASE = '/mediadash/api';

// Scope g() to the view element so it only finds elements in our page
function g(id){ return view.querySelector('#' + id); }
function setText(id,v){ var el=g(id); if(el) el.textContent=v; }

function norm(obj){
  if(Array.isArray(obj)) return obj.map(norm);
  if(obj&&typeof obj==='object'){
    var out={};
    Object.keys(obj).forEach(function(k){
      var ck=k.charAt(0).toLowerCase()+k.slice(1);
      out[ck]=norm(obj[k]);
    });
    return out;
  }
  return obj;
}

function ah(){
  if(window.ApiClient){
    return{'X-Emby-Authorization':
      'MediaBrowser Client="MediaDash", Device="Jellyfin Web", DeviceId="mediadash-plugin-1", Version="1.0.0", Token="'+ApiClient.accessToken()+'"'
    };
  }
  return{};
}

async function api(p,o){
  o=o||{};
  var h=Object.assign({'Content-Type':'application/json','Accept':'application/json'},ah(),o.headers||{});
  try{
    var r=await fetch(BASE+p,Object.assign({},o,{headers:h}));
    if(r.status===401||r.status===403)return{_auth_error:true};
    return r.ok?norm(await r.json()):{};
  } catch(e){console.warn('MediaDash API error',p,e);return{};}
}

function toast(m,c){
  var t=g('toast');
  if(!t)return;
  t.textContent=m;
  t.style.borderColor=c==='g'?'#4caf50':c==='r'?'#f44336':'#00a4dc';
  t.classList.add('on');
  setTimeout(function(){t.classList.remove('on');},2800);
}

// Singleton guard  only run once per page, even if script loads multiple times
if(window.__mediaDashLoaded) return;
window.__mediaDashLoaded = true;
var PLUGIN_ID='4a5c8f2e-1b3d-4e6f-9a2c-7d8e0f1b3c5a';

// Jellyfin's JSON serializer returns PascalCase  normalise to camelCase
function norm(obj){
  if(Array.isArray(obj)) return obj.map(norm);
  if(obj&&typeof obj==='object'){
    var out={};
    Object.keys(obj).forEach(function(k){
      var ck=k.charAt(0).toLowerCase()+k.slice(1);
      out[ck]=norm(obj[k]);
    });
    return out;
  }
  return obj;
}

//  Auth 
function ah(){
  if(window.ApiClient){
    var t=ApiClient.accessToken()||'';
    var sid=typeof ApiClient.serverId==='function'?ApiClient.serverId():'';
    return{
      'X-Emby-Authorization':
        'MediaBrowser Client="MediaDash", Device="Jellyfin Web", DeviceId="mediadash-plugin-1", Version="1.0.6", Token="'+t+'"',
      'X-Emby-Token': t
    };
  }
  return{};
}
async function api(p,o){
  o=o||{};
  var h=Object.assign({'Content-Type':'application/json','Accept':'application/json'},ah(),o.headers||{});
  try{
    var r=await fetch(BASE+p,Object.assign({},o,{headers:h}));
    if(r.status===401||r.status===403)return{_auth_error:true};
    return r.ok?norm(await r.json()):{};  // await is critical here
  } catch(e){console.warn('MediaDash API error',p,e);return{};}
}

//  Toast 
function toast(m,c){
  var t=document.getElementById('toast');
  t.textContent=m;
  t.style.borderColor=c==='g'?'#4caf50':c==='r'?'#f44336':'#00a4dc';
  t.classList.add('on');
  setTimeout(function(){t.classList.remove('on');},2800);
}

//  Tabs 
document.querySelectorAll('#MediaDashPage .tab').forEach(function(b){
  b.addEventListener('click',function(){
    document.querySelectorAll('#MediaDashPage .tab').forEach(function(x){x.classList.remove('on');});
    document.querySelectorAll('#MediaDashPage .panel').forEach(function(x){x.classList.remove('on');});
    b.classList.add('on');
    document.getElementById('pt-'+b.dataset.tab).classList.add('on');
  });
});

//  Helpers 
function fG(g){return g>=1?g.toFixed(1)+'GB':Math.round(g*1024)+'MB';}
function fM(m){return m>=1024?(m/1024).toFixed(1)+'GB':m+'MB';}
function fS(s){if(!s)return'';var h=Math.floor(s/3600),m=Math.floor((s%3600)/60),sc=s%60;return h?h+'h '+m+'m':m?m+'m '+sc+'s':sc+'s';}
function driveCol(p){return p>90?'#f44336':p>75?'#ff9800':'#00a4dc';}
function g(id){return document.getElementById(id);}
function setText(id,v){var el=g(id);if(el)el.textContent=v;}

//  Status 
async function loadStatus(){
  var s=await api('/status');
  if(!s||!s.schedule)return;
  var bs=g('b-status');
  if(s.encodingActive||s.strippingActive){
    bs.className='badge ba';
    bs.innerHTML='<span class="dot"></span> '+(s.encodingActive?'Encoding':'Stripping tracks');
  } else {bs.className='badge bi';bs.textContent='Idle';}
  g('b-quiet').style.display=s.inQuietHours?'flex':'none';
  g('b-pause').style.display=s.paused?'flex':'none';
  var st=s.jellyfinStreams||[];
  var sb=g('b-stream');
  if(st.length){
    sb.style.display='flex';
    g('b-stream-lbl').textContent=st.length+' stream'+(st.length>1?'s':'');
    g('streams-sec').style.display='';
    g('streams-list').innerHTML=st.map(function(x){
      return '<div class="stream-row"><div class="av">'+(x.user||'?').charAt(0).toUpperCase()+'</div>'+
        '<div class="si"><div class="st">'+(x.series?x.series+' \u2014 ':'')+x.title+'</div>'+
        '<div class="ss">'+x.user+' \u00b7 '+x.client+'</div></div>'+
        '<span class="sp">'+x.progressPct+'%</span></div>';
    }).join('');
  } else {sb.style.display='none';g('streams-sec').style.display='none';}
  if(s.schedule){
    g('cfg-ps').value=s.schedule.pauseStart;
    g('cfg-pe').value=s.schedule.pauseEnd;
    schedPrev();
  }
}

//  Encode status 
async function loadEncode(){
  var e=await api('/encode_status');
  var card=g('enc-card');
  if(!e||!e.active){
    card.className='card enc-idle';
    setText('enc-chip','No task running'); setText('enc-spd','');
    setText('enc-name','Encoder is idle');
    setText('enc-sub','Waiting for the next scheduled window');
    ['enc-src','enc-out','enc-fin','enc-sav'].forEach(function(id){setText(id,'\u2013');});
    g('enc-bar').style.width='0%'; setText('enc-pct','0%');
    setText('enc-elapsed',''); setText('enc-eta','');
    return;
  }
  card.className='card enc-active';
  setText('enc-chip',(e.codec||'?').toUpperCase()+' \u2192 '+(e.targetCodec||'H.265'));
  setText('enc-spd',(e.speed&&e.speed!=='N/A')?e.speed:'');
  setText('enc-name',e.name||'\u2013');
  setText('enc-sub','Started '+( e.startedAt||'\u2013')+(e.worker!=null?' \u00b7 Worker '+e.worker:''));
  setText('enc-src',e.sourceGb?e.sourceGb+' GB':'\u2013');
  setText('enc-out',e.tmpSizeGb>0?e.tmpSizeGb+' GB':'\u2013');
  setText('enc-fin',e.estFinalGb>0?e.estFinalGb+' GB':e.pct<3?'\u2026':'\u2013');
  setText('enc-sav',(e.estSavingGb||0)>0?e.estSavingGb.toFixed(1)+' GB saved':e.pct<3?'\u2026':'\u2013');
  var p=e.pct||0;
  g('enc-bar').style.width=p+'%'; setText('enc-pct',p.toFixed(1)+'%');
  setText('enc-elapsed',e.elapsedS?'Elapsed '+fS(e.elapsedS):'');
  if(p>2&&e.elapsedS){var eta=Math.max(0,Math.round(e.elapsedS/(p/100)-e.elapsedS));setText('enc-eta',fS(eta)+' remaining');}
  else setText('enc-eta','');
}

//  Storage 
async function loadDrives(){
  var drives=await api('/drives');
  var c=g('drives-card');
  if(!Array.isArray(drives)||!drives.length){
    c.innerHTML='<div style="font-size:.82em;color:var(--t2)">No storage data \u2014 configure media directories in Settings.</div>';
    return;
  }
  // Deduplicate by mount point, prefer non-root mounts
  var seen={};
  var deduped=drives.filter(function(d){
    if(seen[d.mount])return false;
    seen[d.mount]=true;
    return true;
  });
  c.innerHTML=deduped.map(function(d){
    var col=driveCol(d.pct);
    // Use a friendly label: show the label path, or mount if label==mount
    var name=d.label===d.mount?d.mount:d.label;
    return '<div class="drive">'+
      '<div class="drive-hd"><span class="drive-name" title="'+d.mount+'">'+name+'</span><span class="drive-sz">'+d.usedGb+'GB / '+d.totalGb+'GB ('+d.pct+'%)</span></div>'+
      '<div class="drive-track"><div class="drive-fill" style="width:'+d.pct+'%;background:'+col+'"></div></div>'+
      '<div class="drive-free">'+d.freeGb+'GB free</div>'+
    '</div>';
  }).join('');
}

//  Processing summary 
async function loadSummary(){
  var re=await api('/reencode'),strip=await api('/strip'),rem=await api('/encode_remaining');
  if(Array.isArray(re)){
    var done=re.filter(function(f){return f.status==='done';});
    var sv=done.reduce(function(s,f){return s+(f.savedGb||0);},0);
    setText('ov-enc',done.length);
    setText('ov-enc-sv',sv.toFixed(1)+'GB');
  }
  if(Array.isArray(rem))setText('ov-remaining',rem.length+' file'+(rem.length===1?'':'s')+' in queue');
  if(Array.isArray(strip)){
    var sp=strip.filter(function(f){return f.status==='processed';});
    var smb=strip.reduce(function(s,f){return s+(f.savedMb||0);},0);
    setText('ov-strip',sp.length);
    setText('ov-strip-sv',smb>=1024?(smb/1024).toFixed(1)+'GB saved':Math.round(smb)+'MB saved');
  }
}

//  Performance 
async function loadMetrics(){
  var m=await api('/metrics');
  if(!m||m._auth_error){console.warn('MediaDash: metrics auth failed');return;}
  if(!m.cpuPct&&m.cpuPct!==0)return;
  setText('p-cpu',m.cpuPct+'%'); setText('p-gpu',m.gpuPct+'%'); setText('p-mem',m.memPct+'%');
  g('p-gpu-bar').style.width=m.gpuPct+'%'; setText('p-gpu-v',m.gpuPct+'%');
  var vp=m.vramTotalMb>0?Math.round(m.vramUsedMb/m.vramTotalMb*100):0;
  g('p-vram-bar').style.width=vp+'%'; setText('p-vram-v',vp+'%');
  setText('p-vram-d',fM(m.vramUsedMb)+' / '+fM(m.vramTotalMb));
  g('p-mem-bar').style.width=m.memPct+'%'; setText('p-mem-v',m.memPct+'%');
  setText('p-mem-d',fM(m.memUsedMb)+' / '+fM(m.memTotalMb));
  if(m.temps){
    if(m.temps.cpu!=null)  setText('p-cpu-t', 'CPU: '+m.temps.cpu+'\u00b0C');
    if(m.temps.gpu!=null)  setText('p-gpu-t', 'GPU: '+m.temps.gpu+'\u00b0C');
    if(m.temps.nvme!=null) setText('p-nvme-t','NVMe: '+m.temps.nvme+'\u00b0C');
  }
  var dr=m.diskReadGb, dw=m.diskWriteGb;
  setText('p-dr',dr>=1000?(dr/1000).toFixed(1)+'TB':dr+'GB');
  setText('p-dw',dw>=1000?(dw/1000).toFixed(1)+'TB':dw+'GB');
  if(m.perCore&&m.perCore.length)
    g('core-bars').innerHTML=m.perCore.map(function(p,i){
      return '<div class="br-row"><div class="br-lbl">Core '+i+'</div><div class="br-track"><div class="br-fill" style="background:var(--b);width:'+p+'%"></div></div><div class="br-val">'+p+'%</div></div>';
    }).join('');
}

//  Schedule viz 
window.schedPrev=function(){
  var ps=parseInt(g('cfg-ps').value)||0,pe=parseInt(g('cfg-pe').value)||0;
  g('sched-viz').innerHTML=Array.from({length:24},function(_,i){
    var q=ps<pe?(i>=ps&&i<pe):(i>=ps||i<pe);
    return '<div class="sh" style="background:'+(q?'rgba(255,152,0,.3);border:1px solid var(--a)':'rgba(76,175,80,.2);border:1px solid var(--g)')+'" title="'+i+':00"></div>';
  }).join('');
};

//  Config: load 
async function loadConfig(){
  if(!window.ApiClient)return;
  var cfg=await new Promise(function(res){ApiClient.getPluginConfiguration(PLUGIN_ID).then(res).catch(function(){res({});});});

  // Encoder
  var em=g('cfg-method'); if(em&&cfg.EncodeMethod) em.value=cfg.EncodeMethod;
  var ec=g('cfg-codec');  if(ec&&cfg.TargetCodec)  ec.value=cfg.TargetCodec;
  if(g('cfg-quality')&&cfg.EncodeQuality!=null) g('cfg-quality').value=cfg.EncodeQuality;
  if(g('cfg-workers')&&cfg.EncodeWorkers!=null) g('cfg-workers').value=cfg.EncodeWorkers;
  if(g('cfg-vaapi')) g('cfg-vaapi').value=cfg.VaapiDevice||'';
  if(g('cfg-exts'))  g('cfg-exts').value=cfg.VideoExtensions||'';
  if(g('cfg-skip'))  g('cfg-skip').value=cfg.SkipCodecs||'';

  // Languages
  if(g('cfg-strip-en')) g('cfg-strip-en').checked=!!cfg.EnableTrackStripping;
  if(g('cfg-audio-lang')) g('cfg-audio-lang').value=cfg.KeepAudioLanguages||'';
  if(g('cfg-sub-lang'))   g('cfg-sub-lang').value=cfg.KeepSubtitleLanguages||'';
  if(g('cfg-keep-first')) g('cfg-keep-first').checked=cfg.AlwaysKeepFirstAudio!==false;
  if(g('cfg-keep-commentary')) g('cfg-keep-commentary').checked=!!cfg.KeepCommentaryTracks;

  // Dirs  pre-fill from Jellyfin libraries if not configured
  if(g('cfg-dirs')){
    if(cfg.MediaDirectories&&cfg.MediaDirectories.trim()){
      g('cfg-dirs').value=cfg.MediaDirectories;
    } else {
      // Auto-populate from Jellyfin library paths
      var libs=await api('/libraries');
      if(Array.isArray(libs)&&libs.length){
        var paths=[...new Set(libs.map(function(l){return l.path;}).filter(Boolean))];
        g('cfg-dirs').value=paths.join('\n');
      }
    }
  }

  // Services
  if(g('cfg-enc-svc'))    g('cfg-enc-svc').value=cfg.ReencodeServiceName||'';
  if(g('cfg-strip-svc'))  g('cfg-strip-svc').value=cfg.StripServiceName||'';
  if(g('cfg-enc-proc'))   g('cfg-enc-proc').value=cfg.ReencodeProcessName||'';
  if(g('cfg-strip-proc')) g('cfg-strip-proc').value=cfg.StripProcessName||'';
  if(g('cfg-pause-flag')) g('cfg-pause-flag').value=cfg.PauseFlagPath||'';
  if(g('cfg-force-flag')) g('cfg-force-flag').value=cfg.ForceFlagPath||'';
  if(g('cfg-status-dir')) g('cfg-status-dir').value=cfg.EncodeStatusDir||'';
  if(g('cfg-state-file')) g('cfg-state-file').value=cfg.ReencodeStateFile||'';
  if(g('cfg-enc-log'))    g('cfg-enc-log').value=cfg.ReencodeLogPath||'';
  if(g('cfg-strip-log'))  g('cfg-strip-log').value=cfg.StripLogPath||'';
  if(g('cfg-dupe-report')) g('cfg-dupe-report').value=cfg.DupesReportPath||'';
  if(g('cfg-dupe-script')) g('cfg-dupe-script').value=cfg.DupesScanScript||'';
  if(g('cfg-tmdb-key'))    g('cfg-tmdb-key').value=cfg.TmdbApiKey||'';

  // Schedule
  if(g('cfg-ps')&&cfg.QuietHoursStart!=null) g('cfg-ps').value=cfg.QuietHoursStart;
  if(g('cfg-pe')&&cfg.QuietHoursEnd  !=null) g('cfg-pe').value=cfg.QuietHoursEnd;
  if(g('cfg-pause-streams')) g('cfg-pause-streams').checked=cfg.PauseDuringStreams!==false;
  schedPrev();

  // Run buttons  only show if service is configured
  var rbDiv=g('run-btns'); rbDiv.innerHTML='';
  if(cfg.ReencodeServiceName)
    rbDiv.innerHTML+='<button is="emby-button" class="raised emby-button" onclick="doRun(\'reencode\')">\u25b6 Run re-encoder now</button> ';
  if(cfg.StripServiceName)
    rbDiv.innerHTML+='<button is="emby-button" class="raised emby-button" onclick="doRun(\'strip\')">\u25b6 Run strip tracks now</button>';

  // Show Jellyfin library paths as helper text
  var libs=await api('/libraries');
  if(Array.isArray(libs)&&libs.length){
    g('jellyfin-libs').innerHTML='<div style="font-size:.78em;color:var(--t2);margin-bottom:.5em">Your Jellyfin libraries (used automatically if directories above are blank):</div>'+
      libs.map(function(l){return'<div style="font-size:.75em;font-family:var(--mo);color:var(--t2);padding:.15em 0">'+l.name+' \u2014 '+l.path+'</div>';}).join('');
  }
}

//  Save functions 
function withCfg(fn){
  if(!window.ApiClient){toast('Not connected to Jellyfin','r');return;}
  ApiClient.getPluginConfiguration(PLUGIN_ID).then(function(cfg){fn(cfg);}).catch(function(){toast('Failed to load config','r');});
}
function saveCfg(cfg){
  ApiClient.updatePluginConfiguration(PLUGIN_ID,cfg).then(function(){toast('Saved','g');}).catch(function(){toast('Save failed','r');});
}

window.saveEncoder=function(){withCfg(function(cfg){
  cfg.EncodeMethod   = g('cfg-method').value;
  cfg.TargetCodec    = g('cfg-codec').value;
  cfg.EncodeQuality  = parseInt(g('cfg-quality').value)||22;
  cfg.EncodeWorkers  = parseInt(g('cfg-workers').value)||1;
  cfg.VaapiDevice    = g('cfg-vaapi').value.trim();
  cfg.VideoExtensions= g('cfg-exts').value.trim();
  cfg.SkipCodecs     = g('cfg-skip').value.trim();
  saveCfg(cfg);
});};

window.saveLanguages=function(){withCfg(function(cfg){
  cfg.EnableTrackStripping  = g('cfg-strip-en').checked;
  cfg.KeepAudioLanguages    = g('cfg-audio-lang').value.trim();
  cfg.KeepSubtitleLanguages = g('cfg-sub-lang').value.trim();
  cfg.AlwaysKeepFirstAudio  = g('cfg-keep-first').checked;
  cfg.KeepCommentaryTracks  = g('cfg-keep-commentary').checked;
  saveCfg(cfg);
});};

window.saveLibraries=function(){withCfg(function(cfg){
  cfg.MediaDirectories=g('cfg-dirs').value;
  saveCfg(cfg);
});};

window.saveServices=function(){withCfg(function(cfg){
  cfg.ReencodeServiceName  = g('cfg-enc-svc').value.trim();
  cfg.StripServiceName     = g('cfg-strip-svc').value.trim();
  cfg.ReencodeProcessName  = g('cfg-enc-proc').value.trim();
  cfg.StripProcessName     = g('cfg-strip-proc').value.trim();
  cfg.PauseFlagPath        = g('cfg-pause-flag').value.trim();
  cfg.ForceFlagPath        = g('cfg-force-flag').value.trim();
  cfg.EncodeStatusDir      = g('cfg-status-dir').value.trim();
  cfg.ReencodeStateFile    = g('cfg-state-file').value.trim();
  cfg.ReencodeLogPath      = g('cfg-enc-log').value.trim();
  cfg.StripLogPath         = g('cfg-strip-log').value.trim();
  cfg.DupesReportPath      = g('cfg-dupe-report').value.trim();
  cfg.DupesScanScript      = g('cfg-dupe-script').value.trim();
  cfg.TmdbApiKey           = g('cfg-tmdb-key').value.trim();
  saveCfg(cfg);
});};

window.saveSched=async function(){
  var r=await api('/schedule',{method:'POST',body:JSON.stringify({
    pauseStart:parseInt(g('cfg-ps').value)||0,
    pauseEnd:parseInt(g('cfg-pe').value)||0
  })});
  withCfg(function(cfg){
    cfg.PauseDuringStreams=g('cfg-pause-streams').checked;
    saveCfg(cfg);
  });
  if(r.ok)toast('Schedule saved','g'); else toast('Failed','r');
};

window.doPause  =async function(){await api('/pause', {method:'POST',body:'{}'});toast('Paused','a');loadStatus();};
window.doResume =async function(){await api('/resume',{method:'POST',body:'{}'});toast('Resuming','g');setTimeout(loadStatus,800);setTimeout(loadEncode,2000);};
window.doRun    =async function(s){
  var r=await api('/run',{method:'POST',body:JSON.stringify({script:s})});
  r.ok?toast('Started','g'):toast(r.error||'Service not configured \u2014 check Settings','r');
  setTimeout(loadStatus,1500);
};

//  Init 
function init(){
  loadStatus(); loadEncode(); loadDrives(); loadSummary(); loadMetrics(); loadConfig();
  loadFsLibraries();
  schedPrev();
  setInterval(loadStatus, 15000);
  setInterval(loadEncode,  5000);
  setInterval(loadMetrics, 4000);
  setInterval(loadDrives, 60000);
  setInterval(loadSummary,60000);
}

// 
// FILE EXPLORER
// 

var fsRoot=null, fsCurrent=null, fsClipboard=null, fsClipOp=null;

async function loadFsLibraries(){
  var libs=await api('/libraries');
  var container=g('fs-lib-btns');
  var sortContainer=g('sort-lib-btns');
  if(!Array.isArray(libs)||!libs.length){
    container.innerHTML='<div style="font-size:.82em;color:var(--t2)">No libraries configured.</div>';
    sortContainer.innerHTML=container.innerHTML;
    return;
  }
  // Deduplicate by path
  var seen=new Set();
  var unique=libs.filter(function(l){if(seen.has(l.path))return false;seen.add(l.path);return true;});

  container.innerHTML=unique.map(function(l){
    return '<div class="lib-row">'+
      '<div><div class="lib-row-name">'+l.name+'</div><div class="lib-row-path">'+l.path+'</div></div>'+
      '<button is="emby-button" class="raised emby-button" onclick="fsOpen('+JSON.stringify(l.path)+')">Browse</button>'+
    '</div>';
  }).join('');

  sortContainer.innerHTML=unique.map(function(l){
    return '<div class="sort-lib-row">'+
      '<div><div class="lib-row-name">'+l.name+'</div><div class="lib-row-path">'+l.path+'</div></div>'+
      '<button is="emby-button" class="raised emby-button" onclick="sortOpen('+JSON.stringify(l.path)+')">Scan for duplicates</button>'+
    '</div>';
  }).join('');
}

function fsBack(){
  fsRoot=null; fsCurrent=null;
  g('fs-lib-section').style.display='';
  g('fs-browser').className='fs-wrap';
  fsCancelClip();
}

async function fsOpen(root,path){
  fsRoot=root;
  fsCurrent=path||root;
  g('fs-lib-section').style.display='none';
  g('fs-browser').className='fs-wrap on';
  await fsRender(fsCurrent);
}

async function fsRender(path){
  fsCurrent=path;
  setText('fs-status','Loading\u2026');
  var r=await api('/fs/list?path='+encodeURIComponent(path));
  if(r._auth_error||r.error){setText('fs-status','Error: '+(r.error||'Auth failed'));return;}

  // Breadcrumb
  var bc=g('fs-breadcrumb'); bc.innerHTML='';
  (r.breadcrumbs||[]).forEach(function(seg,i){
    var name=i===0?'\u1f4c1 '+seg.split('/').pop()||seg:seg.split('/').pop()||seg;
    var sp=document.createElement('span');
    sp.className='bc-seg'; sp.textContent=name; sp.title=seg;
    sp.onclick=function(){fsRender(seg);};
    bc.appendChild(sp);
    if(i<(r.breadcrumbs.length-1)){
      var sep=document.createElement('span');
      sep.className='bc-sep'; sep.textContent=' / ';
      bc.appendChild(sep);
    }
  });

  setText('fs-status',(r.dirs.length+r.files.length)+' items');

  var grid=g('fs-grid'); grid.innerHTML='';
  // Dirs first
  (r.dirs||[]).forEach(function(d){
    var el=document.createElement('div');
    el.className='fs-item'; el.dataset.path=d.path; el.dataset.type='dir';
    el.innerHTML='<div class="fs-item-icon">\u1f4c1</div>'+
      '<div class="fs-item-name">'+esc(d.name)+'</div>'+
      '<div class="fs-item-meta">Folder</div>';
    el.ondblclick=function(){fsRender(d.path);};
    el.onclick=function(e){fsSelect(el,e);};
    el.oncontextmenu=function(e){e.preventDefault();fsCtx(e,d.path,d.type,d.name);};
    grid.appendChild(el);
  });
  // Files
  (r.files||[]).forEach(function(f){
    var icon=fsIcon(f.ext);
    var el=document.createElement('div');
    el.className='fs-item'; el.dataset.path=f.path; el.dataset.type='file';
    el.innerHTML='<div class="fs-item-icon">'+icon+'</div>'+
      '<div class="fs-item-name">'+esc(f.name)+'</div>'+
      '<div class="fs-item-meta">'+f.sizeFmt+' \u00b7 '+f.modified+'</div>';
    el.onclick=function(e){fsSelect(el,e);};
    el.oncontextmenu=function(e){e.preventDefault();fsCtx(e,f.path,f.type,f.name);};
    grid.appendChild(el);
  });
  if(!r.dirs.length&&!r.files.length)
    grid.innerHTML='<div class="fs-empty">Empty folder</div>';
}

function fsIcon(ext){
  var video=['.mkv','.mp4','.avi','.mov','.m2ts','.ts','.wmv','.flv','.webm'];
  var sub=['.srt','.ass','.ssa','.sup','.vtt'];
  var img=['.jpg','.jpeg','.png','.webp','.gif','.bmp'];
  if(video.includes(ext))return'\u1f3ac';
  if(sub.includes(ext))return'\u1f4ac';
  if(img.includes(ext))return'\u1f5bc';
  if(ext==='.nfo')return'\u1f4c4';
  return'\u1f4c4';
}

var fsSelected=[];
function fsSelect(el,e){
  if(!e.ctrlKey&&!e.metaKey&&!e.shiftKey){
    document.querySelectorAll('#fs-grid .fs-item').forEach(function(x){x.classList.remove('selected');});
    fsSelected=[];
  }
  el.classList.toggle('selected');
  var path=el.dataset.path;
  if(el.classList.contains('selected'))fsSelected.push(path);
  else fsSelected=fsSelected.filter(function(p){return p!==path;});
  setText('fs-sel-info',fsSelected.length?fsSelected.length+' selected':'');
}

// Context menu
var _ctxMenu=null;
function fsCtx(e,path,type,name){
  fsCloseCtx();
  var menu=document.createElement('div');
  menu.className='ctx-menu';
  menu.style.left=e.clientX+'px'; menu.style.top=e.clientY+'px';
  function mi(txt,cls,fn){var d=document.createElement('div');d.className='ctx-item'+(cls?' '+cls:'');d.textContent=txt;d.onclick=fn;return d;}
  function ms(){var d=document.createElement('div');d.className='ctx-sep';return d;}
  menu.appendChild(mi('Rename','',function(){fsCloseCtx();fsRename(path,name);}));
  menu.appendChild(mi('Cut','',function(){fsCloseCtx();fsClip(path,'move');}));
  menu.appendChild(mi('Copy','',function(){fsCloseCtx();fsClip(path,'copy');}));
  menu.appendChild(ms());
  menu.appendChild(mi('Delete','danger',function(){fsCloseCtx();fsDeletePrompt(path,name,type);}));
  document.getElementById('MediaDashPage').appendChild(menu);
  _ctxMenu=menu;
  setTimeout(function(){document.addEventListener('click',fsCloseCtx,{once:true});},0);
}
function fsCloseCtx(){if(_ctxMenu){_ctxMenu.remove();_ctxMenu=null;}}

// Clipboard
function fsClip(path,op){
  fsClipboard=path; fsClipOp=op;
  g('fs-paste-btn').style.display='';
  g('fs-cancel-btn').style.display='';
  setText('fs-clipboard-lbl',(op==='move'?'\u2702\ufe0f Cut:':'\u1f4cb Copy: ')+path.split('/').pop());
  fsCloseCtx();
}
window.fsCancelClip=function(){
  fsClipboard=null; fsClipOp=null;
  g('fs-paste-btn').style.display='none';
  g('fs-cancel-btn').style.display='none';
  setText('fs-clipboard-lbl','');
};
window.fsPaste=async function(){
  if(!fsClipboard||!fsCurrent)return;
  var r=await api('/fs/'+(fsClipOp==='move'?'move':'copy'),{
    method:'POST',body:JSON.stringify({sourcePath:fsClipboard,destDir:fsCurrent})});
  if(r.ok){toast((fsClipOp==='move'?'Moved':'Copied')+' successfully','g');fsCancelClip();fsRender(fsCurrent);}
  else toast('Failed: '+(r.error||'unknown'),'r');
};

// Rename
window.fsRename=async function(path,currentName){
  fsCloseCtx();
  var newName=prompt('Rename to:',currentName);
  if(!newName||newName===currentName)return;
  var r=await api('/fs/rename',{method:'POST',body:JSON.stringify({path,newName})});
  if(r.ok){toast('Renamed','g');fsRender(fsCurrent);}
  else toast('Rename failed: '+(r.error||'unknown'),'r');
};

// Delete
window.fsDeletePrompt=async function(path,name,type){
  fsCloseCtx();
  var msg=type==='dir'
    ? 'Delete folder "'+name+'" and ALL its contents? This cannot be undone.'
    : 'Delete file "'+name+'"? This cannot be undone.';
  if(!confirm(msg))return;
  var r=await api('/fs/delete',{method:'POST',body:JSON.stringify({path})});
  if(r.ok){toast('Deleted','g');fsRender(fsCurrent);}
  else toast('Delete failed: '+(r.error||'unknown'),'r');
};

function esc(s){var d=document.createElement('div');d.textContent=s;return d.innerHTML;}

// 
// AUTO-SORT
// 

var sortLibRoot=null, sortPreviewData=null;

function sortBack(){
  sortLibRoot=null; sortPreviewData=null;
  g('sort-lib-section').style.display='';
  g('sort-results').style.display='none';
}

async function sortOpen(root){
  sortLibRoot=root;
  g('sort-lib-section').style.display='none';
  g('sort-results').style.display='';
  g('sort-loading').textContent='Run a scan to find duplicates.';
  g('sort-summary').style.display='none';
  g('sort-errors').style.display='none';
  g('sort-empty').style.display='none';
  g('sort-candidates-list').innerHTML='';
  await sortScan();
}

async function sortScan(){
  if(!sortLibRoot)return;
  var tmdbEl=g('cfg-tmdb-key');
  if(!tmdbEl||!tmdbEl.value.trim()){
    toast('Set your TMDB API key in Settings first','r');
    return;
  }
  g('sort-scanning').style.display='flex';
  g('sort-loading').textContent='Scanning\u2026 writing IMDB markers (this may take a few minutes for large libraries)';

  // Step 1: write IMDB markers
  var scan=await api('/sort/scan',{method:'POST',body:JSON.stringify({path:sortLibRoot})});
  if(!scan.ok){toast('Scan failed: '+(scan.error||'?'),'r');g('sort-scanning').style.display='none';return;}

  // Step 2: build preview
  var preview=await api('/sort/preview',{method:'POST',body:JSON.stringify({path:sortLibRoot})});
  g('sort-scanning').style.display='none';

  if(preview._auth_error){toast('Auth error \u2014 try refreshing','r');return;}
  sortPreviewData=preview;
  renderSortPreview(preview);
}

function renderSortPreview(preview){
  var cands=preview.candidates||[];

  // Summary
  if(cands.length){
    setText('sort-summary-txt',cands.length+' group'+(cands.length>1?'s':'')+' of duplicate folders found');
    setText('sort-summary-sub','Merging will consolidate '+preview.totalFolders+' folders into '+cands.length);
    g('sort-merge-all-btn').style.display='';
    g('sort-summary').style.display='';
    g('sort-empty').style.display='none';
  } else {
    g('sort-summary').style.display='none';
    g('sort-empty').style.display='block';
  }

  // Errors
  if(preview.errors&&preview.errors.length){
    g('sort-errors').style.display='';
    g('sort-errors-list').innerHTML=preview.errors.map(function(e){return'<div>'+esc(e)+'</div>';}).join('');
  }

  setText('sort-loading','');

  // Candidate cards
  g('sort-candidates-list').innerHTML=cands.map(function(c,i){
    var id='sc-'+i;
    var typeChip=c.type==='tv'
      ? '<span style="background:rgba(156,39,176,.2);color:#ce93d8;padding:.15em .5em;border-radius:3px;font-size:.68em;font-weight:600">TV</span>'
      : '<span style="background:rgba(0,164,220,.2);color:var(--b);padding:.15em .5em;border-radius:3px;font-size:.68em;font-weight:600">Movie</span>';
    var yearStr=c.year>0?' ('+c.year+')':'';
    var folders=c.groups.map(function(f){
      return '<div class="sort-folder">'+
        '<div class="sort-folder-info">'+
          '<div class="sort-folder-name">'+(f.isCanonical?'\u2b50 ':'')+esc(f.name)+'</div>'+
          '<div class="sort-folder-path">'+esc(f.path)+'</div>'+
        '</div>'+
        '<div class="sort-folder-count">'+f.fileCount+' file'+(f.fileCount!==1?'s':'')+
          (f.isCanonical?' \u00b7 <span style="color:var(--g)">KEEP</span>':' \u00b7 <span style="color:var(--a)">MERGE IN</span>')+
        '</div></div>';
    }).join('');
    return '<div class="sort-candidate">'+
      '<div class="sort-candidate-hd" onclick="sortToggle(''+id+'')">'+
        '<div class="sort-title">'+typeChip+' '+esc(c.title)+yearStr+
          '<span style="font-size:.72em;color:var(--t2);font-family:var(--mo);margin-left:.5em">'+c.imdbId+'</span>'+
        '</div>'+
        '<div class="sort-meta">'+
          '<span style="font-size:.75em;color:var(--t2)">'+c.groups.length+' folders</span>'+
          '<button is="emby-button" class="raised emby-button" style="font-size:.75em;padding:.25em .75em" '+
            'onclick="event.stopPropagation();sortMerge('+JSON.stringify(c.imdbId)+','+JSON.stringify(c.title)+')">Merge</button>'+
        '</div>'+
      '</div>'+
      '<div class="sort-body" id="'+id+'">'+folders+'</div>'+
    '</div>';
  }).join('');
}

window.sortToggle=function(id){g(id).classList.toggle('on');};

window.sortMerge=async function(imdbId,title){
  if(!confirm('Merge all folders for "'+title+'" into the one with the most files? This will move files and cannot be easily undone.'))return;
  var r=await api('/sort/execute',{method:'POST',body:JSON.stringify({libraryRoot:sortLibRoot,imdbId})});
  if(r.ok){toast('Merged successfully \u2014 library refresh triggered','g');triggerLibraryScan();await sortScan();}
  else toast('Merge failed: '+(r.error||'unknown'),'r');
};

window.sortMergeAll=async function(){
  var cands=sortPreviewData&&sortPreviewData.candidates||[];
  if(!cands.length)return;
  if(!confirm('Merge ALL '+cands.length+' duplicate groups? Files will be moved into the canonical folder for each group.'))return;
  var failed=0;
  for(var i=0;i<cands.length;i++){
    var c=cands[i];
    var r=await api('/sort/execute',{method:'POST',body:JSON.stringify({libraryRoot:sortLibRoot,imdbId:c.imdbId})});
    if(!r.ok)failed++;
  }
  if(failed===0){toast('All groups merged \u2014 triggering library scan','g');triggerLibraryScan();}
  else toast(failed+' group(s) failed \u2014 check individual results','r');
  await sortScan();
};

async function triggerLibraryScan(){
  if(!window.ApiClient)return;
  try{
    await ApiClient.ajax({type:'POST',url:ApiClient.getUrl('Library/Refresh')});
  } catch(e){console.warn('Library refresh failed',e);}
}



(function bootstrap(){
  var started = false;
  function tryInit(){
    if(started) return;
    var el = document.getElementById('b-status');
    if(el && document.contains(el)){
      started = true;
      // Small delay to ensure Jellyfin has finished its own post-insert setup
      setTimeout(init, 200);
      return true;
    }
    return false;
  }

  // Try immediately (page might already be ready)
  if(tryInit()) return;

  // MutationObserver: fires when Jellyfin inserts our HTML into the DOM
  var observer = new MutationObserver(function(){
    if(tryInit()) observer.disconnect();
  });
  observer.observe(document.body || document.documentElement, {
    childList: true, subtree: true
  });

  // Jellyfin SPA view lifecycle  fires on every navigation to this page
  document.addEventListener('viewshow', function(e){
    if(e.detail && e.detail.type === 'html') tryInit();
  });
  // Also listen on the page element itself
  document.addEventListener('pageshow', tryInit);

  // Polling fallback: keep trying for up to 30 seconds
  var tries = 0;
  var poll = setInterval(function(){
    if(tryInit() || tries++ > 300) clearInterval(poll);
  }, 100);
})();

// Jellyfin calls this when the view is shown (every SPA navigation to this page)
view.addEventListener('viewshow', function(){
  init();
});

// Init immediately too  viewshow may have already fired
setTimeout(function(){ init(); }, 50);

} // end export default
