(() => {
  const views = {overview:['服务器概览','掌握运行状态，让每一次分发都有条不紊。'],accounts:['账号管理','管理账号期限与程序权限。'],activation:['激活码管理','生成、查看和作废注册及续费激活码。'],programs:['程序管理','整理程序、图标与 Beta 选项。'],release:['版本发布','将新的版本，交付给已授权用户。'],connectivity:['连接诊断','检查客户端与服务器之间的连接。']};
  const sections = [...document.querySelectorAll('[data-view]')];
  function selectView() {
    let saved; try { saved = sessionStorage.getItem('chispa-view'); } catch {}
    const hash = location.hash.slice(1);
    const key = (hash === 'main' ? saved : hash) || saved || 'overview';
    const current = Object.hasOwn(views,key) ? key : 'overview';
    sections.forEach(section => section.hidden = section.dataset.view !== current);
    document.querySelectorAll('nav a').forEach(link => {
      if (link.hash === '#'+current) link.setAttribute('aria-current','page'); else link.removeAttribute('aria-current');
    });
    const title = document.getElementById('pageTitle');
    if (title) { title.textContent = views[current][0]; document.getElementById('pageSubtitle').textContent = views[current][1]; document.title=views[current][0]+' · Chispa'; }
    try { sessionStorage.setItem('chispa-view',current); } catch {}
    requestAnimationFrame(()=>window.scrollTo({top:0,behavior:'instant'}));
  }
  if (sections.length) { selectView(); addEventListener('hashchange',selectView); }
  for (const input of document.querySelectorAll('[data-filter]')) {
    input.addEventListener('input',() => {
      const query=input.value.trim().toLocaleLowerCase(); let visible=0;
      document.querySelectorAll(input.dataset.filter).forEach(row=>{row.hidden=!row.dataset.search.toLocaleLowerCase().includes(query); if(!row.hidden)visible++;});
      const empty=document.getElementById(input.dataset.empty); if(empty)empty.hidden=visible>0;
    });
  }
  const accountRows=[...document.querySelectorAll('.account-row')];
  let accountPage=0, onlineNames=null;
  const accountSearch=document.getElementById('accountSearch'), accountState=document.getElementById('accountState'), accountProgram=document.getElementById('accountProgram');
  function filterAccounts(reset=false){
    if(!accountSearch)return;
    if(reset)accountPage=0;
    const query=accountSearch.value.trim().toLocaleLowerCase(),state=accountState.value,program=accountProgram.value;
    const matches=accountRows.filter(row=>row.dataset.search.toLocaleLowerCase().includes(query)&&
      (state==='all'||(state==='online'?onlineNames?.has(row.dataset.username.toLocaleLowerCase()):row.dataset.state===state))&&
      (program==='all'||row.dataset.programs.split(' ').includes(program)));
    const pages=Math.max(1,Math.ceil(matches.length/20));accountPage=Math.min(accountPage,pages-1);
    const visible=new Set(matches.slice(accountPage*20,accountPage*20+20));
    accountRows.forEach(row=>row.hidden=!visible.has(row));
    document.getElementById('noAccounts').hidden=matches.length>0;
    document.getElementById('accountCount').textContent=matches.length+' / '+accountRows.length+' 个账号';
    const label=document.getElementById('accountPage');if(label)label.textContent=(accountPage+1)+' / '+pages+' 页 · 每页 20 个';
    const prev=document.getElementById('accountPrev'),next=document.getElementById('accountNext');
    if(prev)prev.disabled=accountPage===0;if(next)next.disabled=accountPage>=pages-1;
  }
  accountSearch?.addEventListener('input',()=>filterAccounts(true));
  [accountState,accountProgram].forEach(input=>input?.addEventListener('change',()=>filterAccounts(true)));
  document.getElementById('accountPrev')?.addEventListener('click',()=>{accountPage--;filterAccounts();});
  document.getElementById('accountNext')?.addEventListener('click',()=>{accountPage++;filterAccounts();});
  filterAccounts();
  for(const editor of document.querySelectorAll('.account-row .user-editor')){
    for(const form of [...editor.children]){
      const title=form.querySelector('.formhead')?.textContent || (form.classList.contains('deleteform')?'删除账号':'分程序授权');
      const group=document.createElement('details'),summary=document.createElement('summary');
      group.className='account-action';summary.textContent=title;form.before(group);group.append(summary,form);
    }
  }
  let onlineBusy=false;
  async function updateOnline(){
    if(onlineBusy||!document.getElementById('onlineRows')||document.hidden)return;
    onlineBusy=true;
    const status=document.getElementById('onlineStatus');
    try{
      const response=await fetch('/admin/online',{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(10000)});
      if(response.redirected||response.status===401)throw new Error('登录已过期，请重新登录');
      if(!response.ok)throw new Error('在线状态读取失败');
      const data=await response.json();onlineNames=new Set(data.programs.map(p=>p.username.toLocaleLowerCase()));
      document.getElementById('onlineUsers').textContent=data.onlineUsers.toLocaleString();
      document.getElementById('onlinePrograms').textContent=data.programs.length.toLocaleString();
      const rows=data.programs.map(p=>{
        const row=document.createElement('tr');
        for(const value of [p.username,p.programName+' / '+p.version,p.address||'—',new Date(p.startedAt).toLocaleString(),new Date(p.lastSeen).toLocaleString()]){
          const cell=document.createElement('td');cell.textContent=value;row.append(cell);
        }return row;
      });
      if(!rows.length){const row=document.createElement('tr'),cell=document.createElement('td');cell.colSpan=5;cell.textContent='当前没有运行中的程序';row.append(cell);rows.push(row);}
      document.getElementById('onlineRows').replaceChildren(...rows);
      accountRows.forEach(row=>{const badge=row.querySelector('[data-presence]'),online=onlineNames.has(row.dataset.username.toLocaleLowerCase());badge.textContent=online?'在线':'离线';badge.classList.toggle('is-online',online);});
      filterAccounts();status.textContent='已更新 · '+new Date(data.sampledAt).toLocaleTimeString()+' · 每 15 秒刷新';
    }catch(error){
      onlineNames=null;status.textContent=error.message+' · 上次数据可能已过期，等待重试';
      accountRows.forEach(row=>{const badge=row.querySelector('[data-presence]');badge.textContent='未知';badge.classList.remove('is-online');});filterAccounts();
    }finally{onlineBusy=false;}
  }
  document.getElementById('refreshOperations')?.addEventListener('click',()=>{updateOnline();updateMetrics();});
  updateOnline();setInterval(updateOnline,15000);
  for (const toggle of document.querySelectorAll('[data-reset-toggle]')) {
    const form = toggle.closest('form');
    const fields = form?.querySelector('[data-reset-fields]');
    const submit = form?.querySelector('[data-reset-submit]');
    const sync = () => {
      if (fields) fields.disabled = !toggle.checked;
      if (submit) submit.disabled = !toggle.checked;
    };
    toggle.addEventListener('change', sync);
    sync();
  }
  for (const form of document.querySelectorAll('form')) {
    form.addEventListener('submit',async event => {
      if(form.dataset.download){
        event.preventDefault();
        const button=event.submitter, status=form.querySelector('[data-download-status]');
        if(button.disabled)return;
        button.disabled=true;status.textContent='正在生成…';
        try{
          const response=await fetch(form.action,{method:'POST',body:new FormData(form),credentials:'same-origin'});
          if(!response.ok){let error;try{error=(await response.json()).error;}catch{}throw new Error(error||'生成失败，请检查登录状态。');}
          const url=URL.createObjectURL(await response.blob()),link=document.createElement('a');
          link.href=url;link.download='chispa-activation-'+Date.now()+'.csv';link.click();setTimeout(()=>URL.revokeObjectURL(url),60000);
          status.textContent='已生成并下载。完整激活码只提供这一次，请保存 CSV。刷新页面可查看记录。';
        }catch(error){status.textContent=error.message+' 如果连接中断，请先刷新记录，确认是否已经生成。';}
        finally{button.disabled=false;}
        return;
      }
      if(form.dataset.confirm && !confirm(form.dataset.confirm)){event.preventDefault();return;}
      const button=event.submitter;
      if(button){button.dataset.idleText=button.textContent;button.disabled=true;button.textContent=form.action.endsWith('/releases')?'正在上传并发布…':'正在处理…';}
    });
    const scope=form.querySelector('[name=accessMode]');
    if(scope){const update=()=>{const choices=form.querySelector('.choices');const hidden=scope.value==='all'||scope.value==='none'||scope.value==='beta';if(choices){choices.hidden=hidden;choices.querySelectorAll('input').forEach(input=>input.disabled=hidden);}for(const name of ['days','betaAccess']){const input=form.querySelector('[name='+name+']');if(input)input.closest('label').hidden=scope.value==='beta';}};scope.addEventListener('change',update);update();}
    const beta=form.querySelector('[name=isBeta]'),parent=form.querySelector('[name=parentProgramId]');
    if(beta&&parent){const update=()=>{parent.disabled=!beta.checked;parent.required=beta.checked;parent.closest('label').hidden=!beta.checked;};beta.addEventListener('change',update);update();}
  }
  const releaseForm=document.getElementById('releaseUpload');
  releaseForm?.addEventListener('submit',async event=>{
    event.preventDefault();
    const button=event.submitter||releaseForm.querySelector('button');
    const label=document.getElementById('uploadSummary');
    const file=releaseForm.querySelector('[name=package]').files[0];
    const signatureInput=releaseForm.querySelector('[name=signature]');
    const signatureFile=signatureInput?.files[0];
    const token=releaseForm.querySelector('[name=__RequestVerificationToken]').value;
    const headers={'X-CSRF-TOKEN':token};
    const request=async(url,options={})=>{
      const response=await fetch(url,{credentials:'same-origin',cache:'no-store',...options});
      let data;try{data=await response.json();}catch{data={};}
      if(!response.ok)throw new Error(data.error||'服务器返回 '+response.status+'；请检查服务端日志。');
      return data;
    };
    const retry=async action=>{
      for(let attempt=0;;attempt++){
        try{return await action();}
        catch(error){if(attempt>=3||!(/Failed to fetch|network|timeout|服务器返回 5/i).test(error.message))throw error;
          await new Promise(resolve=>setTimeout(resolve,1000*(attempt+1)));}
      }
    };
    button.disabled=true;
    try{
      if(!file||file.size>1073741824)throw new Error('请选择不超过 1 GB 的 ZIP 文件。');
      if(!signatureFile)throw new Error('请同时选择该版本的 signature.json（由 chispa-sign 生成）。');
      if(signatureFile.size>65536)throw new Error('签名文件过大，不是 chispa-sign 生成的 signature.json。');
      const signature=await signatureFile.text();
      label.textContent='准备上传 '+file.name+'…';
      const started=await request('/admin/uploads',{method:'POST',headers:{...headers,'Content-Type':'application/json'},
        body:JSON.stringify({programId:releaseForm.elements.programId.value,version:releaseForm.elements.version.value,size:file.size,signature})});
      let offset=0;
      while(offset<file.size){
        const end=Math.min(offset+started.chunkSize,file.size);
        const position=offset;
        const result=await retry(()=>request('/admin/uploads/'+started.id,{method:'PUT',
          headers:{...headers,'Content-Type':'application/octet-stream','X-Upload-Offset':String(position)},
          body:file.slice(position,end)}));
        if(result.received<=offset||result.received>end)throw new Error('服务器返回了无效的上传进度。');
        offset=result.received;
        label.textContent='正在上传 '+file.name+' · '+Math.round(offset/file.size*100)+'%';
      }
      await retry(()=>request('/admin/uploads/'+started.id+'/complete',{method:'POST',headers}));
      label.textContent='上传完成，服务器正在校验并发布 ZIP…';
      for(;;){
        await new Promise(resolve=>setTimeout(resolve,1200));
        const result=await retry(()=>request('/admin/uploads/'+started.id));
        if(result.state==='published'){location.href='/admin?notice=published#release';return;}
        if(result.state==='failed')throw new Error(result.error||'版本发布失败。');
      }
    }catch(error){label.textContent='上传失败：'+error.message;button.disabled=false;button.textContent=button.dataset.idleText||'上传并发布';}
  });
  addEventListener('pageshow',()=>document.querySelectorAll('button[data-idle-text]').forEach(button=>{button.disabled=false;button.textContent=button.dataset.idleText;}));
  const upload=document.querySelector('[name=package]');
  upload?.addEventListener('change',()=>{const file=upload.files[0],label=document.getElementById('uploadSummary');upload.setCustomValidity(file&&file.size>1073741824?'ZIP 文件不能超过 1 GB。':'');if(label)label.textContent=file?file.name+' · '+(file.size/1048576).toFixed(1)+' MB · 请同时选择 signature.json':'选择已签名的 ZIP 与其 signature.json。';});
  const cropDialog=document.getElementById('iconCropDialog');
  if(cropDialog){
    const cropCanvas=document.getElementById('iconCropCanvas'),previewCanvas=document.getElementById('iconCropPreview');
    const zoomInput=document.getElementById('iconCropZoom'),cropStatus=document.getElementById('iconCropStatus');
    const confirmButton=document.getElementById('iconCropConfirm');
    const confirmed=new WeakMap(),previewUrls=new WeakMap();
    let active=null,drag=null;
    const setFile=(input,file)=>{
      const transfer=new DataTransfer();
      if(file)transfer.items.add(file);
      input.files=transfer.files;
    };
    const clamp=(value,limit)=>Math.max(-limit,Math.min(limit,value));
    const draw=()=>{
      if(!active)return;
      const {bitmap,zoom}=active;
      const scale=Math.max(256/bitmap.width,256/bitmap.height)*zoom;
      const width=bitmap.width*scale,height=bitmap.height*scale;
      active.x=clamp(active.x,(width-256)/2);
      active.y=clamp(active.y,(height-256)/2);
      const context=cropCanvas.getContext('2d');
      context.clearRect(0,0,256,256);
      context.imageSmoothingQuality='high';
      context.drawImage(bitmap,(256-width)/2+active.x,(256-height)/2+active.y,width,height);
      const preview=previewCanvas.getContext('2d');
      preview.clearRect(0,0,96,96);
      preview.imageSmoothingQuality='high';
      preview.drawImage(cropCanvas,0,0,96,96);
    };
    for(const input of document.querySelectorAll('[data-icon-picker]')){
      input.accept='image/png,image/jpeg,image/webp,image/gif,image/bmp,image/avif';
      input.addEventListener('change',async()=>{
        const source=input.files[0];
        if(!source)return;
        if(source.size>20*1024*1024){alert('原图不能超过 20 MB。');setFile(input,confirmed.get(input));return;}
        try{
          const bitmap=await createImageBitmap(source);
          if(!bitmap.width||!bitmap.height)throw new Error('无法读取图片。');
          active={input,bitmap,zoom:1,x:0,y:0,accepted:false};
          zoomInput.value='1';
          cropStatus.textContent='确认后自动转成 PNG；会根据文件大小调整图标尺寸。';
          cropDialog.showModal();
          draw();
        }catch{
          alert('图片无法读取。请选择 PNG、JPG、WebP、GIF、BMP 或 AVIF。');
          setFile(input,confirmed.get(input));
        }
      });
    }
    zoomInput.addEventListener('input',()=>{if(active){active.zoom=Number(zoomInput.value);draw();}});
    cropCanvas.addEventListener('pointerdown',event=>{
      if(!active)return;
      cropCanvas.setPointerCapture(event.pointerId);
      drag={x:event.clientX,y:event.clientY,left:active.x,top:active.y};
    });
    cropCanvas.addEventListener('pointermove',event=>{
      if(!active||!drag)return;
      const scale=256/cropCanvas.getBoundingClientRect().width;
      active.x=drag.left+(event.clientX-drag.x)*scale;
      active.y=drag.top+(event.clientY-drag.y)*scale;
      draw();
    });
    cropCanvas.addEventListener('pointerup',()=>{drag=null;});
    cropCanvas.addEventListener('pointercancel',()=>{drag=null;});
    document.getElementById('iconCropCancel').addEventListener('click',()=>cropDialog.close());
    cropDialog.addEventListener('close',()=>{
      if(active){
        if(!active.accepted)setFile(active.input,confirmed.get(active.input));
        active.bitmap.close();
      }
      active=null;drag=null;confirmButton.disabled=false;
    });
    confirmButton.addEventListener('click',async()=>{
      if(!active)return;
      confirmButton.disabled=true;
      try{
        let file;
        for(const size of [256,224,192,160,128,96]){
          const output=document.createElement('canvas');
          output.width=output.height=size;
          const context=output.getContext('2d');
          context.imageSmoothingQuality='high';
          context.drawImage(cropCanvas,0,0,size,size);
          const blob=await new Promise(resolve=>output.toBlob(resolve,'image/png'));
          if(blob&&blob.size<=128*1024){file=new File([blob],'icon.png',{type:'image/png'});break;}
        }
        if(!file)throw new Error('转换后的图片仍超过 128 KB，请换一张图片。');
        setFile(active.input,file);
        confirmed.set(active.input,file);
        const result=active.input.parentElement.querySelector('.icon-result');
        if(result)result.textContent='已裁剪为 '+Math.ceil(file.size/1024)+' KB 的 PNG，点击“保存选项”后生效。';
        const image=active.input.parentElement.querySelector('.icon-upload-preview');
        if(image){
          const old=previewUrls.get(active.input);
          if(old)URL.revokeObjectURL(old);
          const url=URL.createObjectURL(file);
          previewUrls.set(active.input,url);
          image.src=url;image.hidden=false;
        }
        active.accepted=true;
        cropDialog.close();
      }catch(error){cropStatus.textContent=error.message;confirmButton.disabled=false;}
    });
  }
  const status=document.getElementById('metricsStatus');
  let previousMetrics=null,metricsBusy=false;
  const bytes=value=>{const units=['B','KiB','MiB','GiB','TiB'];let unit=0;while(value>=1024&&unit<units.length-1){value/=1024;unit++;}return value.toLocaleString(undefined,{maximumFractionDigits:unit?2:0})+' '+units[unit];};
  async function updateMetrics(){
      if(document.hidden||metricsBusy||!document.getElementById('requests'))return;
      metricsBusy=true;
      try{
        const response=await fetch('/admin/metrics',{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(10000)});
        if(response.redirected||response.status===401){if(status)status.textContent='登录已过期，请重新登录';return;}
        if(!response.ok)throw new Error();
        const m=await response.json();
        document.getElementById('receivedBytes').textContent=bytes(m.receivedBytes);
        document.getElementById('sentBytes').textContent=bytes(m.sentBytes);
        const seconds=previousMetrics?(Date.parse(m.sampledAt)-Date.parse(previousMetrics.sampledAt))/1000:0;
        const sameRun=previousMetrics?.startedAt===m.startedAt;
        for(const [label,field] of [['receiveRate','receivedBytes'],['sendRate','sentBytes']])document.getElementById(label).textContent=sameRun&&seconds>0?bytes(Math.max(0,m[field]-previousMetrics[field])/seconds)+'/s':'等待下次采样';
        previousMetrics=m;
        for(const id of ['requests','apiRequests','failedRequests','activeSessions','downloads','loginSuccess','loginFailure','clientProbes'])document.getElementById(id).textContent=m[id].toLocaleString();
        document.getElementById('lastHour').textContent=m.lastHour.reduce((a,b)=>a+b,0).toLocaleString();
        document.getElementById('uptimeMinutes').textContent=Math.floor(m.uptimeSeconds/60)+' 分钟';
        document.getElementById('memory').textContent=Math.round(m.memoryBytes/1048576)+' MB';
        document.getElementById('lastClientProbeAt').textContent=m.lastClientProbeAt?new Date(m.lastClientProbeAt).toLocaleString():'尚无';
        document.getElementById('lastClientProbeAddress').textContent=m.lastClientProbeAddress??'尚无';
        const max=Math.max(1,...m.lastHour);
        document.getElementById('requestChart').replaceChildren(...m.lastHour.map(value=>{const bar=document.createElement('i');bar.className='bar';bar.title=value+' 次';bar.style.height=Math.max(3,value*100/max)+'%';return bar;}));
        if(status)status.textContent='已更新 · '+new Date().toLocaleTimeString();
      }catch{if(status)status.textContent='连接中断 · 显示上次采样，等待重试';}
      finally{metricsBusy=false;}
    }
  if(document.getElementById('requests')) {
    updateMetrics();setInterval(updateMetrics,15000);
    document.getElementById('browserProbe').addEventListener('click',async event=>{
      const button=event.currentTarget,label=document.getElementById('browserProbeResult'),start=performance.now();
      button.disabled=true;label.textContent='正在连接…';
      try{const result=await fetch('/health',{cache:'no-store',signal:AbortSignal.timeout(8000)});label.textContent=result.ok?'连接成功 · '+Math.round(performance.now()-start)+' ms':'服务器返回 '+result.status;}
      catch{label.textContent='连接失败或超时，请检查网络。';}
      finally{button.disabled=false;}
    });
  }
  // 查看用户密码的页面：只在屏幕上停留有限时间。清除的是输入框里的值；再次显示仍要通过服务器端
  // 的短时授权，刷新或关闭页面后服务器不会把密码回填进来。
  const secret = document.getElementById('secretValue');
  if (secret) {
    let left = 30;
    const countdown = document.getElementById('secretCountdown');
    const wipe = () => { secret.value = ''; secret.defaultValue = ''; secret.removeAttribute('value'); };
    const timer = setInterval(() => {
      left -= 1;
      if (countdown) countdown.textContent = left > 0 ? left + ' 秒后自动清除' : '已清除，如需再次查看请重新验证身份';
      if (left <= 0) { clearInterval(timer); wipe(); secret.placeholder = '已清除'; }
    }, 1000);
    addEventListener('pagehide', wipe);
    addEventListener('beforeunload', wipe);
  }
})();
